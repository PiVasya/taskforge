import React, { useEffect, useMemo, useRef, useState } from 'react';
import { Activity, AlertTriangle, Boxes, CheckCircle2, Clock3, Download, FileArchive, HardDrive, Loader2, Server, X } from 'lucide-react';
import { downloadClusterDiagnostics, getClusterDiagnosticsJob, startClusterDiagnostics } from '../../api/systemStatus';
import { handleApiError } from '../../utils/handleApiError';
import {
  bytes,
  diagnosticsBatchProgress,
  diagnosticsEligibility,
  diagnosticsJobProgress,
  DIAGNOSTICS_MIN_AGENT_REVISION,
} from './clusterModel';
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
const JOB_STORAGE_KEY = 'taskforge-cluster-diagnostics-jobs-v2';
const JOB_STORAGE_MAX_AGE_MS = 6 * 60 * 60 * 1000;
const DIAGNOSTICS_PROGRESS_AGENT_REVISION = 68;
const terminal = status => ['completed', 'failed'].includes(String(status || '').toLowerCase());

function jobTone(status) {
  if (status === 'completed') return 'good';
  if (status === 'failed') return 'bad';
  return 'accent';
}

function jobLabel(status) {
  return ({ starting: 'Запуск', queued: 'В очереди', running: 'Собирается', completed: 'Готово', failed: 'Ошибка' })[status] || status || 'Ожидание';
}

function normalizeStatus(job, status) {
  const payload = status && typeof status === 'object' ? status : {};
  return {
    ...job,
    ...payload,
    node: job.node,
    jobId: job.jobId,
    accepted: job.accepted,
    acceptedAt: job.acceptedAt,
    mode: job.mode,
    status: payload.status || job.status,
    archiveName: payload.archive_name || payload.archiveName || job.archiveName,
    sizeBytes: payload.size_bytes ?? payload.sizeBytes ?? job.sizeBytes,
    startedAt: payload.started_at || payload.startedAt || job.startedAt,
    finishedAt: payload.finished_at || payload.finishedAt || job.finishedAt,
    message: payload.message || payload.error || job.message,
    pollError: null,
  };
}

function formatElapsed(seconds) {
  const value = Math.max(0, Number(seconds) || 0);
  if (value < 60) return `${value} сек`;
  const minutes = Math.floor(value / 60);
  const rest = value % 60;
  if (minutes < 60) return rest ? `${minutes} мин ${rest} сек` : `${minutes} мин`;
  const hours = Math.floor(minutes / 60);
  const mins = minutes % 60;
  return mins ? `${hours} ч ${mins} мин` : `${hours} ч`;
}

function readStoredJobs() {
  if (typeof window === 'undefined') return [];
  try {
    const parsed = JSON.parse(window.sessionStorage.getItem(JOB_STORAGE_KEY) || 'null');
    if (!parsed || !Array.isArray(parsed.jobs) || Date.now() - Number(parsed.savedAt || 0) > JOB_STORAGE_MAX_AGE_MS) return [];
    return parsed.jobs;
  } catch {
    return [];
  }
}

export default function ClusterDiagnosticsDialog({ open, nodes, initialNodeId, onClose, notify }) {
  const [selected, setSelected] = useState([]);
  const [mode, setMode] = useState('standard');
  const [since, setSince] = useState('6h');
  const [maxLogMb, setMaxLogMb] = useState(32);
  const [jobs, setJobs] = useState(() => readStoredJobs());
  const [busy, setBusy] = useState(false);
  const [downloading, setDownloading] = useState(null);
  const [lastPollAt, setLastPollAt] = useState(0);
  const [now, setNow] = useState(() => Date.now());
  const pollAbort = useRef(null);
  const jobsRef = useRef([]);
  const eligibleNodes = useMemo(() => nodes.filter(node => diagnosticsEligibility(node).ready), [nodes]);
  const batchProgress = useMemo(() => diagnosticsBatchProgress(jobs), [jobs]);

  useEffect(() => {
    if (!open) return;
    const preferred = eligibleNodes.find(node => node.id === initialNodeId) || eligibleNodes[0];
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
    if (typeof window === 'undefined') return;
    try {
      if (jobs.length) window.sessionStorage.setItem(JOB_STORAGE_KEY, JSON.stringify({ savedAt: Date.now(), jobs }));
      else window.sessionStorage.removeItem(JOB_STORAGE_KEY);
    } catch {}
  }, [jobs]);

  useEffect(() => {
    if (!open || !jobs.some(job => !terminal(job.status))) return undefined;
    const timer = window.setInterval(() => setNow(Date.now()), 1000);
    return () => window.clearInterval(timer);
  }, [open, jobs]);

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
          return {
            ...job,
            pollError: handleApiError(error, false, 'Не удалось проверить сбор логов')?.primaryMessage || 'Нет связи с Node Agent',
          };
        }
      }));
      if (!disposed && !controller.signal.aborted) {
        setJobs(next);
        setLastPollAt(Date.now());
        setNow(Date.now());
      }
    };
    void poll();
    const timer = window.setInterval(poll, 1500);
    return () => {
      disposed = true;
      window.clearInterval(timer);
      pollAbort.current?.abort();
      pollAbort.current = null;
    };
  }, [open]);

  if (!open) return null;

  const toggleNode = id => setSelected(current => current.includes(id) ? current.filter(x => x !== id) : [...current, id]);
  const allEligibleSelected = eligibleNodes.length > 0 && eligibleNodes.every(node => selected.includes(node.id));
  const running = jobs.some(job => job.accepted && !terminal(job.status));

  const start = async () => {
    if (selected.length === 0 || busy || running) return;
    const acceptedAt = Date.now();
    const optimistic = selected.map(node => ({
      node,
      accepted: true,
      jobId: null,
      status: 'starting',
      message: 'Связываемся с Node Agent…',
      acceptedAt,
      mode,
      since,
      maxLogMb,
    }));
    setBusy(true);
    setJobs(optimistic);
    setNow(acceptedAt);
    try {
      const result = await startClusterDiagnostics({ nodes: selected, mode, since, maxLogMb });
      const resultByNode = new Map((result?.jobs || []).map(job => [String(job.node), job]));
      const started = optimistic.map(base => {
        const job = resultByNode.get(String(base.node));
        if (!job) return { ...base, accepted: false, status: 'failed', message: 'Node Agent не вернул идентификатор сборки.' };
        return {
          ...base,
          ...job,
          acceptedAt,
          mode,
          since,
          maxLogMb,
          status: job.status || (job.accepted ? 'queued' : 'failed'),
        };
      });
      setJobs(started);
      const accepted = started.filter(job => job.accepted).length;
      if (accepted) notify?.info(`Сбор логов запущен: ${accepted} ${accepted === 1 ? 'нода' : 'ноды'}.`);
      if (started.some(job => !job.accepted)) notify?.warn('На части нод сбор не запустился. Причина показана в окне диагностики.');
    } catch (error) {
      const message = handleApiError(error, notify, 'Не удалось запустить сбор логов')?.primaryMessage || 'Не удалось запустить сбор логов';
      setJobs(optimistic.map(job => ({ ...job, accepted: false, status: 'failed', message })));
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
        <div className="tf-cluster-title-group"><span className="tf-cluster-server-mark"><FileArchive size={22} /></span><div><h2 id="cluster-diagnostics-title">Собрать логи</h2><p>Запускает штатный сборщик на выбранных серверах. Ноды обрабатываются независимо; сбор не меняет состояние кластера.</p></div></div>
        <button type="button" className="tf-cluster-icon" aria-label="Закрыть" disabled={busy} onClick={onClose}><X size={17} /></button>
      </div>

      <div className="tf-cluster-diagnostics-body">
        <section className="tf-cluster-diagnostics-section">
          <div className="tf-cluster-diagnostics-section-head"><div><strong>Серверы</strong><small>Архив создаётся отдельно на каждой ноде. Сбор поддерживается с Agent r{DIAGNOSTICS_MIN_AGENT_REVISION}+, поконтейнерный прогресс — с r{DIAGNOSTICS_PROGRESS_AGENT_REVISION}+.</small></div>{eligibleNodes.length > 1 && <button type="button" className="tf-cluster-button" onClick={() => setSelected(allEligibleSelected ? [] : eligibleNodes.map(node => node.id))}>{allEligibleSelected ? 'Снять все' : 'Все доступные'}</button>}</div>
          <div className="tf-cluster-diagnostics-nodes">
            {nodes.map(node => {
              const eligibility = diagnosticsEligibility(node);
              const subtitle = !node.online
                ? 'Node Agent офлайн'
                : eligibility.reason === 'agent-too-old'
                  ? `${String(node.appProfile || '—').toUpperCase()} · Agent r${node.bundleRevision || '—'} · нужен r${DIAGNOSTICS_MIN_AGENT_REVISION}+`
                  : `${String(node.appProfile || '—').toUpperCase()} · Agent r${node.bundleRevision || '—'}`;
              return <button type="button" key={node.id} className={`tf-cluster-diagnostics-node ${selected.includes(node.id) ? 'is-selected' : ''}`} aria-pressed={selected.includes(node.id)} disabled={!eligibility.ready || running} onClick={() => toggleNode(node.id)}>
                <span className="tf-cluster-server-letter">{node.id}</span><span><strong>Сервер {node.id}</strong><small>{subtitle}</small></span><Tag tone={!node.online ? 'bad' : eligibility.ready ? 'good' : 'warn'}>{!node.online ? 'Офлайн' : eligibility.ready ? 'Готов' : 'Старая версия'}</Tag>
              </button>;
            })}
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

        {jobs.length > 0 && <section className="tf-cluster-diagnostics-section tf-cluster-diagnostics-live" aria-live="polite">
          <div className="tf-cluster-diagnostics-section-head">
            <div><strong>Сборка</strong><small>Статус обновляется автоматически. Сбор на серверах продолжится, даже если закрыть это окно.</small></div>
            {lastPollAt > 0 && <span className="tf-cluster-diagnostics-freshness"><Activity size={12} /> обновлено {Math.max(0, Math.floor((now - lastPollAt) / 1000))} сек назад</span>}
          </div>

          <div className="tf-cluster-diagnostics-batch">
            <div className="tf-cluster-diagnostics-batch-head">
              <div><strong>{batchProgress.terminal}/{batchProgress.total} нод завершено</strong><small>{batchProgress.running ? `Сейчас собираются: ${batchProgress.running}` : batchProgress.failed ? `Ошибок: ${batchProgress.failed}` : 'Все выбранные ноды завершили сбор'}</small></div>
              <span>{batchProgress.percent}%</span>
            </div>
            <div className="tf-cluster-diagnostics-progress" aria-label={`Завершено ${batchProgress.terminal} из ${batchProgress.total} нод`}>
              <span style={{ width: `${batchProgress.percent}%` }} />
            </div>
          </div>

          <div className="tf-cluster-diagnostics-jobs">
            {jobs.map(job => {
              const key = `${job.node}:${job.jobId || 'none'}`;
              const progress = diagnosticsJobProgress(job, now);
              const node = nodes.find(item => String(item?.id) === String(job.node));
              const agentRevision = Number(node?.bundleRevision || 0);
              const supportsDetailedProgress = Number.isFinite(agentRevision) && agentRevision >= DIAGNOSTICS_PROGRESS_AGENT_REVISION;
              const itemCounter = progress.totalItems > 0 && progress.completedItems != null
                ? `${progress.completedItems}/${progress.totalItems}`
                : null;
              const containerCounter = progress.containersTotal > 0 && progress.containersCompleted != null
                ? `${Math.min(progress.containersCompleted, progress.containersTotal)}/${progress.containersTotal}`
                : null;
              const containerPercent = progress.containersTotal > 0 && progress.containersCompleted != null
                ? Math.max(0, Math.min(100, progress.containersCompleted / progress.containersTotal * 100))
                : null;
              const detail = job.pollError || progress.detail || job.archiveName || job.message || (job.jobId ? `Job ${job.jobId}` : 'Ожидание ответа Node Agent');
              return <div key={key} className={`tf-cluster-diagnostics-job is-${jobTone(job.status)}`}>
                <span className="tf-cluster-diagnostics-job-icon">{job.status === 'completed' ? <CheckCircle2 size={17} /> : job.status === 'failed' ? <AlertTriangle size={17} /> : <Loader2 size={17} className="tf-cluster-spin" />}</span>
                <div className="tf-cluster-diagnostics-job-main">
                  <div><strong>Сервер {job.node}</strong><Tag tone={jobTone(job.status)}>{jobLabel(job.status)}</Tag>{progress.phaseLabel && !terminal(job.status) && <Tag>{progress.phaseLabel}</Tag>}{job.sizeBytes != null && <Tag>{bytes(job.sizeBytes)}</Tag>}</div>
                  <small className={job.pollError ? 'is-error' : ''}>{detail}</small>
                  {progress.hasRealCounters || terminal(job.status) ? <>
                    <div className="tf-cluster-diagnostics-job-progress" aria-label={itemCounter ? `Завершено ${itemCounter} шагов` : 'Диагностика завершена'}>
                      <span style={{ width: `${progress.percent ?? (job.status === 'completed' ? 100 : 0)}%` }} />
                    </div>
                    {itemCounter && <div className="tf-cluster-diagnostics-progress-caption"><span>Шаги сборщика</span><strong>{itemCounter}</strong></div>}
                  </> : <div className="tf-cluster-diagnostics-progress-unavailable">
                    {supportsDetailedProgress ? 'Ждём первый реальный счётчик от Node Agent…' : `Agent r${agentRevision || '?'} не отдаёт поконтейнерный прогресс. Нужен r${DIAGNOSTICS_PROGRESS_AGENT_REVISION}+.`}
                  </div>}
                  {containerCounter && <div className="tf-cluster-diagnostics-container-progress">
                    <div><span><Boxes size={12} /> Контейнеры</span><strong>{containerCounter} собрано</strong></div>
                    <div className="tf-cluster-diagnostics-job-progress"><span style={{ width: `${containerPercent}%` }} /></div>
                    {progress.phase === 'containers' && progress.currentIndex > 0 && <small>Сейчас: контейнер {progress.currentIndex} из {progress.containersTotal}{progress.currentItem ? ` · ${progress.currentItem}` : ''}{progress.stepLabel ? ` · ${progress.stepLabel}` : ''}</small>}
                  </div>}
                  <div className="tf-cluster-diagnostics-job-meta">
                    <span><Clock3 size={12} /> {formatElapsed(progress.elapsedSeconds)}</span>
                    {progress.filesCollected != null && <span><Activity size={12} /> {progress.filesCollected} файлов</span>}
                    {progress.bytesCollected != null && <span><HardDrive size={12} /> {bytes(progress.bytesCollected)} собрано</span>}
                    {progress.archiveBytes > 0 && progress.phase === 'archive' && <span><FileArchive size={12} /> {bytes(progress.archiveBytes)} в архиве</span>}
                  </div>
                </div>
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
