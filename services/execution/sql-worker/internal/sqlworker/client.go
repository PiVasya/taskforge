package sqlworker

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net"
	"net/http"
	"net/url"
	"strings"
	"time"
)

type HTTPFailure struct{ Status int }

func (e *HTTPFailure) Error() string { return fmt.Sprintf("internal SQL HTTP status %d", e.Status) }

type InternalClient struct {
	tasks, execution, key string
	http                  *http.Client
}

func internalURL(value string) (string, error) {
	u, e := url.Parse(value)
	if e != nil || !contains([]string{"http", "https"}, u.Scheme) || u.Hostname() == "" || u.User != nil || u.RawQuery != "" || u.Fragment != "" {
		return "", fmt.Errorf("invalid configured internal service URL")
	}
	return strings.TrimRight(value, "/"), nil
}
func NewInternalClient(tasks, execution, key string) (*InternalClient, error) {
	if len(key) < 16 || strings.ContainsAny(key, "\r\n") {
		return nil, fmt.Errorf("TASKFORGE_INTERNAL_KEY is required")
	}
	t, e := internalURL(tasks)
	if e != nil {
		return nil, e
	}
	x, e := internalURL(execution)
	if e != nil {
		return nil, e
	}
	transport := &http.Transport{Proxy: nil, DialContext: (&net.Dialer{Timeout: 3 * time.Second, KeepAlive: 30 * time.Second}).DialContext, TLSHandshakeTimeout: 3 * time.Second, MaxIdleConns: 16, MaxIdleConnsPerHost: 8, MaxConnsPerHost: 24, IdleConnTimeout: 60 * time.Second, ResponseHeaderTimeout: 8 * time.Second, MaxResponseHeaderBytes: 32768}
	return &InternalClient{t, x, key, &http.Client{Transport: transport, CheckRedirect: func(*http.Request, []*http.Request) error { return http.ErrUseLastResponse }}}, nil
}
func (c *InternalClient) Close() { c.http.CloseIdleConnections() }
func (c *InternalClient) request(ctx context.Context, service, method, path string, body, out any, timeout time.Duration) error {
	if !strings.HasPrefix(path, "/api/internal/") || strings.Contains(path, "..") || strings.ContainsAny(path, "\r\n") {
		return Unavailable("Invalid internal SQL route.")
	}
	base := c.tasks
	if service == "execution" {
		base = c.execution
	} else if service != "tasks" {
		return Unavailable("Unknown internal SQL service.")
	}
	var data []byte
	var e error
	if body != nil {
		data, e = json.Marshal(body)
		if e != nil {
			return e
		}
		if len(data) > 6_000_000 {
			return OutputLimit()
		}
	}
	deadline, cancel := context.WithTimeout(ctx, timeout)
	defer cancel()
	req, e := http.NewRequestWithContext(deadline, method, base+path, bytes.NewReader(data))
	if e != nil {
		return Unavailable("Invalid internal SQL request.")
	}
	req.Header.Set("X-Internal-Key", c.key)
	req.Header.Set("Content-Type", "application/json")
	response, e := c.http.Do(req)
	if e != nil {
		return Unavailable("The durable SQL control plane is temporarily unreachable.")
	}
	defer response.Body.Close()
	if response.StatusCode < 200 || response.StatusCode >= 300 {
		return &HTTPFailure{response.StatusCode}
	}
	raw, e := io.ReadAll(io.LimitReader(response.Body, 8_000_001))
	if e != nil {
		return Unavailable("Incomplete SQL control-plane response.")
	}
	if len(raw) > 8_000_000 {
		return OutputLimit()
	}
	if out == nil {
		return nil
	}
	if len(raw) == 0 {
		return Unavailable("Missing SQL control-plane response.")
	}
	if e = DecodeStrict(raw, out); e != nil {
		return Unavailable("Invalid SQL control-plane response contract.")
	}
	return nil
}
func (c *InternalClient) Register(ctx context.Context, registration Registration) (Profile, error) {
	var p Profile
	e := c.request(ctx, "tasks", "POST", "/api/internal/sql/engines/register", registration, &p, 8*time.Second)
	if e != nil {
		return p, e
	}
	if !hashRE.MatchString(p.Fingerprint) || !uuidRE.MatchString(p.ID) || p.Key != registration.Engine || p.Engine != registration.Engine || p.EngineVersion != registration.EngineVersion || p.RuntimeDigest != registration.RuntimeDigest || p.AdapterVersion != registration.AdapterVersion || !sameJSON(p.Settings, registration.Settings) {
		return Profile{}, Unavailable("Runtime registration changed the requested immutable profile.")
	}
	return p, nil
}
func (c *InternalClient) Capabilities(ctx context.Context, worker string, targets []string, concurrency int) error {
	if targets == nil {
		targets = []string{}
	}
	return c.request(ctx, "execution", "POST", "/api/internal/execution/sql-capabilities", map[string]any{"workerId": worker, "targets": targets, "concurrency": concurrency}, nil, 8*time.Second)
}
func (c *InternalClient) Claim(ctx context.Context, worker string, targets []string) (*Job, error) {
	var result struct {
		Job *Job `json:"job"`
	}
	e := c.request(ctx, "execution", "POST", "/api/internal/execution/sql-jobs/claim-next", map[string]any{"workerId": worker, "kinds": []string{"sql-check", "sql-preview", "sql-materialize"}, "targets": targets}, &result, 8*time.Second)
	if e != nil {
		return nil, e
	}
	if result.Job == nil {
		return nil, nil
	}
	j := result.Job
	if !uuidRE.MatchString(j.ID) || j.LeaseToken == nil || !uuidRE.MatchString(*j.LeaseToken) || j.LeaseExpiresAt == nil || j.LeaseExpiresAt.Before(time.Now().Add(5*time.Second)) || j.Status != "running" || !contains(targets, j.Payload.Profile.Fingerprint) || !contains([]string{"sql-check", "sql-preview", "sql-materialize"}, j.Kind) {
		return nil, Unavailable("The durable SQL queue returned an incompatible or expired lease.")
	}
	return j, nil
}
func leaseFailure(e error) error {
	var h *HTTPFailure
	if errors.As(e, &h) && (h.Status == 404 || h.Status == 409) {
		return ErrLostLease
	}
	return e
}
func (c *InternalClient) Renew(ctx context.Context, worker string, j Job) error {
	if !uuidRE.MatchString(j.ID) || j.LeaseToken == nil {
		return ErrLostLease
	}
	return leaseFailure(c.request(ctx, "execution", "POST", "/api/internal/execution/sql-jobs/"+j.ID+"/renew", map[string]any{"workerId": worker, "leaseToken": *j.LeaseToken}, nil, 5*time.Second))
}
func (c *InternalClient) Complete(ctx context.Context, worker string, j Job, out Outcome, duration time.Duration) error {
	if !uuidRE.MatchString(j.ID) || j.LeaseToken == nil {
		return ErrLostLease
	}
	return leaseFailure(c.request(ctx, "execution", "POST", "/api/internal/execution/sql-jobs/"+j.ID+"/complete", map[string]any{"workerId": worker, "leaseToken": *j.LeaseToken, "verdict": out.Verdict, "passed": out.Verdict == "Accepted", "durationMs": duration.Milliseconds(), "result": out.Result}, nil, 8*time.Second))
}
func (c *InternalClient) Warmup(ctx context.Context, target string) ([]Payload, error) {
	if !hashRE.MatchString(target) {
		return nil, Unavailable("Invalid warmup target.")
	}
	var p []Payload
	e := c.request(ctx, "tasks", "GET", "/api/internal/sql/warmup?target="+target, nil, &p, 8*time.Second)
	if len(p) > 2 {
		return nil, Unavailable("The warmup manifest exceeds its bounded contract.")
	}
	return p, e
}
