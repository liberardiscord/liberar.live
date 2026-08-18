#!/usr/bin/env bash
# Instala um no completo do liberar.live em Debian/Ubuntu.
#
# Uso (como root, com os quatro arquivos no mesmo diretorio):
#   API_DOMAIN=api.example.com \
#   PROXY_DOMAIN=proxy-us-1.example.com \
#   NODE_NAME=socks5 \
#   ./install-node.sh
#
# Arquivos esperados ao lado deste script:
#   droute-broker, gost.yaml.example, reaper.sh
#
# O script e idempotente. O segredo do Redis e criado na VPS e preservado nas
# execucoes seguintes; nenhum segredo operacional pertence ao repositorio.

set -Eeuo pipefail
umask 027

readonly GOST_VERSION="3.2.6"
readonly GOST_ARCHIVE_SHA256="b39037b0380ea001fb3c0c28441c2e10bfc694f90682739a65b53e55dce5238b"
readonly SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"

API_DOMAIN="${API_DOMAIN:-}"
PROXY_DOMAIN="${PROXY_DOMAIN:-}"
NODE_NAME="${NODE_NAME:-socks5}"
SOCKS_PORT="${SOCKS_PORT:-1080}"

die() {
    printf 'install-node: %s\n' "$*" >&2
    exit 1
}

require_root() {
    [[ "${EUID}" -eq 0 ]] || die "execute como root"
}

validate_inputs() {
    [[ "$API_DOMAIN" =~ ^[A-Za-z0-9.-]+$ ]] || die "API_DOMAIN invalido"
    [[ "$PROXY_DOMAIN" =~ ^[A-Za-z0-9.-]+$ ]] || die "PROXY_DOMAIN invalido"
    [[ "$NODE_NAME" =~ ^[A-Za-z0-9._-]+$ ]] || die "NODE_NAME invalido"
    [[ "$SOCKS_PORT" =~ ^[0-9]+$ ]] || die "SOCKS_PORT invalido"
    (( SOCKS_PORT >= 1 && SOCKS_PORT <= 65535 )) || die "SOCKS_PORT fora da faixa"

    [[ -x "${SCRIPT_DIR}/droute-broker" ]] || die "droute-broker ausente ou sem permissao de execucao"
    [[ -r "${SCRIPT_DIR}/gost.yaml.example" ]] || die "gost.yaml.example ausente"
    [[ -r "${SCRIPT_DIR}/reaper.sh" ]] || die "reaper.sh ausente"
    command -v systemctl >/dev/null || die "systemd e obrigatorio"
    [[ "$(dpkg --print-architecture)" == "amd64" ]] || die "este pacote foi preparado para amd64"
}

install_packages() {
    export DEBIAN_FRONTEND=noninteractive
    apt-get update
    apt-get install -y --no-install-recommends \
        ca-certificates curl debian-archive-keyring debian-keyring gnupg \
        iproute2 nftables openssl redis-server unattended-upgrades

    install -d -m 0755 /usr/share/keyrings /etc/apt/sources.list.d
    local caddy_key caddy_list
    caddy_key="$(mktemp)"
    caddy_list="$(mktemp)"
    trap 'rm -f "$caddy_key" "$caddy_list"' RETURN
    curl --proto '=https' --tlsv1.2 -fsSL \
        https://dl.cloudsmith.io/public/caddy/stable/gpg.key -o "$caddy_key"
    gpg --dearmor --yes -o /usr/share/keyrings/caddy-stable-archive-keyring.gpg "$caddy_key"
    curl --proto '=https' --tlsv1.2 -fsSL \
        https://dl.cloudsmith.io/public/caddy/stable/debian.deb.txt -o "$caddy_list"
    install -m 0644 "$caddy_list" /etc/apt/sources.list.d/caddy-stable.list
    chmod 0644 /usr/share/keyrings/caddy-stable-archive-keyring.gpg
    apt-get update
    apt-get install -y --no-install-recommends caddy
    trap - RETURN
    rm -f "$caddy_key" "$caddy_list"
}

install_gost() {
    local archive workdir
    workdir="$(mktemp -d)"
    archive="${workdir}/gost.tar.gz"
    trap 'rm -rf "$workdir"' RETURN

    curl --proto '=https' --tlsv1.2 -fL \
        "https://github.com/go-gost/gost/releases/download/v${GOST_VERSION}/gost_${GOST_VERSION}_linux_amd64.tar.gz" \
        -o "$archive"
    printf '%s  %s\n' "$GOST_ARCHIVE_SHA256" "$archive" | sha256sum -c -
    tar -xzf "$archive" -C "$workdir"
    install -m 0755 "${workdir}/gost" /usr/local/bin/gost

    trap - RETURN
    rm -rf "$workdir"
}

install_application_files() {
    id -u liberar-broker >/dev/null 2>&1 || \
        useradd --system --no-create-home --shell /usr/sbin/nologin liberar-broker
    id -u liberar-gost >/dev/null 2>&1 || \
        useradd --system --no-create-home --shell /usr/sbin/nologin liberar-gost

    install -d -m 0755 /usr/local/libexec/liberar /usr/local/lib/liberar
    install -d -m 0750 /etc/liberar /etc/gost
    chown root:liberar-gost /etc/gost
    install -m 0755 "${SCRIPT_DIR}/droute-broker" /usr/local/libexec/liberar/droute-broker
    # O pacote pode ser montado no Windows. Remover CR no fim das linhas evita
    # que o kernel tente executar um interpretador literalmente chamado
    # "/bin/sh\r" depois do primeiro reboot.
    sed 's/\r$//' "${SCRIPT_DIR}/reaper.sh" > /usr/local/lib/liberar/reaper.sh.new
    install -m 0755 /usr/local/lib/liberar/reaper.sh.new /usr/local/lib/liberar/reaper.sh
    rm -f /usr/local/lib/liberar/reaper.sh.new

    # Somente a primeira ocorrencia e o nome do servico. Os nomes do auther e
    # do limiter continuam os mesmos.
    sed "0,/^  - name: socks5$/s//  - name: ${NODE_NAME}/" \
        "${SCRIPT_DIR}/gost.yaml.example" > /etc/gost/gost.yaml.new
    install -m 0644 /etc/gost/gost.yaml.new /etc/gost/gost.yaml
    rm -f /etc/gost/gost.yaml.new
}

redis_password_from_existing_env() {
    [[ -r /etc/liberar/broker.env ]] || return 0
    sed -n 's/^BROKER_REDIS_PASSWORD=\([0-9a-fA-F]\{64\}\)$/\1/p' \
        /etc/liberar/broker.env | head -n 1
}

configure_redis_and_broker() {
    local redis_conf redis_password
    redis_conf=/etc/redis/redis.conf
    redis_password="$(redis_password_from_existing_env)"
    [[ -n "$redis_password" ]] || redis_password="$(openssl rand -hex 32)"

    cp -a "$redis_conf" "${redis_conf}.before-liberar" 2>/dev/null || true
    sed -i -E \
        '/^[[:space:]]*(bind|protected-mode|requirepass|appendonly|appendfsync|maxmemory|maxmemory-policy)[[:space:]]+/d' \
        "$redis_conf"
    cat >> "$redis_conf" <<EOF

# Managed by liberar.live install-node.sh
bind 127.0.0.1
protected-mode yes
requirepass ${redis_password}
appendonly yes
appendfsync everysec
maxmemory 128mb
maxmemory-policy noeviction
EOF

    cat > /etc/liberar/broker.env <<EOF
BROKER_PROXY_NODES=${NODE_NAME}=${PROXY_DOMAIN}:${SOCKS_PORT}
BROKER_API_LISTEN=127.0.0.1:8080
BROKER_AUTH_LISTEN=127.0.0.1:8000
BROKER_REDIS_ADDR=127.0.0.1:6379
BROKER_REDIS_PASSWORD=${redis_password}
BROKER_REDIS_DB=0
BROKER_SESSION_TTL=6m
BROKER_SESSION_MIN_INTERVAL=0
BROKER_SESSION_DAILY_MAX=2000
BROKER_POW_DIFFICULTY=20
BROKER_POW_MAX_DIFFICULTY=26
BROKER_MAX_AUTHS_PER_CREDENTIAL=1000
BROKER_MAX_AUTH_FAILURES_PER_IP=50
BROKER_AUTH_FAILURE_WINDOW=10m
BROKER_TRUST_PROXY_HEADER=1
EOF
    chmod 0600 /etc/liberar/broker.env

    systemctl enable redis-server
    systemctl restart redis-server
    REDISCLI_AUTH="$redis_password" redis-cli -h 127.0.0.1 ping | grep -qx PONG \
        || die "Redis nao respondeu ao PING autenticado"
}

install_systemd_units() {
    cat > /etc/systemd/system/droute-broker.service <<'EOF'
[Unit]
Description=liberar.live credential broker
After=network-online.target redis-server.service
Wants=network-online.target
Requires=redis-server.service

[Service]
Type=simple
User=liberar-broker
Group=liberar-broker
ExecStart=/usr/local/libexec/liberar/droute-broker
EnvironmentFile=/etc/liberar/broker.env
NoNewPrivileges=yes
PrivateDevices=yes
PrivateTmp=yes
ProtectClock=yes
ProtectControlGroups=yes
ProtectHome=yes
ProtectHostname=yes
ProtectKernelLogs=yes
ProtectKernelModules=yes
ProtectKernelTunables=yes
ProtectSystem=strict
RestrictAddressFamilies=AF_UNIX AF_INET AF_INET6
RestrictRealtime=yes
SystemCallArchitectures=native
Restart=on-failure
RestartSec=2s

[Install]
WantedBy=multi-user.target
EOF

    cat > /etc/systemd/system/liberar-gost.service <<'EOF'
[Unit]
Description=liberar.live authenticated SOCKS5 node
After=network-online.target droute-broker.service
Wants=network-online.target
Requires=droute-broker.service

[Service]
Type=simple
User=liberar-gost
Group=liberar-gost
ExecStart=/usr/local/bin/gost -C /etc/gost/gost.yaml
LimitNOFILE=1048576
NoNewPrivileges=yes
PrivateDevices=yes
PrivateTmp=yes
ProtectClock=yes
ProtectControlGroups=yes
ProtectHome=yes
ProtectHostname=yes
ProtectKernelLogs=yes
ProtectKernelModules=yes
ProtectKernelTunables=yes
ProtectSystem=strict
RestrictAddressFamilies=AF_UNIX AF_INET AF_INET6
RestrictRealtime=yes
SystemCallArchitectures=native
Restart=on-failure
RestartSec=2s

[Install]
WantedBy=multi-user.target
EOF

    cat > /etc/systemd/system/droute-reaper.service <<EOF
[Unit]
Description=liberar.live expired SOCKS5 connection reaper
After=liberar-gost.service

[Service]
Type=oneshot
Environment=SOCKS_PORT=${SOCKS_PORT}
Environment=BROKER_ACTIVE_URL=http://127.0.0.1:8000/active-ips
Environment=PROXY_NODE_NAME=${NODE_NAME}
ExecStart=/usr/local/lib/liberar/reaper.sh
NoNewPrivileges=yes
PrivateTmp=yes
ProtectHome=yes
ProtectSystem=strict
CapabilityBoundingSet=CAP_NET_ADMIN
AmbientCapabilities=CAP_NET_ADMIN
EOF

    cat > /etc/systemd/system/droute-reaper.timer <<'EOF'
[Unit]
Description=Run liberar.live connection reaper every 30 seconds

[Timer]
OnBootSec=30s
OnUnitActiveSec=30s
AccuracySec=5s
Persistent=true

[Install]
WantedBy=timers.target
EOF

    systemctl daemon-reload
    systemctl enable droute-broker.service liberar-gost.service droute-reaper.timer
    systemctl restart droute-broker.service
    systemctl restart liberar-gost.service
    systemctl restart droute-reaper.timer
}

configure_caddy() {
    cat > /etc/caddy/Caddyfile <<EOF
${API_DOMAIN} {
    @public_api path /v1/* /healthz

    handle @public_api {
        header {
            Cache-Control "no-store"
            X-Content-Type-Options "nosniff"
        }
        reverse_proxy 127.0.0.1:8080
    }

    handle {
        respond 404
    }
}
EOF

    caddy fmt --overwrite /etc/caddy/Caddyfile
    caddy validate --config /etc/caddy/Caddyfile
    install -d -m 0755 /etc/systemd/system/caddy.service.d
    cat > /etc/systemd/system/caddy.service.d/60-liberar-restart.conf <<'EOF'
[Service]
Restart=on-failure
RestartSec=2s
EOF
    systemctl daemon-reload
    systemctl enable caddy
    systemctl restart caddy
}

configure_firewall() {
    local primary_ipv4 dns_elements
    local -a dns_ipv4=()
    primary_ipv4="$(ip -4 route get 192.0.2.1 | sed -n 's/.* src \([0-9.]*\).*/\1/p' | head -n 1)"
    [[ "$primary_ipv4" =~ ^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$ ]] \
        || die "nao foi possivel descobrir o IPv4 principal"

    mapfile -t dns_ipv4 < <(
        awk '$1 == "nameserver" && $2 ~ /^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$/ { print $2 }' \
            /etc/resolv.conf | sort -u
    )
    ((${#dns_ipv4[@]} > 0)) || die "nenhum resolvedor DNS IPv4 encontrado"
    printf -v dns_elements '%s, ' "${dns_ipv4[@]}"
    dns_elements="${dns_elements%, }"

    cp -a /etc/nftables.conf /etc/nftables.conf.before-liberar 2>/dev/null || true
    cat > /etc/nftables.conf <<EOF
#!/usr/sbin/nft -f
flush ruleset

table inet liberar_filter {
    set dns_ipv4 {
        type ipv4_addr
        elements = { ${dns_elements} }
    }

    set blocked_ipv4 {
        type ipv4_addr
        flags interval
        elements = { 0.0.0.0/8, 10.0.0.0/8, 100.64.0.0/10, 127.0.0.0/8,
                     169.254.0.0/16, 172.16.0.0/12, 192.0.0.0/24,
                     192.168.0.0/16, 198.18.0.0/15, 224.0.0.0/4, 240.0.0.0/4 }
    }

    set blocked_ipv6 {
        type ipv6_addr
        flags interval
        elements = { ::1/128, fc00::/7, fe80::/10, ff00::/8 }
    }

    chain input {
        type filter hook input priority filter; policy drop;
        iifname "lo" accept
        ct state invalid drop
        ct state established,related accept
        ip protocol icmp accept
        ip6 nexthdr ipv6-icmp accept
        tcp dport { 22, 80, 443, ${SOCKS_PORT} } accept
        # O SOCKS5 abre um relay UDP efemero por associacao autenticada.
        udp dport 1024-65535 accept
    }

    chain forward {
        type filter hook forward priority filter; policy drop;
    }

    chain output {
        type filter hook output priority filter; policy accept;
        ct state established,related accept

        # DNS fica limitado aos resolvedores que o proprio sistema recebeu. TCP
        # 53 cobre respostas grandes sem transformar o no em relay DNS aberto.
        meta skuid "liberar-gost" ip daddr @dns_ipv4 udp dport 53 accept
        meta skuid "liberar-gost" ip daddr @dns_ipv4 tcp dport 53 accept

        # O GOST precisa consultar apenas o auther local. Recusar os demais
        # destinos de loopback fecha inclusive o bypass por hostname "localhost".
        meta skuid "liberar-gost" oifname "lo" tcp dport 8000 accept
        meta skuid "liberar-gost" oifname "lo" reject
        oifname "lo" accept

        # As restricoes abaixo afetam somente trafego criado pelo proxy. O resto
        # do sistema preserva DHCP, atualizacoes, ACME e operacao normal.
        meta skuid "liberar-gost" ip daddr @blocked_ipv4 reject with icmp type admin-prohibited
        meta skuid "liberar-gost" ip daddr ${primary_ipv4} reject with icmp type admin-prohibited
        meta skuid "liberar-gost" ip6 daddr @blocked_ipv6 reject with icmpv6 type admin-prohibited
        meta skuid "liberar-gost" meta nfproto ipv6 reject with icmpv6 type admin-prohibited

        # Discord informa dinamicamente a porta UDP do servidor de voz no payload
        # READY. Permitimos qualquer porta UDP publica para evitar incompatibilidade;
        # as redes privadas/reservadas continuam recusadas acima.
        # O WebSocket de controle de voz tambem pode usar portas TLS alternativas.
        meta skuid "liberar-gost" tcp dport { 80, 443, 2053, 2083, 2087, 2096, 8443 } accept
        meta skuid "liberar-gost" meta l4proto udp accept
        meta skuid "liberar-gost" reject
    }
}
EOF

    nft -c -f /etc/nftables.conf
    nft -f /etc/nftables.conf
    systemctl enable --now nftables
}

configure_kernel_and_ssh() {
    # Garante que os sysctls de conntrack existam quando systemd-sysctl rodar
    # no boot, sem depender da ordem em que o nftables carregar o modulo.
    cat > /etc/modules-load.d/60-liberar-conntrack.conf <<'EOF'
nf_conntrack
EOF
    modprobe nf_conntrack

    cat > /etc/sysctl.d/60-liberar.conf <<'EOF'
net.ipv4.ip_forward=0
net.ipv6.conf.all.forwarding=0
net.ipv4.tcp_syncookies=1
net.ipv4.conf.all.rp_filter=1
net.ipv4.conf.default.rp_filter=1
net.core.somaxconn=8192
net.ipv4.ip_local_port_range=10240 65535
net.ipv4.tcp_max_syn_backlog=8192
net.ipv4.tcp_max_tw_buckets=65536
net.netfilter.nf_conntrack_max=262144
net.netfilter.nf_conntrack_buckets=262144
vm.overcommit_memory=1
EOF
    sysctl --system >/dev/null

    install -d -m 0755 /etc/ssh/sshd_config.d
    cat > /etc/ssh/sshd_config.d/60-liberar-hardening.conf <<'EOF'
PubkeyAuthentication yes
PermitRootLogin prohibit-password
PasswordAuthentication no
KbdInteractiveAuthentication no
PermitEmptyPasswords no
X11Forwarding no
EOF
    sshd -t
    systemctl reload ssh.service 2>/dev/null || systemctl reload sshd.service
}

enable_automatic_updates() {
    cat > /etc/apt/apt.conf.d/20auto-upgrades <<'EOF'
APT::Periodic::Update-Package-Lists "1";
APT::Periodic::Unattended-Upgrade "1";
EOF
    systemctl enable --now unattended-upgrades.service 2>/dev/null || true
}

disable_unneeded_services() {
    # Imagens de VPS frequentemente trazem um MTA sem configuracao. Este no nao
    # envia email e o firewall bloqueia SMTP; impedir o Postfix de iniciar reduz
    # superficie de ataque e evita um boot marcado como degraded sem necessidade.
    if systemctl list-unit-files postfix.service --no-legend 2>/dev/null \
        | grep -q '^postfix.service'; then
        systemctl disable --now postfix.service 2>/dev/null || true
        systemctl mask postfix.service postfix@-.service >/dev/null
        systemctl reset-failed postfix@-.service 2>/dev/null || true
    fi
}

validate_installation() {
    curl -fsS http://127.0.0.1:8080/healthz | grep -q '"status":"ok"' \
        || die "healthcheck local do broker falhou"
    curl -fsS "http://127.0.0.1:8000/active-ips?node=${NODE_NAME}" | grep -q '"ips"' \
        || die "endpoint privado do reaper falhou"
    systemctl is-active --quiet redis-server droute-broker liberar-gost caddy
    systemctl is-active --quiet droute-reaper.timer
    ss -lnt | grep -Eq "127\.0\.0\.1:8000|127\.0\.0\.1:8080" \
        || die "listeners privados do broker ausentes"
    ss -lnt | grep -Eq "0\.0\.0\.0:${SOCKS_PORT}" \
        || die "listener SOCKS5 ausente"
    ! ss -lnt | grep -Eq "(0\.0\.0\.0|\[::\]):(6379|8000|8080)" \
        || die "Redis ou broker foi exposto publicamente"

    printf '\nInstalacao local concluida.\n'
    printf 'API:   https://%s/healthz\n' "$API_DOMAIN"
    printf 'Proxy: %s:%s (no %s)\n' "$PROXY_DOMAIN" "$SOCKS_PORT" "$NODE_NAME"
    printf 'TLS pode levar alguns minutos depois de o DNS propagar.\n'
}

main() {
    require_root
    validate_inputs
    install_packages
    install_gost
    install_application_files
    configure_redis_and_broker
    install_systemd_units
    configure_caddy
    configure_firewall
    configure_kernel_and_ssh
    enable_automatic_updates
    disable_unneeded_services
    validate_installation
}

main "$@"
