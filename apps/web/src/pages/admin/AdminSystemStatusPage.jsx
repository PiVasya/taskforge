import React, { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { Activity, AlertTriangle, ArrowRight, Box, CheckCircle2, Cloud, Database, Pause, Play, RefreshCw, Server, ShieldCheck } from 'lucide-react';
import { getSystemStatus } from '../../api/systemStatus';
import { handleApiError } from '../../utils/handleApiError';
import { useNotify } from '../../components/notify/NotifyProvider';
import ClusterMap from '../../features/cluster/ClusterMap';
import ClusterNodeDetails from '../../features/cluster/ClusterNodeDetails';
import { ClusterEvents, ImageMatrix } from '../../features/cluster/ClusterTables';
import { Empty, Tag } from '../../features/cluster/ClusterShared';
import { array, countPair, DASH, dateTime, PHASE_LABELS, REASONS } from '../../features/cluster/clusterModel';
import '../../features/cluster/cluster.css';

function OverviewItem({ icon: Icon, label, value, hint, tone = 'muted' }) {
  return <div className={`tf-cluster-overview-item is-${tone}`}><Icon size={17} /><div><span>{label}</span><strong>{value}</strong>{hint && <small>{hint}</small>}</div></div>;
}

export default function AdminSystemStatusPage() {
  const notify = useNotify();
  const [data, setData] = useState(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState('');
  const [selectedId, setSelectedId] = useState(null);
  const [auto, setAuto] = useState(true);
  const [clock, setClock] = useState(Date.now());
  const request = useRef(null);
  const notifyRef = useRef(notify);
  notifyRef.current = notify;
  const load = useCallback(async (silent = false) => {
    if (request.current) return;
    const controller = new AbortController();
    request.current = controller;
    setLoading(true);
    try {
      const snapshot = await getSystemStatus({ signal: controller.signal });
      if (!Array.isArray(snapshot?.nodes)) throw new Error('Некорректный ответ телеметрии');
      if (!controller.signal.aborted) {
        setData(snapshot);
        setClock(Date.now());
        setError('');
        setSelectedId(current => snapshot.nodes.some(n => n.id === current) ? current : snapshot.summary?.activeNode || snapshot.nodes[0]?.id || null);
      }
    } catch (e) {
      if (!controller.signal.aborted) {
        setError('Не удалось обновить состояние. Показан последний полученный снимок.');
        if (!silent) handleApiError(e, notifyRef.current, 'Не удалось получить состояние кластера');
      }
    } finally {
      if (request.current === controller) request.current = null;
      if (!controller.signal.aborted) setLoading(false);
    }
  }, []);
  useEffect(() => {
    load();
    return () => { request.current?.abort(); request.current = null; };
  }, [load]);
  useEffect(() => {
    const tick = () => { setClock(Date.now()); if (auto && document.visibilityState !== 'hidden') load(true); };
    const timer = setInterval(tick, 10000);
    const visible = () => { if (document.visibilityState !== 'hidden') tick(); };
    document.addEventListener('visibilitychange', visible);
    return () => { clearInterval(timer); document.removeEventListener('visibilitychange', visible); };
  }, [load, auto]);
  const nodes = useMemo(() => array(data?.nodes).map(node => {
    const timestamp = Date.parse(node.telemetryAt || data?.generatedAt || '');
    return timestamp && clock - timestamp > (data?.telemetryStaleAfterSeconds || 90) * 1000 ? { ...node, telemetryFresh: false } : node;
  }), [data, clock]);
  const activeId = data?.summary?.activeNode;
  const active = nodes.find(n => n.id === activeId);
  const selected = nodes.find(n => n.id === selectedId) || nodes[0];
  const edge = active?.edge?.state;
  const activeFresh = active?.online && active?.telemetryFresh !== false;
  const online = nodes.filter(n => n.online).length;
  const reserves = nodes.filter(n => n.id !== activeId && n.canBePrimary);
  const hot = reserves.filter(n => n.online && n.telemetryFresh !== false && n.hotStartReady).length;
  const pgReady = nodes.length > 0 && nodes.every(n => n.online && n.telemetryFresh !== false && n.postgres?.healthy);
  const healthy = !!data && data.status === 'healthy' && nodes.length > 0 && nodes.every(n => n.telemetryFresh !== false);
  const issues = nodes.flatMap(n => [...new Set([...array(n.healthReasons), ...(n.telemetryFresh === false ? ['telemetry-stale'] : [])])].map(reason => ({ node: n.id, reason })));
  const available = data?.summary?.imageAvailable ?? nodes.reduce((sum, n) => sum + (n.docker?.imagesReady || 0), 0);
  const assigned = data?.summary?.imageAssigned ?? nodes.reduce((sum, n) => sum + (n.docker?.assignedAppCount || 0), 0);
  return <div className="tf-cluster">
    <div className="tf-cluster-page-head"><div><div className="tf-cluster-eyebrow"><Activity size={14} />TASKFORGE / ИНФРАСТРУКТУРА</div><h1>Кластер<span className={`tf-cluster-health ${healthy ? 'is-good' : !data ? 'is-muted' : 'is-warn'}`}><i />{!data ? 'Подключение' : healthy ? 'Работает штатно' : 'Требует внимания'}</span></h1></div><div className="tf-cluster-header-actions"><span className="tf-cluster-updated">Снимок · {dateTime(data?.generatedAt)}</span><button type="button" className="tf-cluster-button" onClick={() => setAuto(v => !v)} aria-pressed={auto} title="Обновлять состояние каждые 10 секунд">{auto ? <Pause size={14} /> : <Play size={14} />}{auto ? 'Авто · 10 с' : 'Авто выключено'}</button><button type="button" className="tf-cluster-button is-primary" disabled={loading} onClick={() => load()}><RefreshCw size={15} className={loading ? 'tf-cluster-spin' : ''} />Обновить</button></div></div>
    {error && <div role="alert" className="tf-cluster-callout is-warn"><AlertTriangle size={18} />{error}</div>}
    {data?.schemaVersion !== 2 && data && <div className="tf-cluster-callout"><AlertTriangle size={17} />API отдаёт сокращённые данные. Обновите образ observability-api вместе с фронтендом; Node Agent переустанавливать не нужно.</div>}
    {!data ? <div className="tf-cluster-panel"><Empty>{loading ? 'Получаем состояние серверов…' : 'Состояние пока недоступно. Нажмите «Обновить».'}</Empty></div> : <>
      <section className="tf-cluster-overview" aria-label="Сводка кластера"><OverviewItem icon={Server} label="Основной" value={activeFresh ? `Сервер ${activeId}` : DASH} hint={`${online}/${nodes.length} агентов онлайн`} tone={activeFresh ? 'accent' : 'warn'} /><OverviewItem icon={ShieldCheck} label="Горячий резерв" value={countPair(hot, reserves.length)} hint="Готовность к запуску" tone={hot === reserves.length && reserves.length ? 'good' : 'warn'} /><OverviewItem icon={Database} label="PostgreSQL" value={pgReady ? 'Здоров' : 'Проверить'} hint={activeFresh ? `Primary ${activeId}` : 'Primary не подтверждён'} tone={pgReady ? 'good' : 'warn'} /><OverviewItem icon={Cloud} label="Cloudflare" value={activeFresh && edge?.dns_synced ? `→ ${edge.target_node || DASH}` : DASH} hint={activeFresh ? PHASE_LABELS[edge?.phase] || 'Нет подтверждения' : 'Нет свежих данных'} tone={activeFresh && edge?.success ? 'good' : 'muted'} /><OverviewItem icon={Box} label="Образы доступны" value={assigned ? countPair(available, assigned) : DASH} hint={data.summary?.images === 'synchronized' ? 'Версии совпадают' : data.summary?.imageDifferent ? `Различаются: ${data.summary.imageDifferent}` : 'Версии не подтверждены'} tone={data.summary?.imageMissing ? 'warn' : 'muted'} /><OverviewItem icon={Activity} label="Участники голосования" value={countPair(data.summary?.quorum, data.summary?.quorumTotal)} hint="По доступности агентов" /></section>
      {data.summary?.primaryConflict && <div role="alert" className="tf-cluster-callout is-bad">Несколько нод заявляют роль Primary: {array(data.summary.primaryCandidates).join(', ')}. Единственная основная нода не подтверждена.</div>}
      {issues.length > 0 && <details className="tf-cluster-attention"><summary><AlertTriangle size={15} />Что требует внимания · {issues.length}</summary><div>{issues.map(({ node, reason }) => <button type="button" key={`${node}-${reason}`} onClick={() => setSelectedId(node)}><Tag>{node}</Tag>{REASONS[reason] || reason}</button>)}</div></details>}
      <div className="tf-cluster-route-bar"><GlobeIcon /><strong>Публичный маршрут</strong><span>Cloudflare</span><ArrowRight size={14} /><span>{activeFresh && edge?.dns_synced ? `Сервер ${edge.target_node}` : 'Не подтверждён'}</span><ArrowRight size={14} /><Tag tone={activeFresh && edge?.tls_ready ? 'good' : 'muted'}>{activeFresh && edge?.tls_ready ? 'HTTPS готов' : 'HTTPS не подтверждён'}</Tag><span className="tf-cluster-route-tail">{activeFresh && active.trafficReady ? <><CheckCircle2 size={14} />Трафик готов</> : 'Проверка готовности'}</span></div>
      {nodes.length > 0 ? <><ClusterMap nodes={nodes} activeId={activeId} selectedId={selected?.id} onSelect={setSelectedId} storageKey={`taskforge.cluster.layout.v2:${data.cluster || 'default'}`} /><ClusterNodeDetails node={selected} nodes={nodes} onSelect={setSelectedId} /><ImageMatrix nodes={nodes} /></> : <div className="tf-cluster-panel"><Empty>Агенты ещё не передали данные о нодах.</Empty></div>}
      <ClusterEvents events={data.events} />
    </>}
  </div>;
}
const GlobeIcon = () => <Cloud size={16} />;