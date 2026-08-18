package main

import (
	"context"
	"errors"
	"fmt"
	"strconv"
	"time"

	"github.com/redis/go-redis/v9"
)

// Store wraps every Redis access. Requires Redis 6.2 or newer for GETDEL, which
// is what makes challenge consumption atomic and therefore single-use.
type Store struct {
	rdb *redis.Client
	cfg Config
}

var ErrNotFound = errors.New("not found")

func NewStore(cfg Config) *Store {
	return &Store{
		rdb: redis.NewClient(&redis.Options{
			Addr:     cfg.RedisAddr,
			Password: cfg.RedisPassword,
			DB:       cfg.RedisDB,
		}),
		cfg: cfg,
	}
}

func (s *Store) Ping(ctx context.Context) error { return s.rdb.Ping(ctx).Err() }
func (s *Store) Close() error                   { return s.rdb.Close() }

// ---------------------------------------------------------------- proof of work

func (s *Store) PutPoWChallenge(ctx context.Context, nonce string, difficulty int) error {
	return s.rdb.Set(ctx, "pow:"+nonce, difficulty, s.cfg.PoWChallengeTTL).Err()
}

// TakePoWChallenge consumes the challenge. A nonce is valid exactly once, so a
// single solved challenge cannot be replayed into many device registrations.
func (s *Store) TakePoWChallenge(ctx context.Context, nonce string) (int, error) {
	v, err := s.rdb.GetDel(ctx, "pow:"+nonce).Result()
	if err == redis.Nil {
		return 0, ErrNotFound
	}
	if err != nil {
		return 0, err
	}
	return strconv.Atoi(v)
}

// RegistrationsFrom counts how many devices a /24 registered recently, which
// feeds the difficulty escalation.
func (s *Store) RegistrationsFrom(ctx context.Context, prefix string) (int64, error) {
	v, err := s.rdb.Get(ctx, "powrate:"+prefix).Int64()
	if err == redis.Nil {
		return 0, nil
	}
	return v, err
}

func (s *Store) NoteRegistration(ctx context.Context, prefix string) error {
	key := "powrate:" + prefix
	pipe := s.rdb.TxPipeline()
	pipe.Incr(ctx, key)
	pipe.Expire(ctx, key, 24*time.Hour)
	_, err := pipe.Exec(ctx)
	return err
}

// ------------------------------------------------------------------- devices

type Device struct {
	ID        string
	PublicKey []byte
	Revoked   bool
}

func (s *Store) PutDevice(ctx context.Context, id string, pubkey []byte) error {
	return s.rdb.HSet(ctx, "dev:"+id, map[string]any{
		"pubkey":  pubkey,
		"created": time.Now().UTC().Format(time.RFC3339),
	}).Err()
}

func (s *Store) GetDevice(ctx context.Context, id string) (*Device, error) {
	m, err := s.rdb.HGetAll(ctx, "dev:"+id).Result()
	if err != nil {
		return nil, err
	}
	if len(m) == 0 {
		return nil, ErrNotFound
	}
	return &Device{
		ID:        id,
		PublicKey: []byte(m["pubkey"]),
		Revoked:   m["revoked"] == "1",
	}, nil
}

// ------------------------------------------------------- signature challenges

func (s *Store) PutAuthChallenge(ctx context.Context, deviceID, nonce string) error {
	return s.rdb.Set(ctx, "authc:"+deviceID+":"+nonce, 1, s.cfg.AuthChallengeTTL).Err()
}

func (s *Store) TakeAuthChallenge(ctx context.Context, deviceID, nonce string) error {
	err := s.rdb.GetDel(ctx, "authc:"+deviceID+":"+nonce).Err()
	if err == redis.Nil {
		return ErrNotFound
	}
	return err
}

// ---------------------------------------------------------------- rate limits

// ClaimSessionSlot enforces the minimum interval between activations for one
// device. SET NX is atomic, so two concurrent requests cannot both win.
//
// A zero (or negative) interval disables the gate entirely, and that is the
// default. Re-issuing to the same device is not an abuse vector: every
// credential is bound to the requester's IP and expires on its own, so a device
// asking again just gets another short-lived credential usable only from the
// same address. Throttling that only punishes the legitimate user who needs to
// re-liberate — after a reconnect, a restart, or simply because the last one
// expired. The daily ceiling and the registration proof-of-work remain as the
// backstops against a client hammering the broker in a loop.
func (s *Store) ClaimSessionSlot(ctx context.Context, deviceID string) (bool, error) {
	if s.cfg.SessionMinInterval <= 0 {
		return true, nil
	}
	return s.rdb.SetNX(ctx, "sesslock:"+deviceID, 1, s.cfg.SessionMinInterval).Result()
}

func (s *Store) ReleaseSessionSlot(ctx context.Context, deviceID string) {
	s.rdb.Del(ctx, "sesslock:"+deviceID)
}

func (s *Store) BumpDailyQuota(ctx context.Context, deviceID string) (int64, error) {
	key := fmt.Sprintf("sessday:%s:%s", deviceID, time.Now().UTC().Format("20060102"))
	pipe := s.rdb.TxPipeline()
	incr := pipe.Incr(ctx, key)
	pipe.Expire(ctx, key, 25*time.Hour)
	if _, err := pipe.Exec(ctx); err != nil {
		return 0, err
	}
	return incr.Val(), nil
}

// --------------------------------------------------------------- credentials

type Credential struct {
	PasswordHash string
	DeviceID     string
	IP           string
	Node         string
}

// PutCredential stores the credential with a real TTL. This single line is what
// turns the five minute limit from a client-side courtesy into a server-side
// rule: once Redis expires the key, the credential no longer exists anywhere.
func (s *Store) PutCredential(ctx context.Context, username string, c Credential) error {
	key := "cred:" + username
	pipe := s.rdb.TxPipeline()
	pipe.HSet(ctx, key, map[string]any{
		"pwhash": c.PasswordHash,
		"device": c.DeviceID,
		"ip":     c.IP,
		"node":   c.Node,
	})
	pipe.Expire(ctx, key, s.cfg.SessionTTL)
	_, err := pipe.Exec(ctx)
	return err
}

func (s *Store) GetCredential(ctx context.Context, username string) (*Credential, error) {
	m, err := s.rdb.HGetAll(ctx, "cred:"+username).Result()
	if err != nil {
		return nil, err
	}
	if len(m) == 0 {
		return nil, ErrNotFound
	}
	return &Credential{
		PasswordHash: m["pwhash"],
		DeviceID:     m["device"],
		IP:           m["ip"],
		Node:         m["node"],
	}, nil
}

// CountAuth caps how many connections one credential may open. gost never tells
// us about disconnects, so this counts authentications instead of live sessions.
func (s *Store) CountAuth(ctx context.Context, username string) (int64, error) {
	key := "credn:" + username
	pipe := s.rdb.TxPipeline()
	incr := pipe.Incr(ctx, key)
	pipe.Expire(ctx, key, s.cfg.SessionTTL)
	if _, err := pipe.Exec(ctx); err != nil {
		return 0, err
	}
	return incr.Val(), nil
}

func (s *Store) NoteAuthFailure(ctx context.Context, ip string) (int64, error) {
	key := "authfail:" + ip
	pipe := s.rdb.TxPipeline()
	incr := pipe.Incr(ctx, key)
	pipe.Expire(ctx, key, s.cfg.AuthFailureWindow)
	if _, err := pipe.Exec(ctx); err != nil {
		return 0, err
	}
	return incr.Val(), nil
}

func (s *Store) AuthFailures(ctx context.Context, ip string) (int64, error) {
	v, err := s.rdb.Get(ctx, "authfail:"+ip).Int64()
	if err == redis.Nil {
		return 0, nil
	}
	return v, err
}

// ActiveCredentialIPs lists source addresses with a live credential for one
// proxy node. An empty node returns the union for compatibility with old
// single-node reapers. Credentials issued before node binding are included in
// every node for their remaining few minutes so a rolling deploy cannot kick a
// legitimate connection.
func (s *Store) ActiveCredentialIPs(ctx context.Context, node string) (map[string]struct{}, error) {
	ips := make(map[string]struct{})
	var cursor uint64
	for {
		keys, next, err := s.rdb.Scan(ctx, cursor, "cred:*", 256).Result()
		if err != nil {
			return nil, err
		}
		for _, k := range keys {
			values, err := s.rdb.HMGet(ctx, k, "ip", "node").Result()
			if err != nil || len(values) != 2 {
				continue
			}
			ip, _ := values[0].(string)
			credentialNode, _ := values[1].(string)
			if ip != "" && (node == "" || credentialNode == "" || credentialNode == node) {
				ips[ip] = struct{}{}
			}
		}
		if next == 0 {
			break
		}
		cursor = next
	}
	return ips, nil
}
