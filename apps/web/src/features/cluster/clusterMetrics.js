const finite = value => value !== null && value !== undefined && value !== '' && Number.isFinite(Number(value)) ? Number(value) : null;
const bounded = (value, max) => {
  const n = finite(value);
  return n !== null && n >= 0 && n <= max ? n : null;
};

export function metricHistory(host, now = Date.now(), minutes = 60) {
  const interval = Math.max(1000, (finite(host?.sample_interval_seconds) || 5) * 1000);
  const rows = new Map();
  for (const row of (Array.isArray(host?.history) ? host.history : []).slice(-720)) {
    const time = Date.parse(row?.at || '');
    if (!Number.isFinite(time) || time > now + interval || time < now - minutes * 60000) continue;
    rows.set(time, {
      time,
      cpu: bounded(row.cpu_percent, 100),
      memory: bounded(row.memory_percent, 100),
      temperature: bounded(row.temperature_c, 130),
      steal: bounded(row.steal_percent, 100),
      iowait: bounded(row.iowait_percent, 100),
      usedBytes: bounded(row.memory_used_bytes, 1e16),
      totalBytes: bounded(row.memory_total_bytes, 1e16),
    });
  }
  const ordered = [...rows.values()].sort((a, b) => a.time - b.time);
  const result = [];
  for (const row of ordered) {
    const previous = result[result.length - 1];
    if (previous && row.time - previous.time > Math.max(15000, interval * 2.5)) {
      result.push({ time: previous.time + interval, cpu: null, memory: null, temperature: null, steal: null, iowait: null });
    }
    result.push(row);
  }
  return result;
}

export function metricsFresh(node, now = Date.now()) {
  const time = Date.parse(node?.host?.sampled_at || '');
  return node?.online === true && node?.telemetryFresh !== false && Number.isFinite(time)
    && time <= now + 15000 && now - time <= 45000;
}

export const UPDATE_STATES = {
  initializing: 'Инициализация', 'waiting-role': 'Ожидание роли', fenced: 'Обслуживание',
  'taking-ownership': 'Подключение обновлений', monitoring: 'Наблюдение', reconciling: 'Сверка',
  applying: 'Обновляется', rollback: 'Откат', 'rolled-back': 'Откат, ожидается повтор',
  healthy: 'Работает', staged: 'Подготовлен на резерве', verifying: 'Проверка здоровья',
  retrying: 'Ожидается повтор', 'awaiting-image': 'Ожидание образа', checking: 'Проверка реестра',
};
