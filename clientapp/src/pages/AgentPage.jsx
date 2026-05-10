import React, { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { useSearchParams } from 'react-router-dom';
import {
  Activity,
  Bot,
  BrainCircuit,
  CheckCircle2,
  ClipboardCopy,
  ChevronLeft,
  ChevronRight,
  FileJson,
  Loader2,
  PanelRightOpen,
  Paperclip,
  Plus,
  RefreshCw,
  Send,
  Sparkles,
  Square,
  TerminalSquare,
  UserRound,
  X,
} from 'lucide-react';
import Layout from '../components/Layout';
import AppErrorPanel from '../components/AppErrorPanel';
import { Button, Card, Textarea, Badge } from '../components/ui';
import StatementViewer from '../components/tiptap/StatementViewer';
import { useAuth } from '../auth/AuthContext';
import { useNotify } from '../components/notify/NotifyProvider';
import { handleApiError } from '../utils/handleApiError';
import {
  cancelAgentRun,
  createAgentConversation,
  getAgentConversation,
  listAgentConversations,
  sendAgentMessage,
  uploadAgentAttachment,
  polishAgentGeneratedTask,
  polishAgentGeneratedTasks,
  applyAgentRunArtifact,
  getAgentConversationDebugDump,
} from '../api/agent';
import { joinAgentConversation, leaveAgentConversation } from '../realtime/agentHub';

const RUNNING_STATUSES = new Set(['queued', 'running', 'planning', 'sleeping', 'waiting_approval']);
const FINAL_STATUSES = new Set(['completed', 'completed_with_warnings', 'failed', 'canceled']);

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
  return artifact?.title || artifact?.data?.title || artifact?.type || 'AI artifact';
}

function isApplyableArtifact(artifact) {
  const type = String(artifact?.type || '').toLowerCase();
  if (['course_edit_proposal', 'assignment_update_batch', 'course_style_update'].includes(type)) return true;
  if (type !== 'approval_request') return false;
  const operation = String(artifact?.data?.operation || artifact?.operation || '').toLowerCase();
  return ['apply_course_edit', 'apply_assignment_update_batch'].includes(operation);
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

function ThinkingDots() {
  return (
    <span className="inline-flex items-center gap-1 pl-1 align-middle" aria-hidden="true">
      <span className="h-1.5 w-1.5 rounded-full bg-current opacity-40 animate-pulse" />
      <span className="h-1.5 w-1.5 rounded-full bg-current opacity-60 animate-pulse [animation-delay:120ms]" />
      <span className="h-1.5 w-1.5 rounded-full bg-current opacity-80 animate-pulse [animation-delay:240ms]" />
    </span>
  );
}

function MessageBubble({ message, runs, onPolishTask, onPolishSelectedTasks, onApplyArtifact, selectedDraftTasks, onToggleDraftTask, onSetDraftTasks, polishingTasks, applyingArtifacts, artifactApplyResults, currentCourseId }) {
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

function ArtifactPreview({ artifact, message, runs, artifactIndex, onPolishTask, onPolishSelectedTasks, onApplyArtifact, selectedDraftTasks, onToggleDraftTask, onSetDraftTasks, polishingTasks, applyingArtifacts, artifactApplyResults, currentCourseId }) {
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
                title={hasPersistTarget ? 'Отправить выбранные задания пачкой. Каждое задание станет отдельным AI-run и может обрабатываться параллельно.' : 'Нельзя создать скрытые черновики: AI не определил курс или позицию вставки.'}
              >
                {anyPolishing ? <Loader2 size={14} className="animate-spin" /> : <CheckCircle2 size={14} />}
                {hasPersistTarget ? `в черновики${selectedInArtifact ? `: ${selectedInArtifact}` : ''}` : 'нужен курс'}
              </button>
            </>
          )}
          <span className="text-xs text-neutral-500">{artifact?.type || 'artifact'}</span>
        </div>
      </div>

      {data?.summary && <div className="mt-2 text-sm text-neutral-600 dark:text-neutral-300">{data.summary}</div>}
      {tasks.length > 0 && !hasPersistTarget && (
        <div className="mt-2 rounded-xl border border-amber-300 bg-amber-50/80 px-3 py-2 text-sm text-amber-800 dark:border-amber-800 dark:bg-amber-950/30 dark:text-amber-200">
          AI не указал курс/позицию для сохранения. Такие задачи можно смотреть как blueprint, но кнопки создания скрытых черновиков заблокированы, чтобы они не “терялись”.
        </div>
      )}

      {findings.length > 0 && (
        <div className="mt-3 space-y-2">
          {findings.slice(0, 2).map((f, i) => (
            <div key={`finding-${i}`} className="rounded-xl bg-white/60 dark:bg-neutral-950/30 p-3 text-sm">
              <div className="font-semibold">{f.concept || f.kind || `Дыра ${i + 1}`}</div>
              <div className="mt-1 text-neutral-600 dark:text-neutral-300">{f.reason || f.summary || 'Найдено слабое место в курсе.'}</div>
            </div>
          ))}
        </div>
      )}

      {canApply && (
        <div className="mt-3 rounded-xl border border-emerald-200 bg-emerald-50/70 p-3 text-sm text-emerald-900 dark:border-emerald-900 dark:bg-emerald-950/20 dark:text-emerald-100">
          <div className="flex flex-wrap items-center justify-between gap-2">
            <div>
              <div className="font-semibold">Proposal можно применить к курсу</div>
              <div className="text-xs opacity-80">Сначала можно сделать dry-run: backend проверит права, форму patch и runner-тесты без записи в БД.</div>
            </div>
            <div className="flex flex-wrap items-center gap-2">
              <button
                type="button"
                disabled={!persistedArtifact?.id || !!applyingMode}
                onClick={() => onApplyArtifact?.({ message, artifact, persistedArtifact, artifactIndex, dryRun: true })}
                className="inline-flex items-center gap-1.5 rounded-xl border border-emerald-300 bg-white/80 px-2.5 py-1.5 text-xs font-medium text-emerald-800 hover:bg-emerald-50 disabled:opacity-60 dark:border-emerald-900 dark:bg-neutral-950/50 dark:text-emerald-200"
                title={persistedArtifact?.id ? 'Проверить proposal без записи в БД' : 'Artifact ещё не синхронизирован из run. Обновите чат после завершения AI-run.'}
              >
                {applyingMode === 'dry-run' ? <Loader2 size={14} className="animate-spin" /> : <Activity size={14} />}
                проверить
              </button>
              <button
                type="button"
                disabled={!persistedArtifact?.id || !!applyingMode}
                onClick={() => onApplyArtifact?.({ message, artifact, persistedArtifact, artifactIndex, dryRun: false })}
                className="inline-flex items-center gap-1.5 rounded-xl border border-emerald-600 bg-emerald-600 px-2.5 py-1.5 text-xs font-medium text-white hover:bg-emerald-700 disabled:opacity-60"
                title={persistedArtifact?.id ? 'Применить proposal после backend-валидации' : 'Artifact ещё не синхронизирован из run. Обновите чат после завершения AI-run.'}
              >
                {applyingMode === 'apply' ? <Loader2 size={14} className="animate-spin" /> : <CheckCircle2 size={14} />}
                применить
              </button>
            </div>
          </div>
          {!persistedArtifact?.id && (
            <div className="mt-2 rounded-lg border border-amber-300 bg-amber-50 px-2.5 py-2 text-xs text-amber-800 dark:border-amber-800 dark:bg-amber-950/30 dark:text-amber-200">
              Для применения нужен сохранённый artifact id. Нажмите обновить чат, если AI-run уже завершился.
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
                      title="Выбрать задание для пакетного вылизывания"
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
                      title={hasPersistTarget ? 'Выбрать только это задание: AI вылижет его, прогонит решение на раннерах и создаст скрытый черновик' : 'Нельзя создать скрытый черновик: AI не определил курс или позицию вставки.'}
                    >
                      {polishing ? <Loader2 size={14} className="animate-spin" /> : <CheckCircle2 size={14} />}
                      {polishing ? 'вылизываю' : hasPersistTarget ? 'одно в черновик' : 'нужен курс'}
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
            <div className="text-sm text-neutral-600 dark:text-neutral-300">Подтягивает контекст курса, выбирает сценарий и собирает ответ.</div>
          </div>
        </div>
        <Badge variant="outline">{run.scenarioId || 'assistant_chat_turn'}</Badge>
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
          <div className="text-xs text-neutral-500 dark:text-neutral-400">курс, задания, аудит, лесенки</div>
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
      <button type="button" className="absolute inset-0 cursor-default" onClick={onClose} aria-label="Закрыть AI logs" />
      <aside className="relative h-full w-[min(52rem,96vw)] border-l border-neutral-200/70 dark:border-neutral-800/70 bg-[rgb(var(--card))] shadow-soft flex flex-col">
        <div className="flex items-center justify-between gap-3 border-b border-neutral-200/70 dark:border-neutral-800/70 p-4">
          <div>
            <div className="flex items-center gap-2 font-semibold"><TerminalSquare size={18} /> AI logs</div>
            <div className="text-xs text-neutral-500 dark:text-neutral-400">Всё, что связано с AI: события SignalR, runs, steps, artifacts, raw JSON.</div>
          </div>
          <div className="flex items-center gap-2">
            <button type="button" className="btn-outline !min-w-0" onClick={onCopyDebugDump} disabled={!conversation?.id || copyingDebugDump} title="Скопировать полный AI debug dump">
              {copyingDebugDump ? <Loader2 size={16} className="animate-spin" /> : <ClipboardCopy size={16} />}
              <span className="hidden sm:inline">Скопировать всё</span>
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
    'Найди дырки перед if и сделай лесенку как на скрине 1.',
    'Создай 5 маленьких C++ задач в стиле курса: дружелюбно, пошагово, с публичными тестами.',
    'Сделай задачи проще: одна новая идея на одно задание, без олимпиадного стиля.',
  ];

  return (
    <div className="mx-auto max-w-4xl py-12 text-center">
      <div className="mx-auto h-16 w-16 rounded-3xl grid place-items-center bg-brand-600 text-white shadow-soft">
        <Sparkles size={28} />
      </div>
      <h1 className="mt-6 text-3xl font-semibold tracking-tight">Живой AI-ассистент TaskForge</h1>
      <p className="mx-auto mt-3 max-w-2xl text-neutral-600 dark:text-neutral-300">
        Пиши как в GPT: можно просить анализ курса, поиск дыр, задачки-лесенки, мостики между темами и черновики в стиле курса.
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

export default function AgentPage() {
  const notify = useNotify();
  const { access } = useAuth();
  const [params] = useSearchParams();
  const courseId = params.get('courseId') || undefined;
  const assignmentId = params.get('assignmentId') || undefined;
  const supportTicketId = params.get('supportTicketId') || undefined;

  const [conversations, setConversations] = useState([]);
  const [selectedId, setSelectedId] = useState(null);
  const [conversation, setConversation] = useState(null);
  const [messages, setMessages] = useState([]);
  const [runs, setRuns] = useState([]);
  const [text, setText] = useState('');
  const [loadingList, setLoadingList] = useState(true);
  const [loadingConversation, setLoadingConversation] = useState(false);
  const [sending, setSending] = useState(false);
  const [error, setError] = useState(null);
  const [logsOpen, setLogsOpen] = useState(false);
  const [copyingDebugDump, setCopyingDebugDump] = useState(false);
  const [sidebarOpen, setSidebarOpen] = useState(true);
  const [realtimeEvents, setRealtimeEvents] = useState([]);
  const [polishingTasks, setPolishingTasks] = useState({});
  const [applyingArtifacts, setApplyingArtifacts] = useState({});
  const [artifactApplyResults, setArtifactApplyResults] = useState({});
  const [selectedDraftTasks, setSelectedDraftTasks] = useState({});
  const [pendingFiles, setPendingFiles] = useState([]);
  const [uploadingFiles, setUploadingFiles] = useState(false);

  const bottomRef = useRef(null);
  const fileInputRef = useRef(null);
  const selectedIdRef = useRef(null);
  const sendingRef = useRef(false);

  const activeRun = useMemo(() => getLatestActiveRun(runs), [runs]);
  const latestRun = useMemo(() => getLatestRun(runs), [runs]);
  const selectedDraftCount = useMemo(() => Object.keys(selectedDraftTasks).length, [selectedDraftTasks]);

  const scrollToBottom = useCallback((behavior = 'smooth') => {
    bottomRef.current?.scrollIntoView({ behavior, block: 'end' });
  }, []);

  const rememberEvent = useCallback((evt) => {
    setRealtimeEvents((prev) => {
      const next = [...prev, { at: nowIso(), event: evt }];
      return next.slice(-200);
    });
  }, []);

  const refreshConversations = useCallback(async ({ selectFirst = false, silent = false } = {}) => {
    try {
      if (!silent) setLoadingList(true);
      const data = await listAgentConversations({ courseId, assignmentId, take: 50 });
      const list = Array.isArray(data) ? data : [];
      setConversations(list);
      if (selectFirst && !selectedIdRef.current && list[0]?.id) setSelectedId(list[0].id);
    } catch (err) {
      if (!silent) {
        const parsed = handleApiError(err, notify, 'Не удалось загрузить AI-чаты');
        setError(parsed);
      }
    } finally {
      if (!silent) setLoadingList(false);
    }
  }, [assignmentId, courseId, notify]);

  const loadConversation = useCallback(async (id, { silent = false } = {}) => {
    if (!id) return;
    try {
      if (!silent) setLoadingConversation(true);
      setError(null);
      const data = await getAgentConversation(id);
      setConversation(data?.conversation || null);
      setMessages(sortByTimeAsc(data?.messages || []));
      setRuns(sortRunsDesc(data?.runs || []));
      setTimeout(() => scrollToBottom('auto'), 0);
    } catch (err) {
      const parsed = handleApiError(err, notify, 'Не удалось открыть AI-чат');
      setError(parsed);
    } finally {
      if (!silent) setLoadingConversation(false);
    }
  }, [notify, scrollToBottom]);

  useEffect(() => {
    selectedIdRef.current = selectedId;
  }, [selectedId]);

  useEffect(() => {
    refreshConversations({ selectFirst: true });
  }, [refreshConversations]);

  useEffect(() => {
    if (!selectedId) {
      setConversation(null);
      setMessages([]);
      setRuns([]);
      return undefined;
    }
    loadConversation(selectedId);

    let disposed = false;
    let conn = null;
    let handler = null;

    const setup = async () => {
      try {
        if (!access) return;
        conn = await joinAgentConversation(access, selectedId);
        if (disposed || !conn) return;

        handler = (evt) => {
          if (!evt || normalizeId(evt.conversationId) !== normalizeId(selectedIdRef.current)) return;
          rememberEvent(evt);
          const type = evt.type || evt.Type;
          const payload = evt.payload || evt.Payload || {};

          if (type === 'message.created' && payload.message) {
            setMessages((prev) => sortByTimeAsc(upsertMessage(prev, payload.message)));
            setTimeout(() => scrollToBottom(), 0);
          }

          if (type === 'run.created' && payload.run) {
            setRuns((prev) => sortRunsDesc(upsertById(prev, payload.run)));
          }

          if (type === 'run.updated') {
            setRuns((prev) => {
              const runId = normalizeId(payload.run?.id || payload.runId);
              if (!runId) return prev;
              const nextRun = payload.run || { id: runId, status: payload.status, workerId: payload.workerId };
              return sortRunsDesc(upsertById(prev, nextRun));
            });
          }

          if (type === 'step.created') {
            const step = payload.step || {
              id: `signal-${payload.runId || selectedIdRef.current}-${Date.now()}`,
              runId: payload.runId,
              seq: Date.now(),
              kind: 'lifecycle',
              status: payload.status || 'running',
              title: payload.title || 'AI делает следующий шаг',
              summary: payload.summary,
              createdAtUtc: nowIso(),
              isVisibleToUser: true,
            };
            setRuns((prev) => prev.map((run) => {
              if (normalizeId(run.id) !== normalizeId(step.runId)) return run;
              return { ...run, steps: sortByTimeAsc(upsertById(run.steps || [], step)) };
            }));
          }

          if ((type === 'run.completed' || type === 'run.failed') && payload.runId) {
            setRuns((prev) => prev.map((run) => (
              normalizeId(run.id) === normalizeId(payload.runId)
                ? { ...run, status: payload.status || (type === 'run.failed' ? 'failed' : 'completed'), result: payload.result || run.result, error: payload.error || run.error, finishedAtUtc: nowIso() }
                : run
            )));
            refreshConversations({ silent: true });
            if (selectedIdRef.current) loadConversation(selectedIdRef.current, { silent: true });
          }
        };

        conn.on('AgentEvent', handler);
      } catch {
        // SignalR не обязателен: REST-история всё равно работает.
      }
    };

    setup();

    return () => {
      disposed = true;
      try {
        if (conn && handler) conn.off('AgentEvent', handler);
        leaveAgentConversation(access, selectedId).catch(() => {});
      } catch {}
    };
  }, [access, loadConversation, refreshConversations, rememberEvent, scrollToBottom, selectedId]);

  useEffect(() => {
    scrollToBottom('smooth');
  }, [messages.length, activeRun?.id, scrollToBottom]);

  const createBlankConversation = async () => {
    try {
      setError(null);
      const res = await createAgentConversation({ courseId, assignmentId, supportTicketId, title: 'AI-чат', mode: 'course-assistant' });
      const conv = res?.conversation;
      if (conv?.id) {
        setConversations((prev) => sortRunsDesc(mergeById(prev, [conv])).sort((a, b) => new Date(b.updatedAtUtc || 0) - new Date(a.updatedAtUtc || 0)));
        setSelectedId(conv.id);
      }
    } catch (err) {
      const parsed = handleApiError(err, notify, 'Не удалось создать AI-чат');
      setError(parsed);
    }
  };

  const sendText = async (overrideText) => {
    const value = safeText(overrideText ?? text);
    const filesToUpload = [...pendingFiles];
    if ((!value && filesToUpload.length === 0) || sendingRef.current) return;

    sendingRef.current = true;
    setSending(true);
    setError(null);
    const clientMessageId = `local-${Date.now()}-${Math.random().toString(16).slice(2)}`;

    try {
      let targetId = selectedId;
      if (!targetId) {
        const created = await createAgentConversation({
          courseId,
          assignmentId,
          supportTicketId,
          title: value,
          mode: 'course-assistant',
        });
        const conv = created?.conversation;
        if (!conv?.id) throw new Error('Backend не вернул id AI-чата.');
        targetId = conv.id;
        setSelectedId(targetId);
        setConversation(conv);
        setConversations((prev) => [conv, ...prev.filter((x) => normalizeId(x.id) !== normalizeId(conv.id))]);
      }

      setUploadingFiles(filesToUpload.length > 0);
      const uploadedAttachments = [];
      for (const file of filesToUpload) {
        const uploaded = await uploadAgentAttachment(targetId, file);
        uploadedAttachments.push(uploaded);
      }

      const optimistic = {
        id: clientMessageId,
        conversationId: targetId,
        role: 'user',
        text: value || '[файлы]',
        source: 'optimistic',
        clientMessageId,
        createdAtUtc: nowIso(),
        attachments: uploadedAttachments.length ? uploadedAttachments : filesToUpload.map((f) => ({ fileName: f.name, sizeBytes: f.size, contentType: f.type })),
      };
      setMessages((prev) => sortByTimeAsc(upsertMessage(prev, optimistic)));
      setText('');
      setPendingFiles([]);
      setTimeout(() => scrollToBottom(), 0);

      const res = await sendAgentMessage(targetId, { text: value, clientMessageId, attachments: uploadedAttachments });
      if (res?.message) {
        setMessages((prev) => sortByTimeAsc(upsertMessage(prev, res.message)));
      }
      if (res?.run) setRuns((prev) => sortRunsDesc(upsertById(prev, res.run)));
      refreshConversations({ silent: true });
    } catch (err) {
      const parsed = handleApiError(err, notify, 'Не удалось отправить сообщение AI');
      setError(parsed);
    } finally {
      sendingRef.current = false;
      setSending(false);
      setUploadingFiles(false);
    }
  };

  const stopActiveRun = async () => {
    if (!activeRun?.id) return;
    try {
      await cancelAgentRun(activeRun.id, 'user_requested');
      await loadConversation(selectedId, { silent: true });
      notify.info('AI-run остановлен');
    } catch (err) {
      handleApiError(err, notify, 'Не удалось остановить AI-run');
    }
  };


  const handleApplyArtifact = async ({ message, persistedArtifact, artifactIndex, dryRun }) => {
    const artifactId = persistedArtifact?.id;
    const runId = persistedArtifact?.runId || message?.runId;
    if (!artifactId || !runId) {
      notify.warn('Artifact ещё не готов к применению. Обнови чат после завершения AI-run.');
      return;
    }

    const key = getArtifactStableKey({ persistedArtifact, message, artifactIndex });
    setApplyingArtifacts((prev) => ({ ...prev, [key]: dryRun ? 'dry-run' : 'apply' }));
    try {
      const result = await applyAgentRunArtifact(runId, artifactId, {
        dryRun: !!dryRun,
        note: dryRun ? 'Пользователь запустил dry-run AI proposal из интерфейса.' : 'Пользователь подтвердил применение AI proposal из интерфейса.',
      });
      setArtifactApplyResults((prev) => ({ ...prev, [key]: result }));
      if (result?.ok) {
        notify.success(dryRun ? 'AI proposal проверен без записи' : 'AI proposal применён');
        if (!dryRun && selectedId) await loadConversation(selectedId, { silent: true });
      } else {
        notify.warn(result?.message || 'AI proposal не прошёл проверку');
      }
    } catch (err) {
      const parsed = handleApiError(err, notify, dryRun ? 'Dry-run AI proposal не прошёл' : 'Не удалось применить AI proposal');
      setArtifactApplyResults((prev) => ({ ...prev, [key]: { ok: false, message: parsed?.message || 'Ошибка применения AI proposal' } }));
    } finally {
      setApplyingArtifacts((prev) => {
        const next = { ...prev };
        delete next[key];
        return next;
      });
    }
  };

  const handlePolishGeneratedTask = async ({ task, taskIndex, artifact, message, taskKey }) => {
    if (!selectedId || !task) return;
    setPolishingTasks((prev) => ({ ...prev, [taskKey]: true }));
    try {
      const placement = task.placement || artifact?.data?.placement || {};
      const res = await polishAgentGeneratedTask(selectedId, {
        sourceMessageId: message?.id || null,
        sourceRunId: message?.runId || null,
        sourceArtifactId: artifact?.id || artifact?.artifactId || null,
        taskIndex,
        courseId: conversation?.courseId || courseId || task.selectedCourseId || artifact?.data?.selectedCourseId || null,
        beforeAssignmentId: placement.beforeAssignmentId || task.beforeAssignmentId || null,
        afterAssignmentId: placement.afterAssignmentId || task.afterAssignmentId || null,
        task,
        note: 'Пользователь выбрал это AI-задание галочкой для вылизывания и создания скрытого черновика.',
      });
      if (res?.message) setMessages((prev) => sortByTimeAsc(upsertMessage(prev, res.message)));
      if (res?.run) setRuns((prev) => sortRunsDesc(upsertById(prev, res.run)));
      notify.success('AI начал вылизывать задание и готовить скрытый черновик');
      setTimeout(() => scrollToBottom(), 0);
    } catch (err) {
      handleApiError(err, notify, 'Не удалось отправить задание на вылизывание');
    } finally {
      setPolishingTasks((prev) => ({ ...prev, [taskKey]: false }));
    }
  };

  const buildPolishPayload = ({ task, taskIndex, artifact, message }) => {
    const placement = task?.placement || artifact?.data?.placement || {};
    return {
      sourceMessageId: message?.id || null,
      sourceRunId: message?.runId || null,
      sourceArtifactId: artifact?.id || artifact?.artifactId || null,
      taskIndex,
      courseId: conversation?.courseId || courseId || task?.selectedCourseId || artifact?.data?.selectedCourseId || null,
      beforeAssignmentId: placement.beforeAssignmentId || task?.beforeAssignmentId || null,
      afterAssignmentId: placement.afterAssignmentId || task?.afterAssignmentId || null,
      task,
      note: 'Пользователь выбрал это AI-задание галочкой для вылизывания и создания скрытого черновика.',
    };
  };

  const toggleDraftTask = (item) => {
    if (!item?.taskKey) return;
    setSelectedDraftTasks((prev) => {
      const next = { ...prev };
      if (next[item.taskKey]) delete next[item.taskKey];
      else next[item.taskKey] = item;
      return next;
    });
  };

  const setDraftTaskSelection = (items, selected) => {
    setSelectedDraftTasks((prev) => {
      const next = { ...prev };
      (items || []).forEach((item) => {
        if (!item?.taskKey) return;
        if (selected) next[item.taskKey] = item;
        else delete next[item.taskKey];
      });
      return next;
    });
  };

  const handlePolishSelectedTasks = async (items) => {
    const chosen = (items && items.length ? items : Object.values(selectedDraftTasks)).filter((item) => item?.task);
    if (!selectedId || chosen.length === 0) return;

    const keys = chosen.map((item) => item.taskKey).filter(Boolean);
    setPolishingTasks((prev) => keys.reduce((acc, key) => ({ ...acc, [key]: true }), { ...prev }));
    try {
      const res = await polishAgentGeneratedTasks(selectedId, {
        parallelize: true,
        note: `Пакетное вылизывание ${chosen.length} AI-заданий и создание скрытых черновиков.`,
        tasks: chosen.map(buildPolishPayload),
      });
      if (res?.message) setMessages((prev) => sortByTimeAsc(upsertMessage(prev, res.message)));
      if (Array.isArray(res?.runs)) setRuns((prev) => sortRunsDesc(mergeById(prev, res.runs)));
      setSelectedDraftTasks((prev) => {
        const next = { ...prev };
        keys.forEach((key) => delete next[key]);
        return next;
      });
      notify.success(`AI поставил в очередь ${res?.count || chosen.length} черновиков`);
      setTimeout(() => scrollToBottom(), 0);
    } catch (err) {
      handleApiError(err, notify, 'Не удалось отправить выбранные задания на вылизывание');
    } finally {
      setPolishingTasks((prev) => {
        const next = { ...prev };
        keys.forEach((key) => { next[key] = false; });
        return next;
      });
    }
  };

  const handleFileChange = (e) => {
    const files = Array.from(e.target.files || []);
    if (files.length > 0) {
      setPendingFiles((prev) => [...prev, ...files].slice(0, 10));
    }
    e.target.value = '';
  };

  const removePendingFile = (index) => {
    setPendingFiles((prev) => prev.filter((_, i) => i !== index));
  };

  const copyAiDebugDump = useCallback(async () => {
    if (!selectedId) {
      notify.warn('Сначала открой AI-чат.');
      return;
    }

    setCopyingDebugDump(true);
    try {
      const backendDump = await getAgentConversationDebugDump(selectedId, { format: 'text' });
      const clientDump = {
        generatedAtUtc: nowIso(),
        page: 'AgentPage',
        selectedId,
        conversation,
        messages,
        runs,
        realtimeEvents,
        activeRun,
        latestRun,
        selectedDraftTasks,
        artifactApplyResults,
        applyingArtifacts,
        polishingTasks,
        pendingFiles: pendingFiles.map((file) => ({ name: file.name, size: file.size, type: file.type, lastModified: file.lastModified })),
        context: { courseId, assignmentId, supportTicketId },
      };
      const textDump = [
        backendDump || 'BACKEND DEBUG DUMP EMPTY',
        '',
        'CLIENT SIDE AI DEBUG SNAPSHOT',
        '='.repeat(96),
        JSON.stringify(clientDump, null, 2),
      ].join('\n');

      if (navigator.clipboard?.writeText) {
        await navigator.clipboard.writeText(textDump);
      } else {
        const ta = document.createElement('textarea');
        ta.value = textDump;
        ta.setAttribute('readonly', 'readonly');
        ta.style.position = 'fixed';
        ta.style.opacity = '0';
        document.body.appendChild(ta);
        ta.select();
        document.execCommand('copy');
        document.body.removeChild(ta);
      }

      notify.success(`AI debug dump скопирован (${Math.round(textDump.length / 1024)} KB).`);
    } catch (err) {
      handleApiError(err, notify, 'Не удалось скопировать AI debug dump');
    } finally {
      setCopyingDebugDump(false);
    }
  }, [activeRun, applyingArtifacts, artifactApplyResults, assignmentId, conversation, courseId, latestRun, messages, notify, pendingFiles, polishingTasks, realtimeEvents, runs, selectedDraftTasks, selectedId, supportTicketId]);

  const handleSubmit = (e) => {
    e.preventDefault();
    sendText();
  };

  const contextBits = [
    courseId ? `курс ${String(courseId).slice(0, 8)}` : null,
    assignmentId ? `задание ${String(assignmentId).slice(0, 8)}` : null,
    supportTicketId ? `тикет ${String(supportTicketId).slice(0, 8)}` : null,
  ].filter(Boolean);

  return (
    <Layout fullWidth hideFooter>
      <div className="h-[calc(100dvh-7.5rem)] min-h-[42rem] grid gap-4 xl:grid-cols-[18rem,minmax(0,1fr)]">
        {sidebarOpen && (
          <div className="hidden xl:block min-h-0">
            <ConversationList
              conversations={conversations}
              selectedId={selectedId}
              onSelect={setSelectedId}
              onCreate={createBlankConversation}
              loading={loadingList}
            />
          </div>
        )}

        <section className="min-h-0 rounded-3xl border border-neutral-200/70 dark:border-neutral-800/70 bg-[rgb(var(--card))]/70 shadow-soft overflow-hidden flex flex-col">
          <div className="border-b border-neutral-200/70 dark:border-neutral-800/70 p-3 sm:p-4 flex flex-wrap items-center justify-between gap-3">
            <div className="flex min-w-0 items-center gap-3">
              <button type="button" className="hidden xl:inline-flex btn-outline !min-w-0 !px-3" onClick={() => setSidebarOpen((v) => !v)} title="Свернуть список чатов">
                {sidebarOpen ? <ChevronLeft size={16} /> : <ChevronRight size={16} />}
              </button>
              <div className="h-11 w-11 rounded-2xl grid place-items-center bg-brand-600 text-white shadow-soft">
                <Bot size={20} />
              </div>
              <div className="min-w-0">
                <div className="font-semibold truncate">{conversation?.title || 'AI-ассистент'}</div>
                <div className="text-xs text-neutral-500 dark:text-neutral-400 truncate">
                  {contextBits.length ? `Контекст: ${contextBits.join(' · ')}` : 'Свободный AI-чат: можно работать с любыми доступными курсами, без привязки к URL'}
                </div>
              </div>
            </div>

            <div className="flex flex-wrap items-center gap-2">
              {latestRun?.status && <Badge variant={getRunBadgeVariant(latestRun.status)}>{getRunStatusLabel(latestRun.status)}</Badge>}
              <button type="button" className="btn-outline !min-w-0 !px-3" onClick={createBlankConversation} title="Новый AI-чат">
                <Plus size={16} />
              </button>
              <button type="button" className="btn-outline !min-w-0 !px-3" onClick={() => loadConversation(selectedId, { silent: true })} disabled={!selectedId} title="Обновить">
                <RefreshCw size={16} />
              </button>
              <button type="button" className="btn-outline !min-w-0" onClick={copyAiDebugDump} disabled={!selectedId || copyingDebugDump} title="Скопировать полный AI debug dump">
                {copyingDebugDump ? <Loader2 size={16} className="animate-spin" /> : <ClipboardCopy size={16} />}
                <span className="hidden sm:inline">Copy AI dump</span>
              </button>
              <button type="button" className="btn-outline !min-w-0" onClick={() => setLogsOpen(true)} title="Открыть AI logs">
                <PanelRightOpen size={16} />
                <span className="hidden sm:inline">AI logs</span>
              </button>
            </div>
          </div>

          {error && <div className="p-4"><AppErrorPanel error={error} title="Проблема в AI-чате" /></div>}
          <div className="min-h-0 flex-1 overflow-y-auto p-3 sm:p-5 space-y-5">
            {loadingConversation ? (
              <div className="h-full grid place-items-center text-neutral-500">
                <div className="flex items-center gap-2"><Loader2 size={18} className="animate-spin" /> Загружаю AI-чат…</div>
              </div>
            ) : messages.length === 0 && !activeRun ? (
              <EmptyChat onTemplate={(template) => setText(template)} />
            ) : (
              <>
                {messages.map((message) => (
                  <MessageBubble
                    key={message.id || message.clientMessageId || `${message.role}-${message.createdAtUtc}`}
                    message={message}
                    runs={runs}
                    onPolishTask={handlePolishGeneratedTask}
                    onPolishSelectedTasks={handlePolishSelectedTasks}
                    onApplyArtifact={handleApplyArtifact}
                    selectedDraftTasks={selectedDraftTasks}
                    onToggleDraftTask={toggleDraftTask}
                    onSetDraftTasks={setDraftTaskSelection}
                    polishingTasks={polishingTasks}
                    applyingArtifacts={applyingArtifacts}
                    artifactApplyResults={artifactApplyResults}
                    currentCourseId={conversation?.courseId || courseId || null}
                  />
                ))}
                <ThinkingPanel run={activeRun} />
                <div ref={bottomRef} />
              </>
            )}
          </div>

          <div className="border-t border-neutral-200/70 dark:border-neutral-800/70 bg-[rgb(var(--card))]/95 p-3">
            {selectedDraftCount > 0 && (
              <div className="mx-auto mb-2 flex max-w-5xl flex-wrap items-center justify-between gap-2 rounded-2xl border border-brand-200 bg-brand-50/70 px-3 py-2 text-sm dark:border-brand-900 dark:bg-brand-950/20">
                <div className="font-medium text-brand-800 dark:text-brand-200">Выбрано задач для черновиков: {selectedDraftCount}</div>
                <div className="flex flex-wrap items-center gap-2">
                  <button type="button" className="btn-outline !min-w-0 !px-3 !py-1.5 text-xs" onClick={() => setSelectedDraftTasks({})}>Очистить</button>
                  <Button type="button" className="!min-w-0 !py-1.5 text-xs" onClick={() => handlePolishSelectedTasks()}>
                    Создать черновики выбранных
                  </Button>
                </div>
              </div>
            )}
            {pendingFiles.length > 0 && (
              <div className="mx-auto mb-2 flex max-w-5xl flex-wrap gap-2 rounded-2xl border border-neutral-200/70 bg-white/70 px-3 py-2 text-xs dark:border-neutral-800/70 dark:bg-neutral-950/30">
                {pendingFiles.map((file, idx) => (
                  <span key={`${file.name}-${idx}`} className="inline-flex max-w-[18rem] items-center gap-1.5 rounded-xl bg-neutral-100 px-2 py-1 dark:bg-neutral-900">
                    <Paperclip size={13} />
                    <span className="truncate">{file.name}</span>
                    <button type="button" className="text-neutral-400 hover:text-danger-600" onClick={() => removePendingFile(idx)} aria-label="Убрать файл">×</button>
                  </span>
                ))}
              </div>
            )}
            <form onSubmit={handleSubmit} className="mx-auto flex max-w-5xl items-end gap-2">
              <div className="min-w-0 flex-1 rounded-2xl border border-neutral-200/80 dark:border-neutral-800/80 bg-white/80 dark:bg-neutral-950/40 px-3 py-2 shadow-soft focus-within:border-brand-400">
                <Textarea
                  value={text}
                  onChange={(e) => setText(e.target.value)}
                  onKeyDown={(e) => {
                    if (e.key === 'Enter' && !e.shiftKey) {
                      e.preventDefault();
                      sendText();
                    }
                  }}
                  rows={1}
                  placeholder="Напиши запрос: проанализируй курс, найди дырки, сделай лесенку..."
                  className="!min-h-[2.5rem] !max-h-28 !border-0 !bg-transparent !p-0 !shadow-none resize-none text-sm"
                />
                <div className="mt-1 flex flex-wrap items-center gap-2 text-[11px] text-neutral-500 dark:text-neutral-400">
                  <span>Enter — отправить, Shift+Enter — новая строка</span>
                  <button type="button" className="hover:text-brand-600" onClick={() => setText('Найди дырки в курсе и предложи задачи-мостики.')}>поиск дыр</button>
                  <span>·</span>
                  <button type="button" className="hover:text-brand-600" onClick={() => setText('Сделай задачки-лесенки: дружелюбно, пошагово, одна микроидея на шаг.')}>лесенка</button>
                  <span>·</span>
                  <button type="button" className="hover:text-brand-600" onClick={() => setText('Создай задачи в стиле курса без резкого скачка сложности.')}>в стиле курса</button>
                </div>
              </div>
              <input ref={fileInputRef} type="file" multiple className="hidden" onChange={handleFileChange} />
              <Button type="button" variant="outline" onClick={() => fileInputRef.current?.click()} className="!min-w-0 !px-3 h-12" title="Прикрепить файлы к AI-контексту">
                <Paperclip size={15} />
              </Button>
              {activeRun && (
                <Button type="button" variant="outline" onClick={stopActiveRun} className="!min-w-0 !px-3 h-12">
                  <Square size={15} />
                </Button>
              )}
              <Button type="submit" disabled={sending || uploadingFiles || (!text.trim() && pendingFiles.length === 0)} className="!min-w-0 h-12 px-4">
                {sending || uploadingFiles ? <Loader2 size={17} className="animate-spin" /> : <Send size={17} />}
                <span className="hidden sm:inline">Отправить</span>
              </Button>
            </form>
          </div>
        </section>
      </div>

      <LogDrawer
        open={logsOpen}
        onClose={() => setLogsOpen(false)}
        conversation={conversation}
        messages={messages}
        runs={runs}
        realtimeEvents={realtimeEvents}
        onCopyDebugDump={copyAiDebugDump}
        copyingDebugDump={copyingDebugDump}
      />
    </Layout>
  );
}
