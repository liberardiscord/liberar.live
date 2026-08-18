package main

import (
	"fmt"
	"log"
	"net"
	"os"
	"strconv"
	"strings"
	"time"
)

// ProxyNode is public connection data, never a credential. Name must match the
// GOST service name on that node so credentials cannot be replayed on another
// member of the pool.
type ProxyNode struct {
	Name string
	Host string
	Port int
}

func (n ProxyNode) Endpoint() string {
	return net.JoinHostPort(n.Host, strconv.Itoa(n.Port))
}

// Config is read entirely from the environment. Nothing here is a secret that
// belongs in the repository: the only sensitive value is the Redis address,
// and Redis itself must never be exposed outside the host.
type Config struct {
	// APIListen serves the client-facing endpoints over plain HTTP. Terminate
	// TLS in front of it (Caddy, nginx) so certificate renewal stays out of
	// this process.
	APIListen string
	// AuthListen serves the gost auther plugin. Keep it on loopback for one node,
	// or bind only to a WireGuard/private address when remote proxy nodes exist.
	// Binding it publicly would expose credential validation to the internet.
	AuthListen string

	RedisAddr     string
	RedisPassword string
	RedisDB       int

	// ProxyNodes are selected per activation and handed to the client with the
	// credential. Editing BROKER_PROXY_NODES changes the fleet without shipping
	// a new Windows build.
	ProxyNodes []ProxyNode

	// PoWDifficulty is the number of leading zero bits required. Each extra bit
	// doubles the work. 20 bits lands around 2-5s on a typical desktop.
	PoWDifficulty    int
	PoWMaxDifficulty int
	PoWChallengeTTL  time.Duration
	AuthChallengeTTL time.Duration

	// SessionTTL is the real, server-side lifetime of an activation. It is the
	// control that replaces the purely client-side enabled_until.
	SessionTTL time.Duration
	// SessionMinInterval, when positive, is the minimum time between activations
	// for one device. It defaults to zero — disabled — because clicking
	// "Liberar" must never be refused: every activation already gets a fresh,
	// IP-bound, short-lived credential, and blocking a re-click only hurts the
	// legitimate user after a reconnect or an expiry. Set it above zero only if
	// abuse appears; it is a throttle, not a security control.
	SessionMinInterval time.Duration
	// SessionDailyMax is a high but finite ceiling — a backstop against a client
	// stuck requesting in a loop, not a limit a person clicking could ever reach.
	// It is not a security control; the short credential TTL and the IP binding
	// are.
	SessionDailyMax int64

	// MaxAuthsPerCredential caps how many connections a single credential may
	// open. gost gives no disconnect callback, so this counts authentications
	// rather than live sessions.
	MaxAuthsPerCredential int64
	// MaxAuthFailuresPerIP throttles online guessing against /socks-auth.
	MaxAuthFailuresPerIP int64
	AuthFailureWindow    time.Duration

	// TrustProxyHeader makes the broker read X-Forwarded-For. Enable it only
	// when a reverse proxy you control is in front, otherwise a client can spoof
	// its own address and defeat the IP binding.
	TrustProxyHeader bool
}

func LoadConfig() Config {
	c := Config{
		APIListen:             env("BROKER_API_LISTEN", "127.0.0.1:8080"),
		AuthListen:            env("BROKER_AUTH_LISTEN", "127.0.0.1:8000"),
		RedisAddr:             env("BROKER_REDIS_ADDR", "127.0.0.1:6379"),
		RedisPassword:         env("BROKER_REDIS_PASSWORD", ""),
		RedisDB:               envInt("BROKER_REDIS_DB", 0),
		PoWDifficulty:         envInt("BROKER_POW_DIFFICULTY", 20),
		PoWMaxDifficulty:      envInt("BROKER_POW_MAX_DIFFICULTY", 26),
		PoWChallengeTTL:       envDuration("BROKER_POW_CHALLENGE_TTL", 2*time.Minute),
		AuthChallengeTTL:      envDuration("BROKER_AUTH_CHALLENGE_TTL", 60*time.Second),
		SessionTTL:            envDuration("BROKER_SESSION_TTL", 6*time.Minute),
		SessionMinInterval:    envDuration("BROKER_SESSION_MIN_INTERVAL", 0),
		SessionDailyMax:       int64(envInt("BROKER_SESSION_DAILY_MAX", 2000)),
		MaxAuthsPerCredential: int64(envInt("BROKER_MAX_AUTHS_PER_CREDENTIAL", 1000)),
		MaxAuthFailuresPerIP:  int64(envInt("BROKER_MAX_AUTH_FAILURES_PER_IP", 50)),
		AuthFailureWindow:     envDuration("BROKER_AUTH_FAILURE_WINDOW", 10*time.Minute),
		TrustProxyHeader:      env("BROKER_TRUST_PROXY_HEADER", "") == "1",
	}

	nodes, err := proxyNodesFromEnvironment()
	if err != nil {
		log.Fatalf("invalid proxy pool: %v", err)
	}
	c.ProxyNodes = nodes
	if c.PoWDifficulty < 1 || c.PoWDifficulty > 32 {
		log.Fatalf("BROKER_POW_DIFFICULTY out of range: %d", c.PoWDifficulty)
	}
	if c.PoWMaxDifficulty < c.PoWDifficulty {
		c.PoWMaxDifficulty = c.PoWDifficulty
	}
	return c
}

func proxyNodesFromEnvironment() ([]ProxyNode, error) {
	if raw := strings.TrimSpace(os.Getenv("BROKER_PROXY_NODES")); raw != "" {
		return parseProxyNodes(raw)
	}

	// Compatibility for an existing single-node deployment. New deployments
	// should use BROKER_PROXY_NODES so adding a second node is configuration only.
	host := strings.TrimSpace(os.Getenv("BROKER_PROXY_HOST"))
	if host == "" {
		return nil, fmt.Errorf("BROKER_PROXY_NODES is required")
	}
	port := envInt("BROKER_PROXY_PORT", 1080)
	if err := validateProxyHost(host); err != nil {
		return nil, err
	}
	if port < 1 || port > 65535 {
		return nil, fmt.Errorf("BROKER_PROXY_PORT out of range: %d", port)
	}
	return []ProxyNode{{Name: "socks5", Host: host, Port: port}}, nil
}

// parseProxyNodes accepts a comma-separated pool such as:
//
//	us-1=proxy-us-1.example:1080,us-2=proxy-us-2.example:1080
//
// Explicit names are required because the same name is sent by GOST as its
// service field and used by the broker/reaper to isolate sessions per node.
func parseProxyNodes(raw string) ([]ProxyNode, error) {
	parts := strings.Split(raw, ",")
	if len(parts) > 64 {
		return nil, fmt.Errorf("at most 64 proxy nodes are allowed")
	}

	nodes := make([]ProxyNode, 0, len(parts))
	seenNames := make(map[string]struct{}, len(parts))
	seenEndpoints := make(map[string]struct{}, len(parts))
	for _, part := range parts {
		fields := strings.SplitN(strings.TrimSpace(part), "=", 2)
		if len(fields) != 2 {
			return nil, fmt.Errorf("node %q must use name=host:port", part)
		}
		name := strings.TrimSpace(fields[0])
		if !validNodeName(name) {
			return nil, fmt.Errorf("invalid node name %q", name)
		}

		host, portText, err := net.SplitHostPort(strings.TrimSpace(fields[1]))
		if err != nil {
			return nil, fmt.Errorf("node %q must contain host:port: %w", name, err)
		}
		host = strings.TrimSpace(host)
		if err := validateProxyHost(host); err != nil {
			return nil, fmt.Errorf("node %q: %w", name, err)
		}
		port, err := strconv.Atoi(portText)
		if err != nil || port < 1 || port > 65535 {
			return nil, fmt.Errorf("node %q has invalid port %q", name, portText)
		}

		node := ProxyNode{Name: name, Host: host, Port: port}
		endpoint := node.Endpoint()
		if _, exists := seenNames[name]; exists {
			return nil, fmt.Errorf("duplicate node name %q", name)
		}
		if _, exists := seenEndpoints[endpoint]; exists {
			return nil, fmt.Errorf("duplicate proxy endpoint %q", endpoint)
		}
		seenNames[name] = struct{}{}
		seenEndpoints[endpoint] = struct{}{}
		nodes = append(nodes, node)
	}

	if len(nodes) == 0 {
		return nil, fmt.Errorf("proxy pool is empty")
	}
	return nodes, nil
}

func validNodeName(value string) bool {
	if value == "" || len(value) > 64 {
		return false
	}
	for _, r := range value {
		if (r >= 'a' && r <= 'z') || (r >= 'A' && r <= 'Z') ||
			(r >= '0' && r <= '9') || r == '-' || r == '_' || r == '.' {
			continue
		}
		return false
	}
	return true
}

func validateProxyHost(host string) error {
	if host == "" || len(host) > 253 {
		return fmt.Errorf("proxy host is empty or too long")
	}
	if ip := net.ParseIP(host); ip != nil {
		if ip.To4() == nil {
			return fmt.Errorf("IPv6 proxy nodes are not supported by the Windows client")
		}
		return nil
	}
	for _, r := range host {
		if (r >= 'a' && r <= 'z') || (r >= 'A' && r <= 'Z') ||
			(r >= '0' && r <= '9') || r == '-' || r == '.' {
			continue
		}
		return fmt.Errorf("invalid proxy host %q", host)
	}
	return nil
}

func env(key, def string) string {
	if v := os.Getenv(key); v != "" {
		return v
	}
	return def
}

func envInt(key string, def int) int {
	v := os.Getenv(key)
	if v == "" {
		return def
	}
	n, err := strconv.Atoi(v)
	if err != nil {
		log.Fatalf("%s must be an integer, got %q", key, v)
	}
	return n
}

func envDuration(key string, def time.Duration) time.Duration {
	v := os.Getenv(key)
	if v == "" {
		return def
	}
	d, err := time.ParseDuration(v)
	if err != nil {
		log.Fatalf("%s must be a duration such as 6m, got %q", key, v)
	}
	return d
}
