// Package native binds maintained PostgreSQL, MariaDB/MySQL and SQLite client
// libraries. It never executes SQL through a shell or implements a DB wire protocol.
package native

/*
#cgo pkg-config: libpq libmariadb sqlite3
#include <libpq-fe.h>
#include <mysql.h>
#include <sqlite3.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>
#include <poll.h>
#include <errno.h>
#include <sys/socket.h>
#include <fcntl.h>
static PGconn *tf_pg_open(const char *host,const char *port,const char *user,const char *password,const char *database,const char *options){
 const char *keys[]={"host","port","user","password","dbname","connect_timeout","gssencmode","sslmode","options","application_name",NULL};
 const char *vals[]={host,port,user,password,database,"3","disable","disable",options,"taskforge-sql",NULL};
 PGconn *c=PQconnectdbParams(keys,vals,0);if(c&&PQstatus(c)==CONNECTION_OK){PQsetnonblocking(c,1);fcntl(PQsocket(c),F_SETFD,FD_CLOEXEC);}return c;
}
static void tf_pg_quiet(void *arg,const char *message){(void)arg;(void)message;}
static void tf_pg_setquiet(PGconn *c){PQsetNoticeProcessor(c,tf_pg_quiet,NULL);}
static int tf_pg_send(PGconn *c,const char *sql){return PQsendQueryParams(c,sql,0,NULL,NULL,NULL,NULL,0);}
static int tf_poll(int fd,int writing,int ms){struct pollfd p={fd,writing?POLLOUT:POLLIN,0};int r=poll(&p,1,ms);return r<0&&errno!=EINTR?-1:r;}
static MYSQL *tf_my_open(const char *host,unsigned int port,const char *user,const char *password,const char *database){
 MYSQL *c=mysql_init(NULL);if(!c)return NULL;unsigned int connect=3,read=15,write=5,zero=0;my_bool reconnect=0;
 mysql_options(c,MYSQL_OPT_CONNECT_TIMEOUT,&connect);mysql_options(c,MYSQL_OPT_READ_TIMEOUT,&read);mysql_options(c,MYSQL_OPT_WRITE_TIMEOUT,&write);
 mysql_options(c,MYSQL_OPT_LOCAL_INFILE,&zero);mysql_options(c,MYSQL_OPT_RECONNECT,&reconnect);mysql_options(c,MYSQL_SET_CHARSET_NAME,"utf8mb4");
 if(mysql_real_connect(c,host,user,password,*database?database:NULL,port,NULL,0))fcntl(mysql_get_socket(c),F_SETFD,FD_CLOEXEC);return c;
}
static const char *tf_my_value(MYSQL_ROW r,unsigned int i){return r[i];}
static unsigned long tf_my_length(unsigned long *lens,unsigned int i){return lens[i];}
static const char *tf_my_name(MYSQL_FIELD *f,unsigned int i){return f[i].name;}
static unsigned int tf_my_type(MYSQL_FIELD *f,unsigned int i){return f[i].type;}
static unsigned int tf_my_charset(MYSQL_FIELD *f,unsigned int i){return f[i].charsetnr;}
static void tf_my_timeout(MYSQL *c,unsigned int seconds){mysql_options(c,MYSQL_OPT_READ_TIMEOUT,&seconds);}
static void tf_my_abort(MYSQL *c){shutdown(mysql_get_socket(c),SHUT_RDWR);}
static void tf_my_init(void){mysql_library_init(0,NULL,NULL);}
typedef struct {struct timespec deadline;} tf_deadline;
static tf_deadline *tf_deadline_new(int ms){tf_deadline *d=calloc(1,sizeof(*d));if(!d)return NULL;clock_gettime(CLOCK_MONOTONIC,&d->deadline);d->deadline.tv_sec+=ms/1000;d->deadline.tv_nsec+=(ms%1000)*1000000L;if(d->deadline.tv_nsec>=1000000000L){d->deadline.tv_sec++;d->deadline.tv_nsec-=1000000000L;}return d;}
static int tf_progress(void *arg){tf_deadline *d=arg;struct timespec now;clock_gettime(CLOCK_MONOTONIC,&now);return now.tv_sec>d->deadline.tv_sec||(now.tv_sec==d->deadline.tv_sec&&now.tv_nsec>=d->deadline.tv_nsec);}
static int tf_auth(void *arg,int action,const char *a,const char *b,const char *db,const char *origin){
 (void)arg;(void)origin;
 switch(action){
 case SQLITE_ATTACH:case SQLITE_DETACH:case SQLITE_PRAGMA:case SQLITE_TRANSACTION:case SQLITE_SAVEPOINT:case SQLITE_CREATE_VTABLE:case SQLITE_DROP_VTABLE:
 case SQLITE_CREATE_TRIGGER:case SQLITE_DROP_TRIGGER:case SQLITE_CREATE_TEMP_TABLE:case SQLITE_CREATE_TEMP_INDEX:case SQLITE_CREATE_TEMP_VIEW:case SQLITE_CREATE_TEMP_TRIGGER:
 case SQLITE_DROP_TEMP_TABLE:case SQLITE_DROP_TEMP_INDEX:case SQLITE_DROP_TEMP_VIEW:case SQLITE_DROP_TEMP_TRIGGER:return SQLITE_DENY;
 case SQLITE_FUNCTION:{const char *f=b?b:a;if(f&&(!sqlite3_stricmp(f,"load_extension")||!sqlite3_stricmp(f,"readfile")||!sqlite3_stricmp(f,"writefile")||!sqlite3_stricmp(f,"edit")||!sqlite3_stricmp(f,"eval")||!sqlite3_stricmp(f,"zipfile")||!sqlite3_stricmp(f,"fts3_tokenizer")))return SQLITE_DENY;break;}
 }
 if(db&&strcmp(db,"main")&&strcmp(db,"temp"))return SQLITE_DENY;return SQLITE_OK;
}
static int tf_sqlite_config(sqlite3 *c,int bytes,int ms,tf_deadline **d){
 sqlite3_enable_load_extension(c,0);if(sqlite3_db_config(c,SQLITE_DBCONFIG_DEFENSIVE,1,NULL)!=SQLITE_OK)return 1;if(sqlite3_db_config(c,SQLITE_DBCONFIG_TRUSTED_SCHEMA,0,NULL)!=SQLITE_OK)return 1;
 sqlite3_limit(c,SQLITE_LIMIT_LENGTH,bytes);sqlite3_limit(c,SQLITE_LIMIT_SQL_LENGTH,100000);sqlite3_limit(c,SQLITE_LIMIT_COLUMN,256);sqlite3_limit(c,SQLITE_LIMIT_EXPR_DEPTH,100);
 sqlite3_limit(c,SQLITE_LIMIT_COMPOUND_SELECT,20);sqlite3_limit(c,SQLITE_LIMIT_VDBE_OP,100000);sqlite3_limit(c,SQLITE_LIMIT_VARIABLE_NUMBER,1000);sqlite3_limit(c,SQLITE_LIMIT_TRIGGER_DEPTH,0);
 sqlite3_limit(c,SQLITE_LIMIT_ATTACHED,0);sqlite3_limit(c,SQLITE_LIMIT_FUNCTION_ARG,32);*d=tf_deadline_new(ms);if(!*d)return 1;sqlite3_progress_handler(c,1000,tf_progress,*d);return 0;
}
static int tf_sqlite_auth(sqlite3 *c,int on){return sqlite3_set_authorizer(c,on?tf_auth:NULL,NULL);}
*/
import "C"
import (
	"context"
	"encoding/hex"
	"errors"
	"fmt"
	"runtime"
	"strconv"
	"strings"
	"sync"
	"sync/atomic"
	"time"
	"unicode/utf8"
	"unsafe"
)

type Config struct {
	Engine, Host, Port, User, Password, Database, Path string
	TimeoutMS                                          int
	Create                                             bool
}
type Value struct {
	Kind  string
	Text  string
	Bytes []byte
	Null  bool
}
type Result struct {
	Columns  []string
	Rows     [][]Value
	Affected int64
	HasRows  bool
}
type Error struct {
	Engine, Code, Message string
	Limit, Timeout        bool
}

func (e *Error) Error() string { return e.Engine + ":" + e.Code + ": " + e.Message }

var ErrLimit = &Error{Code: "SQL_RESULT_LIMIT", Message: "Native result budget exceeded.", Limit: true}

type Session struct {
	engine       string
	pg           *C.PGconn
	my           *C.MYSQL
	sq           *C.sqlite3
	deadline     *C.tf_deadline
	threadLocked bool
	registered   bool
}

var mysqlOnce sync.Once
var activeConnections = map[string]*atomic.Int64{"postgresql": {}, "mysql": {}, "sqlite": {}}

// These are this process's client connections, not an estimate of server load.
func ConnectionCounts() map[string]int64 {
	out := map[string]int64{}
	for engine, counter := range activeConnections {
		out[engine] = counter.Load()
	}
	return out
}

func LibraryVersions() map[string]string {
	return map[string]string{"libpq": strconv.Itoa(int(C.PQlibVersion())), "mariadbConnectorC": C.GoString(C.mysql_get_client_info()), "sqlite": C.GoString(C.sqlite3_libversion()), "go": runtime.Version()}
}
func cstring(s string) (*C.char, func()) {
	v := C.CString(s)
	return v, func() { C.free(unsafe.Pointer(v)) }
}
func Open(cfg Config) (s *Session, err error) {
	for _, v := range []string{cfg.Host, cfg.Port, cfg.User, cfg.Password, cfg.Database, cfg.Path} {
		if strings.ContainsRune(v, 0) {
			return nil, errors.New("NUL in connection configuration")
		}
	}
	s = &Session{engine: cfg.Engine}
	owned := s
	defer func() {
		if err != nil {
			owned.Close()
		}
	}()
	host, fh := cstring(cfg.Host)
	defer fh()
	port, fp := cstring(cfg.Port)
	defer fp()
	user, fu := cstring(cfg.User)
	defer fu()
	pass, fpass := cstring(cfg.Password)
	defer fpass()
	db, fd := cstring(cfg.Database)
	defer fd()
	switch cfg.Engine {
	case "postgresql":
		timeout := cfg.TimeoutMS
		if timeout <= 0 {
			timeout = 15000
		}
		options, fo := cstring(fmt.Sprintf("-c statement_timeout=%d -c lock_timeout=1000 -c timezone=UTC -c standard_conforming_strings=on -c search_path=public", timeout))
		defer fo()
		s.pg = C.tf_pg_open(host, port, user, pass, db, options)
		if s.pg == nil || C.PQstatus(s.pg) != C.CONNECTION_OK {
			return nil, &Error{Engine: cfg.Engine, Code: "08001", Message: "Cannot connect to the dedicated database."}
		}
		C.tf_pg_setquiet(s.pg)
	case "mysql":
		mysqlOnce.Do(func() { C.tf_my_init() })
		runtime.LockOSThread()
		s.threadLocked = true
		C.mysql_thread_init()
		p, e := strconv.Atoi(cfg.Port)
		if e != nil || p < 1 || p > 65535 {
			return nil, errors.New("invalid MySQL port")
		}
		s.my = C.tf_my_open(host, C.uint(p), user, pass, db)
		if s.my == nil || C.mysql_errno(s.my) != 0 {
			return nil, &Error{Engine: cfg.Engine, Code: "2002", Message: "Cannot connect to the dedicated database."}
		}
		for _, sql := range []string{"SET SESSION sql_mode='STRICT_ALL_TABLES,ONLY_FULL_GROUP_BY,ERROR_FOR_DIVISION_BY_ZERO,NO_ZERO_DATE,NO_ZERO_IN_DATE,NO_ENGINE_SUBSTITUTION,NO_BACKSLASH_ESCAPES'", "SET time_zone='+00:00'", "SET autocommit=1"} {
			if _, err = s.Query(context.Background(), sql, 4096, 4<<20); err != nil {
				return nil, err
			}
		}
	case "sqlite":
		path, f := cstring(cfg.Path)
		defer f()
		flags := C.int(C.SQLITE_OPEN_READWRITE | C.SQLITE_OPEN_FULLMUTEX)
		if cfg.Create {
			flags |= C.SQLITE_OPEN_CREATE
		}
		if C.sqlite3_open_v2(path, &s.sq, flags, nil) != C.SQLITE_OK {
			return nil, &Error{Engine: cfg.Engine, Code: "SQLITE_CANTOPEN", Message: "Cannot open the disposable database."}
		}
		C.sqlite3_busy_timeout(s.sq, 100)
		C.sqlite3_extended_result_codes(s.sq, 1)
	default:
		return nil, errors.New("unsupported engine")
	}
	s.registered = true
	activeConnections[s.engine].Add(1)
	return s, nil
}
func (s *Session) Close() {
	if s == nil {
		return
	}
	if s.registered {
		activeConnections[s.engine].Add(-1)
		s.registered = false
	}
	if s.pg != nil {
		C.PQfinish(s.pg)
		s.pg = nil
	}
	if s.my != nil {
		C.mysql_close(s.my)
		s.my = nil
	}
	if s.sq != nil {
		C.sqlite3_progress_handler(s.sq, 0, nil, nil)
		C.sqlite3_close_v2(s.sq)
		s.sq = nil
	}
	if s.deadline != nil {
		C.free(unsafe.Pointer(s.deadline))
		s.deadline = nil
	}
	if s.threadLocked {
		C.mysql_thread_end()
		s.threadLocked = false
		runtime.UnlockOSThread()
	}
}
func (s *Session) ConfigureSQLite(bytes, ms int) error {
	if s.sq == nil {
		return errors.New("not SQLite")
	}
	if C.tf_sqlite_config(s.sq, C.int(bytes), C.int(ms), &s.deadline) != 0 {
		return errors.New("cannot install SQLite limits")
	}
	return nil
}
func (s *Session) AuthorizeSQLite(on bool) error {
	if s.sq == nil {
		return errors.New("not SQLite")
	}
	v := 0
	if on {
		v = 1
	}
	if C.tf_sqlite_auth(s.sq, C.int(v)) != C.SQLITE_OK {
		return errors.New("cannot install SQLite authorization")
	}
	return nil
}
func (s *Session) Exec(ctx context.Context, query string) error {
	_, e := s.Query(ctx, query, 4096, 4<<20)
	return e
}
func (s *Session) Query(ctx context.Context, query string, rowCap, byteCap int) (Result, error) {
	r := Result{Columns: []string{}, Rows: [][]Value{}}
	if strings.ContainsRune(query, 0) || rowCap < 0 || byteCap < 1 {
		return r, errors.New("invalid query or budget")
	}
	if e := ctx.Err(); e != nil {
		return r, e
	}
	switch s.engine {
	case "sqlite":
		return s.sqliteQuery(ctx, query, rowCap, byteCap)
	case "postgresql":
		return s.pgQuery(ctx, query, rowCap, byteCap)
	case "mysql":
		return s.mysqlQuery(ctx, query, rowCap, byteCap)
	}
	return r, errors.New("closed or unknown native session")
}
func appendRow(r *Result, row []Value, rows, bytes int, used *int) error {
	if len(r.Rows) >= rows {
		return ErrLimit
	}
	for _, v := range row {
		*used += len(v.Text) + len(v.Bytes) + 32
	}
	if *used > bytes {
		return ErrLimit
	}
	r.Rows = append(r.Rows, row)
	return nil
}
func (s *Session) sqliteError() *Error {
	if s.sq == nil {
		return &Error{Engine: "sqlite", Code: "SQLITE_CLOSED", Message: "Database closed."}
	}
	code := int(C.sqlite3_extended_errcode(s.sq))
	return &Error{Engine: "sqlite", Code: fmt.Sprintf("SQLITE_%d", code), Message: C.GoString(C.sqlite3_errmsg(s.sq)), Timeout: code&255 == C.SQLITE_INTERRUPT, Limit: code&255 == C.SQLITE_TOOBIG || code&255 == C.SQLITE_NOMEM || code&255 == C.SQLITE_FULL}
}
func (s *Session) sqliteQuery(ctx context.Context, sql string, rows, bytes int) (r Result, err error) {
	r = Result{Columns: []string{}, Rows: [][]Value{}}
	if s.sq == nil {
		return r, errors.New("closed SQLite connection")
	}
	query, free := cstring(sql)
	defer free()
	var stmt *C.sqlite3_stmt
	var tail *C.char
	if C.sqlite3_prepare_v2(s.sq, query, C.int(len(sql)+1), &stmt, &tail) != C.SQLITE_OK {
		return r, s.sqliteError()
	}
	if stmt == nil {
		return r, nil
	}
	defer C.sqlite3_finalize(stmt)
	rest := strings.TrimSpace(C.GoString(tail))
	if rest != "" {
		var extra *C.sqlite3_stmt
		var end *C.char
		if C.sqlite3_prepare_v2(s.sq, tail, -1, &extra, &end) != C.SQLITE_OK {
			return r, s.sqliteError()
		}
		if extra != nil {
			C.sqlite3_finalize(extra)
			return r, errors.New("multiple statements in a native query")
		}
	}
	n := int(C.sqlite3_column_count(stmt))
	if n > 256 {
		return r, ErrLimit
	}
	r.HasRows = n > 0
	used := 0
	before := C.sqlite3_total_changes64(s.sq)
	for i := 0; i < n; i++ {
		name := C.GoString(C.sqlite3_column_name(stmt, C.int(i)))
		used += len(name)
		r.Columns = append(r.Columns, name)
	}
	for {
		if e := ctx.Err(); e != nil {
			return r, e
		}
		rc := C.sqlite3_step(stmt)
		if rc == C.SQLITE_DONE {
			if C.sqlite3_total_changes64(s.sq) > before {
				r.Affected = int64(C.sqlite3_changes64(s.sq))
			}
			return r, nil
		}
		if rc != C.SQLITE_ROW {
			return r, s.sqliteError()
		}
		row := make([]Value, n)
		for i := 0; i < n; i++ {
			typ := C.sqlite3_column_type(stmt, C.int(i))
			size := int(C.sqlite3_column_bytes(stmt, C.int(i)))
			if size > bytes {
				return r, ErrLimit
			}
			switch typ {
			case C.SQLITE_NULL:
				row[i] = Value{Null: true}
			case C.SQLITE_INTEGER:
				row[i] = Value{Kind: "number", Text: strconv.FormatInt(int64(C.sqlite3_column_int64(stmt, C.int(i))), 10)}
			case C.SQLITE_FLOAT:
				row[i] = Value{Kind: "number", Text: strconv.FormatFloat(float64(C.sqlite3_column_double(stmt, C.int(i))), 'g', -1, 64)}
			case C.SQLITE_BLOB:
				row[i] = Value{Kind: "binary", Bytes: C.GoBytes(C.sqlite3_column_blob(stmt, C.int(i)), C.int(size))}
			default:
				v := C.GoStringN((*C.char)(unsafe.Pointer(C.sqlite3_column_text(stmt, C.int(i)))), C.int(size))
				if !utf8.ValidString(v) {
					return r, &Error{Engine: "sqlite", Code: "SQL_RESULT_TYPE", Message: "Invalid UTF-8 in SQL text result."}
				}
				row[i] = Value{Kind: "string", Text: v}
			}
		}
		if e := appendRow(&r, row, rows, bytes, &used); e != nil {
			return r, e
		}
	}
}
func (s *Session) pgError(res *C.PGresult) *Error {
	if res == nil {
		return &Error{Engine: "postgresql", Code: "08006", Message: "The database connection was lost."}
	}
	state := C.GoString(C.PQresultErrorField(res, C.PG_DIAG_SQLSTATE))
	msg := strings.Split(C.GoString(C.PQresultErrorMessage(res)), "\nCONTEXT:")[0]
	return &Error{Engine: "postgresql", Code: state, Message: msg, Timeout: state == "57014", Limit: state == "53200" || state == "53400"}
}
func (s *Session) pgReady(ctx context.Context, write bool) error {
	for {
		if e := ctx.Err(); e != nil {
			return e
		}
		if s.pg == nil {
			return errors.New("closed PostgreSQL connection")
		}
		if write {
			n := C.PQflush(s.pg)
			if n == 0 {
				return nil
			}
			if n < 0 {
				return s.pgError(nil)
			}
		} else {
			if C.PQconsumeInput(s.pg) != 1 {
				return s.pgError(nil)
			}
			if C.PQisBusy(s.pg) == 0 {
				return nil
			}
		}
		if C.tf_poll(C.PQsocket(s.pg), boolInt(write), 50) < 0 {
			return s.pgError(nil)
		}
	}
}
func boolInt(v bool) C.int {
	if v {
		return 1
	}
	return 0
}
func pgValue(oid uint32, text string) (Value, error) {
	switch oid {
	case 16:
		return Value{Kind: "boolean", Text: text}, nil
	case 20, 21, 23, 26, 700, 701, 1700:
		return Value{Kind: "number", Text: text}, nil
	case 17:
		if !strings.HasPrefix(text, `\x`) {
			return Value{}, errors.New("non-hex PostgreSQL bytea")
		}
		b, e := hex.DecodeString(text[2:])
		return Value{Kind: "binary", Bytes: b}, e
	case 1082:
		return Value{Kind: "date", Text: text}, nil
	case 1114, 1184:
		return Value{Kind: "datetime", Text: text}, nil
	case 18, 19, 25, 1042, 1043, 2950, 1083, 1266, 1186:
		return Value{Kind: "string", Text: text}, nil
	default:
		return Value{Kind: "unsupported", Text: text}, nil
	}
}
func (s *Session) pgQuery(ctx context.Context, sql string, rows, bytes int) (r Result, err error) {
	r = Result{Columns: []string{}, Rows: [][]Value{}}
	if s.pg == nil {
		return r, errors.New("closed PostgreSQL connection")
	}
	query, free := cstring(sql)
	defer free()
	if C.tf_pg_send(s.pg, query) != 1 {
		return r, s.pgError(nil)
	}
	if C.PQsetSingleRowMode(s.pg) != 1 {
		s.Close()
		return r, errors.New("PostgreSQL streaming mode unavailable")
	}
	if e := s.pgReady(ctx, true); e != nil {
		s.Close()
		return r, e
	}
	used := 0
	for {
		if e := s.pgReady(ctx, false); e != nil {
			s.Close()
			return r, e
		}
		res := C.PQgetResult(s.pg)
		if res == nil {
			return r, nil
		}
		status := C.PQresultStatus(res)
		if status != C.PGRES_SINGLE_TUPLE && status != C.PGRES_TUPLES_OK && status != C.PGRES_COMMAND_OK {
			e := s.pgError(res)
			C.PQclear(res)
			if status == C.PGRES_FATAL_ERROR {
				for {
					if s.pgReady(ctx, false) != nil {
						s.Close()
						break
					}
					extra := C.PQgetResult(s.pg)
					if extra == nil {
						break
					}
					C.PQclear(extra)
				}
			} else {
				s.Close()
			}
			return r, e
		}
		n := int(C.PQnfields(res))
		if n > 256 {
			C.PQclear(res)
			s.Close()
			return r, ErrLimit
		}
		if n > 0 && !r.HasRows {
			r.HasRows = true
			for i := 0; i < n; i++ {
				name := C.GoString(C.PQfname(res, C.int(i)))
				used += len(name)
				r.Columns = append(r.Columns, name)
			}
		}
		for rowIndex := 0; rowIndex < int(C.PQntuples(res)); rowIndex++ {
			row := make([]Value, n)
			for i := 0; i < n; i++ {
				if C.PQgetisnull(res, C.int(rowIndex), C.int(i)) != 0 {
					row[i] = Value{Null: true}
					continue
				}
				size := int(C.PQgetlength(res, C.int(rowIndex), C.int(i)))
				if size > bytes {
					C.PQclear(res)
					s.Close()
					return r, ErrLimit
				}
				text := C.GoStringN(C.PQgetvalue(res, C.int(rowIndex), C.int(i)), C.int(size))
				v, e := pgValue(uint32(C.PQftype(res, C.int(i))), text)
				if e != nil {
					C.PQclear(res)
					s.Close()
					return r, e
				}
				row[i] = v
			}
			if e := appendRow(&r, row, rows, bytes, &used); e != nil {
				C.PQclear(res)
				s.Close()
				return r, e
			}
		}
		if status == C.PGRES_COMMAND_OK || status == C.PGRES_TUPLES_OK {
			tag := C.GoString(C.PQcmdStatus(res))
			if strings.HasPrefix(tag, "INSERT") || strings.HasPrefix(tag, "UPDATE") || strings.HasPrefix(tag, "DELETE") {
				r.Affected, _ = strconv.ParseInt(C.GoString(C.PQcmdTuples(res)), 10, 64)
			}
		}
		C.PQclear(res)
	}
}
func (s *Session) mysqlError() *Error {
	if s.my == nil {
		return &Error{Engine: "mysql", Code: "2006", Message: "Database connection closed."}
	}
	code := int(C.mysql_errno(s.my))
	return &Error{Engine: "mysql", Code: strconv.Itoa(code), Message: C.GoString(C.mysql_error(s.my)), Timeout: code == 3024 || code == 1317, Limit: code == 1037 || code == 1038 || code == 1114}
}
func mysqlKind(typ, charset uint) string {
	switch typ {
	case 0, 1, 2, 3, 4, 5, 8, 9, 13, 246:
		return "number"
	case 7, 12, 17, 18:
		return "datetime"
	case 10, 14:
		return "date"
	case 16:
		return "binary"
	case 249, 250, 251, 252, 253, 254:
		if charset == 63 {
			return "binary"
		}
		return "string"
	default:
		return "string"
	}
}
func (s *Session) mysqlQuery(ctx context.Context, sql string, rows, bytes int) (r Result, err error) {
	r = Result{Columns: []string{}, Rows: [][]Value{}}
	if s.my == nil {
		return r, errors.New("closed MySQL connection")
	}
	if d, ok := ctx.Deadline(); ok {
		sec := int(time.Until(d).Seconds()) + 1
		if sec < 1 {
			return r, context.DeadlineExceeded
		}
		if sec > 15 {
			sec = 15
		}
		C.tf_my_timeout(s.my, C.uint(sec))
	}
	query, free := cstring(sql)
	defer free()
	if C.mysql_real_query(s.my, query, C.ulong(len(sql))) != 0 {
		return r, s.mysqlError()
	}
	res := C.mysql_use_result(s.my)
	if res == nil {
		if C.mysql_field_count(s.my) != 0 {
			return r, s.mysqlError()
		}
		a := uint64(C.mysql_affected_rows(s.my))
		if a < 1<<63 {
			r.Affected = int64(a)
		}
		return r, nil
	}
	// A failed stream is aborted before free_result; it must not drain unbounded data.
	defer func() {
		if err != nil && s.my != nil {
			C.tf_my_abort(s.my)
		}
		C.mysql_free_result(res)
		if err != nil {
			s.Close()
		}
	}()
	n := int(C.mysql_num_fields(res))
	if n > 256 {
		return r, ErrLimit
	}
	r.HasRows = true
	fields := C.mysql_fetch_fields(res)
	kinds := make([]string, n)
	used := 0
	for i := 0; i < n; i++ {
		name := C.GoString(C.tf_my_name(fields, C.uint(i)))
		used += len(name)
		r.Columns = append(r.Columns, name)
		kinds[i] = mysqlKind(uint(C.tf_my_type(fields, C.uint(i))), uint(C.tf_my_charset(fields, C.uint(i))))
	}
	for {
		if e := ctx.Err(); e != nil {
			return r, e
		}
		rowPtr := C.mysql_fetch_row(res)
		if rowPtr == nil {
			if C.mysql_errno(s.my) != 0 {
				return r, s.mysqlError()
			}
			return r, nil
		}
		lens := C.mysql_fetch_lengths(res)
		row := make([]Value, n)
		for i := 0; i < n; i++ {
			p := C.tf_my_value(rowPtr, C.uint(i))
			if p == nil {
				row[i] = Value{Null: true}
				continue
			}
			size := int(C.tf_my_length(lens, C.uint(i)))
			if size > bytes {
				return r, ErrLimit
			}
			if kinds[i] == "binary" {
				row[i] = Value{Kind: "binary", Bytes: C.GoBytes(unsafe.Pointer(p), C.int(size))}
			} else {
				text := C.GoStringN(p, C.int(size))
				if !utf8.ValidString(text) {
					return r, &Error{Engine: "mysql", Code: "SQL_RESULT_TYPE", Message: "Invalid UTF-8 in SQL text result."}
				}
				row[i] = Value{Kind: kinds[i], Text: text}
			}
		}
		if e := appendRow(&r, row, rows, bytes, &used); e != nil {
			return r, e
		}
	}
}
