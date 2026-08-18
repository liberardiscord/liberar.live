#!/bin/sh
# Destroys SOCKS5 sockets whose credential has expired.
#
# Why this exists: the gost auther plugin only runs when a connection is being
# established. A socket opened one second before the credential expired would
# otherwise stay up indefinitely, which would leave the activation limit as a
# client-side courtesy again. This closes that hole from the server side.
#
# Requirements: root, iproute2 with socket destroy support (CONFIG_INET_DIAG_DESTROY,
# present on any current distribution kernel), and the broker running locally.
#
# Run it from a systemd timer every 30 seconds. See server/README.md.

set -eu

BROKER_ACTIVE_URL="${BROKER_ACTIVE_URL:-http://127.0.0.1:8000/active-ips}"
PROXY_NODE_NAME="${PROXY_NODE_NAME:-socks5}"
SOCKS_PORT="${SOCKS_PORT:-1080}"
DRY_RUN="${DRY_RUN:-0}"

case "$PROXY_NODE_NAME" in
    ''|*[!A-Za-z0-9._-]*)
        echo "reaper: invalid PROXY_NODE_NAME" >&2
        exit 1
        ;;
esac

case "$BROKER_ACTIVE_URL" in
    *\?*) active_url="${BROKER_ACTIVE_URL}&node=${PROXY_NODE_NAME}" ;;
    *)    active_url="${BROKER_ACTIVE_URL}?node=${PROXY_NODE_NAME}" ;;
esac

active_file=$(mktemp)
peers_file=$(mktemp)
trap 'rm -f "$active_file" "$peers_file"' EXIT

# A failure here must not cause a mass disconnect: if the broker cannot be
# reached we have no idea which credentials are live, so we do nothing.
if ! curl -fsS --max-time 5 "$active_url" > "$active_file".json; then
    echo "reaper: broker unreachable, skipping this run" >&2
    exit 0
fi

sed 's/[][",]/ /g' "$active_file".json | tr ' ' '\n' \
    | grep -E '^[0-9a-fA-F:.]+$' | sort -u > "$active_file" || true
rm -f "$active_file".json

# Column 4 of `ss -H -tn state established` is the peer address. The gost
# listener must bind IPv4 (0.0.0.0), so a peer normally arrives as 192.0.2.4:port.
# The `s/^::ffff://` is defence in depth: should the node ever run a dual-stack
# listener, the peer would read ::ffff:192.0.2.4 while the broker reports the plain
# form, they would never match, and this loop would destroy every live socket.
# Stripping the mapping keeps that misconfiguration from becoming a mass kick.
ss -H -tn state established "( sport = :$SOCKS_PORT )" 2>/dev/null \
    | awk '{print $4}' \
    | sed 's/:[0-9]*$//; s/^\[//; s/\]$//; s/^::ffff://' \
    | sort -u > "$peers_file" || true

killed=0
while IFS= read -r peer; do
    [ -n "$peer" ] || continue
    if grep -qxF "$peer" "$active_file"; then
        continue
    fi
    if [ "$DRY_RUN" = "1" ]; then
        echo "reaper: would destroy sockets from $peer"
    else
        ss -K dst "$peer" "( sport = :$SOCKS_PORT )" >/dev/null 2>&1 || true
        echo "reaper: destroyed sockets from $peer"
    fi
    killed=$((killed + 1))
done < "$peers_file"

[ "$killed" -eq 0 ] || echo "reaper: $killed expired peer(s) handled"
