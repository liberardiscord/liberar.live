# servidor

três processos no nó: gost (SOCKS5), broker (emissão e validação de credencial) e Redis, onde a credencial vive com validade real. nenhum arquivo deste diretório contém host, credencial ou valor operacional.

## por que existe um broker

o desenho anterior compilava usuário e senha dentro do `droute.dll`. qualquer pessoa recuperava a credencial com `strings` e se conectava à VPS indefinidamente, por fora do programa. pior, o limite de cinco minutos era imposto apenas pelo cliente, que lia o próprio registro do Windows, e um binário modificado simplesmente ignorava isso.

agora não existe segredo no binário. cada instalação gera um par de chaves ECDSA P-256 local, se registra pagando um proof-of-work, e a cada ativação recebe uma credencial SOCKS5 aleatória de 128 bits, presa ao IP de origem e com validade curta gravada no Redis. a validade passou a ser do servidor, que é o que torna o limite real.

ver [segurança](../docs/SECURITY.md) e [servidor SOCKS5](../docs/PROXY-SERVER.md).

## componentes

| arquivo | função |
|---|---|
| `broker/` | serviço Go: registro de dispositivo, emissão de credencial e endpoint consultado pelo gost |
| `gost.yaml.example` | serviço SOCKS5 com `auther` em modo plugin apontando para o broker |
| `reaper.sh` | derruba sockets cuja credencial já venceu |
| `install-node.sh` | instala e endurece um nó completo Debian/Ubuntu de forma idempotente |

## requisitos

- Redis 6.2 ou mais novo, porque o broker usa `GETDEL`, que é o que torna cada desafio de uso único.
- gost v3.
- Go 1.22+ para compilar o broker.
- `iproute2` com destruição de socket para o reaper.

o Redis deve escutar apenas em loopback e nunca ser exposto: quem escreve nele emite credencial.

## instalação automatizada

compile o broker estaticamente para Linux e monte um diretório com os quatro
arquivos esperados pelo instalador:

```sh
mkdir -p /tmp/liberar-node
CGO_ENABLED=0 GOOS=linux GOARCH=amd64 go build \
  -trimpath -ldflags "-s -w" \
  -o /tmp/liberar-node/droute-broker ./server/broker
cp server/install-node.sh server/gost.yaml.example server/reaper.sh /tmp/liberar-node/
chmod 700 /tmp/liberar-node/install-node.sh
chmod 755 /tmp/liberar-node/droute-broker /tmp/liberar-node/reaper.sh
```

envie o diretório para a VPS e execute como `root`, depois de os dois nomes DNS
apontarem para ela:

```sh
API_DOMAIN=api.example.com \
PROXY_DOMAIN=proxy-us-1.example.com \
NODE_NAME=socks5 \
SOCKS_PORT=1080 \
./install-node.sh
```

o script é idempotente e instala Redis, gost, Caddy, broker, reaper,
`unattended-upgrades`, sysctl, unidades systemd e nftables. o segredo do Redis é
gerado somente na VPS, gravado com modo `0600` e preservado em novas execuções.
as portas 6379, 8000 e 8080 ficam em loopback, externamente são publicados
apenas SSH, HTTP/HTTPS e o SOCKS5 autenticado. as saídas do processo gost ficam
limitadas às portas necessárias e aos resolvedores DNS já configurados no nó.

o perfil instalado traz limites já exercitados em um nó pequeno, da ordem de
2 GiB: `LimitNOFILE=1048576`, conntrack máximo/hash de 262.144, backlog
SYN/listen de 8.192, `tcp_max_tw_buckets=65536` e faixa efêmera `10240-65535`.
o instalador também antecipa o módulo `nf_conntrack` no boot para que os dois
sysctls de 262.144 sejam reaplicados de forma determinística. são tetos
operacionais, não promessa de usuários: meça no seu próprio hardware e
acompanhe memória, FDs, conntrack, CPU/softirq, PPS e perda UDP.

o broker fala HTTP puro. coloque um terminador TLS na frente (Caddy resolve certificado sozinho) publicando apenas `/v1/*` e `/healthz`. nunca publique a porta do `auther`: ela valida credencial e, exposta, vira oráculo de adivinhação.

reaper por timer, a cada 30 segundos:

```ini
# droute-reaper.service
[Service]
Type=oneshot
Environment=PROXY_NODE_NAME=socks5
ExecStart=/usr/local/bin/droute-reaper.sh

# droute-reaper.timer
[Timer]
OnBootSec=1min
OnUnitActiveSec=30s
```

teste com `DRY_RUN=1` antes de habilitar, ele derruba conexões de verdade.

## configuração do broker

| variável | padrão | efeito |
|---|---|---|
| `BROKER_PROXY_NODES` | sem padrão | obrigatório em instalações novas, lista `nome=host:porta` separada por vírgulas |
| `BROKER_PROXY_HOST` / `BROKER_PROXY_PORT` | sem padrão / `1080` | compatibilidade temporária com instalação antiga de um nó |
| `BROKER_SESSION_TTL` | `6m` | validade real da credencial |
| `BROKER_SESSION_MIN_INTERVAL` | `0` (desligado) | intervalo mínimo entre ativações do mesmo dispositivo. zero por padrão de propósito: clicar "liberar" não pode ser recusado, e reemitir pro mesmo dispositivo é inócuo (credencial já é presa ao IP e curta). ligue só se aparecer abuso |
| `BROKER_SESSION_DAILY_MAX` | `2000` | teto diário por dispositivo, freio contra cliente em laço e não um limite que um humano clicando alcance |
| `BROKER_POW_DIFFICULTY` | `20` | bits zero exigidos no registro |
| `BROKER_MAX_AUTHS_PER_CREDENTIAL` | `1000` | teto de conexões por credencial |
| `BROKER_TRUST_PROXY_HEADER` | desligado | ler `X-Forwarded-For` |

`BROKER_TRUST_PROXY_HEADER` só deve ser ligado quando existe um proxy reverso seu na frente. ligado sem isso, o cliente escolhe o próprio IP e o vínculo de endereço deixa de valer.

o broker escolhe os nós em round-robin e entrega `host` e `port` na resposta. a
credencial fica vinculada ao nome do nó: o `services[].name` do gost e o
`PROXY_NODE_NAME` do reaper precisam ser iguais ao nome configurado no pool.

para começar com uma VPS:

```dotenv
BROKER_PROXY_NODES=socks5=proxy-us-1.example.com:1080
```

para adicionar outra sem publicar novo `.exe`:

```dotenv
BROKER_PROXY_NODES=socks5=proxy-us-1.example.com:1080,socks5-us-2=proxy-us-2.example.com:1080
```

no segundo nó, use `name: socks5-us-2` no gost e `PROXY_NODE_NAME=socks5-us-2`
no reaper. depois de validar gost, auther privado e reaper, reinicie apenas o
broker. as ativações seguintes passam a alternar entre os dois nós, nenhum
cliente precisa baixar outra versão.

## testes

```sh
cd server/broker
go test ./...
```

a suíte sobe um Redis em memória (`miniredis`) e roda o caminho inteiro: proof-of-work, registro, desafio assinado, emissão e a chamada que o gost faz a cada conexão. é dependência só de teste e não entra no binário.

o que ela verifica de fato:

| afirmação | teste |
|---|---|
| a credencial morre sozinha quando o TTL vence | `TestCredentialDiesWithItsTTL` |
| credencial copiada não funciona de outro IP | `TestCredentialIsBoundToItsIP` |
| o `X-Forwarded-For` não fura o vínculo de IP | `TestForwardedHeaderIsIgnoredUnlessTrusted` |
| desafio vale uma única vez | `TestAuthChallengeIsSingleUse`, `TestPoWChallengeIsSingleUse` |
| sem assinatura válida não sai credencial | `TestSignatureIsRequired` |
| pedir em laço esbarra em intervalo e cota | `TestSecondActivationIsRateLimited`, `TestDailyQuotaStopsTheDevice` |
| revogar corta na hora | `TestRevokedDeviceCannotActivate` |
| a senha não é guardada em claro | `TestPasswordIsNotStoredInRedis` |
| `/active-ips` acompanha as credenciais vivas | `TestActiveIPsFollowsTheCredentials` |

### interoperabilidade com o cliente Windows

`interop_test.go` verifica material produzido pelo `DeviceIdentity` real: a chave gerada pelo ECDsaCng, o `device_id` derivado dela e duas assinaturas de verdade. isso existe porque compilar dos dois lados não prova que os dois lados concordam: um prefixo SPKI errado ou uma assinatura em DER no lugar de r||s quebraria toda ativação sem nenhum teste unitário reclamar.

o vetor fica em `server/broker/testdata/csharp_interop.json` e é versionado, então a conferência continua valendo em máquinas sem .NET. refazê-lo é raro e não faz parte do build, só é preciso quando o formato da identidade muda de verdade.

se esse dia chegar, escreva um gerador seu no Windows. ele precisa compilar `installer/Classes/DeviceIdentity.cs` junto de um `Main` que crie a identidade, assine dois nonces diferentes e grave um JSON com os campos `device_id`, `public_key`, `nonce`, `signature` e `signature_second`, exatamente como o arquivo atual. faça o gerador recusar rodar quando já existir uma identidade em `%LOCALAPPDATA%\liberar.live\device.key`, e apagar a que ele mesmo criou.

## antes de abrir para distribuição pública

- [ ] Redis em loopback, com `requirepass` se houver qualquer outro serviço na máquina
- [ ] porta do `auther` inacessível de fora (`ss -ltnp` para confirmar)
- [ ] TLS na frente do broker, com apenas `/v1/*` publicado
- [ ] bloqueio de portas de abuso no firewall, 25/SMTP em primeiro lugar
- [ ] reaper validado com `DRY_RUN=1` e depois habilitado
- [ ] endereço de abuso publicado e monitorado
- [ ] dimensionamento validado por benchmark representativo

## rotação e revogação

revogar um dispositivo é marcá-lo no Redis:

```sh
redis-cli HSET dev:<device_id> revoked 1
```

o `device_id` aparece no campo `id` que o broker devolve ao gost, então o log do gost permite ligar abuso a dispositivo. reemitir credencial não contorna a revogação, registrar um dispositivo novo custa outro proof-of-work.
