package sqlworker

import (
	"context"
	"crypto/rand"
	"crypto/sha256"
	"crypto/subtle"
	"encoding/base64"
	"encoding/hex"
	"errors"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"regexp"
	"strconv"
	"strings"
	"syscall"
	"taskforge/sqlworker/internal/native"
)

// Providers own engine-local disposable state, never business data. A future
// reflink/volume provider can implement this interface without changing API jobs.
type SandboxProvider interface {
	Create(context.Context, Payload) (Sandbox, error)
	Destroy(context.Context, Sandbox) error
	Kill(context.Context, Sandbox) error
}
type EngineAdapter interface {
	SandboxProvider
	Engine() string
	Startup(context.Context) error
	Registration(context.Context) (Registration, error)
	Prepare(context.Context, Payload) error
	DropMaterialization(context.Context, string) error
}
type Sandbox struct {
	ID         string
	Connection native.Config
}

func randomHex(n int) (string, error) {
	b := make([]byte, n)
	if _, e := rand.Read(b); e != nil {
		return "", e
	}
	return hex.EncodeToString(b), nil
}
func randomPassword() (string, error) {
	b := make([]byte, 32)
	if _, e := rand.Read(b); e != nil {
		return "", e
	}
	return base64.RawURLEncoding.EncodeToString(b), nil
}
func textHash(v string) string { b := sha256.Sum256([]byte(v)); return hex.EncodeToString(b[:]) }
func ensureSpace(path string, bytes uint64) error {
	var stat syscall.Statfs_t
	if e := syscall.Statfs(path, &stat); e != nil {
		return e
	}
	if stat.Bavail*uint64(stat.Bsize) < bytes {
		return Unavailable("The local disposable cache is full.")
	}
	return nil
}
func runPlan(ctx context.Context, c *native.Session, plan DatasetPlan) error {
	for _, group := range [][]string{plan.Creates, plan.Inserts, plan.After} {
		for _, sql := range group {
			if e := c.Exec(ctx, sql); e != nil {
				return e
			}
		}
	}
	return nil
}
func readScalar(ctx context.Context, c *native.Session, sql string) (string, error) {
	r, e := c.Query(ctx, sql, 1, 1<<20)
	if e != nil {
		return "", e
	}
	if len(r.Rows) != 1 || len(r.Rows[0]) != 1 {
		return "", Unavailable("Unexpected dedicated engine metadata.")
	}
	return r.Rows[0][0].Text, nil
}

type SQLiteAdapter struct{ root, executor string }

func NewSQLiteAdapter(cache, workerID, executor string) (*SQLiteAdapter, error) {
	root := filepath.Join(cache, "sqlite", textHash(workerID)[:12])
	if e := os.MkdirAll(root, 0700); e != nil {
		return nil, e
	}
	root, e := filepath.Abs(root)
	if e != nil {
		return nil, e
	}
	info, e := os.Lstat(root)
	if e != nil || !info.IsDir() || info.Mode()&os.ModeSymlink != 0 {
		return nil, Unavailable("Invalid SQLite namespace.")
	}
	return &SQLiteAdapter{root, executor}, nil
}
func (a *SQLiteAdapter) Engine() string { return "sqlite" }
func (a *SQLiteAdapter) Startup(ctx context.Context) error {
	entries, e := os.ReadDir(a.root)
	if e != nil {
		return e
	}
	own := regexp.MustCompile(`^(?:[a-f0-9]{64}\.golden\.db|[a-f0-9]{32}\.(?:sandbox|building)\.db)(?:-journal|-wal|-shm)?$`)
	for _, entry := range entries {
		if ctx.Err() != nil {
			return ctx.Err()
		}
		if !own.MatchString(entry.Name()) {
			continue
		}
		if entry.IsDir() {
			return Unavailable("Unexpected directory in the SQL cache.")
		}
		if e = os.Remove(filepath.Join(a.root, entry.Name())); e != nil && !os.IsNotExist(e) {
			return e
		}
	}
	return nil
}
func (a *SQLiteAdapter) Registration(ctx context.Context) (Registration, error) {
	if e := ctx.Err(); e != nil {
		return Registration{}, e
	}
	c, e := native.Open(native.Config{Engine: "sqlite", Path: filepath.Join(a.root, "probe.db"), Create: true})
	if e != nil {
		return Registration{}, e
	}
	defer os.Remove(filepath.Join(a.root, "probe.db"))
	defer c.Close()
	flags, e := c.Query(ctx, "PRAGMA compile_options", 512, 1<<20)
	if e != nil {
		return Registration{}, e
	}
	options := []string{}
	for _, r := range flags.Rows {
		options = append(options, r[0].Text)
	}
	return Registration{"sqlite", native.LibraryVersions()["sqlite"], "sha256:" + a.executor, AdapterVersion, map[string]any{"executorFingerprint": a.executor, "implementation": ImplementationVersion, "clientLibraries": native.LibraryVersions(), "compileOptions": options, "storageStrategy": "immutable-file-v1", "foreignKeys": true, "timezone": "UTC", "transactionMode": "autocommit", "identifierPolicy": "portable-lower-v1", "caseFolding": "unicode-default-v1"}}, nil
}
func (a *SQLiteAdapter) golden(key string) (string, error) {
	if !hashRE.MatchString(key) {
		return "", Unavailable("Invalid materialization identity.")
	}
	return filepath.Join(a.root, key+".golden.db"), nil
}
func (a *SQLiteAdapter) Prepare(ctx context.Context, p Payload) (err error) {
	name, e := a.golden(p.MaterializationKey)
	if e != nil {
		return e
	}
	if e = ensureSpace(a.root, 32<<20); e != nil {
		return e
	}
	// Startup clears old local artifacts, and Pool serializes Prepare per key.
	if _, e = os.Lstat(name); e == nil {
		return Unavailable("Unexpected pre-existing materialization outside the pool.")
	}
	if !os.IsNotExist(e) {
		return e
	}
	token, e := randomHex(16)
	if e != nil {
		return e
	}
	temp := filepath.Join(a.root, token+".building.db")
	defer os.Remove(temp)
	plan, e := CompileDataset(p, "sqlite")
	if e != nil {
		return e
	}
	c, e := native.Open(native.Config{Engine: "sqlite", Path: temp, Create: true})
	if e != nil {
		return e
	}
	defer c.Close()
	for _, sql := range []string{"PRAGMA trusted_schema=OFF", "PRAGMA foreign_keys=OFF", "PRAGMA journal_mode=MEMORY", "PRAGMA temp_store=MEMORY", "PRAGMA synchronous=OFF"} {
		if e = c.Exec(ctx, sql); e != nil {
			return e
		}
	}
	if e = c.Exec(ctx, "BEGIN"); e != nil {
		return e
	}
	if e = runPlan(ctx, c, plan); e != nil {
		return e
	}
	if e = c.Exec(ctx, "COMMIT"); e != nil {
		return e
	}
	r, e := c.Query(ctx, "PRAGMA foreign_key_check", 4096, 1<<20)
	if e != nil {
		return e
	}
	if len(r.Rows) != 0 {
		return Fail("SQL_DATASET_FOREIGN_KEY", "Seed data violates a foreign key.")
	}
	check, e := readScalar(ctx, c, "PRAGMA quick_check")
	if e != nil {
		return e
	}
	if check != "ok" {
		return Unavailable("SQLite materialization failed integrity validation.")
	}
	c.Close()
	info, e := os.Stat(temp)
	if e != nil {
		return e
	}
	if info.Size() > 16<<20 {
		return Fail("SQL_DATASET_SIZE", "SQLite dataset exceeds 16 MiB.")
	}
	if e = os.Chmod(temp, 0400); e != nil {
		return e
	}
	return os.Rename(temp, name)
}
func (a *SQLiteAdapter) Create(ctx context.Context, p Payload) (out Sandbox, err error) {
	from, e := a.golden(p.MaterializationKey)
	if e != nil {
		return out, e
	}
	if e = ensureSpace(a.root, 16<<20); e != nil {
		return out, e
	}
	token, e := randomHex(16)
	if e != nil {
		return out, e
	}
	target := filepath.Join(a.root, token+".sandbox.db")
	source, e := os.Open(from)
	if e != nil {
		return out, RecoverCache("The local immutable SQLite artifact is missing.")
	}
	defer source.Close()
	info, e := source.Stat()
	if e != nil || !info.Mode().IsRegular() || info.Size() > 16<<20 {
		return out, Unavailable("Invalid immutable SQLite artifact.")
	}
	dest, e := os.OpenFile(target, os.O_WRONLY|os.O_CREATE|os.O_EXCL, 0600)
	if e != nil {
		return out, e
	}
	defer func() {
		dest.Close()
		if err != nil {
			os.Remove(target)
		}
	}()
	if _, e = io.CopyN(dest, source, info.Size()); e != nil {
		return out, e
	}
	if e = dest.Close(); e != nil {
		return out, e
	}
	if e = ctx.Err(); e != nil {
		return out, e
	}
	return Sandbox{token, native.Config{Engine: "sqlite", Path: target}}, nil
}
func (a *SQLiteAdapter) Destroy(ctx context.Context, s Sandbox) error {
	path := s.Connection.Path
	if s.Connection.Engine != "sqlite" || !regexp.MustCompile(`^[a-f0-9]{32}$`).MatchString(s.ID) || path != filepath.Join(a.root, s.ID+".sandbox.db") {
		return Unavailable("Refusing foreign SQLite sandbox cleanup.")
	}
	for _, suffix := range []string{"", "-journal", "-wal", "-shm"} {
		if e := os.Remove(path + suffix); e != nil && !os.IsNotExist(e) {
			return e
		}
	}
	return nil
}
func (a *SQLiteAdapter) Kill(context.Context, Sandbox) error { return nil }
func (a *SQLiteAdapter) DropMaterialization(ctx context.Context, key string) error {
	path, e := a.golden(key)
	if e != nil {
		return e
	}
	e = os.Remove(path)
	if os.IsNotExist(e) {
		return nil
	}
	return e
}

type ServerConfig struct {
	native.Config
	RuntimeDigest, Marker string
}
type ServerAdapter struct {
	config           ServerConfig
	prefix, executor string
}

func NewServerAdapter(cfg ServerConfig, workerID, executor string) (*ServerAdapter, error) {
	if !contains([]string{"postgresql", "mysql"}, cfg.Engine) || !digestRE.MatchString(cfg.RuntimeDigest) || !hashRE.MatchString(cfg.Marker) {
		return nil, Unavailable("Dedicated SQL engine digest and guard marker are required.")
	}
	if cfg.Host == "" || cfg.User == "" || cfg.Password == "" {
		return nil, Unavailable("Dedicated SQL connection configuration is incomplete.")
	}
	return &ServerAdapter{cfg, "tfq_" + textHash(workerID)[:8] + "_", executor}, nil
}
func (a *ServerAdapter) Engine() string { return a.config.Engine }
func (a *ServerAdapter) admin(database string) (*native.Session, error) {
	cfg := a.config.Config
	cfg.Database = database
	cfg.TimeoutMS = 15000
	if cfg.Engine == "postgresql" && database == "" {
		cfg.Database = "postgres"
	}
	return native.Open(cfg)
}
func (a *ServerAdapter) owned(name string) bool {
	return strings.HasPrefix(name, a.prefix) && regexp.MustCompile(`^[a-z0-9_]+$`).MatchString(name) && len(name) <= 63
}

// Administration statements can contain generated credentials. Never forward a
// native server message from this boundary into validation receipts or job output.
func administrationFailure(err error) error {
	if err == nil {
		return nil
	}
	var db *native.Error
	if !errors.As(err, &db) {
		return err
	}
	if db.Code == "3D000" || db.Code == "1049" {
		return RecoverCache("A dedicated SQL cache database disappeared.")
	}
	return Unavailable("The dedicated SQL engine rejected dataset preparation or sandbox administration.")
}
func (a *ServerAdapter) withAdmin(ctx context.Context, database string, f func(*native.Session) error) (err error) {
	defer func() { err = administrationFailure(err) }()
	if e := ctx.Err(); e != nil {
		return e
	}
	// Guard EVERY administrative batch, not just startup: a restarted endpoint must
	// never redirect orphan cleanup at a different, non-sandbox database server.
	root, e := a.admin("")
	if e != nil {
		return e
	}
	marker, e := readScalar(ctx, root, "SELECT token FROM taskforge_sql_guard.runtime WHERE singleton=1")
	if e != nil || subtle.ConstantTimeCompare([]byte(marker), []byte(a.config.Marker)) != 1 {
		root.Close()
		return Unavailable("Refusing engine administration: dedicated SQL sandbox marker is absent or mismatched.")
	}
	if database == "" {
		defer root.Close()
		return f(root)
	}
	root.Close()
	c, e := a.admin(database)
	if e != nil {
		return e
	}
	defer c.Close()
	return f(c)
}
func (a *ServerAdapter) verifyGuard(ctx context.Context) error {
	return a.withAdmin(ctx, "", func(*native.Session) error { return nil })
}
func (a *ServerAdapter) Startup(ctx context.Context) error {
	if e := a.verifyGuard(ctx); e != nil {
		return e
	}
	if _, e := a.Registration(ctx); e != nil {
		return e
	}
	var databases, users []string
	e := a.withAdmin(ctx, "", func(c *native.Session) error {
		dbQuery, userQuery := "", ""
		if a.Engine() == "postgresql" {
			for _, db := range []string{"postgres", "template1"} {
				if e := c.Exec(ctx, "REVOKE CONNECT ON DATABASE "+q(db, a.Engine())+" FROM PUBLIC"); e != nil {
					return e
				}
			}
			dbQuery = "SELECT datname FROM pg_database WHERE starts_with(datname,'" + a.prefix + "')"
			userQuery = "SELECT rolname FROM pg_roles WHERE starts_with(rolname,'" + a.prefix + "')"
		} else {
			dbQuery = fmt.Sprintf("SELECT SCHEMA_NAME FROM information_schema.SCHEMATA WHERE LEFT(SCHEMA_NAME,%d)='%s'", len(a.prefix), a.prefix)
			userQuery = fmt.Sprintf("SELECT User FROM mysql.user WHERE LEFT(User,%d)='%s' AND Host='%%'", len(a.prefix), a.prefix)
		}
		for i, sql := range []string{dbQuery, userQuery} {
			r, e := c.Query(ctx, sql, 4096, 1<<20)
			if e != nil {
				return e
			}
			for _, row := range r.Rows {
				name := row[0].Text
				if !a.owned(name) {
					return Unavailable("Unsafe SQL namespace detected.")
				}
				if i == 0 {
					databases = append(databases, name)
				} else {
					users = append(users, name)
				}
			}
		}
		return nil
	})
	if e != nil {
		return e
	}
	if a.Engine() == "mysql" {
		for _, user := range users {
			if e = a.killMySQL(ctx, user, ""); e != nil {
				return e
			}
		}
	}
	for _, db := range databases {
		if e = a.dropDatabase(ctx, db); e != nil {
			return e
		}
	}
	for _, user := range users {
		if e = a.dropUser(ctx, user); e != nil {
			return e
		}
	}
	return nil
}
func (a *ServerAdapter) Registration(ctx context.Context) (out Registration, err error) {
	settings := map[string]any{"executorFingerprint": a.executor, "implementation": ImplementationVersion, "clientLibraries": native.LibraryVersions(), "transactionMode": "autocommit", "identifierPolicy": "portable-lower-v1", "caseFolding": "unicode-default-v1"}
	out = Registration{Engine: a.Engine(), RuntimeDigest: a.config.RuntimeDigest, AdapterVersion: AdapterVersion, Settings: settings}
	err = a.withAdmin(ctx, "", func(c *native.Session) error {
		if a.Engine() == "postgresql" {
			version, e := readScalar(ctx, c, "SHOW server_version")
			if e != nil {
				return e
			}
			number, e := readScalar(ctx, c, "SHOW server_version_num")
			if e != nil {
				return e
			}
			n, e := strconv.Atoi(number)
			if e != nil || n < 180000 || n >= 190000 {
				return Unavailable("The PostgreSQL adapter requires PostgreSQL 18.")
			}
			banner, e := readScalar(ctx, c, "SELECT version()")
			if e != nil {
				return e
			}
			out.EngineVersion = version
			settings["encoding"] = "UTF8"
			settings["timezone"] = "UTC"
			settings["collation"] = "C"
			settings["localeProvider"] = "libc"
			settings["serverVersionNum"] = number
			settings["serverBuild"] = banner
			settings["storageStrategy"] = "template-database-v1"
		} else {
			r, e := c.Query(ctx, "SELECT VERSION(),@@version_comment,@@lower_case_table_names", 1, 1<<20)
			if e != nil {
				return e
			}
			if len(r.Rows) != 1 || len(r.Rows[0]) != 3 {
				return Unavailable("Unexpected MySQL metadata.")
			}
			row := r.Rows[0]
			if !strings.HasPrefix(row[0].Text, "8.4.") {
				return Unavailable("The MySQL adapter requires MySQL 8.4.")
			}
			out.EngineVersion = row[0].Text
			settings["encoding"] = "utf8mb4"
			settings["timezone"] = "+00:00"
			settings["collation"] = "utf8mb4_0900_as_cs"
			settings["serverBuild"] = row[1].Text
			settings["lowerCaseTableNames"], _ = strconv.Atoi(row[2].Text)
			settings["storageStrategy"] = "logical-script-v1"
			settings["sqlMode"] = "STRICT_ALL_TABLES,ONLY_FULL_GROUP_BY,ERROR_FOR_DIVISION_BY_ZERO,NO_ZERO_DATE,NO_ZERO_IN_DATE,NO_ENGINE_SUBSTITUTION,NO_BACKSLASH_ESCAPES"
		}
		return nil
	})
	return
}
func (a *ServerAdapter) golden(key string) (string, error) {
	if !hashRE.MatchString(key) {
		return "", Unavailable("Invalid materialization key.")
	}
	return a.prefix + "g_" + key[:28], nil
}
func (a *ServerAdapter) Prepare(ctx context.Context, p Payload) error {
	if a.Engine() == "mysql" {
		// A deterministic, exclusively owned validation name lets the janitor
		// retry cleanup after a failed DROP, rather than losing an orphan name.
		name, e := a.golden(p.MaterializationKey)
		if e != nil {
			return e
		}
		e = a.createMySQLDatabase(ctx, name, p)
		clean := a.dropDatabase(context.WithoutCancel(ctx), name)
		if clean != nil {
			return Unavailable("Materialization cleanup failed.")
		}
		return e
	}
	name, e := a.golden(p.MaterializationKey)
	if e != nil {
		return e
	}
	plan, e := CompileDataset(p, "postgresql")
	if e != nil {
		return e
	}
	e = a.withAdmin(ctx, "", func(c *native.Session) error {
		if e := c.Exec(ctx, "CREATE DATABASE "+q(name, a.Engine())+" TEMPLATE template0 ENCODING 'UTF8' LOCALE_PROVIDER libc LC_COLLATE 'C' LC_CTYPE 'C'"); e != nil {
			return e
		}
		return c.Exec(ctx, "REVOKE ALL ON DATABASE "+q(name, a.Engine())+" FROM PUBLIC")
	})
	if e == nil {
		e = a.withAdmin(ctx, name, func(c *native.Session) error {
			if e := runPlan(ctx, c, plan); e != nil {
				return e
			}
			for _, t := range p.Definition.Tables {
				for _, col := range t.Columns {
					if col.Identity {
						table, colName := q(t.Name, "postgresql"), q(col.Name, "postgresql")
						sql := fmt.Sprintf("SELECT setval(pg_get_serial_sequence('%s','%s'),GREATEST(COALESCE(MAX(%s),0),1),COALESCE(MAX(%s),0)>=1) FROM %s", table, col.Name, colName, colName, table)
						if e := c.Exec(ctx, sql); e != nil {
							return e
						}
					}
				}
			}
			size, e := readScalar(ctx, c, "SELECT pg_database_size(current_database())")
			if e != nil {
				return e
			}
			n, e := strconv.ParseInt(size, 10, 64)
			if e != nil || n > 32<<20 {
				return Fail("SQL_DATASET_SIZE", "PostgreSQL dataset exceeds 32 MiB.")
			}
			return nil
		})
	}
	if e == nil {
		e = a.withAdmin(ctx, "", func(c *native.Session) error {
			return c.Exec(ctx, "ALTER DATABASE "+q(name, a.Engine())+" ALLOW_CONNECTIONS false")
		})
	}
	if e != nil {
		if clean := a.dropDatabase(context.WithoutCancel(ctx), name); clean != nil {
			return Unavailable("Materialization cleanup failed.")
		}
	}
	return e
}
func (a *ServerAdapter) createMySQLDatabase(ctx context.Context, name string, p Payload) error {
	if !a.owned(name) {
		return Unavailable("Invalid MySQL namespace.")
	}
	plan, e := CompileDataset(p, "mysql")
	if e != nil {
		return e
	}
	if e = a.withAdmin(ctx, "", func(c *native.Session) error {
		return c.Exec(ctx, "CREATE DATABASE "+q(name, "mysql")+" CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_as_cs")
	}); e != nil {
		return e
	}
	return a.withAdmin(ctx, name, func(c *native.Session) error {
		if e := runPlan(ctx, c, plan); e != nil {
			return e
		}
		size, e := readScalar(ctx, c, "SELECT COALESCE(SUM(DATA_LENGTH+INDEX_LENGTH),0) FROM information_schema.TABLES WHERE TABLE_SCHEMA=DATABASE()")
		if e != nil {
			return e
		}
		n, e := strconv.ParseInt(size, 10, 64)
		if e != nil || n > 32<<20 {
			return Fail("SQL_DATASET_SIZE", "MySQL dataset exceeds 32 MiB.")
		}
		return nil
	})
}
func (a *ServerAdapter) Create(ctx context.Context, p Payload) (out Sandbox, err error) {
	token, e := randomHex(10)
	if e != nil {
		return out, e
	}
	password, e := randomPassword()
	if e != nil {
		return out, e
	}
	name, user := a.prefix+"s_"+token, a.prefix+token[:16]
	cfg := native.Config{Engine: a.Engine(), Host: a.config.Host, Port: a.config.Port, Database: name, User: user, Password: password}
	out = Sandbox{token, cfg}
	defer func() {
		if err != nil {
			if clean := a.Destroy(context.WithoutCancel(ctx), out); clean != nil {
				err = Unavailable("Failed sandbox creation left quarantined engine objects; cleanup is required.")
			}
		}
	}()
	if a.Engine() == "mysql" {
		if err = a.createMySQLDatabase(ctx, name, p); err != nil {
			return
		}
		err = a.withAdmin(ctx, "", func(c *native.Session) error {
			if e := c.Exec(ctx, fmt.Sprintf("CREATE USER '%s'@'%%' IDENTIFIED BY '%s' WITH MAX_USER_CONNECTIONS 1", user, password)); e != nil {
				return e
			}
			return c.Exec(ctx, fmt.Sprintf("GRANT SELECT,INSERT,UPDATE,DELETE,CREATE,DROP,ALTER,INDEX,REFERENCES,CREATE VIEW,SHOW VIEW ON %s.* TO '%s'@'%%'", q(name, "mysql"), user))
		})
		return
	}
	golden, e := a.golden(p.MaterializationKey)
	if e != nil {
		err = e
		return
	}
	err = a.withAdmin(ctx, "", func(c *native.Session) error {
		commands := []string{fmt.Sprintf("CREATE ROLE %s LOGIN PASSWORD '%s' NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS CONNECTION LIMIT 1", q(user, a.Engine()), password)}
		for _, setting := range []string{"statement_timeout='10s'", "idle_in_transaction_session_timeout='10s'", "work_mem='8MB'", "temp_file_limit='32MB'", "max_parallel_workers_per_gather=0"} {
			commands = append(commands, "ALTER ROLE "+q(user, a.Engine())+" SET "+setting)
		}
		commands = append(commands, "CREATE DATABASE "+q(name, a.Engine())+" TEMPLATE "+q(golden, a.Engine()), "ALTER DATABASE "+q(name, a.Engine())+" ALLOW_CONNECTIONS true", "REVOKE ALL ON DATABASE "+q(name, a.Engine())+" FROM PUBLIC", "GRANT CONNECT,TEMPORARY ON DATABASE "+q(name, a.Engine())+" TO "+q(user, a.Engine()))
		for _, sql := range commands {
			if e := c.Exec(ctx, sql); e != nil {
				return e
			}
		}
		return nil
	})
	if err != nil {
		return
	}
	err = a.withAdmin(ctx, name, func(c *native.Session) error {
		for _, sql := range []string{"REVOKE ALL ON SCHEMA public FROM PUBLIC", "GRANT USAGE,CREATE ON SCHEMA public TO " + q(user, a.Engine())} {
			if e := c.Exec(ctx, sql); e != nil {
				return e
			}
		}
		for _, table := range p.Definition.Tables {
			if e := c.Exec(ctx, "ALTER TABLE public."+q(table.Name, a.Engine())+" OWNER TO "+q(user, a.Engine())); e != nil {
				return e
			}
		}
		return c.Exec(ctx, "GRANT ALL ON ALL SEQUENCES IN SCHEMA public TO "+q(user, a.Engine()))
	})
	return
}
func (a *ServerAdapter) validSandbox(s Sandbox) bool {
	return s.Connection.Engine == a.Engine() && s.Connection.Host == a.config.Host && s.Connection.Port == a.config.Port && a.owned(s.Connection.Database) && a.owned(s.Connection.User) && s.Connection.Database == a.prefix+"s_"+s.ID && len(s.ID) == 20 && s.Connection.User == a.prefix+s.ID[:16]
}
func (a *ServerAdapter) Kill(ctx context.Context, s Sandbox) error {
	if !a.validSandbox(s) {
		return Unavailable("Refusing foreign SQL backend cancellation.")
	}
	if a.Engine() == "mysql" {
		return a.killMySQL(ctx, s.Connection.User, s.Connection.Database)
	}
	return a.withAdmin(ctx, "", func(c *native.Session) error {
		return c.Exec(ctx, fmt.Sprintf("SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname='%s' AND usename='%s' AND pid<>pg_backend_pid()", s.Connection.Database, s.Connection.User))
	})
}
func (a *ServerAdapter) killMySQL(ctx context.Context, user, database string) error {
	if !a.owned(user) || (database != "" && !a.owned(database)) {
		return Unavailable("Refusing foreign MySQL backend cancellation.")
	}
	return a.withAdmin(ctx, "", func(c *native.Session) error {
		sql := "SELECT ID FROM information_schema.PROCESSLIST WHERE USER='" + user + "'"
		if database != "" {
			sql += " AND DB='" + database + "'"
		}
		r, e := c.Query(ctx, sql, 4096, 1<<20)
		if e != nil {
			return e
		}
		for _, row := range r.Rows {
			id, e := strconv.ParseUint(row[0].Text, 10, 64)
			if e != nil {
				return e
			}
			e = c.Exec(ctx, fmt.Sprintf("KILL CONNECTION %d", id))
			var db *native.Error
			if e != nil && !(errors.As(e, &db) && db.Code == "1094") {
				return e
			}
		}
		return nil
	})
}
func (a *ServerAdapter) dropDatabase(ctx context.Context, name string) error {
	if !a.owned(name) {
		return Unavailable("Refusing foreign database cleanup.")
	}
	return a.withAdmin(ctx, "", func(c *native.Session) error {
		sql := "DROP DATABASE IF EXISTS " + q(name, a.Engine())
		if a.Engine() == "postgresql" {
			sql += " WITH (FORCE)"
		}
		return c.Exec(ctx, sql)
	})
}
func (a *ServerAdapter) dropUser(ctx context.Context, user string) error {
	if !a.owned(user) {
		return Unavailable("Refusing foreign user cleanup.")
	}
	return a.withAdmin(ctx, "", func(c *native.Session) error {
		if a.Engine() == "postgresql" {
			return c.Exec(ctx, "DROP ROLE IF EXISTS "+q(user, a.Engine()))
		}
		return c.Exec(ctx, "DROP USER IF EXISTS '"+user+"'@'%'")
	})
}
func (a *ServerAdapter) Destroy(ctx context.Context, s Sandbox) error {
	if !a.validSandbox(s) {
		return Unavailable("Refusing foreign SQL sandbox cleanup.")
	}
	if a.Engine() == "mysql" {
		if e := a.Kill(ctx, s); e != nil {
			return e
		}
	}
	if e := a.dropDatabase(ctx, s.Connection.Database); e != nil {
		return e
	}
	return a.dropUser(ctx, s.Connection.User)
}
func (a *ServerAdapter) DropMaterialization(ctx context.Context, key string) error {
	name, e := a.golden(key)
	if e != nil {
		return e
	}
	return a.dropDatabase(ctx, name)
}
