package sqlworker

import (
	"context"
	"encoding/json"
	"errors"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strings"
	"sync"
	"taskforge/sqlworker/internal/native"
	"testing"
	"time"
)

func testClient(t *testing.T, h http.HandlerFunc) *InternalClient {
	t.Helper()
	s := httptest.NewServer(h)
	t.Cleanup(s.Close)
	c, e := NewInternalClient(s.URL, s.URL, "test-internal-secret-12345")
	if e != nil {
		t.Fatal(e)
	}
	t.Cleanup(c.Close)
	return c
}
func TestInternalClientRejectsRedirectAndLeaksNoSecret(t *testing.T) {
	hits := 0
	other := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) { hits++ }))
	defer other.Close()
	c := testClient(t, func(w http.ResponseWriter, r *http.Request) {
		if r.Header.Get("X-Internal-Key") == "" {
			t.Error("missing key")
		}
		http.Redirect(w, r, other.URL, http.StatusFound)
	})
	e := c.Capabilities(context.Background(), "test", []string{}, 2)
	if e == nil || hits != 0 || strings.Contains(e.Error(), "secret") {
		t.Fatal("unsafe redirect or leaked credential", e, hits)
	}
}
func TestInternalClientStrictResponseAndErrorRedaction(t *testing.T) {
	for _, body := range []string{`{"job":null,"job":null}`, `{"job":null}garbage`, `{"job":null,"unexpected":true}`} {
		t.Run(body, func(t *testing.T) {
			c := testClient(t, func(w http.ResponseWriter, r *http.Request) { _, _ = w.Write([]byte(body)) })
			if _, e := c.Claim(context.Background(), "test", []string{textHash("x")}); e == nil {
				t.Fatal("invalid response accepted")
			}
		})
	}
	c := testClient(t, func(w http.ResponseWriter, r *http.Request) {
		w.WriteHeader(500)
		_, _ = w.Write([]byte("database_password=private-secret"))
	})
	if e := c.Capabilities(context.Background(), "test", nil, 2); e == nil || strings.Contains(e.Error(), "secret") {
		t.Fatal(e)
	}
}
func TestInternalClientRejectsForeignKindProfileAndExpiredLease(t *testing.T) {
	for _, change := range []func(*Job){func(j *Job) { j.Kind = "code" }, func(j *Job) { j.Payload.Profile.Fingerprint = textHash("other") }, func(j *Job) { j.LeaseExpiresAt = ptr(time.Now().Add(-time.Second)) }} {
		j := Job{ID: "10000000-0000-4000-8000-000000000001", Kind: "sql-check", Status: "running", LeaseToken: ptr("10000000-0000-4000-8000-000000000002"), LeaseExpiresAt: ptr(time.Now().Add(45 * time.Second)), Payload: fixture(Profile{Fingerprint: textHash("profile")})}
		change(&j)
		c := testClient(t, func(w http.ResponseWriter, r *http.Request) { _ = json.NewEncoder(w).Encode(map[string]any{"job": j}) })
		if _, e := c.Claim(context.Background(), "test", []string{textHash("profile")}); e == nil {
			t.Fatal("incompatible job accepted")
		}
	}
}
func TestLeaseFencesOnConflictAndNotFound(t *testing.T) {
	for _, status := range []int{404, 409} {
		c := testClient(t, func(w http.ResponseWriter, r *http.Request) { w.WriteHeader(status) })
		j := Job{ID: "10000000-0000-4000-8000-000000000001", LeaseToken: ptr("10000000-0000-4000-8000-000000000002")}
		if !errors.Is(c.Renew(context.Background(), "test", j), ErrLostLease) || !errors.Is(c.Complete(context.Background(), "test", j, Outcome{}, 0), ErrLostLease) {
			t.Fatal("lease conflict not fenced")
		}
	}
}
func TestProfileRegistrationCannotChangeRuntimeIdentity(t *testing.T) {
	reg := Registration{Engine: "sqlite", EngineVersion: "3", RuntimeDigest: "sha256:" + textHash("runtime"), AdapterVersion: AdapterVersion, Settings: map[string]any{"executor": "one"}}
	profile := Profile{ID: "10000000-0000-4000-8000-000000000001", Key: "sqlite", Engine: reg.Engine, EngineVersion: reg.EngineVersion, RuntimeDigest: reg.RuntimeDigest, AdapterVersion: reg.AdapterVersion, Settings: reg.Settings, Fingerprint: textHash("profile")}
	good := true
	c := testClient(t, func(w http.ResponseWriter, r *http.Request) {
		if r.Method == http.MethodGet && strings.HasSuffix(r.URL.Path, "/compatible") {
			_ = json.NewEncoder(w).Encode([]Profile{profile})
			return
		}
		copy := profile
		if !good {
			copy.Settings = map[string]any{"executor": "two"}
		}
		_ = json.NewEncoder(w).Encode(copy)
	})
	registered, e := c.Register(context.Background(), reg)
	if e != nil {
		t.Fatal(e)
	}
	if !registered.CompatibilityConfirmed {
		t.Fatal("successful compatibility lookup was not marked authoritative")
	}
	good = false
	if _, e := c.Register(context.Background(), reg); e == nil {
		t.Fatal("different profile accepted")
	}
}
func TestProfileRegistrationRollingUpgradeFallsBackToExactProfile(t *testing.T) {
	reg := Registration{Engine: "sqlite", EngineVersion: "3", RuntimeDigest: "sha256:" + textHash("runtime"), AdapterVersion: AdapterVersion, Settings: map[string]any{"semantic": "one"}}
	profile := Profile{ID: "10000000-0000-4000-8000-000000000001", Key: "sqlite", Engine: reg.Engine, EngineVersion: reg.EngineVersion, RuntimeDigest: reg.RuntimeDigest, AdapterVersion: reg.AdapterVersion, Settings: reg.Settings, Fingerprint: textHash("profile")}
	c := testClient(t, func(w http.ResponseWriter, r *http.Request) {
		if r.Method == http.MethodGet {
			w.WriteHeader(http.StatusNotFound)
			return
		}
		_ = json.NewEncoder(w).Encode(profile)
	})
	registered, err := c.Register(context.Background(), reg)
	if err != nil {
		t.Fatal(err)
	}
	if registered.Current.Fingerprint != profile.Fingerprint || len(registered.Compatible) != 0 || registered.CompatibilityConfirmed {
		t.Fatal("old Tasks API rolling-upgrade fallback must advertise only the exact unconfirmed profile")
	}
}

func TestProfileRegistrationRejectsForeignCompatibilityAlias(t *testing.T) {
	reg := Registration{Engine: "sqlite", EngineVersion: "3", RuntimeDigest: "sha256:" + textHash("runtime"), AdapterVersion: AdapterVersion, Settings: map[string]any{"semantic": "one"}}
	profile := Profile{ID: "10000000-0000-4000-8000-000000000001", Key: "sqlite", Engine: reg.Engine, EngineVersion: reg.EngineVersion, RuntimeDigest: reg.RuntimeDigest, AdapterVersion: reg.AdapterVersion, Settings: reg.Settings, Fingerprint: textHash("profile")}
	foreign := profile
	foreign.ID = "10000000-0000-4000-8000-000000000002"
	foreign.Engine = "mysql"
	foreign.Key = "mysql"
	foreign.Fingerprint = textHash("foreign")
	c := testClient(t, func(w http.ResponseWriter, r *http.Request) {
		if r.Method == http.MethodGet {
			_ = json.NewEncoder(w).Encode([]Profile{foreign})
			return
		}
		_ = json.NewEncoder(w).Encode(profile)
	})
	if _, err := c.Register(context.Background(), reg); err == nil {
		t.Fatal("foreign engine compatibility alias was accepted")
	}
}

func TestAdministrationErrorCannotLeakGeneratedPassword(t *testing.T) {
	e := administrationFailure(&native.Error{Engine: "mysql", Code: "1064", Message: "CREATE USER example IDENTIFIED BY 'do-not-leak'"})
	if e == nil || strings.Contains(e.Error(), "do-not-leak") {
		t.Fatal("administration secret leaked")
	}
	for _, code := range []string{"3D000", "1049"} {
		f := NormalizeFailure(administrationFailure(&native.Error{Code: code}))
		if f.Code != "SQL_CACHE_RECOVERY_REQUIRED" {
			t.Fatal("cache miss not recoverable")
		}
	}
}
func TestNamespaceLockCompatibleAndExclusive(t *testing.T) {
	root := t.TempDir()
	a, e := lockNamespace(root, "sql:a")
	if e != nil {
		t.Fatal(e)
	}
	defer a.Close()
	if _, e = os.Stat(filepath.Join(root, "sql_a.lock")); e != nil {
		t.Fatal("previous worker lock compatibility lost")
	}
	if b, e := lockNamespace(root, "sql:a"); e == nil {
		b.Close()
		t.Fatal("second worker obtained same namespace")
	}
	b, e := lockNamespace(root, "sql:b")
	if e != nil {
		t.Fatal(e)
	}
	b.Close()
}

type fakeProfiles struct {
	mu         sync.Mutex
	fail       bool
	compatible []Profile
}

func (c *fakeProfiles) Register(ctx context.Context, r Registration) (RegisteredProfiles, error) {
	c.mu.Lock()
	defer c.mu.Unlock()
	if c.fail {
		return RegisteredProfiles{}, errors.New("API unavailable")
	}
	h, _ := ContentHash(r)
	current := Profile{ID: "10000000-0000-4000-8000-000000000001", Key: r.Engine, Engine: r.Engine, EngineVersion: r.EngineVersion, RuntimeDigest: r.RuntimeDigest, AdapterVersion: r.AdapterVersion, Settings: r.Settings, Fingerprint: h}
	return RegisteredProfiles{Current: current, Compatible: append([]Profile(nil), c.compatible...), CompatibilityConfirmed: true}, nil
}
func TestRegistryAdvertisesAndAcquiresCompatibleImmutableProfile(t *testing.T) {
	a := &fakeAdapter{}
	pool, _ := fakePool(t, a, nil)
	legacy := Profile{
		ID: "10000000-0000-4000-8000-000000000099", Key: "fake", Engine: "fake",
		EngineVersion: "1", RuntimeDigest: "sha256:" + textHash("legacy-runtime"), AdapterVersion: AdapterVersion,
		Settings: map[string]any{"semantic": "legacy-contract"}, Fingerprint: textHash("legacy-compatible-profile"),
	}
	c := &fakeProfiles{compatible: []Profile{legacy}}
	r := NewRegistry([]EngineAdapter{a}, c, pool, NewMetrics())
	r.Refresh(context.Background())
	targets := r.Targets()
	if len(targets) != 2 || !contains(targets, legacy.Fingerprint) {
		t.Fatalf("compatible immutable profile was not advertised: %v", targets)
	}
	adapter, profile, release, err := r.Acquire(legacy.Fingerprint)
	if err != nil {
		t.Fatal(err)
	}
	defer release()
	if adapter != a || profile.Fingerprint != legacy.Fingerprint || !sameJSON(profile, legacy) {
		t.Fatal("compatible target did not preserve the exact historical profile contract")
	}
}

func TestRegistryKeepsConfirmedAliasesAcrossTransientCompatibilityLookupFailure(t *testing.T) {
	a := &fakeAdapter{}
	pool, _ := fakePool(t, a, nil)
	legacy := Profile{ID: "10000000-0000-4000-8000-000000000099", Key: "fake", Engine: "fake", EngineVersion: "1", RuntimeDigest: "sha256:" + textHash("legacy-runtime"), AdapterVersion: AdapterVersion, Settings: map[string]any{"semantic": "legacy-contract"}, Fingerprint: textHash("legacy-compatible-profile")}
	c := &compatibilityFlapProfiles{authoritative: true, compatible: []Profile{legacy}}
	r := NewRegistry([]EngineAdapter{a}, c, pool, NewMetrics())
	r.Refresh(context.Background())
	if !contains(r.Targets(), legacy.Fingerprint) {
		t.Fatal("initial confirmed compatibility alias missing")
	}
	c.mu.Lock()
	c.authoritative = false
	c.mu.Unlock()
	r.Refresh(context.Background())
	if !contains(r.Targets(), legacy.Fingerprint) {
		t.Fatal("transient compatibility lookup failure flapped a previously confirmed alias offline")
	}
}

type compatibilityFlapProfiles struct {
	mu            sync.Mutex
	authoritative bool
	compatible    []Profile
}

func (c *compatibilityFlapProfiles) Register(ctx context.Context, r Registration) (RegisteredProfiles, error) {
	c.mu.Lock()
	defer c.mu.Unlock()
	h, _ := ContentHash(r)
	current := Profile{ID: "10000000-0000-4000-8000-000000000001", Key: r.Engine, Engine: r.Engine, EngineVersion: r.EngineVersion, RuntimeDigest: r.RuntimeDigest, AdapterVersion: r.AdapterVersion, Settings: r.Settings, Fingerprint: h}
	if c.authoritative {
		return RegisteredProfiles{Current: current, Compatible: append([]Profile(nil), c.compatible...), CompatibilityConfirmed: true}, nil
	}
	// Exact registration still works, but the compatibility lookup is temporarily
	// unavailable. Registry must retain its last confirmed immutable alias set.
	return RegisteredProfiles{Current: current, CompatibilityConfirmed: false}, nil
}

func TestRegistryControlPlaneOutageDoesNotDestroyCache(t *testing.T) {
	a := &fakeAdapter{}
	pool, _ := fakePool(t, a, nil)
	c := &fakeProfiles{}
	r := NewRegistry([]EngineAdapter{a}, c, pool, NewMetrics())
	r.Refresh(context.Background())
	targets := r.Targets()
	if len(targets) != 1 {
		t.Fatal("registration failed")
	}
	a1, p, release, e := r.Acquire(targets[0])
	if e != nil {
		t.Fatal(e)
	}
	v := fixture(p)
	l, e := pool.Acquire(context.Background(), a1, v)
	if e != nil {
		t.Fatal(e)
	}
	_ = l.Release()
	release()
	c.mu.Lock()
	c.fail = true
	c.mu.Unlock()
	r.Refresh(context.Background())
	if len(r.Targets()) != 0 {
		t.Fatal("failed API still advertised")
	}
	c.mu.Lock()
	c.fail = false
	c.mu.Unlock()
	r.Refresh(context.Background())
	a.mu.Lock()
	defer a.mu.Unlock()
	if a.startups != 1 || a.drops != 0 || a.prepares != 1 {
		t.Fatalf("API outage wiped engine cache: startup=%d drop=%d prepare=%d", a.startups, a.drops, a.prepares)
	}
}
func TestRegistryRecoveryWaitsForActiveJob(t *testing.T) {
	a := &fakeAdapter{}
	pool, _ := fakePool(t, a, nil)
	r := NewRegistry([]EngineAdapter{a}, &fakeProfiles{}, pool, NewMetrics())
	r.Refresh(context.Background())
	target := r.Targets()[0]
	_, _, release, e := r.Acquire(target)
	if e != nil {
		t.Fatal(e)
	}
	r.Invalidate(target)
	r.Refresh(context.Background())
	a.mu.Lock()
	n := a.startups
	a.mu.Unlock()
	if n != 1 {
		t.Fatal("startup cleanup raced active job")
	}
	release()
	release()
	r.Refresh(context.Background())
	if len(r.Targets()) != 1 {
		t.Fatal("runtime did not recover")
	}
	a.mu.Lock()
	defer a.mu.Unlock()
	if a.startups != 2 {
		t.Fatal("missing engine cleanup on recovery")
	}
}

func TestRegistryTransientProbeFailureDoesNotResetHealthyRuntime(t *testing.T) {
	a := &fakeAdapter{}
	pool, _ := fakePool(t, a, nil)
	r := NewRegistry([]EngineAdapter{a}, &fakeProfiles{}, pool, NewMetrics())
	r.Refresh(context.Background())
	if len(r.Targets()) != 1 {
		t.Fatal("initial registration failed")
	}
	a.mu.Lock()
	a.probeFailuresRemaining = 1
	a.mu.Unlock()
	r.Refresh(context.Background())
	if len(r.Targets()) != 1 {
		t.Fatal("single transient probe removed healthy target")
	}
	a.mu.Lock()
	defer a.mu.Unlock()
	if a.startups != 1 || a.drops != 0 {
		t.Fatalf("transient probe reset runtime cache: startup=%d drop=%d", a.startups, a.drops)
	}
}
