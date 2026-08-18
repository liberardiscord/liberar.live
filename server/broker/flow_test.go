package main

import (
	"bytes"
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/rand"
	"crypto/sha256"
	"crypto/x509"
	"encoding/base64"
	"encoding/hex"
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"testing"
	"time"

	"github.com/alicebob/miniredis/v2"
)

// These tests drive the broker against an in-memory Redis, so the whole
// activation path runs for real: proof of work, device registration, the signed
// challenge, credential issue, and the auther call gost makes on every
// connection.
//
// The cases that matter are the negative ones. The claim this design rests on
// is that a credential stops working when its time is up, from anywhere other
// than the address that asked for it, and that a modified client cannot help
// itself to more. Those are the assertions below.

const testIP = "203.0.113.7"

type harness struct {
	t   *testing.T
	srv *Server
	mr  *miniredis.Miniredis
	cfg Config
}

func newHarness(t *testing.T, tune ...func(*Config)) *harness {
	t.Helper()

	mr := miniredis.RunT(t)

	cfg := Config{
		RedisAddr:        mr.Addr(),
		ProxyNodes:       []ProxyNode{{Name: "socks5", Host: "198.51.100.10", Port: 1080}},
		PoWDifficulty:    8, // cheap on purpose; the escalation logic is tested separately
		PoWMaxDifficulty: 12,
		PoWChallengeTTL:  2 * time.Minute,
		AuthChallengeTTL: 60 * time.Second,

		SessionTTL:         6 * time.Minute,
		SessionMinInterval: 5 * time.Minute,
		SessionDailyMax:    48,

		MaxAuthsPerCredential: 200,
		MaxAuthFailuresPerIP:  50,
		AuthFailureWindow:     10 * time.Minute,
	}
	for _, f := range tune {
		f(&cfg)
	}

	store := NewStore(cfg)
	t.Cleanup(func() { _ = store.Close() })

	return &harness{t: t, srv: NewServer(cfg, store), mr: mr, cfg: cfg}
}

// call sends a request the way the real listener would, with a source address,
// and returns the status plus the decoded body.
func (h *harness) call(handler http.HandlerFunc, path string, body any, ip string) (int, map[string]any) {
	h.t.Helper()

	var buf bytes.Buffer
	if body != nil {
		if err := json.NewEncoder(&buf).Encode(body); err != nil {
			h.t.Fatalf("encode body: %v", err)
		}
	} else {
		buf.WriteString("{}")
	}

	req := httptest.NewRequest(http.MethodPost, path, &buf)
	req.RemoteAddr = ip + ":51234"
	rec := httptest.NewRecorder()
	handler(rec, req)

	var decoded map[string]any
	if rec.Body.Len() > 0 {
		if err := json.Unmarshal(rec.Body.Bytes(), &decoded); err != nil {
			h.t.Fatalf("response from %s is not JSON: %v (%q)", path, err, rec.Body.String())
		}
	}
	return rec.Code, decoded
}

func (h *harness) errorCode(body map[string]any) string {
	if v, ok := body["error"].(string); ok {
		return v
	}
	return ""
}

// ------------------------------------------------------------------- client side

// device is the test's stand-in for DeviceIdentity.cs. The signature format is
// the same one the C# client produces, which interop_test.go pins against real
// output from that client.
type device struct {
	key *ecdsa.PrivateKey
	der []byte
	id  string
}

func newDevice(t *testing.T) *device {
	t.Helper()

	key, err := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	if err != nil {
		t.Fatalf("generate key: %v", err)
	}
	der, err := x509.MarshalPKIXPublicKey(&key.PublicKey)
	if err != nil {
		t.Fatalf("marshal key: %v", err)
	}
	sum := sha256.Sum256(der)
	return &device{key: key, der: der, id: hex.EncodeToString(sum[:16])}
}

func (d *device) sign(t *testing.T, nonceHex string) string {
	t.Helper()

	nonce, err := hex.DecodeString(nonceHex)
	if err != nil {
		t.Fatalf("nonce is not hex: %v", err)
	}

	msg := append([]byte(sessionDomain), []byte(d.id)...)
	msg = append(msg, nonce...)
	digest := sha256.Sum256(msg)

	r, s, err := ecdsa.Sign(rand.Reader, d.key, digest[:])
	if err != nil {
		t.Fatalf("sign: %v", err)
	}

	sig := make([]byte, 64)
	r.FillBytes(sig[:32])
	s.FillBytes(sig[32:])
	return base64.StdEncoding.EncodeToString(sig)
}

// solvePoW is the work the client pays to register. The test difficulty is low,
// so this is a few hundred hashes.
func solvePoW(t *testing.T, nonceHex string, difficulty int) uint64 {
	t.Helper()
	for counter := uint64(0); counter < 1<<32; counter++ {
		if verifyPoW(nonceHex, counter, difficulty) {
			return counter
		}
	}
	t.Fatalf("could not solve a difficulty %d challenge", difficulty)
	return 0
}

// ------------------------------------------------------------------ flow steps

func (h *harness) register(d *device, ip string) {
	h.t.Helper()

	status, body := h.call(h.srv.HandleRegisterChallenge, "/v1/register/challenge", nil, ip)
	if status != http.StatusOK {
		h.t.Fatalf("register/challenge: status %d (%v)", status, body)
	}

	nonce, _ := body["nonce"].(string)
	difficulty := int(body["difficulty"].(float64))
	counter := solvePoW(h.t, nonce, difficulty)

	status, body = h.call(h.srv.HandleRegister, "/v1/register", registerRequest{
		Nonce:     nonce,
		Counter:   counter,
		PublicKey: base64.StdEncoding.EncodeToString(d.der),
	}, ip)
	if status != http.StatusOK {
		h.t.Fatalf("register: status %d (%v)", status, body)
	}
	if got, _ := body["device_id"].(string); got != d.id {
		h.t.Fatalf("device id: broker returned %s, client derived %s", got, d.id)
	}
}

func (h *harness) challenge(d *device, ip string) (int, string) {
	h.t.Helper()

	status, body := h.call(h.srv.HandleChallenge, "/v1/challenge",
		challengeRequest{DeviceID: d.id}, ip)
	nonce, _ := body["nonce"].(string)
	return status, nonce
}

func (h *harness) session(d *device, ip string) (int, map[string]any) {
	h.t.Helper()

	status, nonce := h.challenge(d, ip)
	if status != http.StatusOK {
		h.t.Fatalf("challenge: status %d", status)
	}

	return h.call(h.srv.HandleSession, "/v1/session", sessionRequest{
		DeviceID:  d.id,
		Nonce:     nonce,
		Signature: d.sign(h.t, nonce),
	}, ip)
}

// socksAuth is the call gost makes for every connection it accepts.
func (h *harness) socksAuth(username, password, clientIP string) bool {
	return h.socksAuthOnNode(username, password, clientIP, h.cfg.ProxyNodes[0].Name)
}

func (h *harness) socksAuthOnNode(username, password, clientIP, node string) bool {
	h.t.Helper()

	_, body := h.call(h.srv.HandleSocksAuth, "/socks-auth", gostAuthRequest{
		Service:  node,
		Username: username,
		Password: password,
		Client:   clientIP + ":40000",
	}, clientIP)

	ok, _ := body["ok"].(bool)
	return ok
}

func credentialFrom(t *testing.T, body map[string]any) (string, string) {
	t.Helper()

	user, _ := body["username"].(string)
	pass, _ := body["password"].(string)
	if user == "" || pass == "" {
		t.Fatalf("session returned an empty credential: %v", body)
	}
	return user, pass
}

// ----------------------------------------------------------------------- tests

func TestActivationFlow(t *testing.T) {
	h := newHarness(t)
	d := newDevice(t)

	h.register(d, testIP)

	status, body := h.session(d, testIP)
	if status != http.StatusOK {
		t.Fatalf("session: status %d (%v)", status, body)
	}

	user, pass := credentialFrom(t, body)
	if host, _ := body["host"].(string); host != h.cfg.ProxyNodes[0].Host {
		t.Fatalf("session must hand the client the endpoint, got %q", host)
	}
	if len(user) != 32 || len(pass) != 32 {
		t.Fatalf("expected 128 bit hex halves, got %d and %d characters", len(user), len(pass))
	}

	if !h.socksAuth(user, pass, testIP) {
		t.Fatal("gost was told to reject a credential the broker had just issued")
	}
}

func TestProxyPoolRotatesAndBindsCredentialsToNodes(t *testing.T) {
	h := newHarness(t, func(cfg *Config) {
		cfg.ProxyNodes = []ProxyNode{
			{Name: "us-1", Host: "proxy-us-1.example", Port: 1080},
			{Name: "us-2", Host: "proxy-us-2.example", Port: 2080},
		}
		cfg.SessionMinInterval = 0
	})
	d := newDevice(t)
	h.register(d, testIP)

	status, first := h.session(d, testIP)
	if status != http.StatusOK {
		t.Fatalf("first session: status %d (%v)", status, first)
	}
	status, second := h.session(d, testIP)
	if status != http.StatusOK {
		t.Fatalf("second session: status %d (%v)", status, second)
	}

	if got, _ := first["host"].(string); got != "proxy-us-1.example" {
		t.Fatalf("first session used %q, want first node", got)
	}
	if got, _ := second["host"].(string); got != "proxy-us-2.example" {
		t.Fatalf("second session used %q, want second node", got)
	}
	if got := int(second["port"].(float64)); got != 2080 {
		t.Fatalf("second session port %d, want 2080", got)
	}

	user, pass := credentialFrom(t, first)
	if h.socksAuthOnNode(user, pass, testIP, "us-2") {
		t.Fatal("credential issued for us-1 was accepted on us-2")
	}
	if !h.socksAuthOnNode(user, pass, testIP, "us-1") {
		t.Fatal("credential was rejected on its assigned node")
	}
}

// The password is never stored, only its digest. Someone who reads Redis cannot
// walk away with a working credential.
func TestPasswordIsNotStoredInRedis(t *testing.T) {
	h := newHarness(t)
	d := newDevice(t)

	h.register(d, testIP)
	_, body := h.session(d, testIP)
	user, pass := credentialFrom(t, body)

	stored := h.mr.HGet("cred:"+user, "pwhash")
	if stored == "" {
		t.Fatal("credential missing from redis")
	}
	if stored == pass {
		t.Fatal("the plaintext password is sitting in Redis")
	}
	if stored != sha256Hex(pass) {
		t.Fatalf("stored digest does not match the issued password")
	}
}

// This is the test that replaces the old client-side five minute limit. Nothing
// on the user's machine participates: Redis drops the key and the credential
// stops existing.
func TestCredentialDiesWithItsTTL(t *testing.T) {
	h := newHarness(t)
	d := newDevice(t)

	h.register(d, testIP)
	_, body := h.session(d, testIP)
	user, pass := credentialFrom(t, body)

	if !h.socksAuth(user, pass, testIP) {
		t.Fatal("credential rejected while still valid")
	}

	h.mr.FastForward(h.cfg.SessionTTL + time.Second)

	if h.socksAuth(user, pass, testIP) {
		t.Fatal("an expired credential still authenticates; the server side limit is not real")
	}
}

// A credential copied out of the registry must be useless anywhere else.
func TestCredentialIsBoundToItsIP(t *testing.T) {
	h := newHarness(t)
	d := newDevice(t)

	h.register(d, testIP)
	_, body := h.session(d, testIP)
	user, pass := credentialFrom(t, body)

	if h.socksAuth(user, pass, "198.51.100.99") {
		t.Fatal("a credential issued to one address worked from another")
	}
	if !h.socksAuth(user, pass, testIP) {
		t.Fatal("the original address stopped working")
	}
}

func TestWrongPasswordIsRejected(t *testing.T) {
	h := newHarness(t)
	d := newDevice(t)

	h.register(d, testIP)
	_, body := h.session(d, testIP)
	user, _ := credentialFrom(t, body)

	if h.socksAuth(user, "0000000000000000000000000000000000", testIP) {
		t.Fatal("a wrong password authenticated")
	}
	if h.socksAuth("nosuchuser", "whatever", testIP) {
		t.Fatal("an unknown username authenticated")
	}
}

// Guessing online must get expensive before it gets anywhere.
func TestAuthFailuresThrottleAnIP(t *testing.T) {
	h := newHarness(t, func(c *Config) { c.MaxAuthFailuresPerIP = 3 })
	d := newDevice(t)

	h.register(d, testIP)
	_, body := h.session(d, testIP)
	user, pass := credentialFrom(t, body)

	const attacker = "198.51.100.66"
	for i := 0; i < 6; i++ {
		h.socksAuth("guess", "guess", attacker)
	}

	// The valid credential belongs to testIP anyway, but the throttle must also
	// stop the attacker's address from being answered at all.
	if h.socksAuth(user, pass, attacker) {
		t.Fatal("a throttled address was still served")
	}
	if !h.socksAuth(user, pass, testIP) {
		t.Fatal("one address being throttled must not lock out everyone else")
	}
}

// gost never reports disconnects, so the cap counts authentications. It exists
// to stop one credential from becoming a shared relay.
func TestConnectionCapPerCredential(t *testing.T) {
	h := newHarness(t, func(c *Config) { c.MaxAuthsPerCredential = 3 })
	d := newDevice(t)

	h.register(d, testIP)
	_, body := h.session(d, testIP)
	user, pass := credentialFrom(t, body)

	for i := 1; i <= 3; i++ {
		if !h.socksAuth(user, pass, testIP) {
			t.Fatalf("connection %d was rejected before the cap", i)
		}
	}
	if h.socksAuth(user, pass, testIP) {
		t.Fatal("the connection cap did not hold")
	}
}

// Asking again in a loop is the abuse this design cannot stop with randomness,
// so it is stopped with a lock instead.
func TestSecondActivationIsRateLimited(t *testing.T) {
	h := newHarness(t)
	d := newDevice(t)

	h.register(d, testIP)
	if status, body := h.session(d, testIP); status != http.StatusOK {
		t.Fatalf("first session: status %d (%v)", status, body)
	}

	status, body := h.session(d, testIP)
	if status != http.StatusTooManyRequests {
		t.Fatalf("a second immediate activation returned %d, expected 429", status)
	}
	if code := h.errorCode(body); code != "too_soon" {
		t.Fatalf("expected too_soon, got %q", code)
	}
}

func TestDailyQuotaStopsTheDevice(t *testing.T) {
	h := newHarness(t, func(c *Config) {
		c.SessionDailyMax = 2
		c.SessionMinInterval = time.Second
	})
	d := newDevice(t)

	h.register(d, testIP)
	for i := 1; i <= 2; i++ {
		if status, body := h.session(d, testIP); status != http.StatusOK {
			t.Fatalf("session %d: status %d (%v)", i, status, body)
		}
		h.mr.FastForward(2 * time.Second)
	}

	status, body := h.session(d, testIP)
	if status != http.StatusTooManyRequests {
		t.Fatalf("the third activation returned %d, expected 429", status)
	}
	if code := h.errorCode(body); code != "daily_quota_exceeded" {
		t.Fatalf("expected daily_quota_exceeded, got %q", code)
	}
}

// Every activation must get its own secret. Two identical credentials would
// mean the randomness is not what it claims to be.
func TestEachActivationIssuesADifferentCredential(t *testing.T) {
	h := newHarness(t)
	d := newDevice(t)

	h.register(d, testIP)

	_, first := h.session(d, testIP)
	firstUser, firstPass := credentialFrom(t, first)

	h.mr.FastForward(h.cfg.SessionMinInterval + time.Second)

	_, second := h.session(d, testIP)
	secondUser, secondPass := credentialFrom(t, second)

	if firstUser == secondUser || firstPass == secondPass {
		t.Fatal("two activations produced the same credential")
	}
}

func TestSignatureIsRequired(t *testing.T) {
	h := newHarness(t)
	d := newDevice(t)
	other := newDevice(t)

	h.register(d, testIP)

	status, nonce := h.challenge(d, testIP)
	if status != http.StatusOK {
		t.Fatalf("challenge: status %d", status)
	}

	// Signed by a key the broker never registered for this device.
	status, body := h.call(h.srv.HandleSession, "/v1/session", sessionRequest{
		DeviceID:  d.id,
		Nonce:     nonce,
		Signature: other.sign(t, nonce),
	}, testIP)

	if status != http.StatusForbidden {
		t.Fatalf("a foreign signature returned %d, expected 403", status)
	}
	if code := h.errorCode(body); code != "bad_signature" {
		t.Fatalf("expected bad_signature, got %q", code)
	}
}

// A captured challenge must be worth exactly one activation.
func TestAuthChallengeIsSingleUse(t *testing.T) {
	h := newHarness(t, func(c *Config) { c.SessionMinInterval = time.Millisecond })
	d := newDevice(t)

	h.register(d, testIP)

	_, nonce := h.challenge(d, testIP)
	signature := d.sign(t, nonce)
	req := sessionRequest{DeviceID: d.id, Nonce: nonce, Signature: signature}

	if status, body := h.call(h.srv.HandleSession, "/v1/session", req, testIP); status != http.StatusOK {
		t.Fatalf("first use: status %d (%v)", status, body)
	}

	status, body := h.call(h.srv.HandleSession, "/v1/session", req, testIP)
	if status != http.StatusForbidden {
		t.Fatalf("replaying a challenge returned %d, expected 403", status)
	}
	if code := h.errorCode(body); code != "unknown_or_used_challenge" {
		t.Fatalf("expected unknown_or_used_challenge, got %q", code)
	}
}

func TestUnknownDeviceGetsNothing(t *testing.T) {
	h := newHarness(t)
	d := newDevice(t) // never registered

	status, _ := h.challenge(d, testIP)
	if status != http.StatusForbidden {
		t.Fatalf("an unregistered device got %d, expected 403", status)
	}

	status, body := h.call(h.srv.HandleChallenge, "/v1/challenge",
		challengeRequest{DeviceID: "nothexadecimal"}, testIP)
	if status != http.StatusForbidden {
		t.Fatalf("a malformed device id got %d (%v)", status, body)
	}
}

// Revocation is the operator's answer to an abusive device, so it has to bite
// immediately and without a restart.
func TestRevokedDeviceCannotActivate(t *testing.T) {
	h := newHarness(t)
	d := newDevice(t)

	h.register(d, testIP)
	if status, _ := h.session(d, testIP); status != http.StatusOK {
		t.Fatal("session failed before revocation")
	}

	h.mr.HSet("dev:"+d.id, "revoked", "1")

	status, body := h.challenge(d, testIP)
	if status != http.StatusForbidden {
		t.Fatalf("a revoked device got %d, expected 403 (%v)", status, body)
	}
}

func TestBadProofOfWorkIsRejected(t *testing.T) {
	h := newHarness(t)
	d := newDevice(t)

	_, body := h.call(h.srv.HandleRegisterChallenge, "/v1/register/challenge", nil, testIP)
	nonce, _ := body["nonce"].(string)

	status, body := h.call(h.srv.HandleRegister, "/v1/register", registerRequest{
		Nonce:     nonce,
		Counter:   1, // almost certainly not a solution
		PublicKey: base64.StdEncoding.EncodeToString(d.der),
	}, testIP)

	if status != http.StatusBadRequest {
		t.Fatalf("an unsolved challenge returned %d, expected 400", status)
	}
	if code := h.errorCode(body); code != "bad_proof_of_work" {
		t.Fatalf("expected bad_proof_of_work, got %q", code)
	}
}

// One solved challenge must buy one device, not a fleet.
func TestPoWChallengeIsSingleUse(t *testing.T) {
	h := newHarness(t)
	first := newDevice(t)
	second := newDevice(t)

	_, body := h.call(h.srv.HandleRegisterChallenge, "/v1/register/challenge", nil, testIP)
	nonce, _ := body["nonce"].(string)
	counter := solvePoW(t, nonce, int(body["difficulty"].(float64)))

	if status, _ := h.call(h.srv.HandleRegister, "/v1/register", registerRequest{
		Nonce: nonce, Counter: counter,
		PublicKey: base64.StdEncoding.EncodeToString(first.der),
	}, testIP); status != http.StatusOK {
		t.Fatal("the first registration failed")
	}

	status, body := h.call(h.srv.HandleRegister, "/v1/register", registerRequest{
		Nonce: nonce, Counter: counter,
		PublicKey: base64.StdEncoding.EncodeToString(second.der),
	}, testIP)

	if status != http.StatusBadRequest {
		t.Fatalf("reusing a solved challenge returned %d, expected 400", status)
	}
	if code := h.errorCode(body); code != "unknown_or_used_challenge" {
		t.Fatalf("expected unknown_or_used_challenge, got %q", code)
	}
}

func TestRegisterRejectsAJunkKey(t *testing.T) {
	h := newHarness(t)

	_, body := h.call(h.srv.HandleRegisterChallenge, "/v1/register/challenge", nil, testIP)
	nonce, _ := body["nonce"].(string)
	counter := solvePoW(t, nonce, int(body["difficulty"].(float64)))

	status, body := h.call(h.srv.HandleRegister, "/v1/register", registerRequest{
		Nonce: nonce, Counter: counter,
		PublicKey: base64.StdEncoding.EncodeToString([]byte("this is not a key")),
	}, testIP)

	if status != http.StatusBadRequest {
		t.Fatalf("a junk key returned %d, expected 400", status)
	}
	if code := h.errorCode(body); code != "bad_public_key" {
		t.Fatalf("expected bad_public_key, got %q", code)
	}
}

// The IP binding is only as good as the address the broker believes. Honouring
// a forwarded header by default would let any client name its own address.
func TestForwardedHeaderIsIgnoredUnlessTrusted(t *testing.T) {
	h := newHarness(t)
	d := newDevice(t)

	h.register(d, testIP)
	_, nonce := h.challenge(d, testIP)

	body := sessionRequest{DeviceID: d.id, Nonce: nonce, Signature: d.sign(t, nonce)}
	var buf bytes.Buffer
	if err := json.NewEncoder(&buf).Encode(body); err != nil {
		t.Fatalf("encode: %v", err)
	}

	req := httptest.NewRequest(http.MethodPost, "/v1/session", &buf)
	req.RemoteAddr = testIP + ":51234"
	req.Header.Set("X-Forwarded-For", "10.9.9.9")
	rec := httptest.NewRecorder()
	h.srv.HandleSession(rec, req)

	var decoded map[string]any
	if err := json.Unmarshal(rec.Body.Bytes(), &decoded); err != nil {
		t.Fatalf("response: %v", err)
	}
	user, pass := credentialFrom(t, decoded)

	if h.socksAuth(user, pass, "10.9.9.9") {
		t.Fatal("the spoofed forwarded address was used for the binding")
	}
	if !h.socksAuth(user, pass, testIP) {
		t.Fatal("the credential was not bound to the real source address")
	}
}

// The reaper asks for this set and destroys every established socket outside it.
// If the list were wrong, the reaper would either disconnect live users or leave
// expired sessions running.
func TestActiveIPsFollowsTheCredentials(t *testing.T) {
	h := newHarness(t)
	d := newDevice(t)

	h.register(d, testIP)
	if status, _ := h.session(d, testIP); status != http.StatusOK {
		t.Fatal("session failed")
	}

	req := httptest.NewRequest(http.MethodGet, "/active-ips", nil)
	rec := httptest.NewRecorder()
	h.srv.HandleActiveIPs(rec, req)

	var listed struct {
		IPs []string `json:"ips"`
	}
	if err := json.Unmarshal(rec.Body.Bytes(), &listed); err != nil {
		t.Fatalf("active-ips: %v", err)
	}
	if len(listed.IPs) != 1 || listed.IPs[0] != testIP {
		t.Fatalf("expected exactly %s to be active, got %v", testIP, listed.IPs)
	}

	h.mr.FastForward(h.cfg.SessionTTL + time.Second)

	rec = httptest.NewRecorder()
	h.srv.HandleActiveIPs(rec, httptest.NewRequest(http.MethodGet, "/active-ips", nil))
	if err := json.Unmarshal(rec.Body.Bytes(), &listed); err != nil {
		t.Fatalf("active-ips after expiry: %v", err)
	}
	if len(listed.IPs) != 0 {
		t.Fatalf("expired credentials still count as active: %v", listed.IPs)
	}
}

func TestActiveIPsAreFilteredPerProxyNode(t *testing.T) {
	h := newHarness(t, func(cfg *Config) {
		cfg.ProxyNodes = []ProxyNode{
			{Name: "us-1", Host: "proxy-us-1.example", Port: 1080},
			{Name: "us-2", Host: "proxy-us-2.example", Port: 1080},
		}
		cfg.SessionMinInterval = 0
	})

	firstIP := "203.0.113.20"
	secondIP := "203.0.113.21"
	first := newDevice(t)
	second := newDevice(t)
	h.register(first, firstIP)
	if status, _ := h.session(first, firstIP); status != http.StatusOK {
		t.Fatal("first session failed")
	}
	h.register(second, secondIP)
	if status, _ := h.session(second, secondIP); status != http.StatusOK {
		t.Fatal("second session failed")
	}

	assertNodeIPs := func(node, want string) {
		t.Helper()
		req := httptest.NewRequest(http.MethodGet, "/active-ips?node="+node, nil)
		rec := httptest.NewRecorder()
		h.srv.HandleActiveIPs(rec, req)
		if rec.Code != http.StatusOK {
			t.Fatalf("active-ips for %s: status %d", node, rec.Code)
		}
		var listed struct {
			IPs []string `json:"ips"`
		}
		if err := json.Unmarshal(rec.Body.Bytes(), &listed); err != nil {
			t.Fatalf("active-ips for %s: %v", node, err)
		}
		if len(listed.IPs) != 1 || listed.IPs[0] != want {
			t.Fatalf("node %s: got %v, want only %s", node, listed.IPs, want)
		}
	}

	assertNodeIPs("us-1", firstIP)
	assertNodeIPs("us-2", secondIP)

	rec := httptest.NewRecorder()
	h.srv.HandleActiveIPs(rec, httptest.NewRequest(http.MethodGet, "/active-ips?node=typo", nil))
	if rec.Code != http.StatusBadRequest {
		t.Fatalf("unknown node returned status %d, want 400 fail-closed", rec.Code)
	}
}

// Difficulty escalation is what makes farming devices from one place expensive.
func TestDifficultyEscalatesPerNetwork(t *testing.T) {
	cases := []struct {
		registrations int64
		want          int
	}{
		{0, 20}, {9, 20}, {10, 22}, {49, 22}, {50, 24}, {199, 24}, {200, 26},
	}
	for _, c := range cases {
		if got := difficultyFor(20, 28, c.registrations); got != c.want {
			t.Errorf("%d registrations: difficulty %d, expected %d", c.registrations, got, c.want)
		}
	}
	if got := difficultyFor(20, 22, 200); got != 22 {
		t.Errorf("the ceiling was not applied: got %d", got)
	}
}

func TestNetworkPrefixGrouping(t *testing.T) {
	if got := networkPrefix("203.0.113.7"); got != "203.0.113.0" {
		t.Errorf("IPv4 should group by /24, got %s", got)
	}
	if got := networkPrefix("2001:db8:1:2::1"); got != "2001:db8:1::" {
		t.Errorf("IPv6 should group by /48, got %s", got)
	}
}
