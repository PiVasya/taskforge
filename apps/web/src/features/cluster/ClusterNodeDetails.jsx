import React, { useMemo, useState } from 'react';
import { Box, CheckCircle2, Cloud, Cpu, Database, Globe2, Info, Network, Repeat2, Search, Server, ShieldCheck } from 'lucide-react';
import { array, bytes, containerState, containerTone, countPair, DASH, dateTime, duration, nodeTone, number, PHASE_LABELS, primaryRole, primarySwitchEligibility, REASONS } from './clusterModel';
import { CopyValue, Empty, Metric, ResourceBar, Row, Tag } from './ClusterShared';

const roleLabel = n => primaryRole(n.role) ? 'Основной сервер' : !n.online ? 'Сервер недоступен' : ['standby', 'replica'].includes(n.role) ? 'Резервный сервер' : 'Роль уточняется';
const appMode = { primary: 'Основное приложение', 'primary-existing': 'Основное приложение', 'warm-standby': 'Подготовлен к запуску', assist: 'Вспомогательный режим', off: 'Выключено', 'fenced-patroni-unknown': 'Защитная остановка' };
const statusLabel = (c, node) => {
  const state = containerState(c);
  if (state === 'running') return c.health === 'healthy' ? 'Здоров' : c.health === 'unhealthy' ? 'Нездоров' : c.health === 'starting' ? 'Запускается' : 'Работает';
  if (['created', 'exited', 'stopped'].includes(state)) return primaryRole(node.role) ? 'Остановлен' : 'Подготовлен';
  return ({ missing: 'Отсутствует', restarting: 'Перезапуск', dead: 'Остановлен с ошибкой', paused: 'На паузе' })[state] || 'Нет данных';
};

function Containers({ node }) {
  const [query, setQuery] = useState('');
  const [filter, setFilter] = useState('all');
  const containers = array(node.containers);
  const filtered = useMemo(() => containers.filter(c => {
    const state = containerState(c);
    const tone = containerTone(c, node);
    const matchesQuery = String(c.service || c.name || '').toLowerCase().includes(query.trim().toLowerCase());
    const matchesFilter = filter === 'all'
      || (filter === 'issues' && ['bad', 'warn'].includes(tone))
      || (filter === 'running' && state === 'running')
      || (filter === 'prepared' && ['created', 'exited', 'stopped'].includes(state));
    return matchesQuery && matchesFilter;
  }), [containers, query, filter, node]);
  return <div className="tf-cluster-containers"><div className="tf-cluster-table-tools"><label className="tf-cluster-search"><Search size={15} /><input aria-label="Поиск контейнера" value={query} onChange={e => setQuery(e.target.value)} placeholder="Найти сервис…" /></label><div className="tf-cluster-segments" role="group" aria-label="Фильтр контейнеров">{[['all', 'Все'], ['issues', 'Внимание'], ['running', 'Работают'], ['prepared', 'Подготовлены']].map(([key, label]) => <button type="button" key={key} aria-pressed={filter === key} onClick={() => setFilter(key)}>{label}</button>)}</div><span className="tf-cluster-muted">{filtered.length} / {containers.length}</span></div>
    <div className="tf-cluster-table-scroll"><table className="tf-cluster-table"><thead><tr><th>Сервис</th><th>Состояние</th><th>CPU</th><th>RAM</th><th>Рестарты</th><th>Запущен</th></tr></thead><tbody>{filtered.map((c, i) => <tr key={c.service || c.name || i}><th scope="row"><strong>{c.service || c.name || DASH}</strong>{c.name && c.name !== c.service && <small>{c.name}</small>}</th><td><Tag tone={containerTone(c, node)} dot>{statusLabel(c, node)}</Tag>{c.oomKilled && <Tag tone="bad">OOM</Tag>}</td><td>{c.cpu || DASH}</td><td>{c.memory || DASH}</td><td>{c.restartCount ?? DASH}</td><td>{dateTime(c.startedAt)}</td></tr>)}</tbody></table>{!filtered.length && <Empty>По этому фильтру контейнеров нет.</Empty>}</div>
    <p className="tf-cluster-note">Контейнеры приложений. На резервной ноде «Подготовлен» — нормальное состояние. Прочерк означает отсутствие измерения.</p>
  </div>;
}

export function EdgeDetails({ node }) {
  const edge = node.edge;
  const s = edge?.state || {};
  const active = primaryRole(node.role);
  const fresh = node.online && node.telemetryFresh !== false;
  return <div className="tf-cluster-subpanel"><div className="tf-cluster-subtitle"><Cloud size={17} /><h3>Cloudflare и HTTPS</h3><Tag tone={fresh && active && s.success ? 'good' : 'muted'}>{!fresh ? 'Нет свежих данных' : !edge?.configured ? 'Не настроен' : PHASE_LABELS[s.phase] || s.phase || 'Ожидание'}</Tag></div>
    {active ? <><div className="tf-cluster-edge-steps">{[['DNS', s.dns_synced], ['HTTP-маршрут', s.route_ready], ['Сертификат', node.tlsAssetsReady ?? node.tlsReady], ['HTTPS', s.tls_ready]].map(([label, ready]) => <div key={label} className={fresh && ready ? 'is-ready' : ''}><CheckCircle2 size={16} /><span>{label}</span></div>)}</div><Row label="Origin"><CopyValue value={s.target_ip} label={s.target_node ? `Сервер ${s.target_node}` : undefined} /></Row><Row label="Подтверждения этапа">{countPair(s.route_confirmations, s.route_required)}</Row><Row label="Проверен">{dateTime(s.verified_at)}</Row>{s.certificate_action === 'existing-valid-certificate' && <p className="tf-cluster-note">Используется действующий сертификат, без повторного выпуска.</p>}{array(s.route_checks).map(check => <Row key={`${check.host}-${check.scheme}`} label={check.host}><Tag tone={fresh && check.ok ? 'good' : 'warn'}>{String(check.scheme || '').toUpperCase()} {check.http || DASH}</Tag></Row>)}{s.last_error && <div className="tf-cluster-callout is-warn">{s.last_error}</div>}</> : <><p className="tf-cluster-note">Резервная нода не меняет DNS. Публичный маршрут и HTTPS проверяются после назначения Primary.</p><Row label="API Cloudflare"><Tag tone={edge?.configured ? 'good' : 'muted'}>{edge?.configured ? 'Настроен' : 'Не настроен'}</Tag></Row><Row label="Локальный сертификат"><Tag tone={node.tlsAssetsReady ?? node.tlsReady ? 'good' : 'muted'}>{(node.tlsAssetsReady ?? node.tlsReady) ? 'Готов' : 'Ещё не подтверждён'}</Tag></Row></>}
  </div>;
}

function ReplicationDetails({ node }) {
  const pg = node.postgres || {};
  const replicas = array(pg.replicas);
  if (primaryRole(node.role)) return replicas.length ? <div className="tf-cluster-replication"><Row label="Реплики PostgreSQL">{replicas.length}</Row>{replicas.map((r, index) => <Row key={`${r.host || index}-${r.state}`} label={r.host || `Реплика ${index + 1}`}><span>{r.state || DASH} · {r.sync_state || DASH}<br />Отставание: {bytes(r.lag_bytes)}</span></Row>)}</div> : null;
  if (!pg.receiver && number(pg.replay_gap_bytes) === null) return null;
  return <><Row label="WAL receiver">{pg.receiver || DASH}</Row><Row label="Отставание реплики">{bytes(pg.replay_gap_bytes)}</Row></>;
}

function Overview({ node }) {
  const host = node.host || {};
  const missingCpu = number(host.cpu_percent) === null;
  const missingMemory = !host.memory || number(host.memory.total) === null;
  return <div className="tf-cluster-detail-grid">
    <div className="tf-cluster-subpanel"><div className="tf-cluster-subtitle"><Cpu size={17} /><h3>Ресурсы сервера</h3></div><div className="tf-cluster-metrics"><Metric label="CPU" value={missingCpu ? DASH : `${Number(host.cpu_percent).toFixed(1)}%`} hint={host.cpu_count ? `${host.cpu_count} ядер` : null} /><Metric label="Время работы" value={duration(host.uptime_seconds)} /><Metric label="Температура" value={number(host.temperature_c) === null ? DASH : `${host.temperature_c} °C`} /><Metric label="Диск · всего" value={bytes(host.disk?.total)} /></div><ResourceBar label="Оперативная память" used={host.memory?.used} total={host.memory?.total} /><ResourceBar label="Диск /" used={host.disk?.used} total={host.disk?.total} hint={host.disk?.includes_reserved ? 'включая системный резерв' : ''} />{(missingCpu || missingMemory) && <p className="tf-cluster-note"><Info size={14} />CPU/RAM не передаются этим агентом. Это отсутствие измерений, а не нулевая нагрузка.</p>}</div>
    <div className="tf-cluster-subpanel"><div className="tf-cluster-subtitle"><Database size={17} /><h3>Готовность и данные</h3></div><Row label="PostgreSQL"><Tag tone={!node.online || node.telemetryFresh === false ? 'muted' : node.postgres?.healthy ? 'good' : 'bad'}>{node.postgres?.healthy ? 'Здоров' : 'Проверить'}</Tag></Row><Row label="Роль БД">{primaryRole(node.role) ? 'Primary' : node.role === 'standby' || node.role === 'replica' ? 'Реплика' : node.role || DASH}</Row><ReplicationDetails node={node} /><Row label="MinIO"><Tag tone={node.minio?.ready ? 'good' : 'warn'}>{node.minio?.ready ? 'Готов' : 'Не готов'}</Tag></Row><Row label="Режим приложения">{appMode[node.appMode] || node.appMode || DASH}</Row><Row label="Горячий запуск"><Tag tone={node.hotStartReady ? 'good' : 'warn'}>{node.hotStartReady ? 'Готов' : 'Не готов'}</Tag></Row><Row label="Контейнеры подготовлены">{countPair(node.docker?.preparedAppCount, node.docker?.assignedAppCount)}</Row><Row label="Образы доступны">{countPair(node.docker?.imagesReady, node.docker?.assignedAppCount)}</Row><Row label="Watchtower"><Tag tone={node.update?.watchtowerRunning && !node.update?.hasError ? 'good' : 'warn'}>{node.update?.watchtowerRunning ? 'Работает' : 'Не запущен'}</Tag></Row><Row label="Проверка обновлений">{node.update?.pollIntervalSeconds ? `Каждые ${node.update.pollIntervalSeconds} с` : DASH}</Row>{array(node.hotStartBlockers).length > 0 && <div className="tf-cluster-callout is-warn">{node.hotStartBlockers.join(', ')}</div>}{array(node.trafficBlockers).length > 0 && <div className="tf-cluster-callout is-warn">{node.trafficBlockers.join(', ')}</div>}</div>
    <EdgeDetails node={node} />
  </div>;
}

function NetworkDetails({ node }) {
  const peers = array(node.wireguard?.peers);
  return <div className="tf-cluster-network-grid"><div className="tf-cluster-subpanel"><div className="tf-cluster-subtitle"><Globe2 size={17} /><h3>Адреса и защита</h3></div><Row label="Public IP"><CopyValue value={node.network?.publicHost} /></Row><Row label="WireGuard IP"><CopyValue value={node.network?.wireguardIp} /></Row><Row label="Node Agent"><CopyValue value={node.network?.agentUrl} /></Row><Row label="Hostname">{node.host?.hostname || DASH}</Row><Row label="Файрвол"><Tag tone={node.firewall?.status === 'active' ? 'good' : 'muted'}>{node.firewall?.status === 'active' ? 'UFW активен' : node.firewall?.status || 'Не подтверждён'}</Tag></Row><Row label="Ядро ОС">{node.host?.kernel || DASH}</Row></div><div className="tf-cluster-subpanel"><div className="tf-cluster-subtitle"><Network size={17} /><h3>WireGuard</h3><Tag>{peers.length} пиров</Tag></div>{peers.length ? peers.map((peer, i) => <div className="tf-cluster-peer" key={`${peer.allowed_ips}-${i}`}><CopyValue value={peer.allowed_ips} label="Allowed IPs" /><Row label="Endpoint"><code>{peer.endpoint || DASH}</code></Row><Row label="Последний handshake">{number(peer.latest_handshake_epoch) > 0 ? dateTime(new Date(peer.latest_handshake_epoch * 1000).toISOString()) : 'Нет данных'}</Row>{(peer.rx_bytes != null || peer.tx_bytes != null) && <Row label="RX / TX">{bytes(peer.rx_bytes)} / {bytes(peer.tx_bytes)}</Row>}</div>) : <Empty>Данные о пирах ещё не получены.</Empty>}<p className="tf-cluster-note">Время handshake — наблюдение агента, а не постоянный тест доступности пира.</p></div></div>;
}

export default function ClusterNodeDetails({ node, nodes, onSelect, onPromote, primarySwitchDisabled = false }) {
  const [tab, setTab] = useState('overview');
  if (!node) return null;
  const reasons = array(node.healthReasons);
  const active = nodes.find(item => item.isActive || primaryRole(item.role)) || null;
  const switchEligibility = primarySwitchEligibility(node, active, nodes.some(item => item.edge?.configured === true));
  const showPromote = !!onPromote && active?.id !== node.id && node.canBePrimary === true;
  return <section className="tf-cluster-panel" aria-label={`Детали сервера ${node.id}`}><div className="tf-cluster-node-selector" role="group" aria-label="Выбор сервера">{nodes.map(n => <button type="button" key={n.id} aria-pressed={node.id === n.id} onClick={() => onSelect(n.id)}><span className={`tf-cluster-dot is-${nodeTone(n)}`} />Сервер {n.id}<small>{primaryRole(n.role) ? 'PRIMARY' : n.role === 'standby' || n.role === 'replica' ? 'STANDBY' : '—'}</small></button>)}</div><div className="tf-cluster-detail-head"><div className="tf-cluster-title-group"><span className="tf-cluster-server-mark"><Server size={23} /></span><div><h2>Сервер {node.id}</h2><p>{roleLabel(node)} · {String(node.appProfile || DASH).toUpperCase()}{node.bundleRevision ? ` · Agent r${node.bundleRevision}` : ''}</p></div></div><div className="tf-cluster-address-group"><CopyValue value={node.network?.publicHost} label="Public" /><CopyValue value={node.network?.wireguardIp} label="WireGuard" /></div>{showPromote && <button type="button" className="tf-cluster-button tf-cluster-promote-button" disabled={primarySwitchDisabled || !switchEligibility.ready} title={switchEligibility.ready ? `Сделать сервер ${node.id} Primary` : 'Нода пока не проходит предварительную проверку'} onClick={() => onPromote(node.id)}><Repeat2 size={14} />Сделать Primary</button>}<Tag tone={nodeTone(node)} dot>{node.online ? node.telemetryFresh === false ? 'Данные устарели' : 'Онлайн' : 'Нет связи'}</Tag></div>
    {(!node.online || node.telemetryFresh === false) && <div className="tf-cluster-callout is-warn">Показан последний снимок, а не текущее состояние. Телеметрия: {dateTime(node.telemetryAt)}. Последняя связь: {dateTime(node.lastSeenAt)}.</div>}
    {reasons.length > 0 && <div className="tf-cluster-reasons">{reasons.map(reason => <Tag key={reason} tone="warn">{REASONS[reason] || reason}</Tag>)}</div>}
    <div className="tf-cluster-tabs" role="tablist" aria-label="Данные сервера">{[['overview', 'Обзор', Cpu], ['containers', `Контейнеры · ${array(node.containers).length}`, Box], ['network', 'Сеть и адреса', ShieldCheck]].map(([key, title, Icon]) => <button key={key} id={`cluster-tab-${key}`} type="button" role="tab" tabIndex={tab === key ? 0 : -1} onKeyDown={e => { const keys = ['overview', 'containers', 'network']; const index = keys.indexOf(key); const next = e.key === 'ArrowRight' ? keys[(index + 1) % keys.length] : e.key === 'ArrowLeft' ? keys[(index + keys.length - 1) % keys.length] : e.key === 'Home' ? keys[0] : e.key === 'End' ? keys[keys.length - 1] : null; if (next) { e.preventDefault(); setTab(next); document.getElementById(`cluster-tab-${next}`)?.focus(); } }} aria-selected={tab === key} aria-controls={`cluster-pane-${key}`} onClick={() => setTab(key)}><Icon size={15} />{title}</button>)}</div>
    <div id={`cluster-pane-${tab}`} role="tabpanel" aria-labelledby={`cluster-tab-${tab}`} className="tf-cluster-detail-content">{tab === 'overview' ? <Overview node={node} /> : tab === 'containers' ? <Containers node={node} /> : <NetworkDetails node={node} />}</div>
  </section>;
}