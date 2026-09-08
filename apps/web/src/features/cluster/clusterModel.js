export const DASH = '\u2014';
export const number = (value) => (typeof value === 'number' || typeof value === 'string' && value.trim() !== '') && Number.isFinite(Number(value)) ? Number(value) : null;
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
  if (node.telemetryFresh === false || node.healthy === false || primaryRole(node.role) && !node.isActive) return 'warn';
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
  standby: '\u0420\u0435\u0437\u0435\u0440\u0432', ready: '\u0413\u043e\u0442\u043e\u0432',
  'dns-update': '\u041e\u0431\u043d\u043e\u0432\u043b\u0435\u043d\u0438\u0435 DNS', 'edge-settle': '\u041e\u0436\u0438\u0434\u0430\u043d\u0438\u0435 Cloudflare',
  'route-check': '\u041f\u0440\u043e\u0432\u0435\u0440\u043a\u0430 \u043c\u0430\u0440\u0448\u0440\u0443\u0442\u0430',
  'route-confirmed': '\u041c\u0430\u0440\u0448\u0440\u0443\u0442 \u043f\u043e\u0434\u0442\u0432\u0435\u0440\u0436\u0434\u0451\u043d',
  'certbot-running': '\u0412\u044b\u043f\u0443\u0441\u043a \u0441\u0435\u0440\u0442\u0438\u0444\u0438\u043a\u0430\u0442\u0430',
  'certificate-reused': '\u0421\u0435\u0440\u0442\u0438\u0444\u0438\u043a\u0430\u0442 \u0434\u0435\u0439\u0441\u0442\u0432\u0443\u0435\u0442',
  'local-https-check': '\u041f\u0440\u043e\u0432\u0435\u0440\u043a\u0430 HTTPS \u0441\u0435\u0440\u0432\u0435\u0440\u0430',
  'public-https-check': '\u041f\u0443\u0431\u043b\u0438\u0447\u043d\u044b\u0439 HTTPS',
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
