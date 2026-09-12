package main

import (
	"context"
	"encoding/json"
	"fmt"
	"io"
	"log/slog"
	"net/http"
	"os"
	"os/signal"
	"strconv"
	"syscall"
	"taskforge/sqlworker/internal/native"
	"taskforge/sqlworker/internal/sqlworker"
	"time"
)

func main() {
	if len(os.Args) > 1 {
		switch os.Args[1] {
		case "child":
			if len(os.Args) != 2 {
				os.Exit(2)
			}
			if e := sqlworker.ChildMain(os.Stdin, os.Stdout); e != nil {
				fmt.Fprintln(os.Stderr, "SQL_CHILD_CONTRACT_FAILURE")
				os.Exit(2)
			}
			return
		case "self-test-isolation":
			if e := sqlworker.IsolationProbe(); e != nil {
				fmt.Fprintln(os.Stderr, "SQL_ISOLATION_FAILURE")
				os.Exit(1)
			}
			fmt.Println("SQL_ALL_THREAD_ISOLATION_OK")
			return
		case "version":
			_ = json.NewEncoder(os.Stdout).Encode(map[string]any{"implementation": sqlworker.ImplementationVersion, "contractVersion": sqlworker.ContractVersion, "adapterVersion": sqlworker.AdapterVersion, "libraries": native.LibraryVersions()})
			return
		case "health", "ready":
			port := os.Getenv("SQL_HEALTH_PORT")
			if port == "" {
				port = "8081"
			}
			p, e := strconv.Atoi(port)
			if e != nil || p < 1 || p > 65535 {
				os.Exit(2)
			}
			path := "/health"
			if os.Args[1] == "ready" {
				path = "/ready"
			}
			client := &http.Client{Timeout: 2 * time.Second, Transport: &http.Transport{Proxy: nil}, CheckRedirect: func(*http.Request, []*http.Request) error { return http.ErrUseLastResponse }}
			response, e := client.Get("http://127.0.0.1:" + port + path)
			if e != nil {
				os.Exit(1)
			}
			defer response.Body.Close()
			if os.Args[1] == "ready" {
				_, _ = io.Copy(os.Stdout, response.Body)
			}
			if response.StatusCode != http.StatusOK {
				os.Exit(1)
			}
			return
		default:
			fmt.Fprintln(os.Stderr, "SQL_UNKNOWN_COMMAND")
			os.Exit(2)
		}
	}
	cfg, e := sqlworker.LoadConfig()
	if e != nil {
		fmt.Fprintln(os.Stderr, "SQL_CONFIGURATION_INVALID:", e)
		os.Exit(2)
	}
	level := slog.LevelInfo
	if cfg.Debug {
		level = slog.LevelDebug
	}
	slog.SetDefault(slog.New(slog.NewJSONHandler(os.Stdout, &slog.HandlerOptions{Level: level})))
	worker, e := sqlworker.NewWorker(cfg)
	if e != nil {
		slog.Error("sql_worker_initialization_failed", "error_class", fmt.Sprintf("%T", e))
		os.Exit(1)
	}
	ctx, cancel := signal.NotifyContext(context.Background(), syscall.SIGTERM, syscall.SIGINT)
	defer cancel()
	if e = worker.Run(ctx); e != nil {
		slog.Error("sql_worker_stopped", "error_class", fmt.Sprintf("%T", e))
		os.Exit(1)
	}
}
