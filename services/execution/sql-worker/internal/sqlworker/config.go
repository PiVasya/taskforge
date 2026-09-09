package sqlworker

import (
	"fmt"
	"os"
	"path/filepath"
	"regexp"
	"strconv"
	"strings"
	"syscall"
	"taskforge/sqlworker/internal/native"
	"taskforge/sqlworker/internal/wakeup"
	"time"
)

type Config struct {
	WorkerID, Cache, TasksURL, ExecutionURL, InternalKey string
	Concurrency, HealthPort                              int
	Pool                                                 PoolOptions
	Broker                                               wakeup.Config
	Engines                                              []ServerConfig
	Debug                                                bool
}

func env(name, defaultValue string) string {
	if value := os.Getenv(name); value != "" {
		return value
	}
	return defaultValue
}
func envInt(name string, def, low, high int) (int, error) {
	value, e := strconv.Atoi(env(name, strconv.Itoa(def)))
	if e != nil || value < low || value > high {
		return 0, fmt.Errorf("%s must be between %d and %d", name, low, high)
	}
	return value, nil
}
func LoadConfig() (c Config, err error) {
	hostname, _ := os.Hostname()
	c.WorkerID = env("SQL_WORKER_ID", "sql-"+env("TASKFORGE_NODE_ID", hostname))
	if !regexp.MustCompile(`^[A-Za-z0-9_.:-]{1,160}$`).MatchString(c.WorkerID) {
		return c, fmt.Errorf("invalid SQL_WORKER_ID")
	}
	c.Cache = env("SQL_CACHE_DIR", "/var/cache/taskforge-sql")
	c.Cache, err = filepath.Abs(c.Cache)
	if err != nil {
		return
	}
	c.TasksURL = env("TASKS_API_URL", "http://tasks-api:8080")
	c.ExecutionURL = env("EXECUTION_API_URL", "http://execution-api:8080")
	c.InternalKey = os.Getenv("TASKFORGE_INTERNAL_KEY")
	c.Concurrency, err = envInt("SQL_CONCURRENCY", 2, 1, 8)
	if err != nil {
		return
	}
	c.HealthPort, err = envInt("SQL_HEALTH_PORT", 8081, 1, 65535)
	if err != nil {
		return
	}
	c.Pool = DefaultPoolOptions()
	c.Pool.MaxReady, err = envInt("SQL_POOL_MAX_READY", 6, 0, 32)
	if err != nil {
		return
	}
	c.Pool.MaxMaterializations, err = envInt("SQL_POOL_MAX_MATERIALIZATIONS", 4, 1, 16)
	if err != nil {
		return
	}
	var ready, golden int
	ready, err = envInt("SQL_POOL_IDLE_READY_SECONDS", 120, 5, 3600)
	if err != nil {
		return
	}
	golden, err = envInt("SQL_POOL_IDLE_GOLDEN_SECONDS", 600, 30, 86400)
	if err != nil {
		return
	}
	if golden <= ready {
		return c, fmt.Errorf("SQL_POOL_IDLE_GOLDEN_SECONDS must exceed ready-cache idle time")
	}
	c.Pool.IdleReady = time.Duration(ready) * time.Second
	c.Pool.IdleGolden = time.Duration(golden) * time.Second
	c.Broker = wakeup.Config{Host: env("RABBITMQ_HOST", "rabbitmq"), Port: env("RABBITMQ_PORT", "5672"), VHost: env("RABBITMQ_VHOST", "/"), User: os.Getenv("RABBITMQ_USER"), Password: os.Getenv("RABBITMQ_PASSWORD")}
	c.Debug = env("TASKFORGE_DEBUG_LOGS", "1") == "1"
	for _, engine := range []struct{ name, prefix, port, user string }{{"postgresql", "SQL_POSTGRES_", "5432", "postgres"}, {"mysql", "SQL_MYSQL_", "3306", "root"}} {
		host := os.Getenv(engine.prefix + "HOST")
		if host == "" {
			continue
		}
		port, e := envInt(engine.prefix+"PORT", mustInt(engine.port), 1, 65535)
		if e != nil {
			return c, e
		}
		c.Engines = append(c.Engines, ServerConfig{Config: native.Config{Engine: engine.name, Host: host, Port: strconv.Itoa(port), User: env(engine.prefix+"USER", engine.user), Password: os.Getenv(engine.prefix + "PASSWORD")}, RuntimeDigest: os.Getenv(engine.prefix + "RUNTIME_DIGEST"), Marker: os.Getenv("SQL_SANDBOX_MARKER")})
	}
	return
}
func mustInt(s string) int { v, _ := strconv.Atoi(s); return v }
func lockNamespace(cache, workerID string) (*os.File, error) {
	if e := os.MkdirAll(cache, 0700); e != nil {
		return nil, e
	}
	f, e := os.OpenFile(filepath.Join(cache, strings.ReplaceAll(workerID, ":", "_")+".lock"), os.O_CREATE|os.O_RDWR, 0600)
	if e != nil {
		return nil, e
	}
	if e = syscall.Flock(int(f.Fd()), syscall.LOCK_EX|syscall.LOCK_NB); e != nil {
		f.Close()
		return nil, fmt.Errorf("another SQL worker owns this cache namespace")
	}
	return f, nil
}
