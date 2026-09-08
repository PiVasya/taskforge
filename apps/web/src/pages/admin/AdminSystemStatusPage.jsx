import React, { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import {
  Activity, AlertTriangle, ArrowRight, Box, CheckCircle2, Cloud, Database,
  Pause, Play, RefreshCw, Repeat2, Server, ShieldCheck,
} from 'lucide-react';
import { getSystemStatus, switchClusterPrimary } from '../../api/systemStatus';
import { handleApiError } from '../../utils/handleApiError';
import { useNotify } from '../../components/notify/NotifyProvider';
import ClusterMap from '../../features/cluster/ClusterMap';
import ClusterNodeDetails from '../../features/cluster/ClusterNodeDetails';
import PrimarySwitchDialog from '../../features/cluster/PrimarySwitchDialog';
import { ClusterEvents, ImageMatrix } from '../../features/cluster/ClusterTables';
import { Empty, Tag } from '../../features/cluster/ClusterShared';
import {
  array, countPair, DASH, dateTime, PHASE_LABELS, primarySwitchComplete, REASONS,
} from '../../features/cluster/clusterModel';
import '../../features/cluster/cluster.css';

const PENDING_SWITCH_KEY = 'taskforge.cluster.primary-switch.v1';

function readPendingSwitch() {
  try {
    const value = JSON.parse(window.localStorage.getItem(PENDING_SWITCH_KEY));
    if (!value?.target || !Number.isFinite(Number(value.acceptedAt))) return null;
    // Never resurrect a forgotten operation days later.
    if (Date.now() - Number(value.acceptedAt) > 60 * 60 * 1000) return null;
    return value;
  } catch { return null; }
}

function savePendingSwitch(value) {
  try {
    if (value) window.localStorage.setItem(PENDING_SWITCH_KEY, JSON.stringify(value));
    else window.localStorage.removeItem(PENDING_SWITCH_KEY);
  } catch { /* localStorage is optional */ }
}

function OverviewItem({ icon: Icon, label, value, hint, tone = 'muted' }) {
  return <div className={`tf-cluster-overview-item is-${tone}`}>
    <Icon size={17} />
    <div><span>{label}</span><strong>{value}</strong>{hint && <small>{hint}</small>}</div>
  </div>;
}

function SwitchProgress({ pending, activeId, nodes, onDismiss }) {
  const target = nodes.find(node => node.id === pending.target);
  const targetIsObserved = activeId === pending.target;
  const edgeState = target?.edge?.state || {};
  const ageSeconds = Math.max(0, Math.floor((Date.now() - Number(pending.acceptedAt || Date.now())) / 1000));
  const phase = targetIsObserved
    ? PHASE_LABELS[edgeState.phase] || (target?.trafficReady ? 'Проверка финальной готовности' : 'Активация приложений')
    : 'Patroni переключает роль';
  const stalled = ageSeconds > 600;
  return <div className={`tf-cluster-switch-progress ${stalled ? 'is-warn' : ''}`} role="status">
    <span className="tf-cluster-switch-progress-icon"><Repeat2 size={18} /></span>
    <div className="tf-cluster-switch-progress-main">
      <div><strong>Смена Primary</strong><Tag tone="accent">{pending.from || '—'} → {pending.target}</Tag>{pending.uncertain && <Tag tone="warn">Ответ API потерян</Tag>}</div>
      <p>{stalled ? 'Финальная готовность не подтверждена больше 10 минут. Не повторяйте переключение вслепую — сначала обновите состояние.' : phase}</p>
    </div>
    <div className="tf-cluster-switch-progress-state">
      <span>Наблюдаемая Primary</span><strong>{activeId || '—'}</strong>
      <span>Traffic</span><strong>{target?.trafficReady ? 'READY' : 'WAIT'}</strong>
    </div>
    {stalled && <button type="button" className="tf-cluster-button" onClick={onDismiss}>Скрыть ожидание</button>}
  </div>;
}

export default function AdminSystemStatusPage() {
  const notify = useNotify();
  const [data, setData] = useState(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState('');
  const [selectedId, setSelectedId] = useState(null);
  const [auto, setAuto] = useState(true);
  const [clock, setClock] = useState(Date.now());
  const [switchOpen, setSwitchOpen] = useState(false);
  const [switchInitialTarget, setSwitchInitialTarget] = useState(null);
  const [switchBusy, setSwitchBusy] = useState(false);
  const [pendingSwitch, setPendingSwitch] = useState(() => readPendingSwitch());
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
        setSelectedId(current => snapshot.nodes.some(n => n.id === current)
          ? current
          : snapshot.summary?.activeNode || snapshot.nodes[0]?.id || null);
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
    const tick = () => {
      setClock(Date.now());
      if (auto && document.visibilityState !== 'hidden') load(true);
    };
    const timer = setInterval(tick, 10000);
    const visible = () => { if (document.visibilityState !== 'hidden') tick(); };
    document.addEventListener('visibilitychange', visible);
    return () => { clearInterval(timer); document.removeEventListener('visibilitychange', visible); };
  }, [load, auto]);

  // During a switchover the public origin itself moves. Keep the last good
  // snapshot on transient 521/502 and sample faster until the new origin is ready.
  useEffect(() => {
    if (!pendingSwitch) return undefined;
    const timer = setInterval(() => {
      setClock(Date.now());
      if (document.visibilityState !== 'hidden') load(true);
    }, 2000);
    return () => clearInterval(timer);
  }, [pendingSwitch, load]);

  const nodes = useMemo(() => array(data?.nodes).map(node => {
    const timestamp = Date.parse(node.telemetryAt || data?.generatedAt || '');
    return timestamp && clock - timestamp > (data?.telemetryStaleAfterSeconds || 90) * 1000
      ? { ...node, telemetryFresh: false }
      : node;
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
  const issues = nodes.flatMap(n => [...new Set([
    ...array(n.healthReasons), ...(n.telemetryFresh === false ? ['telemetry-stale'] : []),
  ])].map(reason => ({ node: n.id, reason })));
  const available = data?.summary?.imageAvailable ?? nodes.reduce((sum, n) => sum + (n.docker?.imagesReady || 0), 0);
  const assigned = data?.summary?.imageAssigned ?? nodes.reduce((sum, n) => sum + (n.docker?.assignedAppCount || 0), 0);

  useEffect(() => {
    if (!pendingSwitch || !data) return;
    if (!primarySwitchComplete(nodes, activeId, pendingSwitch.target)) return;
    const completed = pendingSwitch;
    setPendingSwitch(null);
    savePendingSwitch(null);
    notifyRef.current.success(`Primary ${completed.from || '—'} → ${completed.target}: приложения, Cloudflare и HTTPS готовы.`, 8000);
  }, [pendingSwitch, data, nodes, activeId]);

  const openPrimarySwitch = useCallback((target = null) => {
    setSwitchInitialTarget(target);
    setSwitchOpen(true);
  }, []);

  const dismissPending = useCallback(() => {
    setPendingSwitch(null);
    savePendingSwitch(null);
  }, []);

  const submitPrimarySwitch = useCallback(async target => {
    const from = data?.summary?.activeNode || null;
    if (!from || !target || switchBusy) return;
    setSwitchBusy(true);
    try {
      const result = await switchClusterPrimary(target);
      if (result?.changed === false) {
        setSwitchOpen(false);
        notifyRef.current.info(result?.message || `Сервер ${target} уже является Primary.`);
        await load(true);
        return;
      }
      const pending = {
        from: result?.from || from,
        target: result?.target || target,
        requestId: result?.requestId || null,
        acceptedAt: Date.now(),
        uncertain: false,
      };
      setPendingSwitch(pending);
      savePendingSwitch(pending);
      setSelectedId(pending.target);
      setSwitchOpen(false);
      notifyRef.current.info(`Patroni принял ${pending.from} → ${pending.target}. Ждём Cloudflare и HTTPS.`, 8000);
      setTimeout(() => load(true), 400);
    } catch (e) {
      const parsed = handleApiError(e, false, 'Не удалось запросить смену Primary');
      // If the old origin disappeared after Patroni accepted the POST, the browser
      // can lose the response. Never retry that situation blindly: observe state.
      if (!parsed?.status || parsed.status >= 500) {
        const pending = { from, target, requestId: null, acceptedAt: Date.now(), uncertain: true };
        setPendingSwitch(pending);
        savePendingSwitch(pending);
        setSelectedId(target);
        setSwitchOpen(false);
        notifyRef.current.warn('Ответ на запрос переключения потерян. Запрос не повторяем: проверяем фактическое состояние кластера.', 10000);
        setTimeout(() => load(true), 800);
      } else {
        handleApiError(e, notifyRef.current, 'Не удалось запросить смену Primary');
      }
    } finally {
      setSwitchBusy(false);
    }
  }, [data?.summary?.activeNode, switchBusy, load]);

  const switchDisabled = !data || !!pendingSwitch || data?.summary?.primaryConflict || !activeFresh || active?.trafficReady !== true;

  return <div className="tf-cluster">
    <div className="tf-cluster-page-head">
      <div>
        <div className="tf-cluster-eyebrow"><Activity size={14} />TASKFORGE / ИНФРАСТРУКТУРА</div>
        <h1>Кластер<span className={`tf-cluster-health ${healthy ? 'is-good' : !data ? 'is-muted' : 'is-warn'}`}><i />{!data ? 'Подключение' : healthy ? 'Работает штатно' : 'Требует внимания'}</span></h1>
      </div>
      <div className="tf-cluster-header-actions">
        <span className="tf-cluster-updated">Снимок · {dateTime(data?.generatedAt)}</span>
        <button type="button" className="tf-cluster-button tf-cluster-primary-control" disabled={switchDisabled} onClick={() => openPrimarySwitch()} title={switchDisabled ? 'Дождитесь полностью готовой и единственной Primary' : 'Безопасно переключить Primary'}><Repeat2 size={15} />Сменить Primary</button>
        <button type="button" className="tf-cluster-button" onClick={() => setAuto(v => !v)} aria-pressed={auto} title="Обновлять состояние каждые 10 секунд">{auto ? <Pause size={14} /> : <Play size={14} />}{auto ? 'Авто · 10 с' : 'Авто выключено'}</button>
        <button type="button" className="tf-cluster-button is-primary" disabled={loading} onClick={() => load()}><RefreshCw size={15} className={loading ? 'tf-cluster-spin' : ''} />Обновить</button>
      </div>
    </div>

    {pendingSwitch && <SwitchProgress pending={pendingSwitch} activeId={activeId} nodes={nodes} onDismiss={dismissPending} />}
    {error && <div role="alert" className="tf-cluster-callout is-warn"><AlertTriangle size={18} />{error}</div>}
    {data?.schemaVersion !== 2 && data && <div className="tf-cluster-callout"><AlertTriangle size={17} />API отдаёт сокращённые данные. Обновите образ observability-api вместе с фронтендом; Node Agent переустанавливать не нужно.</div>}

    {!data ? <div className="tf-cluster-panel"><Empty>{loading ? 'Получаем состояние серверов…' : 'Состояние пока недоступно. Нажмите «Обновить».'}</Empty></div> : <>
      <section className="tf-cluster-overview" aria-label="Сводка кластера">
        <OverviewItem icon={Server} label="Основной" value={activeFresh ? `Сервер ${activeId}` : DASH} hint={`${online}/${nodes.length} агентов онлайн`} tone={activeFresh ? 'accent' : 'warn'} />
        <OverviewItem icon={ShieldCheck} label="Горячий резерв" value={countPair(hot, reserves.length)} hint="Готовность к запуску" tone={hot === reserves.length && reserves.length ? 'good' : 'warn'} />
        <OverviewItem icon={Database} label="PostgreSQL" value={pgReady ? 'Здоров' : 'Проверить'} hint={activeFresh ? `Primary ${activeId}` : 'Primary не подтверждён'} tone={pgReady ? 'good' : 'warn'} />
        <OverviewItem icon={Cloud} label="Cloudflare" value={activeFresh && edge?.dns_synced ? `→ ${edge.target_node || DASH}` : DASH} hint={activeFresh ? PHASE_LABELS[edge?.phase] || 'Нет подтверждения' : 'Нет свежих данных'} tone={activeFresh && edge?.success ? 'good' : 'muted'} />
        <OverviewItem icon={Box} label="Образы доступны" value={assigned ? countPair(available, assigned) : DASH} hint={data.summary?.images === 'synchronized' ? 'Версии совпадают' : data.summary?.imageDifferent ? `Различаются: ${data.summary.imageDifferent}` : 'Версии не подтверждены'} tone={data.summary?.imageMissing ? 'warn' : 'muted'} />
        <OverviewItem icon={Activity} label="Участники голосования" value={countPair(data.summary?.quorum, data.summary?.quorumTotal)} hint="По доступности агентов" />
      </section>

      {data.summary?.primaryConflict && <div role="alert" className="tf-cluster-callout is-bad">Несколько нод заявляют роль Primary: {array(data.summary.primaryCandidates).join(', ')}. Единственная основная нода не подтверждена.</div>}
      {issues.length > 0 && <details className="tf-cluster-attention"><summary><AlertTriangle size={15} />Что требует внимания · {issues.length}</summary><div>{issues.map(({ node, reason }) => <button type="button" key={`${node}-${reason}`} onClick={() => setSelectedId(node)}><Tag>{node}</Tag>{REASONS[reason] || reason}</button>)}</div></details>}

      <div className="tf-cluster-route-bar"><Cloud size={16} /><strong>Публичный маршрут</strong><span>Cloudflare</span><ArrowRight size={14} /><span>{activeFresh && edge?.dns_synced ? `Сервер ${edge.target_node}` : 'Не подтверждён'}</span><ArrowRight size={14} /><Tag tone={activeFresh && edge?.tls_ready ? 'good' : 'muted'}>{activeFresh && edge?.tls_ready ? 'HTTPS готов' : 'HTTPS не подтверждён'}</Tag><span className="tf-cluster-route-tail">{activeFresh && active.trafficReady ? <><CheckCircle2 size={14} />Трафик готов</> : 'Проверка готовности'}</span></div>

      {nodes.length > 0 ? <>
        <ClusterMap nodes={nodes} activeId={activeId} selectedId={selected?.id} onSelect={setSelectedId} storageKey={`taskforge.cluster.layout.v2:${data.cluster || 'default'}`} />
        <ClusterNodeDetails node={selected} nodes={nodes} onSelect={setSelectedId} onPromote={nodeId => openPrimarySwitch(nodeId)} primarySwitchDisabled={!!pendingSwitch} />
        <ImageMatrix nodes={nodes} />
      </> : <div className="tf-cluster-panel"><Empty>Агенты ещё не передали данные о нодах.</Empty></div>}
      <ClusterEvents events={data.events} />
    </>}

    <PrimarySwitchDialog
      open={switchOpen}
      nodes={nodes}
      activeId={activeId}
      initialTarget={switchInitialTarget}
      busy={switchBusy}
      onClose={() => { if (!switchBusy) setSwitchOpen(false); }}
      onSubmit={submitPrimarySwitch}
    />
  </div>;
}
