import React, { memo, useEffect, useMemo, useState } from 'react';
import ReactFlow, { Background, Controls, Handle, Position } from 'reactflow';
import 'reactflow/dist/style.css';
import {
  Activity, AlertTriangle, Box, CheckCircle2, ChevronRight, Cpu, Database,
  Network, RefreshCw, Server, ShieldCheck, Wifi, XCircle,
} from 'lucide-react';
import { Button, Card } from '../../components/ui';
import { getSystemStatus } from '../../api/systemStatus';
import { handleApiError } from '../../utils/handleApiError';
import { useNotify } from '../../components/notify/NotifyProvider';

const fmtBytes = (value) => {
  const n = Number(value);
  if (!Number.isFinite(n) || n < 0) return '—';
  if (n === 0) return '0 Б';
  const units = ['Б', 'КБ', 'МБ', 'ГБ', 'ТБ'];
  let v = n;
  let i = 0;
  while (v >= 1024 && i < units.length - 1) { v /= 1024; i += 1; }
  return `${v >= 10 || i === 0 ? v.toFixed(0) : v.toFixed(1)} ${units[i]}`;
};

const fmtUptime = (seconds) => {
  const total = Math.max(0, Number(seconds || 0));
  const days = Math.floor(total / 86400);
  const hours = Math.floor((total % 86400) / 3600);
  const minutes = Math.floor((total % 3600) / 60);
  if (days) return `${days} д ${hours} ч`;
  if (hours) return `${hours} ч ${minutes} мин`;
  return `${minutes} мин`;
};

const stateText = {
  same: 'Одинаковая версия',
  different: 'Версия отличается',
  missing: 'Не скачан',
  unknown: 'Нет данных',
  'not-assigned': 'Не назначен',
};

const appModeText = {
  'primary-existing': 'Активное приложение',
  primary: 'Активное приложение',
  'warm-standby': 'Горячий standby',
  assist: 'Lite-приложение',
  off: 'Приложение выключено',
};

const updateStatusText = {
  idle: 'Ожидание',
  watchtower: 'Автообновление активно',
  preparing: 'Подготавливается',
  updating: 'Обновляется',
  prepared: 'Обновление подготовлено',
  activated: 'Обновление применено',
  'watchtower-failed': 'Ошибка автообновления',
};

const hotStartBlockerText = (value) => {
  if (!value) return '';
  if (value.startsWith('images:')) return `образы ${value.slice(7)}`;
  if (value.startsWith('containers:')) return `контейнеры ${value.slice(11)}`;
  return ({
    'no-assigned-app-services': 'нет назначенных сервисов',
    'postgres-role-unreachable': 'PostgreSQL недоступен',
    'postgres-not-ready': 'реплика PostgreSQL не готова',
    'minio-not-ready': 'MinIO не готов',
    'origin-tls-not-ready': 'Origin TLS не готов',
  })[value] || value;
};

function Pill({ children, tone = 'neutral' }) {
  const cls = {
    good: 'border-emerald-500/35 bg-emerald-500/10 text-emerald-700 dark:text-emerald-300',
    warn: 'border-amber-500/35 bg-amber-500/10 text-amber-700 dark:text-amber-300',
    bad: 'border-rose-500/35 bg-rose-500/10 text-rose-700 dark:text-rose-300',
    info: 'border-sky-500/35 bg-sky-500/10 text-sky-700 dark:text-sky-300',
    neutral: 'border-neutral-300/80 bg-neutral-100/70 text-neutral-600 dark:border-neutral-700 dark:bg-neutral-900 dark:text-neutral-300',
  }[tone];
  return <span className={`inline-flex items-center rounded-full border px-2.5 py-1 text-xs font-medium ${cls}`}>{children}</span>;
}

function imageTone(status) {
  if (status === 'same') return 'good';
  if (status === 'different') return 'warn';
  if (status === 'missing') return 'bad';
  if (status === 'not-assigned') return 'neutral';
  return 'warn';
}

const ServerNode = memo(({ data }) => {
  const node = data.node;
  const used = Number(node?.host?.memory?.used || 0);
  const total = Number(node?.host?.memory?.total || 0);
  const memoryPercent = total > 0 ? Math.round((used / total) * 100) : null;
  const serviceItems = Array.isArray(node?.services) ? node.services.filter((x) => x.assigned) : [];
  const same = serviceItems.filter((x) => x.imageStatus === 'same').length;
  const imageOk = serviceItems.length > 0 && same === serviceItems.length;
  return (
    <button
      type="button"
      onClick={() => data.onSelect(node.id)}
      className={`w-[250px] rounded-2xl border-2 bg-white p-4 text-left shadow-[5px_5px_0_rgba(0,0,0,0.15)] transition-transform hover:-translate-y-0.5 dark:bg-neutral-950 ${data.selected ? 'border-sky-500' : node.online ? 'border-neutral-900 dark:border-neutral-200' : 'border-rose-500'}`}
    >
      <Handle type="target" position={Position.Left} className="!h-2 !w-2 !border-0 !bg-neutral-500" />
      <Handle type="source" position={Position.Right} className="!h-2 !w-2 !border-0 !bg-neutral-500" />
      <div className="flex items-start justify-between gap-3">
        <div>
          <div className="text-xs font-semibold uppercase tracking-[0.18em] text-neutral-400">Server</div>
          <div className="mt-0.5 text-3xl font-black">{node.id}</div>
        </div>
        <div className={`mt-1 h-3 w-3 rounded-full ${node.online ? 'bg-emerald-500' : 'bg-rose-500'}`} title={node.online ? 'Онлайн' : 'Нет связи'} />
      </div>
      <div className="mt-3 flex flex-wrap gap-1.5">
        {node.isActive ? <Pill tone="good">ACTIVE</Pill> : <Pill>{String(node.role || 'unknown').toUpperCase()}</Pill>}
        <Pill tone={node.appProfile === 'lite' ? 'info' : 'neutral'}>{String(node.appProfile || '?').toUpperCase()}</Pill>
        {!node.isActive && node.hotStartReady ? <Pill tone="good">HOT READY</Pill> : null}
      </div>
      <div className="mt-4 grid grid-cols-2 gap-2 text-xs">
        <div className="rounded-xl bg-neutral-100 p-2.5 dark:bg-neutral-900"><div className="text-neutral-400">CPU</div><b>{node?.host?.cpu_percent == null ? '—' : `${node.host.cpu_percent}%`}</b></div>
        <div className="rounded-xl bg-neutral-100 p-2.5 dark:bg-neutral-900"><div className="text-neutral-400">RAM</div><b>{memoryPercent == null ? '—' : `${memoryPercent}%`}</b></div>
      </div>
      <div className="mt-3 flex items-center justify-between gap-2 text-xs">
        <span className="text-neutral-500">Образы</span>
        <span className={imageOk ? 'font-semibold text-emerald-600 dark:text-emerald-400' : 'font-semibold text-amber-600 dark:text-amber-400'}>
          {serviceItems.length ? (imageOk ? 'Всё одинаково' : `${same}/${serviceItems.length} совпадают`) : '—'}
        </span>
      </div>
      <div className="mt-2 text-xs text-neutral-500">{appModeText[node.appMode] || node.appMode || '—'}</div>
    </button>
  );
});
ServerNode.displayName = 'ServerNode';

const nodeTypes = { server: ServerNode };

function SummaryCard({ icon: Icon, label, value, hint, tone = 'neutral' }) {
  return (
    <Card className="p-4">
      <div className="flex items-center gap-2 text-sm text-neutral-500"><Icon size={16} />{label}</div>
      <div className={`mt-2 text-2xl font-black ${tone === 'good' ? 'text-emerald-600 dark:text-emerald-400' : tone === 'bad' ? 'text-rose-600 dark:text-rose-400' : tone === 'warn' ? 'text-amber-600 dark:text-amber-400' : ''}`}>{value}</div>
      {hint ? <div className="mt-1 text-xs text-neutral-400">{hint}</div> : null}
    </Card>
  );
}

function MetricBar({ label, used, total }) {
  const pct = total > 0 ? Math.min(100, Math.round((used / total) * 100)) : 0;
  return (
    <div>
      <div className="flex justify-between text-sm"><span>{label}</span><span className="text-neutral-500">{fmtBytes(used)} / {fmtBytes(total)}</span></div>
      <div className="mt-1 h-2 overflow-hidden rounded-full bg-neutral-200 dark:bg-neutral-800"><div className="h-full bg-current" style={{ width: `${pct}%` }} /></div>
    </div>
  );
}

export default function AdminSystemStatusPage() {
  const notify = useNotify();
  const [loading, setLoading] = useState(true);
  const [pageError, setPageError] = useState('');
  const [data, setData] = useState(null);
  const [selectedId, setSelectedId] = useState(null);
  const [layer, setLayer] = useState('ha');

  const load = async (silent = false) => {
    try {
      if (!silent) setLoading(true);
      const res = await getSystemStatus();
      setData(res);
      setPageError('');
      setSelectedId((current) => current || res?.summary?.activeNode || res?.nodes?.[0]?.id || null);
    } catch (e) {
      const parsed = handleApiError(e, notify, 'Не удалось получить состояние кластера');
      setPageError(parsed?.userMessage || 'Не удалось получить состояние кластера');
    } finally {
      if (!silent) setLoading(false);
    }
  };

  useEffect(() => {
    load();
    const timer = window.setInterval(() => load(true), 10000);
    return () => window.clearInterval(timer);
  }, []); // eslint-disable-line react-hooks/exhaustive-deps

  const nodesData = useMemo(() => Array.isArray(data?.nodes) ? data.nodes : [], [data]);
  const selected = useMemo(() => nodesData.find((x) => x.id === selectedId) || nodesData[0] || null, [nodesData, selectedId]);

  const flow = useMemo(() => {
    const positions = {
      A: { x: 40, y: 70 },
      B: { x: 390, y: 10 },
      C: { x: 390, y: 240 },
    };
    const fallback = [{ x: 40, y: 70 }, { x: 390, y: 10 }, { x: 390, y: 240 }, { x: 740, y: 120 }];
    const graphNodes = nodesData.map((node, index) => ({
      id: node.id,
      type: 'server',
      position: positions[node.id] || fallback[index] || { x: index * 300, y: 100 },
      data: { node, selected: node.id === selectedId, onSelect: setSelectedId },
      draggable: false,
    }));
    const ids = new Set(nodesData.map((x) => x.id));
    const edge = (id, source, target, label, animated = false) => ids.has(source) && ids.has(target) ? ({
      id, source, target, label, animated,
      style: { strokeWidth: 2 },
      labelStyle: { fontSize: 11, fontWeight: 700 },
    }) : null;
    let graphEdges = [];
    if (layer === 'postgres') {
      const active = data?.summary?.activeNode || 'A';
      graphEdges = nodesData.filter((x) => x.id !== active).map((x) => edge(`pg-${active}-${x.id}`, active, x.id, 'WAL', true)).filter(Boolean);
    } else if (layer === 'minio' || layer === 'network') {
      for (let i = 0; i < nodesData.length; i += 1) for (let j = i + 1; j < nodesData.length; j += 1) {
        graphEdges.push(edge(`${layer}-${nodesData[i].id}-${nodesData[j].id}`, nodesData[i].id, nodesData[j].id, layer === 'minio' ? 'MINIO ↔' : 'WG ↔'));
      }
      graphEdges = graphEdges.filter(Boolean);
    } else {
      const active = data?.summary?.activeNode || 'A';
      graphEdges = nodesData.filter((x) => x.id !== active).map((x) => edge(`ha-${active}-${x.id}`, active, x.id, x.canBePrimary ? 'FAILOVER' : 'ASSIST', true)).filter(Boolean);
    }
    return { nodes: graphNodes, edges: graphEdges };
  }, [nodesData, selectedId, layer, data?.summary?.activeNode]);

  const allServices = useMemo(() => {
    const map = new Map();
    nodesData.forEach((node) => (node.services || []).forEach((svc) => {
      if (!map.has(svc.service)) map.set(svc.service, {});
      map.get(svc.service)[node.id] = svc;
    }));
    return [...map.entries()].sort(([a], [b]) => a.localeCompare(b));
  }, [nodesData]);

  if (loading && !data) return <div className="p-8 text-neutral-500">Собираем состояние кластера…</div>;

  return (
    <div className="space-y-6 pb-12">
      {pageError ? (
        <Card className="border-rose-300 bg-rose-50 p-4 text-rose-700 dark:bg-rose-950/20 dark:text-rose-300">
          <div className="flex items-start gap-2"><AlertTriangle size={18} className="mt-0.5" /><div><b>Нет свежих данных</b><div className="mt-1 text-sm">{pageError}</div></div></div>
        </Card>
      ) : null}

      <div className="flex flex-wrap items-start justify-between gap-4">
        <div>
          <div className="flex items-center gap-3"><Activity size={30} /><h1 className="text-3xl font-black">Кластер TaskForge</h1></div>
          <p className="mt-2 text-neutral-500">Серверы, контейнеры, образы, обновления и готовность к горячему запуску.</p>
        </div>
        <Button variant="outline" onClick={() => load()} disabled={loading}><RefreshCw size={16} className={loading ? 'animate-spin' : ''} /><span className="ml-1">Обновить</span></Button>
      </div>

      <div className="grid gap-3 sm:grid-cols-2 xl:grid-cols-5">
        <SummaryCard icon={ShieldCheck} label="Кластер" value={data?.status === 'healthy' ? 'Здоров' : 'Требует внимания'} tone={data?.status === 'healthy' ? 'good' : 'bad'} />
        <SummaryCard icon={Server} label="Active" value={data?.summary?.activeNode || '—'} hint={`онлайн ${data?.summary?.nodesOnline || 0}/${data?.summary?.nodesTotal || 0}`} />
        <SummaryCard icon={Network} label="Голоса" value={`${data?.summary?.quorum || 0}/${data?.summary?.quorumTotal || '?'}`} hint={data?.summary?.mode === 'quorum' ? 'quorum mode' : 'подготовка / replica mode'} />
        <SummaryCard
          icon={Box}
          label="Образы"
          value={data?.summary?.images === 'synchronized' ? 'Одинаковые' : data?.summary?.images === 'unknown' ? 'Нет данных' : data?.summary?.imageMissing ? 'Не все готовы' : 'Различаются'}
          tone={data?.summary?.images === 'synchronized' ? 'good' : data?.summary?.images === 'unknown' ? 'neutral' : data?.summary?.imageMissing ? 'bad' : 'warn'}
          hint={data?.summary?.imageDifferent ? `временно различаются: ${data.summary.imageDifferent}` : undefined}
        />
        <SummaryCard
          icon={Database}
          label="PostgreSQL"
          value={nodesData.length > 0 && nodesData.every((x) => x.online && x.postgres?.healthy) ? 'Здоров' : 'Проверить'}
          tone={nodesData.length > 0 && nodesData.every((x) => x.online && x.postgres?.healthy) ? 'good' : 'bad'}
          hint={data?.summary?.activeNode ? `primary ${data.summary.activeNode}` : undefined}
        />
      </div>

      <Card className="overflow-hidden p-0">
        <div className="flex flex-wrap items-center justify-between gap-3 border-b p-4 dark:border-neutral-800">
          <div><div className="font-bold">Карта кластера</div><div className="text-xs text-neutral-500">Нажми на сервер, чтобы открыть детали</div></div>
          <div className="flex flex-wrap gap-2">
            {[['ha', 'HA'], ['postgres', 'PostgreSQL'], ['minio', 'MinIO'], ['network', 'WireGuard']].map(([key, label]) => (
              <button type="button" key={key} onClick={() => setLayer(key)} className={`rounded-lg border px-3 py-1.5 text-xs font-semibold ${layer === key ? 'border-neutral-900 bg-neutral-900 text-white dark:border-white dark:bg-white dark:text-black' : 'border-neutral-300 dark:border-neutral-700'}`}>{label}</button>
            ))}
          </div>
        </div>
        <div className="h-[430px] bg-neutral-50 dark:bg-neutral-950/60">
          <ReactFlow nodes={flow.nodes} edges={flow.edges} nodeTypes={nodeTypes} fitView minZoom={0.6} maxZoom={1.35} nodesDraggable={false} nodesConnectable={false} elementsSelectable={false} proOptions={{ hideAttribution: true }}>
            <Background gap={26} size={1} />
            <Controls showInteractive={false} />
          </ReactFlow>
        </div>
      </Card>

      {selected ? (
        <Card className="p-0 overflow-hidden">
          <div className="flex flex-wrap items-center justify-between gap-3 border-b p-5 dark:border-neutral-800">
            <div>
              <div className="flex items-center gap-2 text-2xl font-black"><Server size={22} /> Server {selected.id}</div>
              <div className="mt-1 text-sm text-neutral-500">{selected.host?.hostname || '—'} · {String(selected.role || 'unknown').toUpperCase()} · {String(selected.appProfile || 'unknown').toUpperCase()}</div>
              {selected.bundleVersion ? <div className="mt-1 text-xs text-neutral-400">Cluster manager v{selected.bundleVersion}{selected.bundleRevision ? ` r${selected.bundleRevision}` : ''}</div> : null}
            </div>
            <div className="flex flex-wrap gap-2">
              <Pill tone={selected.online ? 'good' : 'bad'}>{selected.online ? 'ONLINE' : 'OFFLINE'}</Pill>
              <Pill tone={selected.firewall?.status === 'active' ? 'good' : 'bad'}>UFW {selected.firewall?.status || '?'}</Pill>
              <Pill tone={selected.minio?.ready ? 'good' : 'bad'}>MINIO {selected.minio?.ready ? 'READY' : 'DOWN'}</Pill>
              <Pill tone={selected.tlsReady ? 'good' : 'bad'}>TLS {selected.tlsReady ? 'READY' : 'CHECK'}</Pill>
            </div>
          </div>

          <div className="grid gap-5 p-5 xl:grid-cols-[1fr_1fr_1.35fr]">
            <div className="space-y-4">
              <div className="font-bold flex items-center gap-2"><Cpu size={17} /> Железо</div>
              <div className="grid grid-cols-2 gap-2 text-sm">
                <div className="rounded-xl bg-neutral-100 p-3 dark:bg-neutral-900"><div className="text-xs text-neutral-500">CPU</div><b>{selected.host?.cpu_percent == null ? '—' : `${selected.host.cpu_percent}%`}</b></div>
                <div className="rounded-xl bg-neutral-100 p-3 dark:bg-neutral-900"><div className="text-xs text-neutral-500">Ядра</div><b>{selected.host?.cpu_count ?? '—'}</b></div>
                <div className="rounded-xl bg-neutral-100 p-3 dark:bg-neutral-900"><div className="text-xs text-neutral-500">Температура</div><b>{selected.host?.temperature_c == null ? '—' : `${selected.host.temperature_c} °C`}</b></div>
                <div className="rounded-xl bg-neutral-100 p-3 dark:bg-neutral-900"><div className="text-xs text-neutral-500">Uptime</div><b>{fmtUptime(selected.host?.uptime_seconds)}</b></div>
              </div>
              <MetricBar label="RAM" used={selected.host?.memory?.used || 0} total={selected.host?.memory?.total || 0} />
              <MetricBar label="Диск" used={selected.host?.disk?.used || 0} total={selected.host?.disk?.total || 0} />
            </div>

            <div className="space-y-4">
              <div className="font-bold flex items-center gap-2"><Database size={17} /> HA и данные</div>
              <div className="space-y-2 text-sm">
                <div className="flex justify-between rounded-xl bg-neutral-100 p-3 dark:bg-neutral-900"><span>Роль</span><b>{String(selected.role || '—').toUpperCase()}</b></div>
                <div className="flex justify-between rounded-xl bg-neutral-100 p-3 dark:bg-neutral-900"><span>PostgreSQL</span><b className={selected.postgres?.healthy ? 'text-emerald-600 dark:text-emerald-400' : 'text-rose-600 dark:text-rose-400'}>{selected.postgres?.healthy ? 'HEALTHY' : 'CHECK'}</b></div>
                {String(selected.postgres?.actual_role || '').toLowerCase() === 'standby' ? <div className="flex justify-between rounded-xl bg-neutral-100 p-3 dark:bg-neutral-900"><span>WAL receiver</span><b>{String(selected.postgres?.receiver || '—').toUpperCase()} · lag {fmtBytes(selected.postgres?.replay_gap_bytes)}</b></div> : null}
                {String(selected.postgres?.actual_role || '').toLowerCase() === 'primary' && Array.isArray(selected.postgres?.replicas) && selected.postgres.replicas.length ? (
                  <div className="rounded-xl bg-neutral-100 p-3 dark:bg-neutral-900">
                    <div className="mb-2 flex justify-between"><span>Реплики</span><b>{selected.postgres.replicas.length}</b></div>
                    <div className="space-y-1 text-xs text-neutral-500">
                      {selected.postgres.replicas.map((replica) => (
                        <div key={`${replica.host}-${replica.state}`} className="flex justify-between gap-3">
                          <span>{replica.host || 'standby'} · {String(replica.state || '—').toUpperCase()} · {String(replica.sync_state || '—').toUpperCase()}</span>
                          <b className="text-neutral-700 dark:text-neutral-300">lag {fmtBytes(replica.lag_bytes)}</b>
                        </div>
                      ))}
                    </div>
                  </div>
                ) : null}
                <div className="flex justify-between rounded-xl bg-neutral-100 p-3 dark:bg-neutral-900"><span>App mode</span><b>{appModeText[selected.appMode] || selected.appMode || '—'}</b></div>
                <div className="flex justify-between rounded-xl bg-neutral-100 p-3 dark:bg-neutral-900"><span>Traffic</span><b>{selected.trafficReady ? 'READY' : 'STANDBY'}</b></div>
                <div className="flex justify-between rounded-xl bg-neutral-100 p-3 dark:bg-neutral-900"><span>Профиль</span><b>{String(selected.appProfile || '—').toUpperCase()}</b></div>
                <div className="flex justify-between rounded-xl bg-neutral-100 p-3 dark:bg-neutral-900"><span>Образы готовы</span><b>{selected.docker?.imagesReady ?? 0}/{selected.docker?.assignedAppCount ?? 0}</b></div>
                <div className="flex justify-between rounded-xl bg-neutral-100 p-3 dark:bg-neutral-900"><span>Контейнеры готовы</span><b>{selected.docker?.preparedAppCount ?? 0}/{selected.docker?.assignedAppCount ?? 0}</b></div>
                <div className="flex justify-between rounded-xl bg-neutral-100 p-3 dark:bg-neutral-900"><span>Hot start</span><b className={selected.hotStartReady ? 'text-emerald-600 dark:text-emerald-400' : 'text-rose-600 dark:text-rose-400'}>{selected.hotStartReady ? 'READY' : 'NOT READY'}</b></div>
                {!selected.hotStartReady && selected.hotStartBlockers?.length ? <div className="rounded-xl border border-amber-500/30 bg-amber-500/5 p-3 text-xs text-amber-700 dark:text-amber-300">Мешает горячему запуску: {selected.hotStartBlockers.map(hotStartBlockerText).join(', ')}</div> : null}
                <div className="flex justify-between rounded-xl bg-neutral-100 p-3 dark:bg-neutral-900"><span>Watchtower</span><b className={selected.update?.watchtowerRunning ? 'text-emerald-600 dark:text-emerald-400' : 'text-rose-600 dark:text-rose-400'}>{selected.update?.watchtowerRunning ? 'RUNNING' : 'STOPPED'}</b></div>
                <div className="flex justify-between gap-3 rounded-xl bg-neutral-100 p-3 dark:bg-neutral-900"><span>Обновление</span><b className={selected.update?.hasError ? 'text-rose-600 dark:text-rose-400' : ''}>{updateStatusText[selected.update?.status] || selected.update?.status || '—'}</b></div>
                <div className="flex justify-between rounded-xl bg-neutral-100 p-3 dark:bg-neutral-900"><span>Проверка обновлений</span><b>{selected.update?.pollIntervalSeconds ? `каждые ${selected.update.pollIntervalSeconds} с` : '—'}</b></div>
              </div>
              <div className="rounded-xl border p-3 text-xs dark:border-neutral-800">
                <div className="flex items-center gap-2 font-semibold"><Wifi size={14} /> WireGuard</div>
                <div className="mt-2 text-neutral-500">Пиров: {selected.wireguard?.peers?.length ?? 0}</div>
              </div>
            </div>

            <div>
              <div className="mb-3 flex items-center justify-between"><div className="font-bold flex items-center gap-2"><Box size={17} /> Контейнеры</div><div className="text-xs text-neutral-500">{selected.containers?.length || 0}</div></div>
              <div className="max-h-[330px] space-y-2 overflow-auto pr-1">
                {(selected.containers || []).map((c) => {
                  const prepared = ['created', 'exited'].includes(c.state);
                  const tone = c.state === 'running' && c.health !== 'unhealthy' ? 'good' : prepared ? 'neutral' : 'bad';
                  const stateLabel = c.state === 'running' ? (c.health || 'running') : prepared ? 'PREPARED' : (c.health || c.state);
                  return (
                    <div key={c.name || c.service} className="flex items-center justify-between gap-3 rounded-xl border p-3 text-sm dark:border-neutral-800">
                      <div className="min-w-0"><div className="truncate font-semibold">{c.service || c.name}</div><div className="mt-0.5 text-xs text-neutral-500">CPU {c.cpu || '—'} · RAM {c.memory || '—'} · рестарты {c.restartCount || 0}</div></div>
                      <Pill tone={tone}>{stateLabel}</Pill>
                    </div>
                  );
                })}
              </div>
            </div>
          </div>
        </Card>
      ) : null}

      <Card className="p-0 overflow-hidden">
        <div className="border-b p-5 dark:border-neutral-800"><div className="font-bold text-xl">Синхронизация образов</div><div className="mt-1 text-sm text-neutral-500">Технические идентификаторы скрыты. Показывается только совпадение версий между нодами.</div></div>
        <div className="overflow-x-auto">
          <table className="w-full min-w-[720px] text-sm">
            <thead><tr className="border-b text-left text-xs uppercase tracking-wide text-neutral-500 dark:border-neutral-800"><th className="p-4">Сервис</th>{nodesData.map((n) => <th key={n.id} className="p-4">Server {n.id}</th>)}</tr></thead>
            <tbody>
              {allServices.map(([service, perNode]) => (
                <tr key={service} className="border-b last:border-b-0 dark:border-neutral-800/70">
                  <td className="p-4 font-semibold">{service}</td>
                  {nodesData.map((n) => {
                    const svc = perNode[n.id];
                    if (!svc) return <td key={n.id} className="p-4"><Pill>Нет данных</Pill></td>;
                    return <td key={n.id} className="p-4"><Pill tone={imageTone(svc.imageStatus)}>{stateText[svc.imageStatus] || svc.imageStatus}</Pill><div className="mt-1 text-xs text-neutral-400">{svc.containerState === 'running' ? 'Запущен' : ['prepared', 'image-ready'].includes(svc.desiredState) ? 'Готов к запуску' : svc.desiredState === 'not-assigned' ? 'Не используется' : svc.containerState || '—'}</div></td>;
                  })}
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </Card>

      <Card className="p-5">
        <div className="flex items-center justify-between gap-3"><div><div className="font-bold text-xl">События кластера</div><div className="text-sm text-neutral-500">Обновления, переключения ролей и ошибки Node Agent.</div></div><ChevronRight size={18} className="text-neutral-400" /></div>
        <div className="mt-4 divide-y dark:divide-neutral-800">
          {(data?.events || []).slice(0, 30).map((evt) => (
            <div key={evt.id} className="flex gap-3 py-3">
              <div className="mt-0.5">{evt.severity === 'error' ? <XCircle size={17} className="text-rose-500" /> : evt.severity === 'warning' ? <AlertTriangle size={17} className="text-amber-500" /> : <CheckCircle2 size={17} className="text-emerald-500" />}</div>
              <div className="min-w-0 flex-1"><div className="flex flex-wrap items-center gap-2"><b>{evt.title || evt.kind}</b>{evt.node ? <Pill>{evt.node}</Pill> : null}</div><div className="mt-1 text-sm text-neutral-500">{evt.message}</div></div>
              <div className="whitespace-nowrap text-xs text-neutral-400">{evt.at ? new Date(evt.at).toLocaleString() : '—'}</div>
            </div>
          ))}
          {!data?.events?.length ? <div className="py-8 text-center text-sm text-neutral-500">Событий пока нет.</div> : null}
        </div>
      </Card>
    </div>
  );
}
