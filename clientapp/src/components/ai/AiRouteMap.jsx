import React, { useCallback, useEffect, useMemo, useState } from 'react';
import {
  ReactFlow,
  Background,
  Controls,
  MiniMap,
  MarkerType,
  Handle,
  Position,
} from '@xyflow/react';
import '@xyflow/react/dist/style.css';
import {
  AlertTriangle,
  Bot,
  Brain,
  Database,
  GitBranch,
  ShieldCheck,
  Sparkles,
  User,
  Wrench,
} from 'lucide-react';
import { Badge, Button } from '../ui';

function truncateText(value, max = 180) {
  const text = String(value || '');
  if (!text) return '';
  return text.length > max ? `${text.slice(0, max)}…` : text;
}

function normalizeToolCalls(message) {
  if (Array.isArray(message?.toolCalls) && message.toolCalls.length > 0) return message.toolCalls;
  return message?.toolCall ? [message.toolCall] : [];
}

function normalizeToolResults(message) {
  if (Array.isArray(message?.toolResults) && message.toolResults.length > 0) return message.toolResults;
  return message?.toolResult ? [message.toolResult] : [];
}

function createNode(id, kind, position, data) {
  return {
    id,
    type: 'routeMapNode',
    position,
    draggable: false,
    selectable: true,
    data: { ...data, kind },
  };
}

function createEdge(id, source, target, label = '', extra = {}) {
  return {
    id,
    source,
    target,
    label,
    animated: false,
    markerEnd: { type: MarkerType.ArrowClosed },
    style: { strokeWidth: 1.65 },
    labelStyle: { fontSize: 11, fill: 'currentColor' },
    ...extra,
  };
}

const KIND_META = {
  user: {
    title: 'Пользователь',
    icon: User,
    ring: 'rgba(59, 130, 246, 0.28)',
    background: 'rgba(59, 130, 246, 0.10)',
    text: '#1d4ed8',
  },
  assistant: {
    title: 'Ответ модели',
    icon: Bot,
    ring: 'rgba(16, 185, 129, 0.28)',
    background: 'rgba(16, 185, 129, 0.10)',
    text: '#047857',
  },
  route: {
    title: 'Routing',
    icon: GitBranch,
    ring: 'rgba(245, 158, 11, 0.28)',
    background: 'rgba(245, 158, 11, 0.12)',
    text: '#b45309',
  },
  action: {
    title: 'Tool call',
    icon: Wrench,
    ring: 'rgba(139, 92, 246, 0.28)',
    background: 'rgba(139, 92, 246, 0.12)',
    text: '#6d28d9',
  },
  result: {
    title: 'Tool result',
    icon: Sparkles,
    ring: 'rgba(14, 165, 233, 0.28)',
    background: 'rgba(14, 165, 233, 0.12)',
    text: '#0369a1',
  },
  resolution: {
    title: 'Resolution',
    icon: ShieldCheck,
    ring: 'rgba(244, 63, 94, 0.28)',
    background: 'rgba(244, 63, 94, 0.12)',
    text: '#be123c',
  },
  memory: {
    title: 'Состояние',
    icon: Database,
    ring: 'rgba(100, 116, 139, 0.28)',
    background: 'rgba(100, 116, 139, 0.12)',
    text: '#334155',
  },
  system: {
    title: 'Системный шаг',
    icon: Brain,
    ring: 'rgba(148, 163, 184, 0.28)',
    background: 'rgba(148, 163, 184, 0.12)',
    text: '#475569',
  },
};

const TRACE_FILTERS = [
  { key: 'all', label: 'Всё' },
  { key: 'routing', label: 'Routing' },
  { key: 'tools', label: 'Tools' },
  { key: 'entities', label: 'Jobs/Batches' },
  { key: 'failures', label: 'Ошибки' },
];

function getTraceKindMeta(kind) {
  if (kind === 'user-message') return { nodeKind: 'user', lane: 0 };
  if (kind === 'memory') return { nodeKind: 'memory', lane: 1 };
  if (kind === 'routing') return { nodeKind: 'route', lane: 1 };
  if (kind === 'tool-call') return { nodeKind: 'action', lane: 2 };
  if (kind === 'tool-result') return { nodeKind: 'result', lane: 3 };
  if (kind === 'assistant-message') return { nodeKind: 'assistant', lane: 4 };
  if (kind === 'job' || kind === 'batch') return { nodeKind: 'memory', lane: 2 };
  return { nodeKind: 'system', lane: 5 };
}

function RouteMapNode({ data, selected }) {
  const meta = KIND_META[data.kind] || KIND_META.system;
  const Icon = meta.icon;
  const badges = Array.isArray(data.badges) ? data.badges.filter(Boolean).slice(0, 5) : [];

  return (
    <div
      style={{
        width: 248,
        borderRadius: 18,
        border: `1px solid ${meta.ring}`,
        background: meta.background,
        boxShadow: selected ? `0 0 0 2px ${meta.ring}` : 'none',
        color: 'rgb(var(--foreground))',
        overflow: 'hidden',
      }}
    >
      <Handle type="target" position={Position.Left} style={{ opacity: 0 }} />
      <div style={{ padding: '10px 12px', borderBottom: `1px solid ${meta.ring}` }}>
        <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
          <Icon size={16} style={{ color: meta.text, flex: '0 0 auto' }} />
          <div style={{ fontSize: 11, letterSpacing: '0.12em', textTransform: 'uppercase', opacity: 0.72 }}>{meta.title}</div>
        </div>
        <div style={{ marginTop: 6, fontWeight: 600, lineHeight: 1.35 }}>{data.label}</div>
        {data.subtitle ? <div style={{ marginTop: 4, fontSize: 12, opacity: 0.72, lineHeight: 1.4 }}>{data.subtitle}</div> : null}
      </div>
      <div style={{ padding: '10px 12px' }}>
        {badges.length > 0 ? (
          <div style={{ display: 'flex', flexWrap: 'wrap', gap: 6, marginBottom: data.body ? 8 : 0 }}>
            {badges.map((item) => (
              <span
                key={`${data.id || data.label}-${item}`}
                style={{
                  fontSize: 11,
                  borderRadius: 999,
                  border: `1px solid ${meta.ring}`,
                  padding: '2px 8px',
                  opacity: 0.88,
                }}
              >
                {item}
              </span>
            ))}
          </div>
        ) : null}
        {data.body ? <div style={{ fontSize: 12, lineHeight: 1.5, opacity: 0.82, whiteSpace: 'pre-wrap' }}>{data.body}</div> : null}
      </div>
      <Handle type="source" position={Position.Right} style={{ opacity: 0 }} />
    </div>
  );
}

function buildCapabilityGraph() {
  const nodes = [
    createNode('cap-user', 'user', { x: 0, y: 120 }, {
      id: 'cap-user',
      label: 'Сообщение в чате',
      subtitle: 'новый prompt или уточнение',
      body: 'Каждый новый ход пользователя может переключить маршрут, пересобрать цель и перезаписать состояние сессии.',
      badges: ['prompt', 'intent'],
    }),
    createNode('cap-memory', 'memory', { x: 290, y: 0 }, {
      id: 'cap-memory',
      label: 'Память и агентное состояние',
      subtitle: 'summary, facts, draft blueprint, agent state',
      body: 'Backend собирает краткое состояние, ограничения, прошлые действия, цели и текущий план агента.',
      badges: ['memory', 'state', 'director'],
    }),
    createNode('cap-route', 'route', { x: 290, y: 240 }, {
      id: 'cap-route',
      label: 'Routing / scenario selection',
      subtitle: 'anchor, pre-anchor, onboarding, review',
      body: 'На этом шаге решается, чему учить, надо ли продолжать onboarding, запускать batch, review или правку draft.',
      badges: ['route', 'mode', 'anchor'],
    }),
    createNode('cap-actions', 'action', { x: 620, y: 120 }, {
      id: 'cap-actions',
      label: 'Tool calls и orchestration',
      subtitle: 'несколько внутренних действий подряд',
      body: 'AI может ходить по инструментам в mono или multi режиме, цепочкой обновляя внутренний план.',
      badges: ['tools', 'loop', 'worker'],
    }),
    createNode('cap-validation', 'resolution', { x: 950, y: 120 }, {
      id: 'cap-validation',
      label: 'Validation / override / guardrails',
      subtitle: 'коррекция опасных или неверных шагов',
      body: 'Сервер фиксирует причины override, route snapshots, фейлы, validation issues и решение: идём дальше, правим или останавливаемся.',
      badges: ['override', 'validation'],
    }),
    createNode('cap-entities', 'memory', { x: 1280, y: 0 }, {
      id: 'cap-entities',
      label: 'Связанные сущности',
      subtitle: 'jobs, batches, drafts, assignments',
      body: 'Из чата можно управлять пакетами, черновиками, self-check и публикацией, а сервер связывает это с конкретной сессией.',
      badges: ['batch', 'job', 'draft'],
    }),
    createNode('cap-reply', 'assistant', { x: 1280, y: 240 }, {
      id: 'cap-reply',
      label: 'Ответ + debug artefacts',
      subtitle: 'то, что видит оператор',
      body: 'В чат возвращаются текст, tool-results, route snapshots, dev trace, live counters и следующая рекомендация.',
      badges: ['reply', 'debug'],
    }),
  ];

  const edges = [
    createEdge('cap-e1', 'cap-user', 'cap-memory'),
    createEdge('cap-e2', 'cap-user', 'cap-route'),
    createEdge('cap-e3', 'cap-memory', 'cap-actions', 'state'),
    createEdge('cap-e4', 'cap-route', 'cap-actions', 'route'),
    createEdge('cap-e5', 'cap-actions', 'cap-validation'),
    createEdge('cap-e6', 'cap-validation', 'cap-entities', 'linked entities'),
    createEdge('cap-e7', 'cap-validation', 'cap-reply', 'reply'),
    createEdge('cap-e8', 'cap-entities', 'cap-reply', 'evidence'),
  ];

  return { nodes, edges, defaultSelectedId: 'cap-route' };
}

function buildLiveGraphFromTrace(trace) {
  const events = Array.isArray(trace?.events) ? trace.events.slice(-18) : [];
  const nodes = [];
  const edges = [];
  const laneY = [30, 200, 370, 540, 710, 880];

  let previousNodeId = null;
  events.forEach((event, index) => {
    const meta = getTraceKindMeta(event.kind);
    const nodeId = event.id || `trace-${index}`;
    const x = index * 290;
    const y = laneY[meta.lane] || 30;
    const badges = [
      event.status || null,
      event.routeMode ? `route:${event.routeMode}` : null,
      event.actionName ? `action:${event.actionName}` : null,
      event.overrideReason ? `override:${event.overrideReason}` : null,
      event.relatedEntityType && event.relatedEntityId ? `${event.relatedEntityType}:${String(event.relatedEntityId).slice(0, 6)}` : null,
    ].filter(Boolean);

    nodes.push(createNode(nodeId, meta.nodeKind, { x, y }, {
      id: nodeId,
      label: event.title || event.kind || 'event',
      subtitle: [event.stage, event.timestampUtc ? new Date(event.timestampUtc).toLocaleTimeString('ru-RU', { hour: '2-digit', minute: '2-digit' }) : ''].filter(Boolean).join(' · '),
      body: truncateText(event.summary || '', 190),
      badges,
      raw: event,
    }));

    if (previousNodeId) {
      edges.push(createEdge(`edge-${previousNodeId}-${nodeId}`, previousNodeId, nodeId, event.kind === 'routing' ? 'route' : ''));
    }
    previousNodeId = nodeId;
  });

  return { nodes, edges, defaultSelectedId: nodes[0]?.id || null };
}

function buildLiveGraphFromMessages(messages = []) {
  const recent = (messages || []).slice(-8);
  const nodes = [];
  const edges = [];
  let previousId = null;

  recent.forEach((message, index) => {
    const created = message?.createdAtUtc ? new Date(message.createdAtUtc).toLocaleTimeString('ru-RU', { hour: '2-digit', minute: '2-digit' }) : '';
    const x = index * 320;
    const role = String(message?.role || '').toLowerCase();
    const toolCalls = normalizeToolCalls(message);
    const toolResults = normalizeToolResults(message);
    const debugInfo = toolResults.find((item) => item?.debugInfo)?.debugInfo || {};
    const routing = debugInfo.routing || {};

    const primaryId = `msg-${index}`;
    const primaryKind = role === 'user' ? 'user' : role === 'assistant' ? 'assistant' : 'system';
    nodes.push(createNode(primaryId, primaryKind, { x, y: primaryKind === 'user' ? 30 : 730 }, {
      id: primaryId,
      label: truncateText(message?.content || (role === 'user' ? 'Запрос пользователя' : 'Системный шаг'), 70),
      subtitle: [role || 'message', created].filter(Boolean).join(' · '),
      body: truncateText(message?.content || '', 180),
      badges: [message?.status || null].filter(Boolean),
      raw: message,
    }));
    if (previousId) edges.push(createEdge(`edge-${previousId}-${primaryId}`, previousId, primaryId));
    previousId = primaryId;

    if (routing.mode || routing.rawMode) {
      const routeId = `route-${index}`;
      nodes.push(createNode(routeId, 'route', { x, y: 210 }, {
        id: routeId,
        label: routing.mode || routing.rawMode || 'route',
        subtitle: 'routing snapshot',
        body: truncateText(routing.preAnchorReason || '', 160),
        badges: [routing.rawMode && routing.rawMode !== routing.mode ? `raw:${routing.rawMode}` : null].filter(Boolean),
        raw: routing,
      }));
      edges.push(createEdge(`edge-${primaryId}-${routeId}`, primaryId, routeId, 'route'));
      previousId = routeId;
    }

    toolCalls.slice(0, 3).forEach((tool, toolIndex) => {
      const id = `call-${index}-${toolIndex}`;
      nodes.push(createNode(id, 'action', { x, y: 400 + toolIndex * 120 }, {
        id,
        label: tool?.name || 'tool call',
        subtitle: 'tool call',
        body: truncateText(tool?.reason || '', 160),
        badges: [],
        raw: tool,
      }));
      edges.push(createEdge(`edge-${previousId}-${id}`, previousId, id));
      previousId = id;
    });

    toolResults.slice(0, 3).forEach((result, resultIndex) => {
      const id = `result-${index}-${resultIndex}`;
      nodes.push(createNode(id, result?.debugInfo?.resolution?.overrideReason && result?.debugInfo?.resolution?.overrideReason !== 'none' ? 'resolution' : 'result', { x, y: 610 + resultIndex * 120 }, {
        id,
        label: result?.actionName || result?.status || 'tool result',
        subtitle: result?.status || 'result',
        body: truncateText(result?.summary || '', 160),
        badges: [result?.batchId ? `batch:${String(result.batchId).slice(0, 6)}` : null, result?.jobId ? `job:${String(result.jobId).slice(0, 6)}` : null].filter(Boolean),
        raw: result,
      }));
      edges.push(createEdge(`edge-${previousId}-${id}`, previousId, id));
      previousId = id;
    });
  });

  return { nodes, edges, defaultSelectedId: nodes[0]?.id || null };
}

const nodeTypes = { routeMapNode: RouteMapNode };

function matchesTraceFilter(event, filter) {
  if (!event) return false;
  if (filter === 'routing') return event.kind === 'routing' || Boolean(event.routeMode) || Boolean(event.overrideReason);
  if (filter === 'tools') return event.kind === 'tool-call' || event.kind === 'tool-result';
  if (filter === 'entities') return event.kind === 'job' || event.kind === 'batch' || Boolean(event.relatedEntityType);
  if (filter === 'failures') return ['failed', 'error', 'cancelled'].includes(String(event.status || '').toLowerCase()) || Boolean(event.overrideReason);
  return true;
}

function SummaryCard({ title, value, hint, danger = false }) {
  return (
    <div className={`rounded-2xl border px-3 py-3 ${danger ? 'border-red-300/60 dark:border-red-700/40 bg-red-50/40 dark:bg-red-950/10' : 'border-neutral-200/70 dark:border-neutral-800 bg-white/60 dark:bg-neutral-950/30'}`}>
      <div className="text-[11px] uppercase tracking-[0.16em] opacity-55">{title}</div>
      <div className="mt-2 text-2xl font-semibold">{value}</div>
      {hint ? <div className="mt-1 text-xs opacity-70">{hint}</div> : null}
    </div>
  );
}

function TraceTimeline({ events = [], filter = 'all' }) {
  const filtered = events.filter((event) => matchesTraceFilter(event, filter));
  if (filtered.length === 0) {
    return (
      <div className="rounded-2xl border border-dashed border-neutral-300/70 dark:border-neutral-700 p-4 text-sm opacity-70">
        Для выбранного фильтра пока нет событий.
      </div>
    );
  }

  return (
    <div className="space-y-3 max-h-[18rem] overflow-auto pr-1">
      {filtered.map((event) => {
        const danger = ['failed', 'error', 'cancelled'].includes(String(event.status || '').toLowerCase()) || Boolean(event.overrideReason);
        const meta = getTraceKindMeta(event.kind);
        const iconMeta = KIND_META[meta.nodeKind] || KIND_META.system;
        const Icon = iconMeta.icon;
        return (
          <details key={event.id} className={`rounded-2xl border px-3 py-3 ${danger ? 'border-red-300/60 dark:border-red-700/40' : 'border-neutral-200/70 dark:border-neutral-800'} bg-white/70 dark:bg-neutral-950/30`}>
            <summary className="cursor-pointer list-none">
              <div className="flex items-start gap-3">
                <div className="mt-0.5 rounded-2xl p-2" style={{ background: iconMeta.background, color: iconMeta.text }}>
                  <Icon size={15} />
                </div>
                <div className="min-w-0 flex-1">
                  <div className="flex flex-wrap items-center gap-2">
                    <span className="font-medium">{event.title || event.kind}</span>
                    {event.status ? <Badge variant={danger ? 'danger' : 'outline'}>{event.status}</Badge> : null}
                    {event.routeMode ? <Badge variant="outline">route: {event.routeMode}</Badge> : null}
                    {event.overrideReason ? <Badge variant="danger">override: {event.overrideReason}</Badge> : null}
                  </div>
                  <div className="mt-1 text-xs opacity-65">{[event.stage, event.timestampUtc ? new Date(event.timestampUtc).toLocaleString('ru-RU') : ''].filter(Boolean).join(' · ')}</div>
                  {event.summary ? <div className="mt-2 text-sm opacity-80 whitespace-pre-wrap">{truncateText(event.summary, 220)}</div> : null}
                </div>
              </div>
            </summary>
            {event.payloadJson ? <pre className="mt-3 overflow-auto whitespace-pre-wrap break-words text-xs opacity-75">{event.payloadJson}</pre> : null}
          </details>
        );
      })}
    </div>
  );
}

export default function AiRouteMap({ messages = [], session = null, trace = null, traceLoading = false, compact = false }) {
  const [view, setView] = useState('timeline');
  const [selectedId, setSelectedId] = useState(null);
  const [flowInstance, setFlowInstance] = useState(null);
  const [traceFilter, setTraceFilter] = useState('all');

  const capabilityGraph = useMemo(() => buildCapabilityGraph(), []);
  const liveGraph = useMemo(() => {
    if (Array.isArray(trace?.events) && trace.events.length > 0) return buildLiveGraphFromTrace(trace);
    return buildLiveGraphFromMessages(messages);
  }, [messages, trace]);
  const activeGraph = view === 'capability' ? capabilityGraph : liveGraph;

  useEffect(() => {
    setSelectedId(activeGraph.defaultSelectedId || activeGraph.nodes?.[0]?.id || null);
  }, [activeGraph.defaultSelectedId, activeGraph.nodes]);

  const selectedNode = useMemo(() => {
    if (!selectedId) return null;
    return activeGraph.nodes.find((node) => node.id === selectedId) || null;
  }, [activeGraph.nodes, selectedId]);

  const onResetView = useCallback(() => {
    flowInstance?.fitView({ padding: 0.2, duration: 280 });
  }, [flowInstance]);

  const onPaneReady = useCallback((instance) => {
    setFlowInstance(instance);
    window.setTimeout(() => instance.fitView({ padding: 0.2, duration: 280 }), 0);
  }, []);

  const traceSummary = trace?.summary || {};
  const filteredTraceEvents = useMemo(() => {
    const events = Array.isArray(trace?.events) ? trace.events : [];
    return events.filter((event) => matchesTraceFilter(event, traceFilter));
  }, [trace, traceFilter]);

  const safeNodes = activeGraph.nodes.length > 0
    ? activeGraph.nodes
    : [createNode('empty', 'system', { x: 0, y: 0 }, {
      id: 'empty',
      label: 'Пока нет трассы',
      subtitle: 'Нужен хотя бы один осмысленный проход AI',
      body: 'Когда сервер соберет trace events или в сообщениях появятся route snapshots и tool-results, карта построится автоматически.',
      badges: ['waiting'],
      raw: null,
    })];
  const safeEdges = activeGraph.nodes.length > 0 ? activeGraph.edges : [];
  const hasMeaningfulGraph = safeNodes.length > 1 || safeEdges.length > 0;
  const flowHeight = compact ? (view === 'capability' ? 300 : 360) : (view === 'capability' ? 360 : 520);

  const currentSummaryText = trace?.events?.length
    ? `Серверный trace уже собран: ${trace.events.length} событий, ${traceSummary.routeCount || 0} route-шагов, ${traceSummary.toolCallCount || 0} tool calls.`
    : 'Сейчас карта строится в основном из сообщений. Как только backend trace появится, схема станет богаче и точнее.';

  return (
    <div className={`rounded-3xl border border-dashed border-[rgba(var(--accent)/0.35)] bg-[rgba(var(--accent)/0.05)] ${compact ? 'p-3' : 'p-4'}`}>
      <div className="flex flex-col gap-3 xl:flex-row xl:items-start xl:justify-between">
        <div>
          <div className="flex flex-wrap items-center gap-2">
            <Badge variant="success">операционная карта AI</Badge>
            {session?.memory?.messageCount ? <Badge variant="outline">сообщений: {session.memory.messageCount}</Badge> : null}
            {traceLoading ? <Badge variant="outline">trace обновляется…</Badge> : null}
            {trace?.events?.length ? <Badge variant="outline">events: {trace.events.length}</Badge> : null}
          </div>
          <div className="mt-2 text-sm opacity-80 leading-6">{currentSummaryText}</div>
        </div>
        <div className="flex flex-wrap gap-2">
          <Button type="button" variant={view === 'live' ? 'primary' : 'outline'} onClick={() => setView('live')}>Живая карта</Button>
          <Button type="button" variant={view === 'timeline' ? 'primary' : 'outline'} onClick={() => setView('timeline')}>Trace timeline</Button>
          <Button type="button" variant={view === 'capability' ? 'primary' : 'outline'} onClick={() => setView('capability')}>Системная схема</Button>
          <Button type="button" variant="outline" onClick={onResetView}>Сбросить вид</Button>
        </div>
      </div>

      <div className={`mt-4 grid gap-3 md:grid-cols-2 ${compact ? 'xl:grid-cols-5' : '2xl:grid-cols-5 xl:grid-cols-3'}`}>
        <SummaryCard title="Сообщения" value={traceSummary.messageCount ?? messages.length ?? 0} hint="ходы в сессии" />
        <SummaryCard title="Tool calls" value={traceSummary.toolCallCount ?? 0} hint="вызовов инструментов" />
        <SummaryCard title="Tool results" value={traceSummary.toolResultCount ?? 0} hint="результатов действий" />
        <SummaryCard title="Routes" value={traceSummary.routeCount ?? 0} hint="routing snapshots" />
        <SummaryCard title="Проблемы" value={(traceSummary.failedCount ?? 0) + (traceSummary.overrideCount ?? 0)} hint={`failed: ${traceSummary.failedCount ?? 0} · override: ${traceSummary.overrideCount ?? 0}`} danger />
      </div>

      <div className="mt-3 flex flex-wrap items-center gap-2 text-xs opacity-75">
        <span className="inline-flex items-center gap-1"><User size={12} /> пользователь</span>
        <span className="inline-flex items-center gap-1"><GitBranch size={12} /> routing</span>
        <span className="inline-flex items-center gap-1"><Wrench size={12} /> tool call</span>
        <span className="inline-flex items-center gap-1"><Sparkles size={12} /> tool result</span>
        <span className="inline-flex items-center gap-1"><ShieldCheck size={12} /> override</span>
        <span className="inline-flex items-center gap-1"><Database size={12} /> jobs / batches</span>
        <span className="inline-flex items-center gap-1"><Bot size={12} /> reply</span>
      </div>

      {view === 'timeline' ? (
        <div className="mt-4 rounded-3xl border border-neutral-200/70 dark:border-neutral-800 bg-white/70 dark:bg-neutral-950/40 p-4">
          <div className="flex flex-wrap items-center gap-2">
            <Badge variant="outline">фильтр</Badge>
            {TRACE_FILTERS.map((filter) => (
              <Button key={filter.key} type="button" variant={traceFilter === filter.key ? 'primary' : 'outline'} onClick={() => setTraceFilter(filter.key)} className="!rounded-full">
                {filter.label}
              </Button>
            ))}
            <span className="text-xs opacity-60">показано: {filteredTraceEvents.length}</span>
          </div>
          <div className="mt-4">
            <TraceTimeline events={trace?.events || []} filter={traceFilter} />
          </div>
        </div>
      ) : (
        <div className={`mt-4 grid gap-4 ${compact ? 'xl:grid-cols-[minmax(0,1fr)_320px]' : '2xl:grid-cols-[minmax(0,1fr)_360px] xl:grid-cols-[minmax(0,1fr)_320px]'}`}>
          <div className="rounded-3xl border border-neutral-200/70 dark:border-neutral-800 bg-white/70 dark:bg-neutral-950/40 overflow-hidden" style={{ height: flowHeight }}>
            {hasMeaningfulGraph ? <ReactFlow
              nodes={safeNodes}
              edges={safeEdges}
              nodeTypes={nodeTypes}
              onInit={onPaneReady}
              onNodeClick={(_, node) => setSelectedId(node.id)}
              fitView
              fitViewOptions={{ padding: 0.2 }}
              defaultEdgeOptions={{ type: 'smoothstep' }}
              proOptions={{ hideAttribution: true }}
              nodesDraggable={false}
              nodesConnectable={false}
              elementsSelectable
              zoomOnScroll
              panOnScroll
            >
              <MiniMap pannable zoomable style={{ width: compact ? 120 : 160, height: compact ? 80 : 100 }} />
              <Controls showInteractive={false} />
              <Background gap={20} size={1} />
            </ReactFlow> : (
              <div className="h-full grid place-items-center p-6">
                <div className="max-w-md rounded-3xl border border-dashed border-neutral-300/70 dark:border-neutral-700 bg-neutral-50/70 dark:bg-neutral-900/50 p-5 text-sm leading-6 opacity-80">
                  <div className="font-medium text-base">Живая карта появится после первого осмысленного прохода</div>
                  <div className="mt-2">Сейчас у сессии недостаточно route snapshots, tool calls или ответов, чтобы строить большой граф. Пока удобнее смотреть timeline справа.</div>
                </div>
              </div>
            )}
          </div>

          <div className="space-y-4">
            <div className="rounded-3xl border border-neutral-200/70 dark:border-neutral-800 bg-white/70 dark:bg-neutral-950/40 p-4">
              <div className="text-xs uppercase tracking-[0.16em] opacity-55">инспектор узла</div>
              {selectedNode ? (
                <>
                  <div className="mt-2 text-lg font-semibold">{selectedNode.data?.label}</div>
                  {selectedNode.data?.subtitle ? <div className="mt-1 text-sm opacity-70">{selectedNode.data.subtitle}</div> : null}
                  {selectedNode.data?.badges?.length > 0 ? (
                    <div className="mt-3 flex flex-wrap gap-2">
                      {selectedNode.data.badges.map((badge) => <Badge key={`${selectedNode.id}-${badge}`} variant="outline">{badge}</Badge>)}
                    </div>
                  ) : null}
                  {selectedNode.data?.body ? <div className="mt-3 text-sm whitespace-pre-wrap opacity-85 leading-6">{selectedNode.data.body}</div> : null}
                  {selectedNode.data?.raw ? (
                    <details className="mt-4 rounded-2xl border border-neutral-200/70 dark:border-neutral-800 bg-neutral-50/70 dark:bg-neutral-900/60 px-3 py-2 text-xs" open>
                      <summary className="cursor-pointer font-medium opacity-80">сырой payload</summary>
                      <pre className="mt-2 max-h-[24rem] overflow-auto whitespace-pre-wrap break-words opacity-75">{JSON.stringify(selectedNode.data.raw, null, 2)}</pre>
                    </details>
                  ) : null}
                </>
              ) : (
                <div className="mt-3 text-sm opacity-70 leading-6">Кликни по узлу на карте, чтобы посмотреть детали шага.</div>
              )}
            </div>

            <div className="rounded-3xl border border-neutral-200/70 dark:border-neutral-800 bg-white/70 dark:bg-neutral-950/40 p-4">
              <div className="flex items-center gap-2">
                <Badge variant="outline">короткий trace</Badge>
                {TRACE_FILTERS.map((filter) => (
                  <button key={filter.key} type="button" className={`text-xs rounded-full border px-2 py-1 ${traceFilter === filter.key ? 'border-[rgb(var(--accent))] bg-[rgba(var(--accent)/0.12)]' : 'border-neutral-200/70 dark:border-neutral-800'}`} onClick={() => setTraceFilter(filter.key)}>
                    {filter.label}
                  </button>
                ))}
              </div>
              <div className="mt-4">
                <TraceTimeline events={(trace?.events || []).slice(-8)} filter={traceFilter} />
              </div>
            </div>

            {traceSummary.failedCount > 0 || traceSummary.overrideCount > 0 ? (
              <div className="rounded-3xl border border-red-300/60 dark:border-red-700/40 bg-red-50/40 dark:bg-red-950/10 p-4 text-sm leading-6">
                <div className="flex items-center gap-2 font-medium text-red-700 dark:text-red-300"><AlertTriangle size={16} /> Проблемные точки</div>
                <div className="mt-2 opacity-80">В trace зафиксированы ошибки, отмены или override. Это полезно, чтобы понять, где агент сбивается, а где сервер его принудительно корректирует.</div>
              </div>
            ) : null}
          </div>
        </div>
      )}
    </div>
  );
}
