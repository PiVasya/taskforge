import React, { useEffect, useMemo, useState } from 'react';
import {
  Activity,
  Bot,
  BrainCircuit,
  CheckCircle2,
  ClipboardCopy,
  FileJson,
  GitCompare,
  Loader2,
  Paperclip,
  Plus,
  Sparkles,
  TerminalSquare,
  UserRound,
  X,
} from 'lucide-react';
import { Card, Badge } from '../../../components/ui';
import StatementViewer from '../../../components/tiptap/StatementViewer';

const RUNNING_STATUSES = new Set(['queued', 'running', 'planning', 'sleeping', 'waiting_approval']);
const FINAL_STATUSES = new Set(['completed', 'completed_with_warnings', 'failed', 'canceled']);
const AI_DIAGNOSTICS_ENABLED = true;

function nowIso() {
  return new Date().toISOString();
}

function normalizeId(value) {
  return value == null ? '' : String(value);
}

function safeText(value, fallback = '') {
  const text = value == null ? '' : String(value);
  return text.trim() || fallback;
}

function getRunStatusLabel(status) {
  const s = String(status || '').toLowerCase();
  if (s === 'queued') return 'в очереди';
  if (s === 'running') return 'думает';
  if (s === 'planning') return 'строит план';
  if (s === 'sleeping') return 'ждёт продолжения';
  if (s === 'waiting_approval') return 'ждёт подтверждения';
  if (s === 'completed') return 'готово';
  if (s === 'completed_with_warnings') return 'готово с заметками';
  if (s === 'failed') return 'ошибка';
  if (s === 'canceled') return 'остановлено';
  return s || 'нет статуса';
}

function getRunBadgeVariant(status) {
  const s = String(status || '').toLowerCase();
  if (s === 'completed') return 'success';
  if (s === 'failed' || s === 'canceled') return 'danger';
  return 'outline';
}

function sortByTimeAsc(items) {
  return [...(items || [])].sort((a, b) => new Date(a.createdAtUtc || a.createdAt || 0) - new Date(b.createdAtUtc || b.createdAt || 0));
}

function sortRunsDesc(items) {
  return [...(items || [])].sort((a, b) => new Date(b.createdAtUtc || 0) - new Date(a.createdAtUtc || 0));
}

function mergeById(prev, incoming) {
  const map = new Map();
  [...(prev || []), ...(incoming || [])].forEach((item) => {
    const id = normalizeId(item?.id);
    if (!id) return;
    map.set(id, { ...(map.get(id) || {}), ...item });
  });
  return Array.from(map.values());
}

function upsertById(prev, item) {
  const id = normalizeId(item?.id);
  if (!id) return prev || [];
  const found = (prev || []).some((x) => normalizeId(x.id) === id);
  if (!found) return [...(prev || []), item];
  return (prev || []).map((x) => (normalizeId(x.id) === id ? { ...x, ...item } : x));
}

function upsertMessage(prev, item) {
  const id = normalizeId(item?.id);
  const clientId = normalizeId(item?.clientMessageId);
  if (!id && !clientId) return prev || [];

  const cleaned = (prev || []).filter((x) => {
    const sameId = id && normalizeId(x.id) === id;
    const sameClientId = clientId && normalizeId(x.clientMessageId) === clientId;
    return !(sameId || sameClientId);
  });

  return [...cleaned, item];
}

function getArtifactData(message) {
  const data = message?.data || message?.Data;
  if (!data || typeof data !== 'object') return [];
  const artifacts = Array.isArray(data.artifacts) ? data.artifacts : [];
  return artifacts;
}

function getMessageAttachments(message) {
  const fromDto = Array.isArray(message?.attachments) ? message.attachments : [];
  const data = message?.data || message?.Data;
  const fromData = data && typeof data === 'object' && Array.isArray(data.attachments) ? data.attachments : [];
  return fromDto.length ? fromDto : fromData;
}

function pickReadableArtifactTitle(artifact) {
  return artifact?.title || artifact?.data?.title || getArtifactTypeLabel(artifact?.type);
}


function getArtifactTypeLabel(type) {
  const value = String(type || '').toLowerCase();
  if (value === 'assignment_draft_ready') return 'Черновик задания';
  if (value === 'polished_assignment_draft') return 'Доработанный черновик';
  if (value === 'course_gap_audit') return 'Анализ курса';
  if (value === 'course_edit_proposal') return 'Предложение правок';
  if (value === 'approval_request') return 'Ожидает подтверждения';
  if (value === 'agent_plan') return 'План действий';
  if (value === 'course_skill_map_ready') return 'Карта навыков курса';
  return 'Материал от ассистента';
}

function isApplyableArtifact(artifact) {
  const type = String(artifact?.type || '').toLowerCase();
  if (['course_edit_proposal', 'assignment_update_batch', 'course_style_update', 'course_patch_set'].includes(type)) return true;
  if (type !== 'approval_request') return false;
  const operation = String(artifact?.data?.operation || artifact?.operation || '').toLowerCase();
  return ['apply_course_edit', 'apply_assignment_update_batch', 'save_hidden_draft'].includes(operation);
}


function isPatchSetArtifact(artifact) {
  const type = String(artifact?.type || artifact?.Type || artifact?.data?.type || '').toLowerCase();
  const data = artifact?.data || artifact?.Data || artifact;
  return type === 'course_patch_set' || type.includes('patch_set') || Array.isArray(data?.patches);
}

function getPatchSetData(artifact) {
  const data = artifact?.data || artifact?.Data || artifact || {};
  return data?.patches ? data : data?.data?.patches ? data.data : data;
}

function collectPatchSets(messages, runs) {
  const result = [];
  const push = (artifact, source) => {
    if (!artifact || !isPatchSetArtifact(artifact)) return;
    const data = getPatchSetData(artifact);
    result.push({ artifact, data, source, key: normalizeId(artifact.id || artifact.artifactId || `${source}-${result.length}`) || `${source}-${result.length}` });
  };
  (messages || []).forEach((message) => getArtifactData(message).forEach((artifact, idx) => push(artifact, `message-${message?.id || idx}`)));
  (runs || []).forEach((run) => (Array.isArray(run?.artifacts) ? run.artifacts : []).forEach((artifact, idx) => push(artifact, `run-${run?.id || idx}`)));
  const seen = new Set();
  return result.filter((item) => {
    const key = item.key;
    if (seen.has(key)) return false;
    seen.add(key);
    return true;
  });
}

function formatDiffValue(value) {
  if (value == null) return 'null';
  if (typeof value === 'string') return JSON.stringify(value);
  try { return JSON.stringify(value); } catch { return String(value); }
}

function getArtifactStableKey({ persistedArtifact, message, artifactIndex, artifact }) {
  return normalizeId(persistedArtifact?.id || artifact?.id || artifact?.artifactId || `${message?.runId || message?.id || 'artifact'}-${artifactIndex}`);
}

function findPersistedArtifactForMessage(runs, message, artifact, artifactIndex) {
  const directId = artifact?.id || artifact?.artifactId;
  if (directId) return { ...artifact, id: directId };

  const runId = normalizeId(message?.runId);
  if (!runId) return null;
  const run = (runs || []).find((x) => normalizeId(x?.id) === runId);
  const artifacts = Array.isArray(run?.artifacts) ? run.artifacts : [];
  if (!artifacts.length) return null;

  const type = String(artifact?.type || '').toLowerCase();
  const sameType = artifacts.filter((x) => String(x?.type || '').toLowerCase() === type);
  if (sameType.length === 1) return sameType[0];
  if (sameType[artifactIndex]) return sameType[artifactIndex];

  const title = pickReadableArtifactTitle(artifact);
  return sameType.find((x) => pickReadableArtifactTitle(x) === title) || artifacts[artifactIndex] || null;
}

function summarizeApplyResult(result) {
  if (!result) return '';
  const updated = Array.isArray(result.updated) ? result.updated.length : 0;
  const validations = Array.isArray(result.validations) ? result.validations.length : 0;
  const bits = [];
  bits.push(result.dryRun ? 'Проверено без записи' : 'Применено');
  bits.push(`действий: ${updated}`);
  if (validations) bits.push(`проверок: ${validations}`);
  if (result.reordered) bits.push('порядок обновляется');
  return bits.join(' · ');
}

function getLatestActiveRun(runs) {
  const ordered = sortRunsDesc(runs || []);
  return ordered.find((r) => RUNNING_STATUSES.has(String(r.status || '').toLowerCase())) || null;
}

function getLatestRun(runs) {
  const ordered = sortRunsDesc(runs || []);
  return ordered[0] || null;
}


function getRunTypeLabel(run) {
  const value = String(run?.scenarioId || run?.jobType || '').toLowerCase();
  if (value.includes('polish')) return 'Доработка задания';
  if (value.includes('draft')) return 'Черновик задания';
  if (value.includes('audit') || value.includes('gap')) return 'Анализ курса';
  if (value.includes('edit')) return 'Правки курса';
  return 'Ответ ассистента';
}

function ThinkingDots() {
  return (
    <span className="inline-flex items-center gap-1 pl-1 align-middle" aria-hidden="true">
      <span className="h-1.5 w-1.5 rounded-full bg-current opacity-40 animate-pulse" />
      <span className="h-1.5 w-1.5 rounded-full bg-current opacity-60 animate-pulse [animation-delay:120ms]" />
      <span className="h-1.5 w-1.5 rounded-full bg-current opacity-80 animate-pulse [animation-delay:240ms]" />
    </span>
  );
}

function MessageBubble({ message, runs, onPolishTask, onPolishSelectedTasks, onApplyArtifact, onOpenPatchSet, selectedDraftTasks, onToggleDraftTask, onSetDraftTasks, polishingTasks, applyingArtifacts, artifactApplyResults, currentCourseId }) {
  const role = String(message?.role || '').toLowerCase();
  const isUser = role === 'user';
  const artifacts = getArtifactData(message);
  const text = safeText(message?.text || message?.Text, isUser ? 'Сообщение' : 'Готово.');

  return (
    <div className={`flex gap-3 ${isUser ? 'justify-end' : 'justify-start'}`}>
      {!isUser && (
        <div className="mt-1 h-9 w-9 shrink-0 rounded-2xl grid place-items-center border border-brand-200/70 dark:border-brand-900/70 bg-brand-600/10 text-brand-700 dark:text-brand-300">
          <Bot size={18} />
        </div>
      )}
      <div className={`max-w-[min(54rem,92%)] ${isUser ? 'items-end' : 'items-start'} flex flex-col gap-2`}>
        <div
          className={
            isUser
              ? 'rounded-2xl rounded-br-md bg-brand-600 text-white px-4 py-3 shadow-soft whitespace-pre-wrap leading-relaxed'
              : 'rounded-2xl rounded-bl-md border border-neutral-200/70 dark:border-neutral-800/70 bg-[rgb(var(--card))]/90 px-4 py-3 shadow-soft whitespace-pre-wrap leading-relaxed'
          }
        >
          {text}
        </div>

        {getMessageAttachments(message).length > 0 && (
          <div className="flex flex-wrap gap-2 px-1">
            {getMessageAttachments(message).map((file, idx) => (
              <a
                key={`${file?.key || file?.fileName || idx}`}
                href={file?.url || (file?.key ? `/api/private-files/${encodeURIComponent(file.key)}` : undefined)}
                target="_blank"
                rel="noreferrer"
                className="inline-flex max-w-[18rem] items-center gap-1.5 rounded-xl border border-neutral-200/70 bg-white/70 px-2.5 py-1 text-xs text-neutral-700 hover:border-brand-300 dark:border-neutral-800/70 dark:bg-neutral-950/40 dark:text-neutral-200"
                title={file?.fileName || file?.key || 'файл'}
              >
                <Paperclip size={13} />
                <span className="truncate">{file?.fileName || file?.key || 'файл'}</span>
              </a>
            ))}
          </div>
        )}

        {artifacts.length > 0 && (
          <div className="w-full space-y-2">
            {artifacts.map((artifact, idx) => (
              <ArtifactPreview
                key={`${artifact?.type || 'artifact'}-${idx}`}
                artifact={artifact}
                message={message}
                artifactIndex={idx}
                onPolishTask={onPolishTask}
                runs={runs}
                onPolishSelectedTasks={onPolishSelectedTasks}
                onApplyArtifact={onApplyArtifact}
                onOpenPatchSet={onOpenPatchSet}
                selectedDraftTasks={selectedDraftTasks}
                onToggleDraftTask={onToggleDraftTask}
                onSetDraftTasks={onSetDraftTasks}
                polishingTasks={polishingTasks}
                applyingArtifacts={applyingArtifacts}
                artifactApplyResults={artifactApplyResults}
                currentCourseId={currentCourseId}
              />
            ))}
          </div>
        )}

        <div className="text-[11px] text-neutral-500 dark:text-neutral-400 px-2">
          {message?.createdAtUtc ? new Date(message.createdAtUtc).toLocaleString() : ''}
        </div>
      </div>
      {isUser && (
        <div className="mt-1 h-9 w-9 shrink-0 rounded-2xl grid place-items-center border border-neutral-200/70 dark:border-neutral-800/70 bg-neutral-100/80 dark:bg-neutral-900/80">
          <UserRound size={18} />
        </div>
      )}
    </div>
  );
}

function ArtifactPreview({ artifact, message, runs, artifactIndex, onPolishTask, onPolishSelectedTasks, onApplyArtifact, onOpenPatchSet, selectedDraftTasks, onToggleDraftTask, onSetDraftTasks, polishingTasks, applyingArtifacts, artifactApplyResults, currentCourseId }) {
  const persistedArtifact = findPersistedArtifactForMessage(runs, message, artifact, artifactIndex);
  const artifactKey = getArtifactStableKey({ persistedArtifact, message, artifactIndex, artifact });
  const data = artifact?.data || persistedArtifact?.data || {};
  const tasks = Array.isArray(data.tasks) ? data.tasks : Array.isArray(data.drafts) ? data.drafts : [];
  const findings = Array.isArray(data.findings) ? data.findings : [];
  const title = pickReadableArtifactTitle(artifact);
  const taskItems = tasks.map((task, i) => {
    const taskKey = `${message?.id || message?.runId || 'message'}-${artifactIndex}-${task?.index || i}`;
    return { task, taskIndex: task.index ?? i + 1, artifact, artifactIndex, message, taskKey };
  });
  const artifactPlacement = data?.placement || {};
  const hasPersistTarget = Boolean(
    currentCourseId ||
    data?.selectedCourseId ||
    data?.courseId ||
    artifactPlacement.beforeAssignmentId ||
    artifactPlacement.afterAssignmentId ||
    taskItems.some(({ task }) => task?.selectedCourseId || task?.courseId || task?.beforeAssignmentId || task?.afterAssignmentId || task?.placement?.beforeAssignmentId || task?.placement?.afterAssignmentId),
  );
  const selectedInArtifact = taskItems.filter((item) => selectedDraftTasks?.[item.taskKey]).length;
  const allSelected = taskItems.length > 0 && selectedInArtifact === taskItems.length;
  const anyPolishing = taskItems.some((item) => polishingTasks?.[item.taskKey]);
  const isPatchSet = isPatchSetArtifact(artifact) || isPatchSetArtifact(persistedArtifact);
  const canApply = isApplyableArtifact(artifact);
  const applyingMode = applyingArtifacts?.[artifactKey];
  const applyResult = artifactApplyResults?.[artifactKey];

  const selectAll = () => {
    if (!onSetDraftTasks) return;
    onSetDraftTasks(taskItems, !allSelected);
  };

  return (
    <div className="rounded-2xl border border-brand-200/70 dark:border-brand-900/70 bg-brand-50/50 dark:bg-brand-950/20 p-3">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <div className="flex items-center gap-2 font-semibold">
          <FileJson size={16} />
          <span>{title}</span>
        </div>
        <div className="flex flex-wrap items-center gap-2">
          {isPatchSet && (
            <button
              type="button"
              onClick={() => onOpenPatchSet?.(artifactKey)}
              className="inline-flex items-center gap-1.5 rounded-xl border border-brand-200 bg-white/80 px-2.5 py-1.5 text-xs font-medium text-brand-700 hover:bg-brand-50 dark:border-brand-900 dark:bg-neutral-950/50 dark:text-brand-300"
            >
              <GitCompare size={14} />
              открыть патчи
            </button>
          )}
          {tasks.length > 0 && (
            <>
              <button
                type="button"
                onClick={selectAll}
                className="inline-flex items-center gap-1.5 rounded-xl border border-brand-200 bg-white/80 px-2.5 py-1.5 text-xs font-medium text-brand-700 hover:bg-brand-50 dark:border-brand-900 dark:bg-neutral-950/50 dark:text-brand-300"
              >
                {allSelected ? 'снять выбор' : 'выбрать все'}
              </button>
              <button
                type="button"
                disabled={!selectedInArtifact || anyPolishing || !hasPersistTarget}
                onClick={() => onPolishSelectedTasks?.(taskItems.filter((item) => selectedDraftTasks?.[item.taskKey]))}
                className="inline-flex items-center gap-1.5 rounded-xl border border-brand-200 bg-brand-600 px-2.5 py-1.5 text-xs font-medium text-white hover:bg-brand-700 disabled:opacity-60 dark:border-brand-900"
                title={hasPersistTarget ? 'Отправить выбранные задания пачкой. Каждое задание будет обработано отдельно.' : 'Нельзя создать скрытые черновики: ассистент не определил курс или место вставки.'}
              >
                {anyPolishing ? <Loader2 size={14} className="animate-spin" /> : <CheckCircle2 size={14} />}
                {hasPersistTarget ? `в черновики${selectedInArtifact ? `: ${selectedInArtifact}` : ''}` : 'нужен курс'}
              </button>
            </>
          )}
          <span className="text-xs text-neutral-500">{getArtifactTypeLabel(artifact?.type)}</span>
        </div>
      </div>

      {data?.summary && <div className="mt-2 text-sm text-neutral-600 dark:text-neutral-300">{data.summary}</div>}
      {tasks.length > 0 && !hasPersistTarget && (
        <div className="mt-2 rounded-xl border border-amber-300 bg-amber-50/80 px-3 py-2 text-sm text-amber-800 dark:border-amber-800 dark:bg-amber-950/30 dark:text-amber-200">
          Ассистент не определил курс или место вставки. Можно просмотреть предложения, но сохранить их как черновики пока нельзя.
        </div>
      )}

      {findings.length > 0 && (
        <div className="mt-3 space-y-2">
          {findings.slice(0, 2).map((f, i) => (
            <div key={`finding-${i}`} className="rounded-xl bg-white/60 dark:bg-neutral-950/30 p-3 text-sm">
              <div className="font-semibold">{f.concept || f.kind || `Сложное место ${i + 1}`}</div>
              <div className="mt-1 text-neutral-600 dark:text-neutral-300">{f.reason || f.summary || 'Найдено слабое место в курсе.'}</div>
            </div>
          ))}
        </div>
      )}

      {canApply && (
        <div className="mt-3 rounded-xl border border-emerald-200 bg-emerald-50/70 p-3 text-sm text-emerald-900 dark:border-emerald-900 dark:bg-emerald-950/20 dark:text-emerald-100">
          <div className="flex flex-wrap items-center justify-between gap-2">
            <div>
              <div className="font-semibold">Предложение можно применить к курсу</div>
              <div className="text-xs opacity-80">Сначала можно выполнить безопасную проверку: система проверит права, структуру изменений и тесты без сохранения.</div>
            </div>
            <div className="flex flex-wrap items-center gap-2">
              <button
                type="button"
                disabled={!persistedArtifact?.id || !!applyingMode}
                onClick={() => onApplyArtifact?.({ message, artifact, persistedArtifact, artifactIndex, dryRun: true })}
                className="inline-flex items-center gap-1.5 rounded-xl border border-emerald-300 bg-white/80 px-2.5 py-1.5 text-xs font-medium text-emerald-800 hover:bg-emerald-50 disabled:opacity-60 dark:border-emerald-900 dark:bg-neutral-950/50 dark:text-emerald-200"
                title={persistedArtifact?.id ? 'Проверить изменения без сохранения' : 'Материал ещё не готов к применению. Обновите чат после завершения обработки.'}
              >
                {applyingMode === 'check' ? <Loader2 size={14} className="animate-spin" /> : <Activity size={14} />}
                проверить
              </button>
              <button
                type="button"
                disabled={!persistedArtifact?.id || !!applyingMode}
                onClick={() => onApplyArtifact?.({ message, artifact, persistedArtifact, artifactIndex, dryRun: false })}
                className="inline-flex items-center gap-1.5 rounded-xl border border-emerald-600 bg-emerald-600 px-2.5 py-1.5 text-xs font-medium text-white hover:bg-emerald-700 disabled:opacity-60"
                title={persistedArtifact?.id ? 'Применить изменения после проверки' : 'Материал ещё не готов к применению. Обновите чат после завершения обработки.'}
              >
                {applyingMode === 'apply' ? <Loader2 size={14} className="animate-spin" /> : <CheckCircle2 size={14} />}
                применить
              </button>
            </div>
          </div>
          {!persistedArtifact?.id && (
            <div className="mt-2 rounded-lg border border-amber-300 bg-amber-50 px-2.5 py-2 text-xs text-amber-800 dark:border-amber-800 dark:bg-amber-950/30 dark:text-amber-200">
              Для применения нужно дождаться сохранения результата. Нажмите обновить чат, если обработка уже завершилась.
            </div>
          )}
          {applyResult && (
            <div className={`mt-2 rounded-lg px-2.5 py-2 text-xs ${applyResult.ok ? 'bg-white/70 text-emerald-900 dark:bg-neutral-950/40 dark:text-emerald-100' : 'bg-red-50 text-red-800 dark:bg-red-950/30 dark:text-red-200'}`}>
              {summarizeApplyResult(applyResult) || applyResult.message}
              {applyResult.message && <div className="mt-1 opacity-80">{applyResult.message}</div>}
            </div>
          )}
        </div>
      )}

      {taskItems.length > 0 && (
        <div className="mt-3 space-y-2">
          {taskItems.map((item, i) => {
            const { task, taskKey } = item;
            const polishing = !!polishingTasks?.[taskKey];
            const selected = !!selectedDraftTasks?.[taskKey];
            return (
              <div key={`${task?.title || 'task'}-${i}`} className={`rounded-xl bg-white/70 dark:bg-neutral-950/30 p-3 text-sm transition ${selected ? 'ring-2 ring-brand-300/80 dark:ring-brand-800' : ''}`}>
                <div className="flex flex-wrap items-start justify-between gap-2">
                  <label className="flex min-w-0 flex-1 cursor-pointer items-start gap-2">
                    <input
                      type="checkbox"
                      checked={selected}
                      onChange={() => onToggleDraftTask?.(item)}
                      className="mt-1 h-4 w-4 rounded border-brand-300 text-brand-600 focus:ring-brand-500"
                      title="Выбрать задание для доработки"
                    />
                    <div className="min-w-0">
                      <div className="font-semibold">{task.title || `Задание ${i + 1}`}</div>
                      <div className="mt-1 flex flex-wrap items-center gap-2 text-xs text-neutral-500">
                        <Badge variant="secondary">сложность {task.difficulty || 1}</Badge>
                        {task.language && <span>{task.language}</span>}
                      </div>
                    </div>
                  </label>
                  {onPolishTask && (
                    <button
                      type="button"
                      disabled={polishing || !hasPersistTarget}
                      onClick={() => onPolishTask(item)}
                      className="inline-flex items-center gap-1.5 rounded-xl border border-brand-200 bg-white/80 px-2.5 py-1.5 text-xs font-medium text-brand-700 hover:bg-brand-50 disabled:opacity-60 dark:border-brand-900 dark:bg-neutral-950/50 dark:text-brand-300"
                      title={hasPersistTarget ? 'Выбрать только это задание: ассистент доработает его, проверит решение и создаст скрытый черновик' : 'Нельзя создать скрытый черновик: ассистент не определил курс или место вставки.'}
                    >
                      {polishing ? <Loader2 size={14} className="animate-spin" /> : <CheckCircle2 size={14} />}
                      {polishing ? 'дорабатываю' : hasPersistTarget ? 'одно в черновик' : 'нужен курс'}
                    </button>
                  )}
                </div>
                <div className="mt-2 text-neutral-700 dark:text-neutral-200 text-sm">
                  <StatementViewer value={task.description || task.goal || task.pedagogicalGoal || 'Черновик задания готов.'} />
                </div>
                {Array.isArray(task.publicTests) && task.publicTests.length > 0 && (
                  <div className="mt-2 rounded-lg border border-neutral-200/60 dark:border-neutral-800/60 bg-[rgb(var(--card))]/60 p-2 text-xs">
                    <div className="uppercase tracking-wide text-neutral-500">Публичный тест</div>
                    <div className="mt-1 grid gap-1 sm:grid-cols-2">
                      <div><span className="opacity-60">Ввод:</span> <code>{task.publicTests[0]?.input || '—'}</code></div>
                      <div><span className="opacity-60">Вывод:</span> <code>{task.publicTests[0]?.expectedOutput || '—'}</code></div>
                    </div>
                  </div>
                )}
              </div>
            );
          })}
        </div>
      )}
    </div>
  );
}

function ThinkingPanel({ run }) {
  if (!run || FINAL_STATUSES.has(String(run.status || '').toLowerCase())) return null;
  const steps = sortByTimeAsc(run.steps || []).filter((x) => x?.isVisibleToUser !== false).slice(-5);
  const statusLabel = getRunStatusLabel(run.status);

  return (
    <div className="rounded-3xl border border-brand-200/70 dark:border-brand-900/70 bg-brand-50/70 dark:bg-brand-950/20 p-4 shadow-soft">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <div className="flex items-center gap-3">
          <div className="h-10 w-10 rounded-2xl grid place-items-center bg-brand-600 text-white shadow-soft">
            <BrainCircuit size={20} />
          </div>
          <div>
            <div className="font-semibold">AI сейчас {statusLabel}<ThinkingDots /></div>
            <div className="text-sm text-neutral-600 dark:text-neutral-300">Подтягивает контекст курса, проверяет данные и собирает ответ.</div>
          </div>
        </div>
        <Badge variant="outline">{getRunTypeLabel(run)}</Badge>
      </div>

      {steps.length > 0 && (
        <div className="mt-4 grid gap-2">
          {steps.map((step) => (
            <div key={step.id || `${step.seq}-${step.title}`} className="flex gap-3 rounded-2xl bg-white/70 dark:bg-neutral-950/30 p-3 text-sm">
              <div className="mt-0.5 h-6 w-6 rounded-xl grid place-items-center bg-brand-600/10 text-brand-700 dark:text-brand-300">
                {step.status === 'running' ? <Loader2 size={14} className="animate-spin" /> : <Activity size={14} />}
              </div>
              <div className="min-w-0">
                <div className="font-medium truncate">{step.title || step.actionName || 'AI-шаг'}</div>
                {step.summary && <div className="text-neutral-600 dark:text-neutral-300">{step.summary}</div>}
              </div>
            </div>
          ))}
        </div>
      )}
    </div>
  );
}

function ConversationList({ conversations, selectedId, onSelect, onCreate, loading }) {
  return (
    <Card className="h-full p-3 flex flex-col gap-3">
      <div className="flex items-center justify-between gap-2 px-1">
        <div>
          <div className="text-sm font-semibold">AI-чаты</div>
          <div className="text-xs text-neutral-500 dark:text-neutral-400">курсы, задания, улучшения</div>
        </div>
        <button type="button" className="btn-outline !min-w-0 !px-3" onClick={onCreate} title="Новый чат">
          <Plus size={16} />
        </button>
      </div>

      <div className="min-h-0 flex-1 overflow-y-auto pr-1 space-y-2">
        {loading && <div className="text-sm text-neutral-500 p-3">Загрузка чатов…</div>}
        {!loading && conversations.length === 0 && (
          <div className="rounded-2xl border border-dashed border-neutral-300/70 dark:border-neutral-700/70 p-4 text-sm text-neutral-500 dark:text-neutral-400">
            Пока нет AI-чатов. Напиши первый запрос — чат создастся сам.
          </div>
        )}
        {conversations.map((conv) => {
          const active = normalizeId(conv.id) === normalizeId(selectedId);
          return (
            <button
              key={conv.id}
              type="button"
              onClick={() => onSelect(conv.id)}
              className={`w-full rounded-2xl border p-3 text-left transition ${
                active
                  ? 'border-brand-400 bg-brand-50/80 dark:bg-brand-950/30'
                  : 'border-neutral-200/70 dark:border-neutral-800/70 bg-white/40 dark:bg-neutral-950/20 hover:border-brand-300'
              }`}
            >
              <div className="flex items-start gap-2">
                <Bot size={17} className="mt-0.5 shrink-0 text-brand-600" />
                <div className="min-w-0 flex-1">
                  <div className="font-medium truncate">{conv.title || 'AI-чат'}</div>
                  <div className="text-xs text-neutral-500 dark:text-neutral-400 truncate mt-1">{conv.lastMessageText || 'Новый разговор'}</div>
                  <div className="mt-2 flex items-center justify-between gap-2 text-[11px] text-neutral-500">
                    <span>{conv.updatedAtUtc ? new Date(conv.updatedAtUtc).toLocaleDateString() : ''}</span>
                    {conv.lastRunStatus && <Badge variant={getRunBadgeVariant(conv.lastRunStatus)}>{getRunStatusLabel(conv.lastRunStatus)}</Badge>}
                  </div>
                </div>
              </div>
            </button>
          );
        })}
      </div>
    </Card>
  );
}


function PatchSetDrawer({ open, onClose, patchSets, onApplyPatchSet, applyingArtifacts, artifactApplyResults }) {
  const [selectedKey, setSelectedKey] = useState(null);
  const selected = useMemo(() => {
    if (!patchSets.length) return null;
    return patchSets.find((x) => x.key === selectedKey) || patchSets[0];
  }, [patchSets, selectedKey]);

  useEffect(() => {
    if (open && patchSets.length && !patchSets.some((x) => x.key === selectedKey)) setSelectedKey(patchSets[0].key);
  }, [open, patchSets, selectedKey]);

  if (!open) return null;
  const data = selected?.data || {};
  const patches = Array.isArray(data.patches) ? data.patches : [];
  const artifact = selected?.artifact || {};
  const artifactKey = normalizeId(artifact.id || artifact.artifactId || selected?.key);
  const applyingMode = applyingArtifacts?.[artifactKey];
  const applyResult = artifactApplyResults?.[artifactKey];

  return (
    <div className="fixed inset-0 z-50 flex justify-end bg-black/25 backdrop-blur-sm">
      <button type="button" className="absolute inset-0 cursor-default" onClick={onClose} aria-label="Закрыть меню патчей" />
      <aside className="relative h-full w-[min(76rem,98vw)] border-l border-neutral-200/70 dark:border-neutral-800/70 bg-[rgb(var(--card))] shadow-soft flex flex-col">
        <div className="flex items-center justify-between gap-3 border-b border-neutral-200/70 dark:border-neutral-800/70 p-4">
          <div>
            <div className="flex items-center gap-2 font-semibold"><GitCompare size={18} /> Патчи курса</div>
            <div className="text-xs text-neutral-500 dark:text-neutral-400">Диффы перед применением: как в GitHub, но для заданий курса. Сначала проверь изменения, потом применяй.</div>
          </div>
          <div className="flex items-center gap-2">
            {selected && (
              <>
                <button
                  type="button"
                  className="btn-outline !min-w-0"
                  disabled={!artifact?.id || !!applyingMode}
                  onClick={() => onApplyPatchSet?.({ artifact, persistedArtifact: artifact, dryRun: true })}
                  title={artifact?.id ? 'Проверить patch set без сохранения' : 'Патч ещё не сохранён как материал run-а'}
                >
                  {applyingMode === 'check' ? <Loader2 size={16} className="animate-spin" /> : <Activity size={16} />}
                  <span className="hidden sm:inline">Проверить</span>
                </button>
                <button
                  type="button"
                  className="btn-outline !min-w-0 border-emerald-500 text-emerald-700 hover:bg-emerald-50 dark:text-emerald-300 dark:hover:bg-emerald-950/20"
                  disabled={!artifact?.id || !!applyingMode}
                  onClick={() => onApplyPatchSet?.({ artifact, persistedArtifact: artifact, dryRun: false })}
                  title={artifact?.id ? 'Применить patch set к заданиям' : 'Патч ещё не сохранён как материал run-а'}
                >
                  {applyingMode === 'apply' ? <Loader2 size={16} className="animate-spin" /> : <CheckCircle2 size={16} />}
                  <span className="hidden sm:inline">Применить</span>
                </button>
              </>
            )}
            <button type="button" className="btn-outline !min-w-0 !px-3" onClick={onClose} title="Закрыть">
              <X size={16} />
            </button>
          </div>
        </div>

        <div className="min-h-0 flex-1 grid md:grid-cols-[20rem,1fr]">
          <div className="min-h-0 overflow-auto border-r border-neutral-200/70 dark:border-neutral-800/70 p-3 space-y-2">
            {patchSets.length === 0 && <div className="rounded-2xl border border-dashed border-neutral-300 p-4 text-sm text-neutral-500">Патчей пока нет.</div>}
            {patchSets.map((item) => {
              const itemData = item.data || {};
              const active = item.key === selected?.key;
              const count = Array.isArray(itemData.patches) ? itemData.patches.length : 0;
              return (
                <button
                  key={item.key}
                  type="button"
                  onClick={() => setSelectedKey(item.key)}
                  className={`w-full rounded-2xl border p-3 text-left transition ${active ? 'border-brand-400 bg-brand-50/80 dark:bg-brand-950/30' : 'border-neutral-200/70 dark:border-neutral-800/70 hover:border-brand-300'}`}
                >
                  <div className="font-semibold text-sm">{itemData.title || item.artifact?.title || item.artifact?.Title || 'Патч курса'}</div>
                  <div className="mt-1 text-xs text-neutral-500">{count} изменений · {itemData.field || itemData.operation || 'patch set'}</div>
                  {itemData.summary && <div className="mt-2 text-xs text-neutral-600 dark:text-neutral-300 line-clamp-3">{itemData.summary}</div>}
                </button>
              );
            })}
          </div>

          <div className="min-h-0 overflow-auto p-4">
            {!selected ? (
              <div className="text-sm text-neutral-500">Выбери patch set слева.</div>
            ) : (
              <div className="space-y-4">
                <div className="rounded-2xl border border-neutral-200/70 dark:border-neutral-800/70 p-4">
                  <div className="flex flex-wrap items-start justify-between gap-3">
                    <div>
                      <div className="text-lg font-semibold">{data.title || artifact.title || artifact.Title || 'Патч курса'}</div>
                      {data.summary && <div className="mt-1 text-sm text-neutral-600 dark:text-neutral-300">{data.summary}</div>}
                    </div>
                    <div className="flex flex-wrap gap-2 text-xs">
                      <Badge variant="secondary">изменений: {patches.length}</Badge>
                      {data.field && <Badge variant="outline">поле: {data.field}</Badge>}
                    </div>
                  </div>
                  {applyResult && (
                    <div className={`mt-3 rounded-xl px-3 py-2 text-sm ${applyResult.ok ? 'bg-emerald-50 text-emerald-800 dark:bg-emerald-950/30 dark:text-emerald-200' : 'bg-red-50 text-red-800 dark:bg-red-950/30 dark:text-red-200'}`}>
                      {summarizeApplyResult(applyResult) || applyResult.message}
                      {applyResult.message && <div className="mt-1 opacity-80">{applyResult.message}</div>}
                    </div>
                  )}
                </div>

                {patches.map((patch, idx) => <PatchDiffCard key={`${patch.assignmentId || patch.title}-${idx}`} patch={patch} index={idx} />)}
              </div>
            )}
          </div>
        </div>
      </aside>
    </div>
  );
}

function PatchDiffCard({ patch, index }) {
  const changes = Array.isArray(patch?.changes) ? patch.changes : [];
  const diff = patch?.diff || {};
  const hunks = Array.isArray(diff.hunks) && diff.hunks.length
    ? diff.hunks
    : changes.map((change) => ({
        header: `@@ assignment.${change.field || 'field'} @@`,
        lines: [
          { type: 'context', text: `${patch?.title || `Задание ${index + 1}`}` },
          { type: 'removed', text: `"${change.field}": ${formatDiffValue(change.oldValue)}` },
          { type: 'added', text: `"${change.field}": ${formatDiffValue(change.newValue)}` },
        ],
      }));

  return (
    <div className="overflow-hidden rounded-2xl border border-neutral-200/80 dark:border-neutral-800/80 bg-white/70 dark:bg-neutral-950/30">
      <div className="flex flex-wrap items-start justify-between gap-3 border-b border-neutral-200/70 dark:border-neutral-800/70 bg-neutral-50/80 dark:bg-neutral-900/60 px-4 py-3">
        <div className="min-w-0">
          <div className="font-semibold truncate">{patch?.title || `Задание ${index + 1}`}</div>
          <div className="mt-1 text-xs text-neutral-500 truncate">{diff.filePath || patch?.assignmentId || 'assignment.json'}</div>
        </div>
        <Badge variant="outline">{changes.length} изм.</Badge>
      </div>
      {changes.length > 0 && (
        <div className="border-b border-neutral-200/70 dark:border-neutral-800/70 px-4 py-3 text-sm">
          {changes.map((change, i) => (
            <div key={`${change.field}-${i}`} className="mb-2 last:mb-0">
              <span className="font-medium">{change.field}</span>
              {change.reason && <span className="text-neutral-500"> — {change.reason}</span>}
            </div>
          ))}
        </div>
      )}
      <div className="font-mono text-xs">
        {hunks.map((hunk, hunkIndex) => (
          <div key={`hunk-${hunkIndex}`}>
            <div className="bg-brand-50 px-4 py-2 text-brand-800 dark:bg-brand-950/30 dark:text-brand-200">{hunk.header || '@@ patch @@'}</div>
            {(Array.isArray(hunk.lines) ? hunk.lines : []).map((line, lineIndex) => {
              const type = String(line.type || 'context').toLowerCase();
              const isAdd = type === 'added' || type === 'add';
              const isRemove = type === 'removed' || type === 'remove';
              return (
                <div
                  key={`line-${lineIndex}`}
                  className={`grid grid-cols-[2.5rem,1fr] border-t border-neutral-100 dark:border-neutral-900 ${isAdd ? 'bg-emerald-50/90 text-emerald-950 dark:bg-emerald-950/25 dark:text-emerald-100' : isRemove ? 'bg-red-50/90 text-red-950 dark:bg-red-950/25 dark:text-red-100' : 'bg-white/60 dark:bg-neutral-950/20'}`}
                >
                  <div className="select-none border-r border-current/10 px-3 py-1.5 text-right opacity-60">{isAdd ? '+' : isRemove ? '-' : ' '}</div>
                  <pre className="overflow-x-auto px-3 py-1.5 whitespace-pre-wrap">{line.text || ''}</pre>
                </div>
              );
            })}
          </div>
        ))}
      </div>
    </div>
  );
}

function LogDrawer({ open, onClose, conversation, messages, runs, realtimeEvents, onCopyDebugDump, copyingDebugDump }) {
  if (!open) return null;
  const payload = {
    conversation,
    messages,
    runs,
    realtimeEvents,
  };

  return (
    <div className="fixed inset-0 z-50 flex justify-end bg-black/25 backdrop-blur-sm">
      <button type="button" className="absolute inset-0 cursor-default" onClick={onClose} aria-label="Закрыть журнал AI" />
      <aside className="relative h-full w-[min(52rem,96vw)] border-l border-neutral-200/70 dark:border-neutral-800/70 bg-[rgb(var(--card))] shadow-soft flex flex-col">
        <div className="flex items-center justify-between gap-3 border-b border-neutral-200/70 dark:border-neutral-800/70 p-4">
          <div>
            <div className="flex items-center gap-2 font-semibold"><TerminalSquare size={18} /> Журнал AI</div>
            <div className="text-xs text-neutral-500 dark:text-neutral-400">Подробная трасса: сообщения, решения агента, шаги, результаты проверок, артефакты и realtime-события.</div>
          </div>
          <div className="flex items-center gap-2">
            <button type="button" className="btn-outline !min-w-0" onClick={onCopyDebugDump} disabled={!conversation?.id || copyingDebugDump} title="Скопировать полный AI-отчёт">
              {copyingDebugDump ? <Loader2 size={16} className="animate-spin" /> : <ClipboardCopy size={16} />}
              <span className="hidden sm:inline">Скопировать отчёт</span>
            </button>
            <button type="button" className="btn-outline !min-w-0 !px-3" onClick={onClose} title="Закрыть">
              <X size={16} />
            </button>
          </div>
        </div>
        <div className="min-h-0 flex-1 overflow-auto p-4">
          <pre className="text-xs leading-relaxed whitespace-pre-wrap rounded-2xl border border-neutral-200/70 dark:border-neutral-800/70 bg-neutral-950 text-neutral-100 p-4">
            {JSON.stringify(payload, null, 2)}
          </pre>
        </div>
      </aside>
    </div>
  );
}

function EmptyChat({ onTemplate }) {
  const templates = [
    'Проанализируй курс и найди самые резкие скачки сложности.',
    'Найди сложные места в курсе и предложи задания для плавного перехода.',
    'Создай 5 маленьких C++ задач в стиле курса: дружелюбно, пошагово, с публичными тестами.',
    'Сделай задачи проще: одна новая идея на одно задание, без олимпиадного стиля.',
    'Пересчитай рейтинги всех заданий курса по сложности и покажи патчи с диффами.',
  ];

  return (
    <div className="mx-auto max-w-4xl py-12 text-center">
      <div className="mx-auto h-16 w-16 rounded-3xl grid place-items-center bg-brand-600 text-white shadow-soft">
        <Sparkles size={28} />
      </div>
      <h1 className="mt-6 text-3xl font-semibold tracking-tight">Живой AI-ассистент TaskForge</h1>
      <p className="mx-auto mt-3 max-w-2xl text-neutral-600 dark:text-neutral-300">
        Пиши как в обычном чате: можно просить анализ курса, поиск сложных мест, пошаговые задания и черновики в стиле выбранной темы.
      </p>
      <div className="mt-6 grid gap-3 sm:grid-cols-2">
        {templates.map((text) => (
          <button
            key={text}
            type="button"
            onClick={() => onTemplate(text)}
            className="rounded-2xl border border-neutral-200/70 dark:border-neutral-800/70 bg-[rgb(var(--card))]/80 p-4 text-left text-sm hover:border-brand-300 hover:bg-brand-50/60 dark:hover:bg-brand-950/20 transition"
          >
            {text}
          </button>
        ))}
      </div>
    </div>
  );
}


export {
  AI_DIAGNOSTICS_ENABLED,
  nowIso,
  normalizeId,
  safeText,
  getRunStatusLabel,
  getRunBadgeVariant,
  sortByTimeAsc,
  sortRunsDesc,
  mergeById,
  upsertById,
  upsertMessage,
  collectPatchSets,
  getArtifactStableKey,
  getLatestActiveRun,
  getLatestRun,
  MessageBubble,
  ThinkingPanel,
  ConversationList,
  PatchSetDrawer,
  LogDrawer,
  EmptyChat,
};
