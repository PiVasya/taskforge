import React, { useEffect, useMemo, useRef, useState } from 'react';
import { AlertTriangle, CheckCircle2, Clock3, Download, FileArchive, HardDrive, Loader2, Server, X } from 'lucide-react';
import { downloadClusterDiagnostics, getClusterDiagnosticsJob, startClusterDiagnostics } from '../../api/systemStatus';
import { handleApiError } from '../../utils/handleApiError';
import { bytes } from './clusterModel';
import { Tag } from './ClusterShared';

const MODES = [
  { id: 'quick', title: 'Быстро', text: 'Состояние хоста, кластера, Docker и журналы systemd. Без docker logs и docker events.' },
  { id: 'standard', title: 'Обычно', text: 'Всё из быстрой диагностики + события Docker и логи каждого контейнера за выбранный период.' },
  { id: 'full', title: 'Полная история', text: 'Полные docker logs каждого контейнера без ограничения периода и размера. Архив может быть очень большим.' },
];

const SINCE_OPTIONS = [
  ['30m', '30 минут'], ['1h', '1 час'], ['3h', '3 часа'], ['6h', '6 часов'],
  ['12h', '12 часов'], ['24h', '24 часа'], ['2d', '2 дня'], ['7d', '7 дней'],
];
const MAX_MB_OPTIONS = [8, 16, 32, 64, 128, 256];
const terminal = status => ['completed', 'failed'].includes(String(status || '').toLowerCase());

function jobTone(status) {
  if (status === 'completed') return 'good';
  if (status === 'failed') return 'bad';
  return 'accent';
}

function jobLabel(status) {
  return ({ queued: 'В очереди', running: 'Собирается', completed: 'Готово', failed: 'Ошибка' })[status] || status || 'Ожидание';
}

function normalizeStatus(job, status) {
  return {
    ...job,
    status: status?.status || job.status,
    archiveName: status?.archive_name || status?.archiveName || job.archiveName,
    sizeBytes: status?.size_bytes ?? status?.sizeBytes ?? job.sizeBytes,
    startedAt: status?.started_at || status?.startedAt || job.startedAt,
    finishedAt: status?.finished_at || status?.finishedAt || job.finishedAt,
    message: status?.message || status?.error || job.message,
  };
}

export default function ClusterDiagnosticsDialog({ open, nodes, initialNodeId, onClose, notify }) {
  const [selected, setSelected] = useState([]);
  const [mode, setMode] = useState('standard');
  const [since, setSince] = useState('6h');
  const [maxLogMb, setMaxLogMb] = useState(32);
  const [jobs, setJobs] = useState([]);
  const [busy, setBusy] = useState(false);
  const [downloading, setDownloading] = useState(null);
  const pollAbort = useRef(null);
  const jobsRef = useRef([]);
  const onlineNodes = useMemo(() => nodes.filter(node => node.online), [nodes]);

  useEffect(() => {
    if (!open) return;
    const preferred = onlineNodes.find(node => node.id === initialNodeId) || onlineNodes[0] || nodes[0];
    setSelected(preferred ? [preferred.id] : []);
  }, [open, initialNodeId]); // eslint-disable-line react-hooks/exhaustive-deps

  useEffect(() => {
    if (!open) return undefined;
    const handler = event => { if (event.key === 'Escape' && !busy) onClose(); };
    window.addEventListener('keydown', handler);
    return () => window.removeEventListener('keydown', handler);
  }, [open, busy, onClose]);

  useEffect(() => { jobsRef.current = jobs; }, [jobs]);

  useEffect(() => {
    if (!open) return undefined;
    let disposed = false;
    const poll = async () => {
      const current = jobsRef.current;
      if (!current.some(job => job.accepted && job.jobId && !terminal(job.status))) return;
      pollAbort.current?.abort();
      const controller = new AbortController();
      pollAbort.current = controller;
      const next = await Promise.all(current.map(async job => {
        if (!job.accepted || !job.jobId || terminal(job.status)) return job;
        try {
          const status = await getClusterDiagnosticsJob(job.node, job.jobId, { signal: controller.signal });
          return normalizeStatus(job, status);
        } catch (error) {
          if (controller.signal.aborted) return job;
          return { ...job, pollError: handleApiError(error, false, 'Не удалось проверить сбор логов')?.primaryMessage || 'Нет связи с Node Agent' };
        }
      }));
      if (!disposed && !controller.signal.aborted) setJobs(next);
    };
    const timer = window.setInterval(poll, 1800);
    return () => {
      disposed = true;
      window.clearInterval(timer);
      pollAbort.current?.abort();
      pollAbort.current = null;
    };
  }, [open]);

  if (!open) return null;

  const toggleNode = id => setSelected(current => current.includes(id) ? current.filter(x => x !== id) : [...current, id]);
  const allOnlineSelected = onlineNodes.length > 0 && onlineNodes.every(node => selected.includes(node.id));
  const running = jobs.some(job => job.accepted && !terminal(job.status));

  const start = async () => {
    if (selected.length === 0 || busy || running) return;
    setBusy(true);
    setJobs([]);
    try {
      const result = await startClusterDiagnostics({ nodes: selected, mode, since, maxLogMb });
      const started = (result?.jobs || []).map(job => ({ ...job, status: job.status || (job.accepted ? 'queued' : 'failed') }));
      setJobs(started);
      const accepted = started.filter(job => job.accepted).length;
      if (accepted) notify?.info(`Сбор логов запущен: ${accepted} ${accepted === 1 ? 'нода' : 'ноды'}.`);
      if (started.some(job => !job.accepted)) notify?.warn('На части нод сбор не запустился. Причина показана в окне диагностики.');
    } catch (error) {
      handleApiError(error, notify, 'Не удалось запустить сбор логов');
    } finally {
      setBusy(false);
    }
  };

  const download = async job => {
    if (!job.jobId || job.status !== 'completed' || downloading) return;
    const key = `${job.node}:${job.jobId}`;
    setDownloading(key);
    try {
      const { blob, fileName } = await downloadClusterDiagnostics(job.node, job.jobId);
      const url = URL.createObjectURL(blob);
      const anchor = document.createElement('a');
      anchor.href = url;
      anchor.download = fileName;
      document.body.appendChild(anchor);
      anchor.click();
      anchor.remove();
      URL.revokeObjectURL(url);
    } catch (error) {
      handleApiError(error, notify, 'Не удалось скачать архив диагностики');
    } finally {
      setDownloading(null);
    }
  };

  return <div className="tf-cluster-dialog-backdrop" role="presentation" onMouseDown={event => { if (event.target === event.currentTarget && !busy) onClose(); }}>
    <section className="tf-cluster-dialog tf-cluster-diagnostics-dialog" role="dialog" aria-modal="true" aria-labelledby="cluster-diagnostics-title">
      <div className="tf-cluster-dialog-head">
        <div className="tf-cluster-title-group"><span className="tf-cluster-server-mark"><FileArchive size={22} /></span><div><h2 id="cluster-diagnostics-title">Собрать логи</h2><p>Запускает штатный cluster.sh diagnostics на выбранных серверах. Команда не меняет состояние кластера.</p></div></div>
        <button type="button" className="tf-cluster-icon" aria-label="Закрыть" disabled={busy} onClick={onClose}><X size={17} /></button>
      </div>

      <div className="tf-cluster-diagnostics-body">
        <section className="tf-cluster-diagnostics-section">
          <div className="tf-cluster-diagnostics-section-head"><div><strong>Серверы</strong><small>Архив создаётся отдельно на каждой ноде.</small></div>{onlineNodes.length > 1 && <button type="button" className="tf-cluster-button" onClick={() => setSelected(allOnlineSelected ? [] : onlineNodes.map(node => node.id))}>{allOnlineSelected ? 'Снять все' : 'Все онлайн'}</button>}</div>
          <div className="tf-cluster-diagnostics-nodes">
            {nodes.map(node => <button type="button" key={node.id} className={`tf-cluster-diagnostics-node ${selected.includes(node.id) ? 'is-selected' : ''}`} aria-pressed={selected.includes(node.id)} disabled={!node.online || running} onClick={() => toggleNode(node.id)}>
              <span className="tf-cluster-server-letter">{node.id}</span><span><strong>Сервер {node.id}</strong><small>{node.online ? `${String(node.appProfile || '—').toUpperCase()} · Agent r${node.bundleRevision || '—'}` : 'Node Agent офлайн'}</small></span><Tag tone={node.online ? 'good' : 'bad'}>{node.online ? 'Онлайн' : 'Офлайн'}</Tag>
            </button>)}
          </div>
        </section>

        <section className="tf-cluster-diagnostics-section">
          <div className="tf-cluster-diagnostics-section-head"><div><strong>Что собирать</strong><small>Три безопасных режима штатного сборщика.</small></div></div>
          <div className="tf-cluster-diagnostics-modes">
            {MODES.map(item => <button type="button" key={item.id} className={`tf-cluster-diagnostics-mode ${mode === item.id ? 'is-selected' : ''}`} aria-pressed={mode === item.id} disabled={running} onClick={() => setMode(item.id)}><strong>{item.title}</strong><span>{item.text}</span></button>)}
          </div>
        </section>

        <section className="tf-cluster-diagnostics-section">
          <div className="tf-cluster-diagnostics-section-head"><div><strong>Сколько логов</strong><small>Ограничения применяются отдельно к каждому контейнеру.</small></div></div>
          <div className="tf-cluster-diagnostics-limits">
            <label><span><Clock3 size={14} />Период</span><select value={since} disabled={mode !== 'standard' || running} onChange={event => setSince(event.target.value)}>{SINCE_OPTIONS.map(([value, label]) => <option key={value} value={value}>{label}</option>)}</select></label>
            <label><span><HardDrive size={14} />Максимум docker log</span><select value={maxLogMb} disabled={mode !== 'standard' || running} onChange={event => setMaxLogMb(Number(event.target.value))}>{MAX_MB_OPTIONS.map(value => <option key={value} value={value}>{value} МБ / контейнер</option>)}</select></label>
          </div>
          {mode === 'quick' && <div className="tf-cluster-diagnostics-hint">Quick: host/status/doctor/systemd/Docker state без журналов контейнеров и Docker events.</div>}
          {mode === 'full' && <div className="tf-cluster-callout is-warn"><AlertTriangle size={17} /><div><strong>Полная история без лимита.</strong><div>Если контейнеры давно работают и много пишут в stdout/stderr, архив может занять сотни мегабайт или больше.</div></div></div>}
        </section>

        {jobs.length > 0 && <section className="tf-cluster-diagnostics-section">
          <div className="tf-cluster-diagnostics-section-head"><div><strong>Сборка</strong><small>Статус обновляется автоматически примерно раз в 2 секунды.</small></div></div>
          <div className="tf-cluster-diagnostics-jobs">
            {jobs.map(job => {
              const key = `${job.node}:${job.jobId || 'none'}`;
              return <div key={key} className={`tf-cluster-diagnostics-job is-${jobTone(job.status)}`}>
                <span className="tf-cluster-diagnostics-job-icon">{job.status === 'completed' ? <CheckCircle2 size={17} /> : job.status === 'failed' ? <AlertTriangle size={17} /> : <Loader2 size={17} className="tf-cluster-spin" />}</span>
                <div className="tf-cluster-diagnostics-job-main"><div><strong>Сервер {job.node}</strong><Tag tone={jobTone(job.status)}>{jobLabel(job.status)}</Tag>{job.sizeBytes != null && <Tag>{bytes(job.sizeBytes)}</Tag>}</div><small>{job.archiveName || job.message || job.pollError || (job.jobId ? `Job ${job.jobId}` : 'Сбор не запущен')}</small></div>
                {job.status === 'completed' && <button type="button" className="tf-cluster-button is-primary" disabled={!!downloading} onClick={() => download(job)}><Download size={14} />{downloading === key ? 'Скачиваем…' : 'Скачать'}</button>}
              </div>;
            })}
          </div>
        </section>}
      </div>

      <div className="tf-cluster-dialog-actions">
        <button type="button" className="tf-cluster-button" disabled={busy} onClick={onClose}>{running ? 'Закрыть' : 'Отмена'}</button>
        <button type="button" className="tf-cluster-button is-primary tf-cluster-diagnostics-start" disabled={busy || running || selected.length === 0} onClick={start}><Server size={15} />{busy ? 'Запускаем…' : running ? 'Сбор идёт…' : 'Собрать логи'}</button>
      </div>
    </section>
  </div>;
}
