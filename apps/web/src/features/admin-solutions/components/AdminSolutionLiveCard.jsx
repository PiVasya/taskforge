import React, { useEffect, useState } from 'react';
import { ExternalLink } from 'lucide-react';
import { Badge, Button } from '../../../components/ui';
import CodeEditor from '../../../components/CodeEditor';
import MathAttemptReview from '../../../components/math/MathAttemptReview';
import { loadAdminSolutionLiveDetail } from '../../../api/adminSolutionLive';
import {
  formatDateTime,
  getImageSolutionCode,
  getImageSolutionPercent,
  getImageSolutionThreshold,
  getImageSolutionUrl,
  getSolutionCode,
  getSolutionPassedFailed,
} from '../../../utils/solutionDto';
import { solutionKindLabel, solutionStatusLabel } from '../adminSolutionLiveModel';
import { CompactEmpty, RunnerOutput, TestAttemptReview } from './AdminSolutionViews';

export function AssignmentLinkButton({ assignmentId }) {
  if (!assignmentId) return null;
  return (
    <a
      href={`/assignment/${assignmentId}`}
      target="_blank"
      rel="noreferrer"
      title="К заданию"
      aria-label="К заданию"
      className="inline-flex h-7 w-7 shrink-0 items-center justify-center rounded-md border border-neutral-300 bg-transparent text-neutral-700 transition hover:bg-neutral-100 focus:outline-none focus-visible:ring-2 focus-visible:ring-[rgba(var(--accent)/0.45)] dark:border-neutral-700 dark:text-neutral-200 dark:hover:bg-neutral-800"
    >
      <ExternalLink size={14} aria-hidden="true" />
    </a>
  );
}

function detailActionLabel(kind, expanded) {
  if (expanded) return 'Скрыть';
  const value = String(kind || '').toLowerCase();
  return value === 'code' || value === 'sql' ? 'Показать код' : 'Просмотреть';
}

function CodeLiveDetails({ detail }) {
  const code = getSolutionCode(detail);
  const { passed, failed } = getSolutionPassedFailed(detail);

  return (
    <div className="space-y-3">
      {passed !== null || failed !== null ? (
        <div className="flex flex-wrap gap-2">
          <Badge intent="secondary">Пройдено: {passed ?? 0} / Провалено: {failed ?? 0}</Badge>
        </div>
      ) : null}
      {code ? (
        <div className="rounded-xl overflow-hidden border border-neutral-700">
          <CodeEditor
            language={detail?.language || detail?.Language || 'text'}
            value={code}
            readOnly
            onChange={() => {}}
            height={360}
          />
        </div>
      ) : (
        <CompactEmpty>Код не найден для этого решения.</CompactEmpty>
      )}
      <RunnerOutput item={detail} />
    </div>
  );
}

function ImageLiveDetails({ detail }) {
  const percent = getImageSolutionPercent(detail);
  const threshold = getImageSolutionThreshold(detail);
  const referenceUrl = getImageSolutionUrl(detail, 'reference');
  const submittedUrl = getImageSolutionUrl(detail, 'submitted');
  const code = getImageSolutionCode(detail);

  return (
    <div className="space-y-4">
      {percent !== null ? (
        <div className="flex flex-wrap gap-2">
          <Badge intent="secondary">
            {Math.round(percent)}%{threshold !== null ? ` (порог ${Math.round(threshold)}%)` : ''}
          </Badge>
        </div>
      ) : null}

      <div className="grid gap-4 md:grid-cols-2">
        <div className="space-y-2">
          <div className="text-xs uppercase tracking-wide text-neutral-500">Эталон</div>
          {referenceUrl ? (
            <img src={referenceUrl} alt="Эталон" className="w-full rounded-lg border border-neutral-200 dark:border-neutral-700" />
          ) : (
            <CompactEmpty>Эталон недоступен</CompactEmpty>
          )}
        </div>
        <div className="space-y-2">
          <div className="text-xs uppercase tracking-wide text-neutral-500">Результат</div>
          {submittedUrl ? (
            <img src={submittedUrl} alt="Результат" className="w-full rounded-lg border border-neutral-200 dark:border-neutral-700" />
          ) : (
            <CompactEmpty>Результат недоступен</CompactEmpty>
          )}
        </div>
      </div>

      {code ? (
        <div className="space-y-2">
          <div className="text-xs uppercase tracking-wide text-neutral-500">Код</div>
          <div className="rounded-xl overflow-hidden border border-neutral-700">
            <CodeEditor
              language={detail?.language || detail?.Language || 'text'}
              value={code}
              readOnly
              onChange={() => {}}
              height={360}
            />
          </div>
        </div>
      ) : null}

      <RunnerOutput item={detail} />
    </div>
  );
}

function LiveDetails({ kind, detail }) {
  const value = String(kind || '').toLowerCase();
  if (value === 'test') return <TestAttemptReview dto={detail} />;
  if (value === 'math') return <MathAttemptReview dto={detail} admin />;
  if (value === 'image') return <ImageLiveDetails detail={detail} />;
  return <CodeLiveDetails detail={detail} />;
}

export default function AdminSolutionLiveCard({ item }) {
  const [expanded, setExpanded] = useState(false);
  const [detail, setDetail] = useState(item?.detail || null);
  const [loading, setLoading] = useState(false);
  const [detailError, setDetailError] = useState('');

  useEffect(() => {
    if (!item?.detail) return;
    setDetail(item.detail);
    setDetailError('');
  }, [item?.detail]);

  const ensureDetail = async () => {
    if (detail || loading) return;
    setLoading(true);
    setDetailError('');
    try {
      const loaded = await loadAdminSolutionLiveDetail(item);
      setDetail(loaded || null);
      if (!loaded) setDetailError('Детали решения пока недоступны.');
    } catch {
      setDetailError('Не удалось загрузить детали решения.');
    } finally {
      setLoading(false);
    }
  };

  const toggleDetails = async () => {
    if (expanded) {
      setExpanded(false);
      return;
    }
    setExpanded(true);
    await ensureDetail();
  };

  const statusLabel = solutionStatusLabel(item?.status);
  const language = String(detail?.language || detail?.Language || '').trim();

  return (
    <div className="rounded-xl border border-neutral-200 dark:border-neutral-800/40 bg-[rgb(var(--card))] p-3">
      <div className="flex flex-col gap-2 sm:flex-row sm:items-center sm:justify-between">
        <div className="min-w-0">
          <div className="font-medium truncate">{item?.assignmentTitle}</div>
          <div className="text-xs text-neutral-600 dark:text-neutral-400">
            {item?.userLabel} • {formatDateTime(item?.occurredAtUtc)}{language ? ` • ${language}` : ''}
          </div>
        </div>
        <div className="flex flex-wrap items-center gap-2">
          <Badge intent="secondary">{solutionKindLabel(item?.kind)}</Badge>
          {statusLabel ? <Badge>{statusLabel}</Badge> : null}
          {item?.score != null && Number.isFinite(Number(item.score)) ? (
            <Badge intent="secondary">{Number(item.score)}%</Badge>
          ) : null}
          <AssignmentLinkButton assignmentId={item?.assignmentId} />
          <Button variant="outline" onClick={toggleDetails} disabled={loading}>
            {loading ? 'Загружаю…' : detailActionLabel(item?.kind, expanded)}
          </Button>
        </div>
      </div>

      {expanded ? (
        <div className="mt-3 space-y-3 border-t border-neutral-200 pt-3 dark:border-neutral-800/60">
          {loading ? <CompactEmpty>Загружаю детали решения…</CompactEmpty> : null}
          {!loading && detail ? <LiveDetails kind={item?.kind} detail={detail} /> : null}
          {!loading && !detail && detailError ? (
            <div className="space-y-2">
              <CompactEmpty>{detailError}</CompactEmpty>
              <Button variant="outline" onClick={ensureDetail}>Повторить загрузку</Button>
            </div>
          ) : null}
        </div>
      ) : null}
    </div>
  );
}
