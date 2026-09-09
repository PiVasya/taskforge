package sqlworker

import (
	"bytes"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"io"
	"math"
	"sort"
	"strconv"
	"strings"
	"unicode/utf16"
	"unicode/utf8"
)

// DecodeStrict first walks tokens to detect duplicate keys at every nesting level.
// A second typed decode rejects fields the receiver cannot understand.
func DecodeStrict(raw []byte, target any) error {
	if len(raw) > 8_000_000 || !utf8.Valid(raw) {
		return fmt.Errorf("invalid or oversized UTF-8 JSON")
	}
	if err := validUnicodeEscapes(raw); err != nil {
		return err
	}
	d := json.NewDecoder(bytes.NewReader(raw))
	d.UseNumber()
	if _, err := readJSON(d, 0); err != nil {
		return err
	}
	if _, err := d.Token(); err != io.EOF {
		return fmt.Errorf("trailing JSON data")
	}
	d = json.NewDecoder(bytes.NewReader(raw))
	d.UseNumber()
	d.DisallowUnknownFields()
	return d.Decode(target)
}
func readJSON(d *json.Decoder, depth int) (any, error) {
	if depth > 48 {
		return nil, fmt.Errorf("JSON depth exceeds contract")
	}
	t, err := d.Token()
	if err != nil {
		return nil, err
	}
	switch v := t.(type) {
	case json.Delim:
		if v == '{' {
			m := map[string]any{}
			for d.More() {
				k, e := d.Token()
				if e != nil {
					return nil, e
				}
				s, ok := k.(string)
				if !ok {
					return nil, fmt.Errorf("invalid property")
				}
				if _, ok = m[s]; ok {
					return nil, fmt.Errorf("duplicate JSON property")
				}
				x, e := readJSON(d, depth+1)
				if e != nil {
					return nil, e
				}
				m[s] = x
			}
			end, e := d.Token()
			if e != nil || end != json.Delim('}') {
				return nil, fmt.Errorf("invalid object")
			}
			return m, nil
		}
		if v == '[' {
			a := []any{}
			for d.More() {
				x, e := readJSON(d, depth+1)
				if e != nil {
					return nil, e
				}
				a = append(a, x)
			}
			end, e := d.Token()
			if e != nil || end != json.Delim(']') {
				return nil, fmt.Errorf("invalid array")
			}
			return a, nil
		}
		return nil, fmt.Errorf("unexpected JSON delimiter")
	case json.Number:
		if _, e := canonicalNumber(v.String()); e != nil {
			return nil, e
		}
		return v, nil
	default:
		return v, nil
	}
}
func validUnicodeEscapes(raw []byte) error {
	for i := 0; i < len(raw); i++ {
		if raw[i] != '"' {
			continue
		}
		i++
		for ; i < len(raw) && raw[i] != '"'; i++ {
			if raw[i] != '\\' {
				continue
			}
			i++
			if i >= len(raw) {
				break
			}
			if raw[i] != 'u' {
				continue
			}
			if i+4 >= len(raw) {
				return fmt.Errorf("bad unicode escape")
			}
			v, e := strconv.ParseUint(string(raw[i+1:i+5]), 16, 16)
			if e != nil {
				return e
			}
			i += 4
			if v >= 0xd800 && v <= 0xdbff {
				if i+6 >= len(raw) || string(raw[i+1:i+3]) != "\\u" {
					return fmt.Errorf("unpaired JSON surrogate")
				}
				w, e := strconv.ParseUint(string(raw[i+3:i+7]), 16, 16)
				if e != nil || w < 0xdc00 || w > 0xdfff {
					return fmt.Errorf("unpaired JSON surrogate")
				}
				i += 6
			} else if v >= 0xdc00 && v <= 0xdfff {
				return fmt.Errorf("unpaired JSON surrogate")
			}
		}
	}
	return nil
}
func Marshal(v any) ([]byte, error) { return json.Marshal(v) }
func Canonical(v any) ([]byte, error) {
	raw, err := json.Marshal(v)
	if err != nil {
		return nil, err
	}
	d := json.NewDecoder(bytes.NewReader(raw))
	d.UseNumber()
	x, err := readJSON(d, 0)
	if err != nil {
		return nil, err
	}
	var b strings.Builder
	if err = writeCanonical(&b, x, 0); err != nil {
		return nil, err
	}
	return []byte(b.String()), nil
}
func ContentHash(v any) (string, error) {
	raw, e := Canonical(v)
	if e != nil {
		return "", e
	}
	h := sha256.Sum256(raw)
	return hex.EncodeToString(h[:]), nil
}
func sameJSON(a, b any) bool {
	x, e := Canonical(a)
	if e != nil {
		return false
	}
	y, e := Canonical(b)
	return e == nil && bytes.Equal(x, y)
}
func writeText(b *strings.Builder, s string) {
	b.WriteByte('"')
	for _, c := range utf16.Encode([]rune(s)) {
		switch c {
		case '"':
			b.WriteString(`\"`)
		case '\\':
			b.WriteString(`\\`)
		case '\b':
			b.WriteString(`\b`)
		case '\f':
			b.WriteString(`\f`)
		case '\n':
			b.WriteString(`\n`)
		case '\r':
			b.WriteString(`\r`)
		case '\t':
			b.WriteString(`\t`)
		default:
			if c < 32 || c >= 127 {
				fmt.Fprintf(b, `\u%04x`, c)
			} else {
				b.WriteByte(byte(c))
			}
		}
	}
	b.WriteByte('"')
}
func utf16Less(a, b string) bool {
	x, y := utf16.Encode([]rune(a)), utf16.Encode([]rune(b))
	for i := 0; i < len(x) && i < len(y); i++ {
		if x[i] != y[i] {
			return x[i] < y[i]
		}
	}
	return len(x) < len(y)
}
func writeCanonical(b *strings.Builder, v any, depth int) error {
	if depth > 48 {
		return fmt.Errorf("JSON depth exceeds contract")
	}
	switch x := v.(type) {
	case nil:
		b.WriteString("null")
	case bool:
		b.WriteString(strconv.FormatBool(x))
	case string:
		writeText(b, x)
	case json.Number:
		n, e := canonicalNumber(string(x))
		if e != nil {
			return e
		}
		b.WriteString(n)
	case []any:
		b.WriteByte('[')
		for i, y := range x {
			if i > 0 {
				b.WriteByte(',')
			}
			if e := writeCanonical(b, y, depth+1); e != nil {
				return e
			}
		}
		b.WriteByte(']')
	case map[string]any:
		keys := make([]string, 0, len(x))
		for k := range x {
			keys = append(keys, k)
		}
		sort.Slice(keys, func(i, j int) bool { return utf16Less(keys[i], keys[j]) })
		b.WriteByte('{')
		for i, k := range keys {
			if i > 0 {
				b.WriteByte(',')
			}
			writeText(b, k)
			b.WriteByte(':')
			if e := writeCanonical(b, x[k], depth+1); e != nil {
				return e
			}
		}
		b.WriteByte('}')
	default:
		return fmt.Errorf("unsupported canonical value %T", v)
	}
	return nil
}
func canonicalNumber(text string) (string, error) {
	if len(text) > 100000 || text == "" {
		return "", fmt.Errorf("invalid number")
	}
	negative := text[0] == '-'
	s := strings.TrimPrefix(text, "-")
	exponent := 0
	if i := strings.IndexAny(s, "eE"); i >= 0 {
		x, e := strconv.Atoi(s[i+1:])
		if e != nil || x < -10000 || x > 10000 {
			return "", fmt.Errorf("numeric exponent exceeds contract")
		}
		exponent = x
		s = s[:i]
	}
	fraction := 0
	if i := strings.IndexByte(s, '.'); i >= 0 {
		fraction = len(s) - i - 1
	}
	digits := strings.TrimLeft(strings.ReplaceAll(s, ".", ""), "0")
	if digits == "" {
		return "0", nil
	}
	for _, c := range digits {
		if c < '0' || c > '9' {
			return "", fmt.Errorf("invalid number")
		}
	}
	significant := strings.TrimRight(digits, "0")
	sign := ""
	if negative {
		sign = "-"
	}
	return sign + significant + "e" + strconv.Itoa(exponent-fraction+len(digits)-len(significant)), nil
}
func NumberText(text string) (string, error) {
	if text == "" {
		return "", fmt.Errorf("empty number")
	}
	// Validate the token independently; canonicalNumber is also used after a JSON lexer.
	if !json.Valid([]byte(text)) {
		return "", Fail("SQL_NONFINITE_NUMBER", "A non-finite numeric result is not supported.")
	}
	n, e := canonicalNumber(text)
	if e != nil {
		return "", e
	}
	if n == "0" {
		return n, nil
	}
	sign := ""
	if n[0] == '-' {
		sign = "-"
		n = n[1:]
	}
	at := strings.IndexByte(n, 'e')
	digits := n[:at]
	exp, _ := strconv.Atoi(n[at+1:])
	if len(digits)+int(math.Abs(float64(exp))) > 20000 {
		return "", OutputLimit()
	}
	if exp >= 0 {
		return sign + digits + strings.Repeat("0", exp), nil
	}
	point := len(digits) + exp
	if point > 0 {
		return sign + digits[:point] + "." + digits[point:], nil
	}
	return sign + "0." + strings.Repeat("0", -point) + digits, nil
}
