package sqlworker

import (
	"strings"
	"unicode"
)

type Statement struct {
	SQL            string
	Words, Strings []string
}

// SplitScript describes the supported exercise profile; it is not a substitute
// for disposable credentials, server privileges, networking or kernel isolation.
func SplitScript(source, engine string, multiple bool, maxStatements int) ([]Statement, error) {
	s := []rune(source)
	if len(s) == 0 || len(s) > 100000 || strings.ContainsRune(source, 0) {
		return nil, Fail("SQL_SOURCE", "SQL must contain 1..100000 characters without NUL bytes.")
	}
	out := []Statement{}
	words, strs := []string{}, []string{}
	start, i := 0, 0
	meaningful := false
	match := func(at int, v string) bool {
		r := []rune(v)
		if at+len(r) > len(s) {
			return false
		}
		return string(s[at:at+len(r)]) == v
	}
	appendStmt := func(end int) {
		if meaningful {
			out = append(out, Statement{strings.TrimSpace(string(s[start:end])), words, strs})
		}
		words, strs = []string{}, []string{}
		meaningful = false
	}
	for i < len(s) {
		c := s[i]
		if unicode.IsSpace(c) {
			i++
			continue
		}
		if match(i, "--") || (c == '#' && engine == "mysql") {
			if engine == "mysql" && match(i, "--") && i+2 < len(s) && !unicode.IsSpace(s[i+2]) {
				meaningful = true
				i += 2
				continue
			}
			for i < len(s) && s[i] != '\n' {
				i++
			}
			continue
		}
		if match(i, "/*") {
			if i+2 < len(s) && (s[i+2] == '!' || s[i+2] == '+') {
				return nil, Fail("SQL_EXECUTABLE_COMMENT", "Executable comments and optimizer hints are outside this profile.")
			}
			depth := 1
			i += 2
			for i < len(s) && depth > 0 {
				if match(i, "/*") {
					if engine != "postgresql" {
						return nil, Fail("SQL_COMMENT", "Nested comments require PostgreSQL.")
					}
					depth++
					i += 2
				} else if match(i, "*/") {
					depth--
					i += 2
				} else {
					i++
				}
			}
			if depth != 0 {
				return nil, Fail("SQL_COMMENT", "Unterminated SQL comment.")
			}
			continue
		}
		if c == ';' {
			appendStmt(i)
			i++
			start = i
			continue
		}
		meaningful = true
		if c == '\'' || c == '"' || c == '`' || (c == '[' && engine == "sqlite") {
			quote := c
			if quote == '[' {
				quote = ']'
			}
			begin := i
			i++
			content := []rune{}
			closed := false
			escaped := engine == "postgresql" && c == '\'' && begin > 0 && (s[begin-1] == 'e' || s[begin-1] == 'E') && (begin < 2 || (!unicode.IsLetter(s[begin-2]) && !unicode.IsDigit(s[begin-2]) && s[begin-2] != '_'))
			for i < len(s) {
				if s[i] == quote {
					if i+1 < len(s) && s[i+1] == quote && c != '[' {
						content = append(content, quote)
						i += 2
						continue
					}
					i++
					closed = true
					break
				}
				if escaped && s[i] == '\\' && i+1 < len(s) {
					content = append(content, s[i], s[i+1])
					i += 2
					continue
				}
				content = append(content, s[i])
				i++
			}
			if !closed {
				return nil, Fail("SQL_QUOTE", "Unterminated SQL literal or identifier.")
			}
			if c == '\'' {
				strs = append(strs, string(content))
			} else {
				words = append(words, "@"+strings.ToLower(string(content)))
			}
			continue
		}
		if c == '$' && engine == "postgresql" {
			j := i + 1
			for j < len(s) && (unicode.IsLetter(s[j]) || s[j] == '_' || (j > i+1 && unicode.IsDigit(s[j]))) {
				j++
			}
			if j < len(s) && s[j] == '$' {
				tag := string(s[i : j+1])
				end := -1
				for k := j + 1; k < len(s); k++ {
					if match(k, tag) {
						end = k
						break
					}
				}
				if end < 0 {
					return nil, Fail("SQL_QUOTE", "Unterminated dollar-quoted SQL literal.")
				}
				strs = append(strs, string(s[j+1:end]))
				i = end + len([]rune(tag))
				continue
			}
		}
		if unicode.IsLetter(c) || c == '_' {
			j := i + 1
			for j < len(s) && (unicode.IsLetter(s[j]) || unicode.IsDigit(s[j]) || s[j] == '_' || s[j] == '$') {
				j++
			}
			words = append(words, strings.ToLower(string(s[i:j])))
			i = j
		} else {
			i++
		}
	}
	appendStmt(len(s))
	if len(out) == 0 {
		return nil, Fail("SQL_SOURCE", "The script contains no SQL statements.")
	}
	if len(out) > maxStatements || (!multiple && len(out) != 1) {
		return nil, Fail("SQL_STATEMENT_LIMIT", "This assignment does not allow this number of statements.")
	}
	for _, st := range out {
		if err := validateStatement(st); err != nil {
			return nil, err
		}
	}
	return out, nil
}
func validateStatement(st Statement) error {
	w := st.Words
	if len(w) == 0 {
		return Fail("SQL_STATEMENT", "Unknown SQL statement.")
	}
	// SQL-level defence in depth. Connection credentials still enforce the real scope.
	forbidden := map[string]bool{"set_config": true, "load_file": true, "load_extension": true, "readfile": true, "writefile": true, "dblink": true, "dblink_connect": true, "pg_read_file": true, "pg_read_binary_file": true, "pg_ls_dir": true, "pg_logdir_ls": true, "pg_reload_conf": true, "pg_terminate_backend": true, "pg_cancel_backend": true, "lo_import": true, "lo_export": true, "pg_write_file": true, "pg_execute_server_program": true}
	for _, v := range w {
		if forbidden[strings.TrimPrefix(v, "@")] || v == "outfile" || v == "dumpfile" {
			return Fail("SQL_PROFILE_POLICY", "File, network and session-administration operations are not allowed.")
		}
	}
	switch w[0] {
	case "select", "with", "insert", "update", "delete", "replace":
		return nil
	case "create", "drop", "alter":
		tail := w[1:]
		if len(tail) >= 2 && w[0] == "create" && tail[0] == "or" && tail[1] == "replace" {
			tail = tail[2:]
		}
		if len(tail) > 0 && (tail[0] == "temp" || tail[0] == "temporary") {
			return Fail("SQL_PROFILE_POLICY", "Temporary objects are outside the persistent-schema exercise profile.")
		}
		if len(tail) > 0 && tail[0] == "unique" {
			tail = tail[1:]
		}
		if len(tail) > 0 && (tail[0] == "table" || tail[0] == "index" || tail[0] == "view") {
			for _, v := range w {
				if v == "tablespace" || v == "data_directory" || v == "index_directory" {
					return Fail("SQL_STORAGE_POLICY", "External storage placement is not allowed.")
				}
			}
			return nil
		}
	}
	return Fail("SQL_PROFILE_POLICY", "Only query, data and table/index/view statements are allowed. Server administration, session configuration and explicit transactions are unavailable.")
}
func ValidateReference(stmts []Statement, mode string) error {
	if mode == "schema" {
		return nil
	}
	volatile := strings.Fields("current_timestamp current_date current_time localtimestamp localtime now clock_timestamp statement_timestamp transaction_timestamp timeofday random rand randomblob uuid uuid_short gen_random_uuid uuid_generate_v4 sysdate")
	banned := map[string]bool{}
	for _, v := range volatile {
		banned[v] = true
	}
	for _, st := range stmts {
		dateFn := false
		for _, w := range st.Words {
			w = strings.TrimPrefix(w, "@")
			if banned[w] {
				return Fail("SQL_REFERENCE_NONDETERMINISTIC", "The reference must not depend on runtime clocks, random values or UUID generation.")
			}
			if w == "date" || w == "time" || w == "datetime" || w == "julianday" || w == "unixepoch" || w == "strftime" {
				dateFn = true
			}
		}
		if dateFn {
			for _, s := range st.Strings {
				if strings.EqualFold(s, "now") || strings.EqualFold(s, "localtime") {
					return Fail("SQL_REFERENCE_NONDETERMINISTIC", "Use an explicit reproducible date/time in the reference.")
				}
			}
		}
	}
	return nil
}
