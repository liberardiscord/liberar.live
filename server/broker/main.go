package main

import (
	"context"
	"errors"
	"log"
	"net/http"
	"os"
	"os/signal"
	"syscall"
	"time"
)

func main() {
	log.SetFlags(log.LstdFlags | log.LUTC)

	cfg := LoadConfig()
	store := NewStore(cfg)
	defer store.Close()

	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	if err := store.Ping(ctx); err != nil {
		cancel()
		log.Fatalf("redis unreachable at %s: %v", cfg.RedisAddr, err)
	}
	cancel()

	srv := NewServer(cfg, store)

	// Two listeners on purpose. The client-facing API is meant to sit behind a
	// TLS terminator; the auther endpoint stays on loopback for one node, or on a
	// private/WireGuard address for a fleet. Credential validation must never be
	// reachable from the public Internet.
	apiMux := http.NewServeMux()
	apiMux.Handle("/v1/register/challenge", post(srv.HandleRegisterChallenge))
	apiMux.Handle("/v1/register", post(srv.HandleRegister))
	apiMux.Handle("/v1/challenge", post(srv.HandleChallenge))
	apiMux.Handle("/v1/session", post(srv.HandleSession))
	apiMux.HandleFunc("/healthz", srv.HandleHealth)

	authMux := http.NewServeMux()
	authMux.Handle("/socks-auth", post(srv.HandleSocksAuth))
	authMux.HandleFunc("/active-ips", srv.HandleActiveIPs)

	apiSrv := &http.Server{
		Addr:              cfg.APIListen,
		Handler:           apiMux,
		ReadHeaderTimeout: 5 * time.Second,
		ReadTimeout:       15 * time.Second,
		WriteTimeout:      15 * time.Second,
		IdleTimeout:       60 * time.Second,
	}
	authSrv := &http.Server{
		Addr:              cfg.AuthListen,
		Handler:           authMux,
		ReadHeaderTimeout: 2 * time.Second,
		ReadTimeout:       5 * time.Second,
		WriteTimeout:      5 * time.Second,
	}

	go serve("api", apiSrv)
	go serve("auther", authSrv)

	log.Printf("broker up: api=%s auther=%s proxy_nodes=%d session_ttl=%s pow=%d bits",
		cfg.APIListen, cfg.AuthListen, len(cfg.ProxyNodes),
		cfg.SessionTTL, cfg.PoWDifficulty)

	stop := make(chan os.Signal, 1)
	signal.Notify(stop, os.Interrupt, syscall.SIGTERM)
	<-stop

	log.Print("shutting down")
	shutdownCtx, shutdownCancel := context.WithTimeout(context.Background(), 10*time.Second)
	defer shutdownCancel()
	_ = apiSrv.Shutdown(shutdownCtx)
	_ = authSrv.Shutdown(shutdownCtx)
}

func serve(name string, srv *http.Server) {
	if err := srv.ListenAndServe(); err != nil && !errors.Is(err, http.ErrServerClosed) {
		log.Fatalf("%s listener failed: %v", name, err)
	}
}

// post rejects anything but POST so a stray GET cannot consume a challenge.
func post(h http.HandlerFunc) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.Method != http.MethodPost {
			w.Header().Set("Allow", http.MethodPost)
			writeError(w, http.StatusMethodNotAllowed, "method_not_allowed")
			return
		}
		h(w, r)
	})
}
