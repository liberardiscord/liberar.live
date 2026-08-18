package main

import (
	"context"
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/rand"
	"crypto/sha256"
	"crypto/subtle"
	"crypto/x509"
	"encoding/base64"
	"encoding/hex"
	"encoding/json"
	"errors"
	"log"
	"math/big"
	"net"
	"net/http"
	"strings"
	"sync/atomic"
	"time"
)

const (
	maxBodyBytes = 8 * 1024

	// sessionDomain keeps a signature produced for one purpose from being
	// replayed into another. The C# client must prepend the identical bytes.
	sessionDomain = "droute-session-v1\x00"
)

type Server struct {
	cfg       Config
	store     *Store
	nextProxy atomic.Uint64
}

func NewServer(cfg Config, store *Store) *Server {
	return &Server{cfg: cfg, store: store}
}

func (s *Server) chooseProxyNode() ProxyNode {
	index := s.nextProxy.Add(1) - 1
	return s.cfg.ProxyNodes[index%uint64(len(s.cfg.ProxyNodes))]
}

func (s *Server) hasProxyNode(name string) bool {
	for _, node := range s.cfg.ProxyNodes {
		if node.Name == name {
			return true
		}
	}
	return false
}

// ------------------------------------------------------------------- helpers

func writeJSON(w http.ResponseWriter, status int, v any) {
	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(status)
	_ = json.NewEncoder(w).Encode(v)
}

func writeError(w http.ResponseWriter, status int, code string) {
	writeJSON(w, status, map[string]string{"error": code})
}

func decodeBody(w http.ResponseWriter, r *http.Request, dst any) bool {
	r.Body = http.MaxBytesReader(w, r.Body, maxBodyBytes)
	dec := json.NewDecoder(r.Body)
	dec.DisallowUnknownFields()
	if err := dec.Decode(dst); err != nil {
		writeError(w, http.StatusBadRequest, "malformed_body")
		return false
	}
	return true
}

// clientIP resolves the address the credential will be bound to. The forwarded
// header is honoured only when explicitly trusted, because otherwise a client
// could name its own address and walk straight past the IP binding.
func (s *Server) clientIP(r *http.Request) string {
	if s.cfg.TrustProxyHeader {
		if xff := r.Header.Get("X-Forwarded-For"); xff != "" {
			first := strings.TrimSpace(strings.Split(xff, ",")[0])
			if ip := net.ParseIP(first); ip != nil {
				return ip.String()
			}
		}
	}
	host, _, err := net.SplitHostPort(r.RemoteAddr)
	if err != nil {
		host = r.RemoteAddr
	}
	// Normalize so the bound IP matches what the gost node reports to
	// /socks-auth. A dual-stack listener ([::] / *) hands IPv4 peers back as
	// IPv4-mapped ("::ffff:192.0.2.4"); the SOCKS node, bound to IPv4, reports
	// the plain form ("192.0.2.4"). Without this the two never compare equal and
	// IP binding rejects the legitimate client. net.IP.String() collapses the
	// mapped form back to dotted decimal.
	if ip := net.ParseIP(host); ip != nil {
		return ip.String()
	}
	return host
}

// networkPrefix groups addresses for rate limiting: /24 for IPv4, /48 for IPv6.
func networkPrefix(ipStr string) string {
	ip := net.ParseIP(ipStr)
	if ip == nil {
		return ipStr
	}
	if v4 := ip.To4(); v4 != nil {
		return v4.Mask(net.CIDRMask(24, 32)).String()
	}
	return ip.Mask(net.CIDRMask(48, 128)).String()
}

func randomHex(n int) (string, error) {
	buf := make([]byte, n)
	if _, err := rand.Read(buf); err != nil {
		return "", err
	}
	return hex.EncodeToString(buf), nil
}

func sha256Hex(s string) string {
	sum := sha256.Sum256([]byte(s))
	return hex.EncodeToString(sum[:])
}

// ------------------------------------------------------- POST /v1/register/challenge

type registerChallengeResponse struct {
	Nonce      string `json:"nonce"`
	Difficulty int    `json:"difficulty"`
	ExpiresIn  int    `json:"expires_in"`
}

func (s *Server) HandleRegisterChallenge(w http.ResponseWriter, r *http.Request) {
	ctx := r.Context()
	prefix := networkPrefix(s.clientIP(r))

	recent, err := s.store.RegistrationsFrom(ctx, prefix)
	if err != nil {
		log.Printf("register/challenge: registrations lookup: %v", err)
		writeError(w, http.StatusServiceUnavailable, "unavailable")
		return
	}

	difficulty := difficultyFor(s.cfg.PoWDifficulty, s.cfg.PoWMaxDifficulty, recent)

	nonce, err := newPoWNonce()
	if err != nil {
		writeError(w, http.StatusInternalServerError, "internal")
		return
	}
	if err := s.store.PutPoWChallenge(ctx, nonce, difficulty); err != nil {
		log.Printf("register/challenge: store: %v", err)
		writeError(w, http.StatusServiceUnavailable, "unavailable")
		return
	}

	writeJSON(w, http.StatusOK, registerChallengeResponse{
		Nonce:      nonce,
		Difficulty: difficulty,
		ExpiresIn:  int(s.cfg.PoWChallengeTTL.Seconds()),
	})
}

// ---------------------------------------------------------------- POST /v1/register

type registerRequest struct {
	Nonce     string `json:"nonce"`
	Counter   uint64 `json:"counter"`
	PublicKey string `json:"public_key"` // base64 SubjectPublicKeyInfo DER
}

type registerResponse struct {
	DeviceID string `json:"device_id"`
}

func (s *Server) HandleRegister(w http.ResponseWriter, r *http.Request) {
	ctx := r.Context()

	var req registerRequest
	if !decodeBody(w, r, &req) {
		return
	}

	difficulty, err := s.store.TakePoWChallenge(ctx, req.Nonce)
	if errors.Is(err, ErrNotFound) {
		writeError(w, http.StatusBadRequest, "unknown_or_used_challenge")
		return
	}
	if err != nil {
		writeError(w, http.StatusServiceUnavailable, "unavailable")
		return
	}

	if !verifyPoW(req.Nonce, req.Counter, difficulty) {
		writeError(w, http.StatusBadRequest, "bad_proof_of_work")
		return
	}

	der, err := base64.StdEncoding.DecodeString(req.PublicKey)
	if err != nil || len(der) == 0 || len(der) > 512 {
		writeError(w, http.StatusBadRequest, "bad_public_key")
		return
	}
	if _, err := parseP256PublicKey(der); err != nil {
		writeError(w, http.StatusBadRequest, "bad_public_key")
		return
	}

	// The identifier is derived from the key itself, so a client that registers
	// twice converges on the same device instead of multiplying entries.
	sum := sha256.Sum256(der)
	deviceID := hex.EncodeToString(sum[:16])

	if err := s.store.PutDevice(ctx, deviceID, der); err != nil {
		log.Printf("register: put device: %v", err)
		writeError(w, http.StatusServiceUnavailable, "unavailable")
		return
	}
	if err := s.store.NoteRegistration(ctx, networkPrefix(s.clientIP(r))); err != nil {
		log.Printf("register: note registration: %v", err)
	}

	writeJSON(w, http.StatusOK, registerResponse{DeviceID: deviceID})
}

// --------------------------------------------------------------- POST /v1/challenge

type challengeRequest struct {
	DeviceID string `json:"device_id"`
}

type challengeResponse struct {
	Nonce     string `json:"nonce"`
	ExpiresIn int    `json:"expires_in"`
}

func (s *Server) HandleChallenge(w http.ResponseWriter, r *http.Request) {
	ctx := r.Context()

	var req challengeRequest
	if !decodeBody(w, r, &req) {
		return
	}
	if _, err := s.loadActiveDevice(ctx, req.DeviceID); err != nil {
		writeError(w, http.StatusForbidden, "unknown_device")
		return
	}

	nonce, err := randomHex(16)
	if err != nil {
		writeError(w, http.StatusInternalServerError, "internal")
		return
	}
	if err := s.store.PutAuthChallenge(ctx, req.DeviceID, nonce); err != nil {
		writeError(w, http.StatusServiceUnavailable, "unavailable")
		return
	}

	writeJSON(w, http.StatusOK, challengeResponse{
		Nonce:     nonce,
		ExpiresIn: int(s.cfg.AuthChallengeTTL.Seconds()),
	})
}

// ----------------------------------------------------------------- POST /v1/session

type sessionRequest struct {
	DeviceID  string `json:"device_id"`
	Nonce     string `json:"nonce"`
	Signature string `json:"signature"` // base64, IEEE P1363 r||s, 64 bytes
}

type sessionResponse struct {
	Host      string `json:"host"`
	Port      int    `json:"port"`
	Username  string `json:"username"`
	Password  string `json:"password"`
	ExpiresIn int    `json:"expires_in"`
}

func (s *Server) HandleSession(w http.ResponseWriter, r *http.Request) {
	ctx := r.Context()

	var req sessionRequest
	if !decodeBody(w, r, &req) {
		return
	}

	device, err := s.loadActiveDevice(ctx, req.DeviceID)
	if err != nil {
		writeError(w, http.StatusForbidden, "unknown_device")
		return
	}

	// Consuming the challenge before verifying the signature means a wrong
	// signature costs the caller a fresh round trip, which removes any value in
	// hammering one nonce.
	if err := s.store.TakeAuthChallenge(ctx, req.DeviceID, req.Nonce); err != nil {
		writeError(w, http.StatusForbidden, "unknown_or_used_challenge")
		return
	}

	if !verifySessionSignature(device.PublicKey, req.DeviceID, req.Nonce, req.Signature) {
		writeError(w, http.StatusForbidden, "bad_signature")
		return
	}

	claimed, err := s.store.ClaimSessionSlot(ctx, req.DeviceID)
	if err != nil {
		writeError(w, http.StatusServiceUnavailable, "unavailable")
		return
	}
	if !claimed {
		writeError(w, http.StatusTooManyRequests, "too_soon")
		return
	}

	used, err := s.store.BumpDailyQuota(ctx, req.DeviceID)
	if err != nil {
		s.store.ReleaseSessionSlot(ctx, req.DeviceID)
		writeError(w, http.StatusServiceUnavailable, "unavailable")
		return
	}
	if used > s.cfg.SessionDailyMax {
		writeError(w, http.StatusTooManyRequests, "daily_quota_exceeded")
		return
	}

	// Both halves come from crypto/rand. Nothing here is derived from the
	// device, the time, or any other value an attacker could reconstruct from
	// the published source.
	username, err := randomHex(16)
	if err != nil {
		writeError(w, http.StatusInternalServerError, "internal")
		return
	}
	password, err := randomHex(16)
	if err != nil {
		writeError(w, http.StatusInternalServerError, "internal")
		return
	}

	ip := s.clientIP(r)
	node := s.chooseProxyNode()
	if err := s.store.PutCredential(ctx, username, Credential{
		PasswordHash: sha256Hex(password),
		DeviceID:     req.DeviceID,
		IP:           ip,
		Node:         node.Name,
	}); err != nil {
		log.Printf("session: put credential: %v", err)
		writeError(w, http.StatusServiceUnavailable, "unavailable")
		return
	}

	writeJSON(w, http.StatusOK, sessionResponse{
		Host:      node.Host,
		Port:      node.Port,
		Username:  username,
		Password:  password,
		ExpiresIn: int(s.cfg.SessionTTL.Seconds()),
	})
}

func (s *Server) loadActiveDevice(ctx context.Context, id string) (*Device, error) {
	if len(id) != 32 {
		return nil, ErrNotFound
	}
	if _, err := hex.DecodeString(id); err != nil {
		return nil, ErrNotFound
	}
	device, err := s.store.GetDevice(ctx, id)
	if err != nil {
		return nil, err
	}
	if device.Revoked {
		return nil, ErrNotFound
	}
	return device, nil
}

// --------------------------------------------------- POST /socks-auth (gost plugin)

// gostAuthRequest mirrors httpPluginRequest in go-gost/x/auth/plugin/http.go.
type gostAuthRequest struct {
	Service  string `json:"service"`
	Username string `json:"username"`
	Password string `json:"password"`
	Client   string `json:"client"`
}

type gostAuthResponse struct {
	OK bool   `json:"ok"`
	ID string `json:"id"`
}

func (s *Server) HandleSocksAuth(w http.ResponseWriter, r *http.Request) {
	ctx := r.Context()

	var req gostAuthRequest
	r.Body = http.MaxBytesReader(w, r.Body, maxBodyBytes)
	if err := json.NewDecoder(r.Body).Decode(&req); err != nil {
		writeJSON(w, http.StatusOK, gostAuthResponse{OK: false})
		return
	}

	// gost sends "IP:port"; the port is the ephemeral source port and is not
	// part of the binding.
	sourceIP := req.Client
	if host, _, err := net.SplitHostPort(req.Client); err == nil {
		sourceIP = host
	}

	failures, err := s.store.AuthFailures(ctx, sourceIP)
	if err == nil && failures > s.cfg.MaxAuthFailuresPerIP {
		writeJSON(w, http.StatusOK, gostAuthResponse{OK: false})
		return
	}

	cred, err := s.store.GetCredential(ctx, req.Username)
	if err != nil {
		// Either it never existed or its TTL elapsed. Both are the same answer.
		s.noteFailure(ctx, sourceIP)
		writeJSON(w, http.StatusOK, gostAuthResponse{OK: false})
		return
	}

	if subtle.ConstantTimeCompare([]byte(sha256Hex(req.Password)), []byte(cred.PasswordHash)) != 1 {
		s.noteFailure(ctx, sourceIP)
		writeJSON(w, http.StatusOK, gostAuthResponse{OK: false})
		return
	}

	if cred.IP != sourceIP {
		log.Printf("socks-auth: credential for device %s presented from %s, issued to %s",
			cred.DeviceID, sourceIP, cred.IP)
		s.noteFailure(ctx, sourceIP)
		writeJSON(w, http.StatusOK, gostAuthResponse{OK: false})
		return
	}
	if cred.Node != "" && cred.Node != req.Service {
		log.Printf("socks-auth: credential for device %s belongs to node %s, presented to %s",
			cred.DeviceID, cred.Node, req.Service)
		writeJSON(w, http.StatusOK, gostAuthResponse{OK: false})
		return
	}

	count, err := s.store.CountAuth(ctx, req.Username)
	if err == nil && count > s.cfg.MaxAuthsPerCredential {
		log.Printf("socks-auth: credential for device %s exceeded connection cap", cred.DeviceID)
		writeJSON(w, http.StatusOK, gostAuthResponse{OK: false})
		return
	}

	writeJSON(w, http.StatusOK, gostAuthResponse{OK: true, ID: cred.DeviceID})
}

func (s *Server) noteFailure(ctx context.Context, ip string) {
	if _, err := s.store.NoteAuthFailure(ctx, ip); err != nil {
		log.Printf("socks-auth: note failure: %v", err)
	}
}

// ------------------------------------------------------------------ signatures

func parseP256PublicKey(der []byte) (*ecdsa.PublicKey, error) {
	parsed, err := x509.ParsePKIXPublicKey(der)
	if err != nil {
		return nil, err
	}
	pub, ok := parsed.(*ecdsa.PublicKey)
	if !ok {
		return nil, errors.New("not an ECDSA key")
	}
	if pub.Curve != elliptic.P256() {
		return nil, errors.New("not a P-256 key")
	}
	return pub, nil
}

// verifySessionSignature accepts the IEEE P1363 layout that .NET produces:
// r and s concatenated, each padded to 32 bytes. Go's ASN.1 helpers expect DER,
// so the halves are read directly instead.
func verifySessionSignature(der []byte, deviceID, nonceHex, signatureB64 string) bool {
	pub, err := parseP256PublicKey(der)
	if err != nil {
		return false
	}

	sig, err := base64.StdEncoding.DecodeString(signatureB64)
	if err != nil || len(sig) != 64 {
		return false
	}

	nonce, err := hex.DecodeString(nonceHex)
	if err != nil {
		return false
	}

	msg := make([]byte, 0, len(sessionDomain)+len(deviceID)+len(nonce))
	msg = append(msg, sessionDomain...)
	msg = append(msg, deviceID...)
	msg = append(msg, nonce...)
	digest := sha256.Sum256(msg)

	r := new(big.Int).SetBytes(sig[:32])
	sv := new(big.Int).SetBytes(sig[32:])
	return ecdsa.Verify(pub, digest[:], r, sv)
}

// ------------------------------------------------------- GET /active-ips (reaper)

// HandleActiveIPs lists the addresses that still hold a live credential. The
// auther only runs when a connection is established, so a socket opened while
// the credential was valid would otherwise outlive its TTL. The reaper asks for
// this set and destroys every established socket that is not in it.
func (s *Server) HandleActiveIPs(w http.ResponseWriter, r *http.Request) {
	node := strings.TrimSpace(r.URL.Query().Get("node"))
	if node != "" && (!validNodeName(node) || !s.hasProxyNode(node)) {
		writeError(w, http.StatusBadRequest, "invalid_node")
		return
	}
	ips, err := s.store.ActiveCredentialIPs(r.Context(), node)
	if err != nil {
		log.Printf("active-ips: %v", err)
		writeError(w, http.StatusServiceUnavailable, "unavailable")
		return
	}
	list := make([]string, 0, len(ips))
	for ip := range ips {
		list = append(list, ip)
	}
	writeJSON(w, http.StatusOK, map[string]any{"ips": list})
}

// ---------------------------------------------------------------------- health

func (s *Server) HandleHealth(w http.ResponseWriter, r *http.Request) {
	ctx, cancel := context.WithTimeout(r.Context(), 2*time.Second)
	defer cancel()
	if err := s.store.Ping(ctx); err != nil {
		writeError(w, http.StatusServiceUnavailable, "redis_down")
		return
	}
	writeJSON(w, http.StatusOK, map[string]string{"status": "ok"})
}
