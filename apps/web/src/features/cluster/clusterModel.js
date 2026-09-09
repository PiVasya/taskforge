export const DASH = '\u2014';
export const number = (value) => ((typeof value === 'number' || (typeof value === 'string' && value.trim() !== '')) && Number.isFinite(Number(value))) ? Number(value) : null;
export const array = (value) => Array.isArray(value) ? value : [];
export const primaryRole = (role) => ['primary', 'master', 'leader'].includes(String(role || '').toLowerCase());
export function bytes(value) {
  const n = number(value);
  if (n === null || n < 0) return DASH;
  const units = ['\u0411', '\u041a\u0411', '\u041c\u0411', '\u0413\u0411', '\u0422\u0411'];
  const i = n > 0 ? Math.max(0, Math.min(4, Math.floor(Math.log(n) / Math.log(1024)))) : 0;
  return `${new Intl.NumberFormat('ru-RU', { maximumFractionDigits: i ? 1 : 0 }).format(n / (1024 ** i))} ${units[i]}`;
}
export function duration(value) {
  const n = number(value);
  if (n === null || n < 0) return DASH;
  if (n >= 86400) return `${Math.floor(n / 86400)} \u0434 ${Math.floor(n % 86400 / 3600)} \u0447`;
  if (n >= 3600) return `${Math.floor(n / 3600)} \u0447 ${Math.floor(n % 3600 / 60)} \u043c\u0438\u043d`;
  return `${Math.floor(n / 60)} \u043c\u0438\u043d`;
}
export function dateTime(value) {
  const time = value ? new Date(value) : null;
  return time && Number.isFinite(time.getTime()) ? time.toLocaleString('ru-RU', { day: '2-digit', month: '2-digit', hour: '2-digit', minute: '2-digit', second: '2-digit' }) : DASH;
}
export function percent(used, total) {
  const u = number(used), t = number(total);
  return u !== null && u >= 0 && t !== null && t > 0 ? Math.max(0, Math.min(100, u / t * 100)) : null;
}
export function containerState(c) {
  return String(c.state || c.status || 'unknown').toLowerCase();
}
export function containerTone(c, node) {
  const state = containerState(c);
  if (!node.online || node.telemetryFresh === false) return 'muted';
  if (c.oomKilled || state === 'restarting' || state === 'dead' || c.health === 'unhealthy') return 'bad';
  if (state === 'missing') return 'bad';
  if (state === 'running') return c.health === 'starting' ? 'warn' : 'good';
  if (['created', 'exited', 'stopped'].includes(state)) return primaryRole(node.role) ? 'warn' : 'muted';
  return 'muted';
}
export function nodeTone(node) {
  if (!node.online) return 'bad';
  if (node.telemetryFresh === false || node.healthy === false || (primaryRole(node.role) && !node.isActive)) return 'warn';
  return 'good';
}
export function countPair(ready, total) {
  const r = number(ready), t = number(total);
  return r === null || t === null ? DASH : `${r}/${t}`;
}
export function servicesMatrix(nodes) {
  const rows = new Map();
  for (const node of nodes) for (const service of array(node.services)) {
    if (!service || typeof service !== 'object' || !service.service) continue;
    if (!rows.has(service.service)) rows.set(service.service, {});
    rows.get(service.service)[node.id] = service;
  }
  return [...rows].sort(([a], [b]) => a.localeCompare(b)).map(([name, cells]) => ({ name, cells }));
}
export const NODE_WIDTH = 266;
export const NODE_HEIGHT = 262;
export const EDGE_ID = '__cloudflare__';
// Fixed card dimensions and a generous row pitch make the initial placement
// independent of text length. Saved positions are never replaced by polling.
export function autoPositions(nodes, activeId, withEdge = false) {
  const ids = nodes.map(n => n.id).sort();
  const main = ids.includes(activeId) ? activeId : ids[0];
  const peers = ids.filter(id => id !== main);
  const pitch = NODE_HEIGHT + 76;
  const offset = withEdge ? 300 : 0;
  const positions = {};
  if (main) positions[main] = { x: offset + 20, y: Math.max(0, (peers.length - 1) * pitch / 2) };
  peers.forEach((id, index) => { positions[id] = { x: offset + 420, y: index * pitch }; });
  if (withEdge) positions[EDGE_ID] = { x: 0, y: (positions[main]?.y || 0) + 54 };
  return positions;
}
export function readLayout(storage, key) {
  try {
    const raw = JSON.parse(storage.getItem(key));
    const positions = {};
    for (const [id, p] of Object.entries(raw?.positions || {}))
      if (p && Number.isFinite(p.x) && Number.isFinite(p.y) && Math.abs(p.x) <= 100000 && Math.abs(p.y) <= 100000)
        positions[id] = { x: p.x, y: p.y };
    const v = raw?.viewport;
    const viewport = v && Number.isFinite(v.x) && Number.isFinite(v.y) && Number.isFinite(v.zoom) && v.zoom >= 0.15 && v.zoom <= 2 ? v : null;
    return { positions, viewport };
  } catch { return { positions: {}, viewport: null }; }
}
export function saveLayout(storage, key, positions, viewport) {
  try { storage.setItem(key, JSON.stringify({ version: 2, positions, viewport })); return true; } catch { return false; }
}
export const PHASE_LABELS = {
  standby: '\u0420\u0435\u0437\u0435\u0440\u0432', ready: '\u0413\u043e\u0442\u043e\u0432', starting: '\u0417\u0430\u043f\u0443\u0441\u043a edge',
  'dns-update': '\u041e\u0431\u043d\u043e\u0432\u043b\u0435\u043d\u0438\u0435 DNS', 'dns-synced': 'DNS \u043f\u043e\u0434\u0442\u0432\u0435\u0440\u0436\u0434\u0451\u043d',
  'static-route': '\u0421\u0442\u0430\u0442\u0438\u0447\u0435\u0441\u043a\u0438\u0439 \u043c\u0430\u0440\u0448\u0440\u0443\u0442', 'edge-settle': '\u041e\u0436\u0438\u0434\u0430\u043d\u0438\u0435 Cloudflare',
  'route-check': '\u041f\u0440\u043e\u0432\u0435\u0440\u043a\u0430 \u043c\u0430\u0440\u0448\u0440\u0443\u0442\u0430',
  'route-confirmed': '\u041c\u0430\u0440\u0448\u0440\u0443\u0442 \u043f\u043e\u0434\u0442\u0432\u0435\u0440\u0436\u0434\u0451\u043d',
  'certbot-running': '\u0412\u044b\u043f\u0443\u0441\u043a \u0441\u0435\u0440\u0442\u0438\u0444\u0438\u043a\u0430\u0442\u0430',
  'certificate-reused': '\u0421\u0435\u0440\u0442\u0438\u0444\u0438\u043a\u0430\u0442 \u0434\u0435\u0439\u0441\u0442\u0432\u0443\u0435\u0442',
  'local-https-check': '\u041f\u0440\u043e\u0432\u0435\u0440\u043a\u0430 HTTPS \u0441\u0435\u0440\u0432\u0435\u0440\u0430',
  'gateway-reload': '\u041f\u0435\u0440\u0435\u0437\u0430\u043f\u0443\u0441\u043a gateway', 'public-https-check': '\u041f\u0443\u0431\u043b\u0438\u0447\u043d\u044b\u0439 HTTPS',
  'retry-wait': '\u041e\u0436\u0438\u0434\u0430\u043d\u0438\u0435 \u043f\u043e\u0432\u0442\u043e\u0440\u0430', cancelled: '\u041f\u0440\u043e\u0432\u0435\u0440\u043a\u0430 \u043e\u0442\u043c\u0435\u043d\u0435\u043d\u0430',
};
export const REASONS = {
  'agent-unavailable': '\u0410\u0433\u0435\u043d\u0442 \u043d\u0435\u0434\u043e\u0441\u0442\u0443\u043f\u0435\u043d',
  'telemetry-unavailable': '\u041d\u0435\u0442 \u0442\u0435\u043b\u0435\u043c\u0435\u0442\u0440\u0438\u0438',
  'telemetry-stale': '\u0422\u0435\u043b\u0435\u043c\u0435\u0442\u0440\u0438\u044f \u0443\u0441\u0442\u0430\u0440\u0435\u043b\u0430',
  'firewall-unconfirmed': '\u0421\u0442\u0430\u0442\u0443\u0441 \u0444\u0430\u0439\u0440\u0432\u043e\u043b\u0430 \u043d\u0435 \u043f\u043e\u0434\u0442\u0432\u0435\u0440\u0436\u0434\u0451\u043d',
  'minio-not-ready': 'MinIO \u043d\u0435 \u0433\u043e\u0442\u043e\u0432', 'postgres-not-ready': 'PostgreSQL \u043d\u0435 \u0433\u043e\u0442\u043e\u0432',
  'role-unconfirmed': '\u0420\u043e\u043b\u044c \u043d\u0435 \u043f\u043e\u0434\u0442\u0432\u0435\u0440\u0436\u0434\u0435\u043d\u0430',
  'traffic-not-ready': '\u041f\u0443\u0431\u043b\u0438\u0447\u043d\u044b\u0439 \u0442\u0440\u0430\u0444\u0438\u043a \u043d\u0435 \u0433\u043e\u0442\u043e\u0432',
  'hot-start-not-ready': '\u0413\u043e\u0440\u044f\u0447\u0438\u0439 \u0440\u0435\u0437\u0435\u0440\u0432 \u043d\u0435 \u0433\u043e\u0442\u043e\u0432',
  'updater-not-ready': '\u041f\u0440\u043e\u0432\u0435\u0440\u0438\u0442\u044c \u0430\u0432\u0442\u043e\u043e\u0431\u043d\u043e\u0432\u043b\u0435\u043d\u0438\u0435',
  'tls-not-ready': 'TLS \u043d\u0435 \u0433\u043e\u0442\u043e\u0432',
};
export const IMAGE_LABELS = {
  same: '\u0421\u043e\u0432\u043f\u0430\u0434\u0430\u0435\u0442', different: '\u041e\u0442\u043b\u0438\u0447\u0430\u0435\u0442\u0441\u044f',
  missing: '\u041d\u0435\u0442 \u043e\u0431\u0440\u0430\u0437\u0430', unknown: '\u041d\u0435\u0442 \u0434\u0430\u043d\u043d\u044b\u0445',
  unverified: '\u0415\u0441\u0442\u044c \u00b7 \u0431\u0435\u0437 \u0441\u0440\u0430\u0432\u043d\u0435\u043d\u0438\u044f',
  'not-assigned': '\u041d\u0435 \u043d\u0430\u0437\u043d\u0430\u0447\u0435\u043d',
};

export function imageCell(node, supplied) {
  if (supplied) return supplied;
  const assigned = number(node.docker?.assignedAppCount);
  const complete = assigned !== null && array(node.services).filter(s => s?.assigned === true).length === assigned;
  return complete && node.online && node.telemetryFresh !== false
    ? { assigned: false, imageStatus: 'not-assigned' }
    : null;
}

export const PRIMARY_SWITCH_REASON = {
  'no-primary': 'Текущая Primary не подтверждена',
  'already-primary': 'Уже является Primary',
  'cannot-be-primary': 'Для этой ноды can_be_primary=false',
  offline: 'Node Agent не отвечает',
  stale: 'Телеметрия устарела',
  'not-reconciled': 'Node Agent ещё не reconciled',
  fenced: 'Control plane временно fenced',
  'not-standby': 'Нода сейчас не standby/replica',
  'leader-mismatch': 'Нода не подтверждает текущую Primary',
  'not-hot-ready': 'Горячий запуск не готов',
  'postgres-not-ready': 'PostgreSQL реплика не готова',
  'revision-mismatch': 'Версия Node Agent отличается от Primary',
  'edge-not-configured': 'Cloudflare failover не настроен на этой ноде',
};

export function primarySwitchEligibility(node, active, edgeRequired = false) {
  const reasons = [];
  if (!active || !primaryRole(active.role)) reasons.push('no-primary');
  if (!node || !node.id) return { ready: false, reasons: ['no-primary'] };
  if (active && node.id === active.id) reasons.push('already-primary');
  if (node.canBePrimary !== true) reasons.push('cannot-be-primary');
  if (!node.online) reasons.push('offline');
  if (node.telemetryFresh === false) reasons.push('stale');
  if (node.reconciled !== true) reasons.push('not-reconciled');
  if (node.controlPlaneFenced === true) reasons.push('fenced');
  if (!['standby', 'replica', 'slave'].includes(String(node.role || '').toLowerCase())) reasons.push('not-standby');
  if (active?.id && node.leader && String(node.leader).toUpperCase() !== String(active.id).toUpperCase()) reasons.push('leader-mismatch');
  if (node.hotStartReady !== true) reasons.push('not-hot-ready');
  if (node.postgres?.healthy !== true) reasons.push('postgres-not-ready');
  if (active?.bundleRevision && node.bundleRevision && String(active.bundleRevision) !== String(node.bundleRevision)) reasons.push('revision-mismatch');
  if (edgeRequired && node.edge?.configured !== true) reasons.push('edge-not-configured');
  return { ready: reasons.length === 0, reasons };
}

export function primarySwitchComplete(nodes, activeId, targetId) {
  const node = array(nodes).find(item => String(item.id) === String(targetId));
  if (!node || String(activeId) !== String(targetId) || !primaryRole(node.role) || node.trafficReady !== true) return false;
  if (node.edge?.configured !== true) return true;
  const state = node.edge?.state || {};
  return state.success === true
    && state.phase === 'ready'
    && state.dns_synced === true
    && state.route_ready === true
    && state.tls_ready === true
    && String(state.target_node || '') === String(targetId);
}


export function primarySwitchProgress(nodes, activeId, pending, now = Date.now()) {
  const list = array(nodes);
  const targetId = String(pending?.target || '');
  const fromId = String(pending?.from || '');
  const target = list.find(item => String(item.id) === targetId) || null;
  const source = list.find(item => String(item.id) === fromId) || null;
  const edgeRequired = list.some(item => item.edge?.configured === true);
  const edge = target?.edge?.state || {};
  const ageSeconds = Math.max(0, Math.floor((Number(now) - Number(pending?.acceptedAt || now)) / 1000));
  const targetPrimary = !!target && String(activeId || '') === targetId && primaryRole(target.role);
  const sourcePrimary = !!source && String(activeId || '') === fromId && primaryRole(source.role);
  const targetRole = String(target?.role || 'unknown').toLowerCase();
  const targetFresh = target?.online === true && target?.telemetryFresh !== false;
  const sourceFresh = source?.online === true && source?.telemetryFresh !== false;
  const roleStillStandby = ['standby', 'replica', 'slave'].includes(targetRole);
  const leaderStillSource = !target?.leader || !fromId || String(target.leader).toUpperCase() === fromId.toUpperCase();
  const stableNoTransition = pending?.uncertain === true && ageSeconds >= 20 && sourcePrimary && sourceFresh && targetFresh && roleStillStandby && leaderStillSource;

  const routeRequired = Math.max(0, number(edge.route_required) ?? 0);
  const routeConfirmations = Math.max(0, number(edge.route_confirmations) ?? 0);
  const confirmationsReady = routeRequired <= 0 || routeConfirmations >= routeRequired;
  const appsReady = targetPrimary && target?.applicationsActive === true;
  const dnsReady = !edgeRequired || (edge.dns_synced === true && String(edge.target_node || '') === targetId);
  const routeReady = !edgeRequired || (edge.route_ready === true && confirmationsReady);
  const tlsReady = !edgeRequired || edge.tls_ready === true || edge.http_only === true;
  const trafficReady = targetPrimary && target?.trafficReady === true;
  const complete = primarySwitchComplete(list, activeId, targetId);
  const requestConfirmed = pending?.uncertain !== true || targetPrimary;
  const waiting = complete ? 'done' : 'waiting';

  const stages = [
    {
      id: 'request', label: 'Запрос',
      state: requestConfirmed ? 'done' : stableNoTransition ? 'warn' : 'unknown',
      detail: requestConfirmed ? (pending?.uncertain ? 'Подтверждён фактом' : 'Patroni принял') : 'Ответ POST не получен',
    },
    { id: 'role', label: 'Роль БД', state: targetPrimary ? 'done' : waiting, detail: targetPrimary ? `Primary ${targetId}` : activeId ? `Сейчас ${activeId}` : 'Не определена' },
    { id: 'apps', label: 'Приложения', state: appsReady ? 'done' : targetPrimary ? 'active' : 'waiting', detail: appsReady ? 'Активны' : targetPrimary ? 'Запускаются' : 'Ждут роли' },
    { id: 'dns', label: 'DNS', state: dnsReady && targetPrimary ? 'done' : targetPrimary && edgeRequired ? 'active' : edgeRequired ? 'waiting' : 'done', detail: edgeRequired ? dnsReady && targetPrimary ? `→ ${targetId}` : PHASE_LABELS[edge.phase] || 'Ожидание' : 'Не требуется' },
    { id: 'route', label: 'Маршрут', state: routeReady && targetPrimary ? 'done' : targetPrimary && edgeRequired ? 'active' : edgeRequired ? 'waiting' : 'done', detail: edgeRequired ? `${routeConfirmations}/${routeRequired || '—'}` : 'Не требуется' },
    { id: 'tls', label: 'HTTPS', state: tlsReady && targetPrimary ? 'done' : targetPrimary && edgeRequired ? 'active' : edgeRequired ? 'waiting' : 'done', detail: edgeRequired ? tlsReady && targetPrimary ? 'Готов' : PHASE_LABELS[edge.phase] || 'Ожидание' : 'Не требуется' },
    { id: 'traffic', label: 'Traffic', state: trafficReady ? 'done' : targetPrimary ? 'active' : 'waiting', detail: trafficReady ? 'READY' : 'WAIT' },
  ];

  let summary;
  if (complete) summary = `Сервер ${targetId} полностью принял роль Primary; публичный маршрут и HTTPS подтверждены.`;
  else if (stableNoTransition) summary = `По свежей телеметрии смена роли пока не наблюдается: Primary остаётся ${fromId}, сервер ${targetId} всё ещё ${targetRole}.`;
  else if (!targetPrimary) summary = activeId ? `Patroni/Agent пока показывают Primary ${activeId}. Ждём появления ${targetId} в роли Primary.` : 'Во время смены роли единственная Primary пока не подтверждена.';
  else if (!appsReady) summary = `Сервер ${targetId} уже Primary. Сейчас активируются приложения.`;
  else if (edgeRequired && !dnsReady) summary = `Сервер ${targetId} уже Primary. Сейчас переключается Cloudflare DNS.`;
  else if (edgeRequired && !routeReady) summary = `DNS уже направлен на ${targetId}. Проверяется публичный маршрут (${routeConfirmations}/${routeRequired || '—'}).`;
  else if (edgeRequired && !tlsReady) summary = `Маршрут до ${targetId} подтверждён. Сейчас проверяется сертификат и публичный HTTPS.`;
  else summary = `Сервер ${targetId} уже Primary. Ждём финальный traffic_ready.`;

  return {
    target, source, edge, edgeRequired, ageSeconds, complete, stableNoTransition,
    allowDismiss: stableNoTransition || ageSeconds >= 600,
    summary, stages, targetPrimary, appsReady, dnsReady, routeReady, tlsReady, trafficReady,
    routeConfirmations, routeRequired,
    phase: String(edge.phase || ''),
    lastError: targetPrimary || String(edge.phase || '') !== 'standby' ? String(edge.last_error || '') : '',
    retryInSeconds: Math.max(0, number(edge.retry_in_seconds) ?? 0),
  };
}
