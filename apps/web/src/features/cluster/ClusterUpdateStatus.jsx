import React from 'react';
import { RefreshCw } from 'lucide-react';
import { dateTime } from './clusterModel';
import { UPDATE_STATES } from './clusterMetrics';
import { Tag } from './ClusterShared';

const digest = value => value ? value.replace(/^sha256:/, '').slice(0, 12) : '—';
export default function ClusterUpdateStatus({ node }) {
  const update = node.update || {};
  const rows = Array.isArray(update.services) ? update.services : [];
  if (update.mode !== 'node-agent') return null;
  return <div className="tf-cluster-subpanel tf-cluster-update-panel">
    <div className="tf-cluster-subtitle"><RefreshCw size={17} /><h3>Автообновление приложений</h3><Tag tone={update.hasError ? 'warn' : update.running ? 'good' : 'muted'}>{UPDATE_STATES[update.status] || update.status || 'Нет данных'}</Tag></div>
    <p className="tf-cluster-note">Последний обход реестра: {dateTime(update.lastScanAt)} · следующий: {dateTime(update.nextScanAt)}{update.currentService ? ` · сейчас ${update.currentService}` : ''}</p>
    {update.error && <div className="tf-cluster-callout is-warn">{update.error}</div>}
    <div className="tf-cluster-table-scroll"><table className="tf-cluster-table"><thead><tr><th>Сервис</th><th>Состояние</th><th>Установлен → загружен</th><th>Повтор / причина</th></tr></thead><tbody>{rows.map(row => <tr key={row.service}><th>{row.service}</th><td><Tag tone={row.error || row.pullError ? 'warn' : row.status === 'healthy' ? 'good' : 'muted'}>{UPDATE_STATES[row.status] || row.status || 'Ожидание'}</Tag>{row.pullStatus === 'checking' && <small> Проверка реестра</small>}</td><td><code title={row.currentImage || ''}>{digest(row.currentImage)}</code> → <code title={row.targetImage || ''}>{digest(row.targetImage)}</code></td><td>{row.retryInSeconds > 0 && <span>Через {row.retryInSeconds} с · </span>}{row.error || row.pullError || '—'}</td></tr>)}</tbody></table></div>
  </div>;
}
