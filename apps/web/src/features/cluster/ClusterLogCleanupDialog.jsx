import React, { useEffect, useMemo, useState } from 'react';
import { AlertTriangle, CheckCircle2, HardDrive, Loader2, Server, ShieldCheck, Trash2, X } from 'lucide-react';
import { cleanupClusterLogs } from '../../api/systemStatus';
import { handleApiError } from '../../utils/handleApiError';
import { bytes, logCleanupEligibility, LOG_CLEANUP_MIN_AGENT_REVISION } from './clusterModel';
import { Tag } from './ClusterShared';

function eligibilityText(node, eligibility) {
  if (eligibility.ready) return `Agent r${eligibility.revision}`;
  if (eligibility.reason === 'offline') return 'Node Agent недоступен';
  return `Нужен Agent r${LOG_CLEANUP_MIN_AGENT_REVISION}+ · сейчас r${eligibility.revision ?? '?'}`;
}

function resultTone(item) {
  return item?.success ? 'good' : 'bad';
}

export default function ClusterLogCleanupDialog({ open, nodes, onClose, notify, onFinished }) {
  const candidates = useMemo(() => (Array.isArray(nodes) ? nodes : []).map(node => ({
    node,
    eligibility: logCleanupEligibility(node),
  })), [nodes]);
  const readyIds = useMemo(() => candidates.filter(x => x.eligibility.ready).map(x => x.node.id), [candidates]);
  const readySignature = readyIds.join('|');
  const [selected, setSelected] = useState([]);
  const [busy, setBusy] = useState(false);
  const [result, setResult] = useState(null);

  useEffect(() => {
    if (!open) return;
    setSelected(readyIds);
    setResult(null);
  }, [open, readySignature]); // eslint-disable-line react-hooks/exhaustive-deps

  useEffect(() => {
    if (!open) return undefined;
    const key = event => {
      if (event.key === 'Escape' && !busy) onClose?.();
    };
    window.addEventListener('keydown', key);
    return () => window.removeEventListener('keydown', key);
  }, [open, busy, onClose]);

  if (!open) return null;

  const toggle = id => {
    if (busy) return;
    setResult(null);
    setSelected(current => current.includes(id) ? current.filter(x => x !== id) : [...current, id]);
  };

  const run = async () => {
    if (busy || selected.length === 0) return;
    setBusy(true);
    setResult(null);
    try {
      const response = await cleanupClusterLogs(selected);
      setResult(response);
      const items = Array.isArray(response?.nodes) ? response.nodes : [];
      const successes = items.filter(item => item?.success);
      const failures = items.filter(item => !item?.success);
      if (successes.length > 0 && failures.length === 0) {
        notify?.success?.(`Логи очищены на ${successes.length} сервер${successes.length === 1 ? 'е' : 'ах'} · освобождено ${bytes(response?.reclaimedBytes)}`, 8000);
      } else if (successes.length > 0) {
        notify?.warn?.(`Очистка выполнена частично: ${successes.length}/${items.length} нод · освобождено ${bytes(response?.reclaimedBytes)}`, 10000);
      } else {
        notify?.error?.('Ни на одной выбранной ноде очистка логов не выполнена.', 10000);
      }
      onFinished?.(response);
    } catch (error) {
      handleApiError(error, notify, 'Не удалось очистить логи серверов');
    } finally {
      setBusy(false);
    }
  };

  const resultItems = Array.isArray(result?.nodes) ? result.nodes : [];

  return <div className="tf-cluster-dialog-backdrop" role="presentation" onMouseDown={event => {
    if (event.target === event.currentTarget && !busy) onClose?.();
  }}>
    <section className="tf-cluster-dialog tf-cluster-log-cleanup-dialog" role="dialog" aria-modal="true" aria-labelledby="cluster-log-cleanup-title">
      <div className="tf-cluster-dialog-head">
        <div className="tf-cluster-title-group">
          <span className="tf-cluster-server-mark is-danger"><Trash2 size={22} /></span>
          <div>
            <h2 id="cluster-log-cleanup-title">Очистить логи серверов</h2>
            <p>Освобождает место на выбранных нодах без удаления данных TaskForge.</p>
          </div>
        </div>
        <button type="button" className="tf-cluster-icon" disabled={busy} onClick={() => onClose?.()} aria-label="Закрыть"><X size={16} /></button>
      </div>

      <div className="tf-cluster-log-cleanup-body">
        <div className="tf-cluster-callout is-warn tf-cluster-log-cleanup-warning">
          <AlertTriangle size={18} />
          <div><strong>Логи удаляются безвозвратно.</strong><span>Очищаются Docker json-file логи контейнеров TaskForge, <code>/var/log/taskforge/*.log</code> и архивированный systemd journal. Журнал после ротации ужимается примерно до 128 МБ.</span></div>
        </div>

        <div className="tf-cluster-log-cleanup-preserved">
          <ShieldCheck size={17} />
          <div><strong>Что останется нетронутым</strong><span>Базы данных, Docker volumes, Docker images, MinIO и сохранённые диагностические архивы не удаляются.</span></div>
        </div>

        <section className="tf-cluster-diagnostics-section">
          <div className="tf-cluster-diagnostics-section-head">
            <div><strong>Серверы</strong><small>По умолчанию выбраны все доступные ноды с Node Agent r{LOG_CLEANUP_MIN_AGENT_REVISION}+.</small></div>
            <Tag tone={selected.length ? 'warn' : 'muted'}>{selected.length}/{readyIds.length} выбрано</Tag>
          </div>
          <div className="tf-cluster-diagnostics-nodes">
            {candidates.map(({ node, eligibility }) => {
              const checked = selected.includes(node.id);
              return <button
                type="button"
                key={node.id}
                className={`tf-cluster-diagnostics-node tf-cluster-log-cleanup-node ${checked ? 'is-selected' : ''}`}
                disabled={busy || !eligibility.ready}
                aria-pressed={checked}
                onClick={() => toggle(node.id)}
              >
                <span className="tf-cluster-log-cleanup-node-icon"><Server size={15} /></span>
                <span><strong>Сервер {node.id}</strong><small>{eligibilityText(node, eligibility)}</small></span>
                {eligibility.ready ? <span className={`tf-cluster-log-cleanup-check ${checked ? 'is-selected' : ''}`}>{checked && <CheckCircle2 size={15} />}</span> : <Tag tone="muted">Недоступно</Tag>}
              </button>;
            })}
          </div>
          {readyIds.length === 0 && <div className="tf-cluster-diagnostics-hint is-error">Нет доступных Node Agent r{LOG_CLEANUP_MIN_AGENT_REVISION}+. Сначала обновите серверный пакет кластера.</div>}
        </section>

        {resultItems.length > 0 && <section className="tf-cluster-log-cleanup-results" aria-live="polite">
          <div className="tf-cluster-log-cleanup-summary">
            <HardDrive size={17} />
            <div><span>Освобождено по отчётам очистки</span><strong>{bytes(result?.reclaimedBytes)}</strong></div>
          </div>
          <div className="tf-cluster-log-cleanup-result-list">
            {resultItems.map(item => <div key={item.node} className={`tf-cluster-log-cleanup-result is-${resultTone(item)}`}>
              <span className="tf-cluster-log-cleanup-result-icon">{item.success ? <CheckCircle2 size={16} /> : <AlertTriangle size={16} />}</span>
              <div>
                <div><strong>Сервер {item.node}</strong><Tag tone={item.success ? 'good' : 'bad'}>{item.success ? 'Очищено' : 'Ошибка'}</Tag></div>
                {item.success
                  ? <small>Логи контейнеров: {item.containersCleared ?? 0} · пропущено: {item.containersSkipped ?? 0} · host logs: {item.hostLogFilesCleared ?? 0} · освобождено: {bytes(item.reclaimedBytes)}</small>
                  : <small className="is-error">{item.message || item.code || 'Node Agent отклонил очистку.'}</small>}
              </div>
            </div>)}
          </div>
        </section>}
      </div>

      <div className="tf-cluster-dialog-actions">
        <button type="button" className="tf-cluster-button" disabled={busy} onClick={() => onClose?.()}>Закрыть</button>
        <button type="button" className="tf-cluster-button tf-cluster-log-cleanup-start" disabled={busy || selected.length === 0} onClick={run}>
          {busy ? <Loader2 size={15} className="tf-cluster-spin" /> : <Trash2 size={15} />}
          {busy ? 'Очищаем…' : `Очистить логи${selected.length > 0 ? ` · ${selected.length}` : ''}`}
        </button>
      </div>
    </section>
  </div>;
}
