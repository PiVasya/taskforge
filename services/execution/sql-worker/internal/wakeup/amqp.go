// Package wakeup implements the bounded AMQP 0-9-1 consumer subset needed for
// RabbitMQ fanout notifications. It does NOT implement a durable job queue, an
// application message parser, publishing, confirms or transaction semantics.
// See docs/sql/GO_RUNTIME.md for the wire subset and interoperability gate.
package wakeup

import (
	"context"
	"encoding/binary"
	"errors"
	"fmt"
	"io"
	"net"
	"strings"
	"sync"
	"time"
)

const Exchange = "taskforge.sql.wakeup"
const frameLimit = 131072
const bodyLimit = 65536

var errProtocol = errors.New("invalid or unsupported AMQP wakeup frame")

type Config struct{ Host, Port, VHost, User, Password string }

func (c Config) Validate() error {
	if c.Host == "" || c.Port == "" || c.User == "" || len(c.User) > 255 || len(c.Password) > 4096 || len(c.VHost) > 255 {
		return fmt.Errorf("invalid RabbitMQ wakeup configuration")
	}
	for _, s := range []string{c.Host, c.Port, c.User, c.Password, c.VHost} {
		if strings.ContainsRune(s, 0) {
			return fmt.Errorf("invalid RabbitMQ wakeup configuration")
		}
	}
	return nil
}

type frame struct {
	kind    byte
	channel uint16
	data    []byte
}
type wire struct {
	conn      net.Conn
	mu        sync.Mutex
	max       uint32
	heartbeat time.Duration
}

func (w *wire) read() (frame, error) {
	_ = w.conn.SetReadDeadline(time.Now().Add(w.heartbeat))
	var head [7]byte
	if _, e := io.ReadFull(w.conn, head[:]); e != nil {
		return frame{}, e
	}
	n := binary.BigEndian.Uint32(head[3:])
	if n > w.max-8 {
		return frame{}, errProtocol
	}
	kind := head[0]
	channel := binary.BigEndian.Uint16(head[1:])
	if channel > 1 || !containsByte([]byte{1, 2, 3, 8}, kind) {
		return frame{}, errProtocol
	}
	if kind == 8 && (channel != 0 || n != 0) {
		return frame{}, errProtocol
	}
	data := make([]byte, n+1)
	if _, e := io.ReadFull(w.conn, data); e != nil {
		return frame{}, e
	}
	if data[n] != 0xce {
		return frame{}, errProtocol
	}
	return frame{kind, channel, data[:n]}, nil
}
func containsByte(a []byte, v byte) bool {
	for _, x := range a {
		if x == v {
			return true
		}
	}
	return false
}
func (w *wire) write(f frame) error {
	if len(f.data) > int(w.max)-8 {
		return errProtocol
	}
	w.mu.Lock()
	defer w.mu.Unlock()
	_ = w.conn.SetWriteDeadline(time.Now().Add(5 * time.Second))
	data := make([]byte, 8+len(f.data))
	data[0] = f.kind
	binary.BigEndian.PutUint16(data[1:], f.channel)
	binary.BigEndian.PutUint32(data[3:], uint32(len(f.data)))
	copy(data[7:], f.data)
	data[len(data)-1] = 0xce
	for len(data) > 0 {
		n, e := w.conn.Write(data)
		if e != nil {
			return e
		}
		if n == 0 {
			return io.ErrShortWrite
		}
		data = data[n:]
	}
	return nil
}
func method(channel, class, method uint16, fields []byte) frame {
	data := make([]byte, 4, len(fields)+4)
	binary.BigEndian.PutUint16(data, class)
	binary.BigEndian.PutUint16(data[2:], method)
	data = append(data, fields...)
	return frame{1, channel, data}
}
func (w *wire) expect(channel, class, meth uint16) ([]byte, error) {
	for {
		f, e := w.read()
		if e != nil {
			return nil, e
		}
		if f.kind == 8 {
			continue
		}
		if f.kind != 1 || f.channel != channel || len(f.data) < 4 || binary.BigEndian.Uint16(f.data) != class || binary.BigEndian.Uint16(f.data[2:]) != meth {
			return nil, errProtocol
		}
		return f.data[4:], nil
	}
}
func short(v string) []byte { return append([]byte{byte(len(v))}, []byte(v)...) }
func long(v []byte) []byte {
	b := make([]byte, 4, len(v)+4)
	binary.BigEndian.PutUint32(b, uint32(len(v)))
	return append(b, v...)
}
func join(v ...[]byte) []byte {
	var out []byte
	for _, b := range v {
		out = append(out, b...)
	}
	return out
}

type cursor struct {
	b   []byte
	err error
}

func (c *cursor) take(n int) []byte {
	if c.err != nil || n < 0 || n > len(c.b) {
		c.err = errProtocol
		return nil
	}
	b := c.b[:n]
	c.b = c.b[n:]
	return b
}
func (c *cursor) u32() uint32 {
	b := c.take(4)
	if b == nil {
		return 0
	}
	return binary.BigEndian.Uint32(b)
}
func (c *cursor) short() string {
	b := c.take(1)
	if b == nil {
		return ""
	}
	return string(c.take(int(b[0])))
}
func (c *cursor) long() []byte { return c.take(int(c.u32())) }

func (w *wire) handshake(cfg Config) error {
	_ = w.conn.SetWriteDeadline(time.Now().Add(5 * time.Second))
	if _, e := w.conn.Write([]byte{'A', 'M', 'Q', 'P', 0, 0, 9, 1}); e != nil {
		return e
	}
	data, e := w.expect(0, 10, 10)
	if e != nil {
		return e
	}
	c := cursor{b: data}
	version := c.take(2)
	c.long()
	mechanisms, locales := string(c.long()), string(c.long())
	if c.err != nil || len(c.b) != 0 || len(version) != 2 || version[0] != 0 || version[1] != 9 || !containsWord(mechanisms, "PLAIN") || !containsWord(locales, "en_US") {
		return errProtocol
	}
	response := []byte("\x00" + cfg.User + "\x00" + cfg.Password)
	if e = w.write(method(0, 10, 11, join([]byte{0, 0, 0, 0}, short("PLAIN"), long(response), short("en_US")))); e != nil {
		return e
	}
	tune, e := w.expect(0, 10, 30)
	if e != nil {
		return e
	}
	if len(tune) != 8 {
		return errProtocol
	}
	channels := binary.BigEndian.Uint16(tune)
	if channels != 0 && channels < 1 {
		return errProtocol
	}
	serverMax := binary.BigEndian.Uint32(tune[2:])
	if serverMax != 0 {
		if serverMax < 4096 {
			return errProtocol
		}
		if serverMax < w.max {
			w.max = serverMax
		}
	}
	heartbeat := binary.BigEndian.Uint16(tune[6:])
	if heartbeat == 0 || heartbeat > 30 {
		heartbeat = 30
	}
	w.heartbeat = time.Duration(heartbeat) * time.Second
	reply := make([]byte, 8)
	binary.BigEndian.PutUint16(reply, 1)
	binary.BigEndian.PutUint32(reply[2:], w.max)
	binary.BigEndian.PutUint16(reply[6:], heartbeat)
	if e = w.write(method(0, 10, 31, reply)); e != nil {
		return e
	}
	if e = w.write(method(0, 10, 40, join(short(cfg.VHost), short(""), []byte{0}))); e != nil {
		return e
	}
	if _, e = w.expect(0, 10, 41); e != nil {
		return e
	}
	if e = w.write(method(1, 20, 10, short(""))); e != nil {
		return e
	}
	if _, e = w.expect(1, 20, 11); e != nil {
		return e
	}
	// exchange.declare: reserved=0, fanout, durable=true, other bits false.
	if e = w.write(method(1, 40, 10, join([]byte{0, 0}, short(Exchange), short("fanout"), []byte{2, 0, 0, 0, 0}))); e != nil {
		return e
	}
	if _, e = w.expect(1, 40, 11); e != nil {
		return e
	}
	// Server-named, exclusive, auto-delete queue; never touches durable job storage.
	if e = w.write(method(1, 50, 10, join([]byte{0, 0}, short(""), []byte{12, 0, 0, 0, 0}))); e != nil {
		return e
	}
	data, e = w.expect(1, 50, 11)
	if e != nil {
		return e
	}
	c = cursor{b: data}
	queue := c.short()
	c.take(8)
	if c.err != nil || len(c.b) != 0 || queue == "" {
		return errProtocol
	}
	if e = w.write(method(1, 50, 20, join([]byte{0, 0}, short(queue), short(Exchange), short(""), []byte{0, 0, 0, 0, 0}))); e != nil {
		return e
	}
	if _, e = w.expect(1, 50, 21); e != nil {
		return e
	}
	if e = w.write(method(1, 60, 20, join([]byte{0, 0}, short(queue), short("taskforge-sql-wakeup"), []byte{2, 0, 0, 0, 0}))); e != nil {
		return e
	}
	data, e = w.expect(1, 60, 21)
	if e != nil {
		return e
	}
	c = cursor{b: data}
	tag := c.short()
	if c.err != nil || len(c.b) != 0 || tag != "taskforge-sql-wakeup" {
		return errProtocol
	}
	return nil
}
func containsWord(list, word string) bool {
	for _, v := range strings.Fields(list) {
		if v == word {
			return true
		}
	}
	return false
}
func (w *wire) consume(onWake func()) error {
	state := 0
	var remaining uint64
	for {
		f, e := w.read()
		if e != nil {
			return e
		}
		if f.kind == 8 {
			continue
		}
		if f.kind == 1 {
			if len(f.data) < 4 {
				return errProtocol
			}
			class, meth := binary.BigEndian.Uint16(f.data), binary.BigEndian.Uint16(f.data[2:])
			if f.channel == 0 && class == 10 && (meth == 60 || meth == 61) {
				continue
			} // Connection blocked/unblocked extension.
			if state != 0 || f.channel != 1 || class != 60 || meth != 60 {
				return errProtocol
			}
			c := cursor{b: f.data[4:]}
			tag := c.short()
			c.take(9)
			exchange := c.short()
			c.short()
			if c.err != nil || len(c.b) != 0 || tag != "taskforge-sql-wakeup" || exchange != Exchange {
				return errProtocol
			}
			state = 1
			continue
		}
		if f.kind == 2 {
			if state != 1 || f.channel != 1 || len(f.data) < 14 || binary.BigEndian.Uint16(f.data) != 60 || binary.BigEndian.Uint16(f.data[2:]) != 0 {
				return errProtocol
			}
			remaining = binary.BigEndian.Uint64(f.data[4:])
			if remaining > bodyLimit {
				return errProtocol
			}
			state = 2
			if remaining == 0 {
				state = 0
				onWake()
			}
			continue
		}
		if f.kind == 3 {
			if state != 2 || f.channel != 1 || len(f.data) == 0 || uint64(len(f.data)) > remaining {
				return errProtocol
			}
			remaining -= uint64(len(f.data))
			if remaining == 0 {
				state = 0
				onWake()
			}
			continue
		}
		return errProtocol
	}
}
func ConsumeConnection(ctx context.Context, conn net.Conn, cfg Config, onWake func(), onConnected func()) error {
	if e := cfg.Validate(); e != nil {
		conn.Close()
		return e
	}
	defer conn.Close()
	w := &wire{conn: conn, max: frameLimit, heartbeat: 10 * time.Second}
	stop := make(chan struct{})
	defer close(stop)
	go func() {
		select {
		case <-ctx.Done():
			conn.Close()
		case <-stop:
		}
	}()
	if e := w.handshake(cfg); e != nil {
		return e
	}
	onConnected()
	heartbeats := make(chan struct{})
	var heartbeatDone sync.WaitGroup
	heartbeatDone.Add(1)
	go func() {
		defer heartbeatDone.Done()
		tick := time.NewTicker(w.heartbeat / 2)
		defer tick.Stop()
		for {
			select {
			case <-heartbeats:
				return
			case <-ctx.Done():
				return
			case <-tick.C:
				if w.write(frame{kind: 8}) != nil {
					conn.Close()
					return
				}
			}
		}
	}()
	e := w.consume(onWake)
	close(heartbeats)
	heartbeatDone.Wait()
	return e
}
func Listen(ctx context.Context, cfg Config, onWake func(), connected func(bool)) {
	if cfg.Validate() != nil {
		connected(false)
		return
	}
	delay := time.Second
	for ctx.Err() == nil {
		conn, e := (&net.Dialer{Timeout: 3 * time.Second, KeepAlive: 15 * time.Second}).DialContext(ctx, "tcp", net.JoinHostPort(cfg.Host, cfg.Port))
		if e == nil {
			_ = ConsumeConnection(ctx, conn, cfg, onWake, func() { delay = time.Second; connected(true) })
		}
		connected(false)
		timer := time.NewTimer(delay)
		select {
		case <-ctx.Done():
			timer.Stop()
			return
		case <-timer.C:
		}
		delay = min(delay*2, 15*time.Second)
	}
}
