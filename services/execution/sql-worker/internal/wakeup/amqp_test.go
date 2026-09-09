package wakeup

import (
	"bytes"
	"context"
	"encoding/binary"
	"encoding/json"
	"fmt"
	"io"
	"net"
	"net/http"
	"os"
	"strings"
	"sync/atomic"
	"testing"
	"time"
)

func brokerHandshake(w *wire) error {
	header := make([]byte, 8)
	if _, e := io.ReadFull(w.conn, header); e != nil {
		return e
	}
	if !bytes.Equal(header, []byte{'A', 'M', 'Q', 'P', 0, 0, 9, 1}) {
		return errProtocol
	}
	if e := w.write(method(0, 10, 10, join([]byte{0, 9, 0, 0, 0, 0}, long([]byte("PLAIN")), long([]byte("en_US"))))); e != nil {
		return e
	}
	start, e := w.expect(0, 10, 11)
	if e != nil {
		return e
	}
	c := cursor{b: start}
	c.long()
	mech := c.short()
	credentials := c.long()
	locale := c.short()
	if c.err != nil || len(c.b) != 0 || mech != "PLAIN" || locale != "en_US" || !bytes.Equal(credentials, []byte("\x00user\x00password")) {
		return errProtocol
	}
	tune := make([]byte, 8)
	binary.BigEndian.PutUint32(tune[2:], frameLimit)
	binary.BigEndian.PutUint16(tune[6:], 2)
	if e = w.write(method(0, 10, 30, tune)); e != nil {
		return e
	}
	if _, e = w.expect(0, 10, 31); e != nil {
		return e
	}
	if _, e = w.expect(0, 10, 40); e != nil {
		return e
	}
	if e = w.write(method(0, 10, 41, short(""))); e != nil {
		return e
	}
	if _, e = w.expect(1, 20, 10); e != nil {
		return e
	}
	if e = w.write(method(1, 20, 11, []byte{0, 0, 0, 0})); e != nil {
		return e
	}
	data, e := w.expect(1, 40, 10)
	if e != nil {
		return e
	}
	c = cursor{b: data}
	c.take(2)
	exchange, kind := c.short(), c.short()
	flags := c.take(1)
	c.long()
	if c.err != nil || len(c.b) != 0 || exchange != Exchange || kind != "fanout" || len(flags) != 1 || flags[0] != 2 {
		return errProtocol
	}
	if e = w.write(method(1, 40, 11, nil)); e != nil {
		return e
	}
	data, e = w.expect(1, 50, 10)
	if e != nil {
		return e
	}
	c = cursor{b: data}
	c.take(2)
	queue := c.short()
	flags = c.take(1)
	c.long()
	if c.err != nil || len(c.b) != 0 || queue != "" || len(flags) != 1 || flags[0] != 12 {
		return errProtocol
	}
	if e = w.write(method(1, 50, 11, join(short("amq.test"), make([]byte, 8)))); e != nil {
		return e
	}
	if _, e = w.expect(1, 50, 20); e != nil {
		return e
	}
	if e = w.write(method(1, 50, 21, nil)); e != nil {
		return e
	}
	data, e = w.expect(1, 60, 20)
	if e != nil {
		return e
	}
	c = cursor{b: data}
	c.take(2)
	queue = c.short()
	tag := c.short()
	flags = c.take(1)
	c.long()
	if c.err != nil || len(c.b) != 0 || queue != "amq.test" || tag != "taskforge-sql-wakeup" || len(flags) != 1 || flags[0] != 2 {
		return errProtocol
	}
	return w.write(method(1, 60, 21, short(tag)))
}
func delivery(size uint64) []frame {
	header := make([]byte, 14)
	binary.BigEndian.PutUint16(header, 60)
	binary.BigEndian.PutUint64(header[4:], size)
	return []frame{method(1, 60, 60, join(short("taskforge-sql-wakeup"), make([]byte, 9), short(Exchange), short(""))), {kind: 2, channel: 1, data: header}}
}
func TestAMQPHandshakeWakeupFragmentationAndHeartbeat(t *testing.T) {
	left, right := net.Pipe()
	defer left.Close()
	defer right.Close()
	ctx, cancel := context.WithTimeout(context.Background(), 3*time.Second)
	defer cancel()
	wake := make(chan struct{}, 2)
	done := make(chan error, 1)
	go func() {
		done <- ConsumeConnection(ctx, left, Config{"localhost", "5672", "/", "user", "password"}, func() { wake <- struct{}{} }, func() {})
	}()
	w := &wire{conn: right, max: frameLimit, heartbeat: 3 * time.Second}
	if e := brokerHandshake(w); e != nil {
		t.Fatal(e)
	}
	frames := append(delivery(4), frame{kind: 3, channel: 1, data: []byte("{")}, frame{kind: 8}, frame{kind: 3, channel: 1, data: []byte("}xx")})
	for _, f := range frames {
		if e := w.write(f); e != nil {
			t.Fatal(e)
		}
	}
	select {
	case <-wake:
	case <-ctx.Done():
		t.Fatal("missing complete wakeup")
	}
	for _, f := range delivery(0) {
		if e := w.write(f); e != nil {
			t.Fatal(e)
		}
	}
	select {
	case <-wake:
	case <-ctx.Done():
		t.Fatal("missing empty body wakeup")
	}
	cancel()
	select {
	case <-done:
	case <-time.After(time.Second):
		t.Fatal("AMQP shutdown leaked goroutine")
	}
}
func TestAMQPRejectsUnexpectedFramesAndOversizedBodies(t *testing.T) {
	cases := map[string][]frame{
		"body without method":         {{kind: 3, channel: 1, data: []byte("x")}},
		"too large body":              delivery(bodyLimit + 1),
		"second delivery before body": append(delivery(2), delivery(0)[0]),
		"body overflow":               append(delivery(1), frame{kind: 3, channel: 1, data: []byte("xx")}),
	}
	for name, frames := range cases {
		t.Run(name, func(t *testing.T) {
			left, right := net.Pipe()
			defer left.Close()
			defer right.Close()
			w := &wire{conn: left, max: frameLimit, heartbeat: time.Second}
			other := &wire{conn: right, max: frameLimit, heartbeat: time.Second}
			done := make(chan error, 1)
			go func() { done <- w.consume(func() { t.Error("invalid message woke worker") }) }()
			for _, f := range frames {
				if e := other.write(f); e != nil {
					t.Fatal(e)
				}
			}
			if e := <-done; e == nil {
				t.Fatal("invalid frame accepted")
			}
		})
	}
}
func TestAMQPFrameHeaderLimitBeforeAllocation(t *testing.T) {
	for _, kind := range []byte{1, 8, 99} {
		left, right := net.Pipe()
		done := make(chan error, 1)
		go func() { w := &wire{conn: left, max: frameLimit, heartbeat: time.Second}; _, e := w.read(); done <- e }()
		header := make([]byte, 7)
		header[0] = kind
		binary.BigEndian.PutUint32(header[3:], frameLimit+1)
		_, _ = right.Write(header)
		if e := <-done; e == nil {
			t.Fatal("oversized header accepted")
		}
		left.Close()
		right.Close()
	}
}
func FuzzAMQPBoundedCursor(f *testing.F) {
	for _, v := range [][]byte{{}, {0}, {255}, {0, 0, 0, 4, 1, 2, 3, 4}} {
		f.Add(v)
	}
	f.Fuzz(func(t *testing.T, b []byte) {
		if len(b) > 4096 {
			return
		}
		c := cursor{b: b}
		_ = c.short()
		_ = c.long()
		_ = c.take(16)
		_ = c.long()
	})
}

// The Docker engine gate also starts a disposable RabbitMQ, never a second
// production broker. A management publish mirrors the existing C# publisher.
func TestRealRabbitWakeup(t *testing.T) {
	if os.Getenv("SQL_TEST_ENGINE_GATE") != "1" {
		t.Skip("requires the isolated Docker RabbitMQ gate")
	}
	host := os.Getenv("SQL_TEST_RABBIT_HOST")
	if host == "" {
		t.Fatal("SQL_TEST_RABBIT_HOST is required")
	}
	password := os.Getenv("SQL_TEST_PASSWORD")
	ctx, cancel := context.WithTimeout(context.Background(), 20*time.Second)
	defer cancel()
	connected := make(chan struct{}, 1)
	wake := make(chan struct{}, 1)
	var state atomic.Bool
	done := make(chan struct{})
	go func() {
		defer close(done)
		Listen(ctx, Config{host, "5672", "/", "taskforge", password}, func() {
			select {
			case wake <- struct{}{}:
			default:
			}
		}, func(ok bool) {
			state.Store(ok)
			if ok {
				select {
				case connected <- struct{}{}:
				default:
				}
			}
		})
	}()
	select {
	case <-connected:
	case <-ctx.Done():
		t.Fatal("RabbitMQ AMQP handshake failed")
	}
	body := `{"properties":{},"routing_key":"","payload":"{}","payload_encoding":"string"}`
	req, e := http.NewRequestWithContext(ctx, "POST", fmt.Sprintf("http://%s:15672/api/exchanges/%%2F/%s/publish", host, Exchange), strings.NewReader(body))
	if e != nil {
		t.Fatal(e)
	}
	req.SetBasicAuth("taskforge", password)
	req.Header.Set("Content-Type", "application/json")
	client := &http.Client{Transport: &http.Transport{Proxy: nil}, Timeout: 5 * time.Second}
	defer client.CloseIdleConnections()
	res, e := client.Do(req)
	if e != nil {
		t.Fatal(e)
	}
	defer res.Body.Close()
	var result struct {
		Routed bool `json:"routed"`
	}
	if e = json.NewDecoder(io.LimitReader(res.Body, 1024)).Decode(&result); e != nil || res.StatusCode != 200 || !result.Routed {
		t.Fatal("management publish not routed", e, res.StatusCode)
	}
	select {
	case <-wake:
	case <-ctx.Done():
		t.Fatal("real RabbitMQ message did not wake consumer")
	}
	cancel()
	select {
	case <-done:
	case <-time.After(2 * time.Second):
		t.Fatal("RabbitMQ consumer did not stop")
	}
}
