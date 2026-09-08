import React, { useMemo, useState } from 'react';
import { Activity, AlertTriangle, ArrowDown, Box, CheckCircle2, Info, Search, XCircle } from 'lucide-react';
import { array, dateTime, IMAGE_LABELS, imageCell, primaryRole, servicesMatrix } from './clusterModel';
import { Empty, Tag } from './ClusterShared';

export function ImageMatrix({ nodes }) {
  const [query, setQuery] = useState('');
  const [onlyIssues, setOnlyIssues] = useState(false);
  const rows = useMemo(() => servicesMatrix(nodes), [nodes]);
  const filtered = rows.filter(row => row.name.toLowerCase().includes(query.toLowerCase()) && (!onlyIssues || Object.values(row.cells).some(c => ['missing', 'different'].includes(c.imageStatus))));
  const unverified = rows.some(row => Object.values(row.cells).some(c => c.assigned && ['unverified', 'unknown'].includes(c.imageStatus)));
  return <section className="tf-cluster-panel"><div className="tf-cluster-panel-head"><div className="tf-cluster-heading"><Box size={18} /><h2>Образы и готовность сервисов</h2><Tag>{rows.length} сервисов</Tag></div></div><div className="tf-cluster-table-tools"><label className="tf-cluster-search"><Search size={15} /><input aria-label="Поиск образа" placeholder="Найти сервис…" value={query} onChange={e => setQuery(e.target.value)} /></label><button type="button" className="tf-cluster-button" aria-pressed={onlyIssues} onClick={() => setOnlyIssues(v => !v)}><AlertTriangle size={14} />Только различия и отсутствующие</button><span className="tf-cluster-muted">{filtered.length} / {rows.length}</span></div>
    {unverified && <p className="tf-cluster-note tf-cluster-matrix-note"><Info size={15} />Наличие образа и совпадение версий — разные проверки. Без fingerprint агент подтверждает только наличие, а не синхронизацию.</p>}
    <div className="tf-cluster-table-scroll"><table className="tf-cluster-table tf-cluster-image-table"><thead><tr><th>Сервис</th>{nodes.map(n => <th key={n.id}>Сервер {n.id}<small>{primaryRole(n.role) ? 'Primary' : n.role === 'standby' || n.role === 'replica' ? 'Резерв' : 'Роль не подтверждена'} · {String(n.appProfile || '').toUpperCase()}</small></th>)}</tr></thead><tbody>{filtered.map(row => <tr key={row.name}><th scope="row">{row.name}</th>{nodes.map(node => {
      const cell = imageCell(node, row.cells[node.id]);
      const stale = !node.online || node.telemetryFresh === false;
      const status = cell?.imageStatus || 'unknown';
      const tone = stale ? 'muted' : status === 'same' ? 'good' : status === 'missing' ? 'bad' : status === 'different' ? 'warn' : 'muted';
      const state = cell?.containerState;
      return <td key={node.id}><Tag tone={tone}>{IMAGE_LABELS[status] || 'Нет данных'}</Tag><small>{stale ? 'Последний снимок' : !cell ? 'Нет назначения в телеметрии' : cell.assigned === false ? 'Не используется этим профилем' : state === 'running' ? 'Запущен' : ['exited', 'created', 'stopped'].includes(state) ? 'Подготовлен к запуску' : state === 'missing' ? 'Контейнер отсутствует' : 'Состояние не передано'}</small></td>;
    })}</tr>)}</tbody></table>{!filtered.length && <Empty>{rows.length ? 'Нет сервисов по выбранному фильтру.' : 'Данные о сервисах ещё не получены.'}</Empty>}</div></section>;
}

const eventTitles = {
  'edge.public_ready': 'Маршрут и сертификат подтверждены',
  'edge.bootstrap_pending': 'Ожидание публичного маршрута',
  'cluster.leader_changed': 'Смена основной ноды',
  'cluster.node_down': 'Нода недоступна',
  'cluster.node_recovered': 'Связь с нодой восстановлена',
  'agent.tick_error': 'Ошибка Node Agent',
  'update.activated': 'Обновление применено',
  'update.prepared': 'Обновление подготовлено',
};
export function ClusterEvents({ events }) {
  const [filter, setFilter] = useState('all');
  const [limit, setLimit] = useState(12);
  const list = useMemo(() => {
    const seen = new Set();
    return array(events).filter(event => { const key = event.id || `${event.at}-${event.node}-${event.kind}-${event.message}`; if (seen.has(key)) return false; seen.add(key); return true; });
  }, [events]);
  const filtered = list.filter(e => filter === 'all' || filter === 'issues' && ['error', 'warning'].includes(e.severity) || filter === 'edge' && String(e.kind).startsWith('edge.') || filter === 'updates' && String(e.kind).startsWith('update.'));
  return <section className="tf-cluster-panel"><div className="tf-cluster-panel-head"><div className="tf-cluster-heading"><Activity size={18} /><h2>События кластера</h2><Tag>{list.length}</Tag></div><div className="tf-cluster-segments" role="group" aria-label="Фильтр событий">{[['all', 'Все'], ['issues', 'Внимание'], ['edge', 'Маршрут'], ['updates', 'Обновления']].map(([key, label]) => <button type="button" key={key} aria-pressed={filter === key} onClick={() => { setFilter(key); setLimit(12); }}>{label}</button>)}</div></div><div className="tf-cluster-events">{filtered.slice(0, limit).map(e => {
    const tone = e.severity === 'error' ? 'bad' : e.severity === 'warning' ? 'warn' : e.severity === 'success' ? 'good' : 'muted';
    const Icon = tone === 'bad' ? XCircle : tone === 'warn' ? AlertTriangle : tone === 'good' ? CheckCircle2 : Info;
    return <details className={`tf-cluster-event is-${tone}`} key={e.id || `${e.at}-${e.node}-${e.kind}`}><summary><Icon size={17} /><strong>{eventTitles[e.kind] || e.title || e.kind}</strong>{e.node && <Tag>{e.node}</Tag>}<time dateTime={e.at}>{dateTime(e.at)}</time></summary><div>{e.message || 'Дополнительных сведений нет.'}<small>{e.kind}</small></div></details>;
  })}{!filtered.length && <Empty>Событий по этому фильтру нет.</Empty>}</div>{filtered.length > limit && <button type="button" className="tf-cluster-show-more" onClick={() => setLimit(v => v + 20)}><ArrowDown size={15} />Показать ещё</button>}</section>;
}