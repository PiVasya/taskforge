package sqlworker

import (
	"fmt"
	"sort"
	"strings"
	"sync"
	"taskforge/sqlworker/internal/native"
	"time"
)

type metricKey struct{ name, engine string }
type Metrics struct {
	mu     sync.Mutex
	values map[metricKey]float64
}

func NewMetrics() *Metrics { return &Metrics{values: map[metricKey]float64{}} }
func (m *Metrics) Inc(name, engine string, n float64) {
	if m == nil {
		return
	}
	m.mu.Lock()
	defer m.mu.Unlock()
	m.values[metricKey{name, engine}] += n
}
func (m *Metrics) Set(name, engine string, n float64) {
	if m == nil {
		return
	}
	m.mu.Lock()
	defer m.mu.Unlock()
	m.values[metricKey{name, engine}] = n
}
func (m *Metrics) Measure(name, engine string) func() {
	start := time.Now()
	return func() {
		m.Inc(name+"_seconds_sum", engine, time.Since(start).Seconds())
		m.Inc(name+"_seconds_count", engine, 1)
	}
}
func (m *Metrics) Render() string {
	m.mu.Lock()
	defer m.mu.Unlock()
	keys := make([]metricKey, 0, len(m.values))
	for k := range m.values {
		keys = append(keys, k)
	}
	sort.Slice(keys, func(i, j int) bool {
		if keys[i].name == keys[j].name {
			return keys[i].engine < keys[j].engine
		}
		return keys[i].name < keys[j].name
	})
	var b strings.Builder
	for _, k := range keys {
		fmt.Fprintf(&b, "%s{engine=%q} %g\n", k.name, k.engine, m.values[k])
	}
	connections := native.ConnectionCounts()
	for _, engine := range []string{"postgresql", "mysql", "sqlite"} {
		fmt.Fprintf(&b, "sql_engine_connections{engine=%q,scope=\"coordinator\"} %d\n", engine, connections[engine])
	}
	return b.String()
}
