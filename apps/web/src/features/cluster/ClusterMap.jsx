import React, { memo, useCallback, useEffect, useMemo, useRef, useState } from 'react';
import ReactFlow, { applyNodeChanges, Background, Handle, MarkerType, MiniMap, Position } from 'reactflow';
import 'reactflow/dist/style.css';
import { Cloud, Database, Focus, Grip, LayoutGrid, LockKeyhole, Maximize2, Minimize2, Minus, Move, Network, Plus, Server, UnlockKeyhole } from 'lucide-react';
import { array, autoPositions, countPair, dateTime, EDGE_ID, nodeTone, NODE_HEIGHT, NODE_WIDTH, PHASE_LABELS, primaryRole, readLayout, saveLayout } from './clusterModel';
import { CopyValue, Tag } from './ClusterShared';

const Handles = () => <>{[['l', Position.Left], ['r', Position.Right], ['t', Position.Top], ['b', Position.Bottom]].flatMap(([id, position]) =>
  ['source', 'target'].map(type => <Handle key={`${type}-${id}`} id={`${type}-${id}`} type={type} position={position} isConnectable={false} />))}</>;

const ServerCard = memo(({ data }) => {
  const n = data.node;
  const isPrimary = primaryRole(n.role);
  const tone = nodeTone(n);
  return <div className={`tf-cluster-node ${data.selected ? 'is-selected' : ''} is-${tone}`}>
    <Handles />
    <div className="tf-cluster-node-head"><span className="tf-cluster-node-letter">{n.id}</span><div><strong>Сервер {n.id}</strong><span>{n.host?.hostname || `Node Agent${n.bundleRevision ? ` · r${n.bundleRevision}` : ''}`}</span></div><Grip size={16} className="tf-cluster-grip" /></div>
    <div className="tf-cluster-node-tags"><Tag tone={isPrimary ? 'accent' : 'muted'}>{isPrimary ? 'PRIMARY' : n.role === 'unreachable' ? 'НЕТ СВЯЗИ' : ['standby', 'replica'].includes(n.role) ? 'STANDBY' : 'РОЛЬ ?'}</Tag><Tag>{String(n.appProfile || '—').toUpperCase()}</Tag><Tag tone={tone} dot>{n.online ? n.telemetryFresh === false ? 'Устарело' : 'Онлайн' : 'Офлайн'}</Tag></div>
    <div className="tf-cluster-node-addresses"><CopyValue value={n.network?.publicHost} label="IP" compact /><CopyValue value={n.network?.wireguardIp} label="WG" compact /></div>
    <div className="tf-cluster-node-bottom"><span>{isPrimary ? 'Публичный трафик' : 'Горячий резерв'}</span><Tag tone={!n.online || n.telemetryFresh === false ? 'muted' : (isPrimary ? n.trafficReady : n.hotStartReady) ? 'good' : 'warn'}>{!n.online || n.telemetryFresh === false ? 'Нет свежих данных' : (isPrimary ? n.trafficReady : n.hotStartReady) ? 'Готов' : 'Не готов'}</Tag></div>
    <div className="tf-cluster-node-foot"><span>Контейнеры подготовлены</span><strong>{countPair(n.docker?.preparedAppCount, n.docker?.assignedAppCount)}</strong></div>
  </div>;
});
ServerCard.displayName = 'ClusterServerCard';
const EdgeCard = memo(({ data }) => {
  const s = data.edge?.state || {};
  return <div className="tf-cluster-edge-node"><Handles /><div className="tf-cluster-edge-icon"><Cloud size={24} /></div><strong>Cloudflare DNS</strong><Tag tone={data.fresh && s.success ? 'good' : 'muted'}>{!data.fresh ? 'Нет свежих данных' : data.edge?.configured ? PHASE_LABELS[s.phase] || s.status || 'Ожидание' : 'Не настроен'}</Tag><code>{s.target_ip || 'Origin не подтверждён'}</code></div>;
});
EdgeCard.displayName = 'ClusterEdgeCard';
const localStore = () => { try { return window.localStorage; } catch { return null; } };
const nodeTypes = { server: ServerCard, edge: EdgeCard };
const layers = [['ha', 'Обзор'], ['postgres', 'PostgreSQL'], ['minio', 'MinIO'], ['network', 'WireGuard']];

export default function ClusterMap({ nodes: items, activeId, selectedId, onSelect, storageKey }) {
  const [layer, setLayer] = useState('ha');
  const [locked, setLocked] = useState(false);
  const [full, setFull] = useState(false);
  const [storageWarning, setStorageWarning] = useState(false);
  const [fullscreenError, setFullscreenError] = useState('');
  const [nodes, setNodes] = useState([]);
  const panel = useRef(null);
  const instance = useRef(null);
  const saved = useMemo(() => readLayout(localStore(), storageKey), [storageKey]);
  const positions = useRef(saved.positions);
  const viewport = useRef(saved.viewport);
  const active = items.find(n => n.id === activeId);
  const hasEdge = layer === 'ha' && !!active?.edge;
  const structure = `${items.map(n => n.id).sort().join('|')}|${hasEdge}`;
  const placed = useRef(false);
  const fit = useCallback(() => instance.current?.fitView({ padding: 0.15, minZoom: 0.15, maxZoom: 1.1, duration: 240 }), []);
  const persist = useCallback(() => {
    setStorageWarning(!saveLayout(localStore(), storageKey, positions.current, viewport.current));
  }, [storageKey]);
  useEffect(() => {
    positions.current = saved.positions;
    viewport.current = saved.viewport;
    placed.current = false;
  }, [saved]);
  useEffect(() => {
    const defaults = autoPositions(items, activeId, hasEdge);
    setNodes(previous => {
      const byId = new Map(previous.map(n => [n.id, n]));
      const result = items.map(item => ({
        ...(byId.get(item.id) || {}), id: item.id, type: 'server',
        position: byId.get(item.id)?.position || positions.current[item.id] || defaults[item.id],
        data: { node: item, selected: item.id === selectedId },
        style: { width: NODE_WIDTH, height: NODE_HEIGHT },
        draggable: !locked, selectable: true, focusable: true,
        ariaLabel: `Сервер ${item.id}. ${item.isActive ? 'Основной' : 'Резервный'}. Нажмите Enter для выбора.`,
      }));
      if (hasEdge) result.push({ id: EDGE_ID, type: 'edge', position: byId.get(EDGE_ID)?.position || positions.current[EDGE_ID] || defaults[EDGE_ID], data: { edge: active?.edge, fresh: active?.online && active?.telemetryFresh !== false }, draggable: !locked, selectable: false, style: { width: 210, height: 160 } });
      return result;
    });
  }, [items, activeId, selectedId, locked, hasEdge, active, saved]);
  useEffect(() => {
    const timer = setTimeout(() => {
      if (!placed.current && instance.current && items.length) {
        if (viewport.current) instance.current.setViewport(viewport.current);
        else fit();
        placed.current = true;
      }
    }, 100);
    return () => clearTimeout(timer);
  }, [structure, items.length, fit]);
  useEffect(() => {
    const listener = () => { setFull(document.fullscreenElement === panel.current); };
    document.addEventListener('fullscreenchange', listener);
    return () => document.removeEventListener('fullscreenchange', listener);
  }, []);
  useEffect(() => {
    const canvas = panel.current?.querySelector('.tf-cluster-canvas');
    if (!canvas || typeof ResizeObserver === 'undefined') return undefined;
    let previous = null;
    let timer;
    const observer = new ResizeObserver(([entry]) => {
      const size = { width: entry.contentRect.width, height: entry.contentRect.height };
      if (previous && placed.current && (Math.abs(size.width - previous.width) > 40 || Math.abs(size.height - previous.height) > 80)) {
        clearTimeout(timer);
        timer = setTimeout(fit, 100);
      }
      previous = size;
    });
    observer.observe(canvas);
    return () => { observer.disconnect(); clearTimeout(timer); };
  }, [fit]);
  const toggleFull = async () => {
    try {
      if (document.fullscreenElement === panel.current) await document.exitFullscreen();
      else await panel.current.requestFullscreen();
      setFullscreenError('');
    } catch { setFullscreenError('Полноэкранный режим недоступен в этом браузере.'); }
  };
  const reset = () => {
    const defaults = autoPositions(items, activeId, hasEdge);
    positions.current = defaults;
    setNodes(current => current.map(n => ({ ...n, position: defaults[n.id] || n.position })));
    viewport.current = null;
    persist();
    setTimeout(fit, 80);
  };
  const edges = useMemo(() => {
    const byId = new Map(nodes.map(n => [n.id, n]));
    const result = [];
    const add = (source, target, label, tone = 'muted', animated = false) => {
      if (!byId.has(source) || !byId.has(target)) return;
      const from = byId.get(source).position, to = byId.get(target).position;
      const dx = to.x - from.x, dy = to.y - from.y;
      const vertical = Math.abs(dy) > Math.abs(dx);
      const direction = vertical ? dy >= 0 ? ['b', 't'] : ['t', 'b'] : dx >= 0 ? ['r', 'l'] : ['l', 'r'];
      result.push({ id: `${layer}-${source}-${target}`, source, target, sourceHandle: `source-${direction[0]}`, targetHandle: `target-${direction[1]}`, type: 'smoothstep', label, animated, markerEnd: { type: MarkerType.ArrowClosed, color: `var(--cl-${tone})`, width: 16, height: 16 }, style: { stroke: `var(--cl-${tone})`, strokeWidth: 1.6, strokeDasharray: tone === 'muted' ? '5 5' : undefined }, labelStyle: { fill: 'var(--cl-text)', fontSize: 11, fontWeight: 600 }, labelBgStyle: { fill: 'var(--cl-surface)' }, labelBgPadding: [7, 4], labelBgBorderRadius: 5 });
    };
    if (layer === 'network' || layer === 'minio') {
      items.forEach((node, i) => items.slice(i + 1).forEach(peer => {
        const observed = layer === 'network' && array(node.wireguard?.peers).some(p => String(p.allowed_ips || '').split(',').some(ip => ip.trim().split('/')[0] === peer.network?.wireguardIp));
        add(node.id, peer.id, layer === 'network' ? observed ? 'WG · пир' : 'Топология WG' : 'MinIO · топология', 'muted');
      }));
    } else {
      if (hasEdge && activeId) {
        const target = active?.edge?.state?.target_node;
        if (target) add(EDGE_ID, target, active?.edge?.state?.dns_synced ? 'Публичный маршрут' : 'Назначение DNS', active?.edge?.state?.success && active?.telemetryFresh !== false ? 'good' : 'warn', false);
      }
      if (activeId) items.filter(n => n.id !== activeId).forEach(n => add(activeId, n.id, layer === 'postgres' ? 'Реплика PostgreSQL' : n.canBePrimary ? 'Резерв' : 'Участник', n.online && n.telemetryFresh !== false && (layer === 'postgres' ? n.postgres?.healthy : n.hotStartReady) ? 'accent' : 'muted', false));
    }
    return result;
  }, [nodes, layer, items, activeId, hasEdge, active]);
  return <section className="tf-cluster-panel tf-cluster-map-panel" ref={panel} onKeyDown={e => { if (e.key === 'Enter' && !e.target.closest('button')) { const id = e.target.closest('.react-flow__node')?.dataset.id; if (items.some(n => n.id === id)) { e.preventDefault(); onSelect(id); } } }} aria-label="Интерактивная карта кластера">
    <div className="tf-cluster-panel-head"><div className="tf-cluster-heading"><Network size={18} /><h2>Карта кластера</h2></div><div className="tf-cluster-map-tools"><button type="button" className="tf-cluster-button" onClick={reset}><LayoutGrid size={15} />Расставить</button><button type="button" className="tf-cluster-icon" onClick={toggleFull} title={full ? 'Выйти из полного экрана' : 'На весь экран'} aria-label={full ? 'Выйти из полного экрана' : 'На весь экран'}>{full ? <Minimize2 size={18} /> : <Maximize2 size={18} />}</button></div></div>
    <div className="tf-cluster-map-tabs" role="group" aria-label="Слой карты">{layers.map(([key, label]) => <button key={key} type="button" aria-pressed={layer === key} onClick={() => setLayer(key)}>{key === 'postgres' ? <Database size={14} /> : key === 'ha' ? <Server size={14} /> : <Network size={14} />}{label}</button>)}<span><Move size={13} />Перетаскивай узлы · колесо — масштаб</span></div>
    <div className="tf-cluster-canvas">
      <ReactFlow nodes={nodes} edges={edges} nodeTypes={nodeTypes} onInit={flow => { instance.current = flow; }} onNodesChange={changes => setNodes(current => applyNodeChanges(changes.filter(c => c.type !== 'remove'), current))} onNodeClick={(_, node) => { if (node.type === 'server') onSelect(node.id); }} onNodeDragStop={(_, node) => { positions.current = { ...positions.current, [node.id]: node.position }; persist(); }} onMoveEnd={(_, v) => { viewport.current = v; persist(); }} minZoom={0.15} maxZoom={2} nodesDraggable={!locked} nodesConnectable={false} deleteKeyCode={null} panOnDrag zoomOnScroll selectionOnDrag={false} fitView fitViewOptions={{ padding: 0.15, minZoom: 0.15, maxZoom: 1.1 }} proOptions={{ hideAttribution: true }}>
        <Background gap={24} size={1} color="var(--cl-grid)" />
        <MiniMap className="tf-cluster-minimap" pannable zoomable nodeColor={node => node.id === activeId ? 'var(--cl-accent)' : 'var(--cl-muted)'} maskColor="var(--cl-minimap-mask)" />
      </ReactFlow>
      <div className="tf-cluster-map-controls"><button type="button" aria-label="Приблизить" onClick={() => instance.current?.zoomIn()}><Plus size={18} /></button><button type="button" aria-label="Отдалить" onClick={() => instance.current?.zoomOut()}><Minus size={18} /></button><button type="button" aria-label="Вписать карту" title="Вписать карту" onClick={fit}><Focus size={18} /></button><button type="button" aria-label={locked ? 'Разблокировать узлы' : 'Закрепить узлы'} title={locked ? 'Разблокировать узлы' : 'Закрепить узлы'} onClick={() => setLocked(v => !v)}>{locked ? <LockKeyhole size={17} /> : <UnlockKeyhole size={17} />}</button></div>
    </div>
    <div className="tf-cluster-map-caption"><span>{layer === 'network' ? 'Связи показывают настроенных пиров, а не измерение доступности канала.' : layer === 'minio' ? 'Схема топологии. Состояние репликации объектов не измеряется этой картой.' : 'Выбери сервер для подробностей. Расстановка сохраняется в этом браузере.'}</span><span>{fullscreenError || (storageWarning ? 'Браузер запретил сохранение расстановки' : active?.edge?.state?.verified_at && layer === 'ha' ? `Маршрут проверен ${dateTime(active.edge.state.verified_at)}` : '')}</span></div>
  </section>;
}