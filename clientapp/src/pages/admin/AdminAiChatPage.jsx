import React, { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react';
import { Link, useSearchParams } from 'react-router-dom';
import Layout from '../../components/Layout';
import { Badge, Button, Card, Field, Select, Textarea } from '../../components/ui';
import {
  confirmAiChatTool,
  createAiChatSession,
  deleteAiChatSession,
  downloadAiChatExport,
  getAiChatSession,
  getAiChatSessions,
  sendAiChatMessage,
  updateAiChatSession,
  uploadAiChatFile,
} from '../../api/aiChat';
import { getAiBatch, getAiDrafts, getAiJob } from '../../api/aiAdmin';
import { getCourses } from '../../api/courses';
import { handleApiError } from '../../utils/handleApiError';
import { useNotify } from '../../components/notify/NotifyProvider';
import {
  Bot,
  CheckCircle2,
  Download,
  Expand,
  LoaderCircle,
  Paperclip,
  Pencil,
  Plus,
  RefreshCcw,
  Search,
  Send,
  ShieldCheck,
  Sparkles,
  Trash2,
  Upload,
  User,
  Wrench,
  X,
  Code2,
  ExternalLink,
  Package,
  FileText,
  Shrink,
} from 'lucide-react';

const SUGGESTIONS = [
  'Сделай batch на 7 задач по массивам для выбранного курса.',
  'Используй прикреплённый файл и создай 3 code-test задания.',
  'Проверь последний draft и запусти self-check.',
  'Опубликуй последний approved draft в выбранный курс.',
  'Запусти AI-review последней попытки пользователя по курсу.',
  'Сделай risk-review пользователя с последними попытками и support-данными.',
];

const EXPERIMENT_SUGGESTIONS = [
  'Покажи 3 возможных ветки маршрутизации и объясни, почему выбираешь одну из них.',
  'Сделай один и тот же шаг в mono и multi логике, а потом сравни путь.',
  'Перед действием перечисли возможные actions, которые ты рассматриваешь в этом ходе.',
  'Запусти эксперимент: несколько вариантов лесенки и короткое сравнение по стилю.',
  'Сделай dev-разбор: какой route, какие actions, какие проверки и почему.',
];

const CONTINUE_MESSAGE = 'Продолжай по памяти этой сессии. Если параметров уже достаточно, не уточняй лишнее и переходи к следующему действию.';
const GENERATE_MESSAGE = 'По памяти этой сессии начни генерацию заданий. Если уместнее batch — создай batch, если лучше одиночная генерация — используй текст или последний файл.';

function formatDate(value) {
  if (!value) return '';
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return '';
  return date.toLocaleString('ru-RU', {
    day: '2-digit',
    month: '2-digit',
    hour: '2-digit',
    minute: '2-digit',
  });
}

function formatBytes(bytes) {
  const value = Number(bytes || 0);
  if (!Number.isFinite(value) || value <= 0) return '';
  if (value < 1024) return `${value} Б`;
  if (value < 1024 * 1024) return `${(value / 1024).toFixed(1)} КБ`;
  return `${(value / (1024 * 1024)).toFixed(1)} МБ`;
}

function uniqueStrings(values = []) {
  return Array.from(new Set((values || []).filter(Boolean)));
}

function truncateText(value, max = 320) {
  const text = String(value || '');
  if (!text) return '';
  return text.length > max ? `${text.slice(0, max)}…` : text;
}

function debugArray(value) {
  return Array.isArray(value) ? value.filter(Boolean) : [];
}

function parseJsonSafely(value) {
  if (!value) return null;
  if (typeof value === 'object') return value;
  try {
    return JSON.parse(String(value));
  } catch {
    return null;
  }
}

function prettyJson(value) {
  if (value == null) return '';
  try {
    return JSON.stringify(value, null, 2);
  } catch {
    return String(value);
  }
}

function collectRouteSnapshots(messages = []) {
  return (messages || [])
    .flatMap((message) => normalizeToolResults(message).map((result) => result?.debugInfo || null))
    .filter(Boolean);
}

function buildSessionExperimentSummary(messages = []) {
  const toolCalls = [];
  const toolResults = [];
  const modes = [];
  const rawModes = [];
  const actions = [];
  const overrides = [];
  const hints = [];

  (messages || []).forEach((message) => {
    normalizeToolCalls(message).forEach((tool) => {
      if (tool?.name) toolCalls.push(tool.name);
    });
    normalizeToolResults(message).forEach((result) => {
      if (result?.status) toolResults.push(result.status);
      if (result?.debugInfo?.actionName) actions.push(result.debugInfo.actionName);
      const routing = result?.debugInfo?.routing || {};
      const resolution = result?.debugInfo?.resolution || {};
      if (routing.mode) modes.push(routing.mode);
      if (routing.rawMode) rawModes.push(routing.rawMode);
      if (resolution.overrideReason && resolution.overrideReason !== 'none') overrides.push(resolution.overrideReason);
      if (resolution.hintMode && resolution.hintMode !== 'none') hints.push(`${resolution.hintMode}${resolution.hintConcept ? `:${resolution.hintConcept}` : ''}`);
    });
  });

  return {
    toolCalls: uniqueStrings(toolCalls),
    toolResults: uniqueStrings(toolResults),
    modes: uniqueStrings(modes),
    rawModes: uniqueStrings(rawModes),
    actions: uniqueStrings(actions),
    overrides: uniqueStrings(overrides),
    hints: uniqueStrings(hints),
  };
}

function buildExperimentTrace(message) {
  const toolCalls = normalizeToolCalls(message);
  const toolResults = normalizeToolResults(message);
  const debugInfo = toolResults.find((item) => item?.debugInfo)?.debugInfo || {};
  const routing = debugInfo.routing || {};
  const resolution = debugInfo.resolution || {};
  const agentLoop = debugInfo.agentLoop || {};
  const parsedArgs = toolCalls.map((tool) => ({
    name: tool?.name || 'action',
    reason: tool?.reason || '',
    args: parseJsonSafely(tool?.argumentsJson),
  }));

  return {
    toolCalls,
    toolResults,
    debugInfo,
    routing,
    resolution,
    agentLoop,
    parsedArgs,
  };
}

function ExperimentChip({ label, tone = 'outline' }) {
  return <Badge variant={tone}>{label}</Badge>;
}

function JsonPreview({ title, value, defaultOpen = false }) {
  if (value == null || value === '') return null;
  const rendered = typeof value === 'string' ? value : prettyJson(value);
  if (!rendered) return null;
  return (
    <details className="rounded-xl border border-neutral-200/70 dark:border-neutral-800 bg-neutral-50/70 dark:bg-neutral-900/50 px-3 py-2 text-xs" open={defaultOpen}>
      <summary className="cursor-pointer font-medium opacity-80">{title}</summary>
      <pre className="mt-2 overflow-x-auto whitespace-pre-wrap break-words opacity-75">{rendered}</pre>
    </details>
  );
}

function ExperimentTurnPanel({ message }) {
  const trace = buildExperimentTrace(message);
  const routing = trace.routing || {};
  const resolution = trace.resolution || {};
  const routeTone = routing.mode === 'pre-anchor' ? 'danger' : routing.mode === 'anchor-onboarding' ? 'success' : 'outline';
  const callNames = trace.toolCalls.map((tool) => tool?.name).filter(Boolean);
  const resultStatuses = trace.toolResults.map((item) => item?.status).filter(Boolean);
  const autoActions = debugArray(trace.agentLoop.autoActions);
  const requestedActions = debugArray(trace.agentLoop.requestedActions);

  if (callNames.length === 0 && resultStatuses.length === 0 && !routing.mode && !trace.agentLoop.traceSummary) return null;

  return (
    <div className="mb-3 rounded-2xl border border-dashed border-[rgba(var(--accent)/0.35)] bg-[rgba(var(--accent)/0.05)] px-3 py-3 text-xs">
      <div className="flex flex-wrap items-center gap-2">
        <ExperimentChip label="dev-проход" />
        {routing.mode ? <ExperimentChip label={`route: ${routing.mode}`} tone={routeTone} /> : null}
        {routing.rawMode ? <ExperimentChip label={`raw: ${routing.rawMode}`} /> : null}
        {resolution.overrideReason && resolution.overrideReason !== 'none' ? <ExperimentChip label={`override: ${resolution.overrideReason}`} tone="success" /> : null}
        {callNames.length > 0 ? <ExperimentChip label={`actions: ${callNames.length}`} /> : null}
        {resultStatuses.length > 0 ? <ExperimentChip label={`results: ${resultStatuses.length}`} /> : null}
      </div>

      <div className="mt-3 flex flex-wrap items-center gap-2 opacity-85">
        <span className="font-medium">Путь:</span>
        <Badge variant="outline">prompt</Badge>
        {requestedActions.map((name, index) => <React.Fragment key={`rq-${name}-${index}`}><span className="opacity-40">→</span><Badge variant="outline">request:{name}</Badge></React.Fragment>)}
        {callNames.map((name, index) => <React.Fragment key={`call-${name}-${index}`}><span className="opacity-40">→</span><Badge variant="outline">exec:{name}</Badge></React.Fragment>)}
        {autoActions.map((name, index) => <React.Fragment key={`auto-${name}-${index}`}><span className="opacity-40">→</span><Badge variant="outline">auto:{name}</Badge></React.Fragment>)}
        {resultStatuses.map((status, index) => <React.Fragment key={`st-${status}-${index}`}><span className="opacity-40">→</span><Badge variant={String(status).toLowerCase() === 'done' ? 'success' : String(status).toLowerCase().includes('fail') ? 'danger' : 'outline'}>{status}</Badge></React.Fragment>)}
      </div>

      {trace.agentLoop.traceSummary ? <div className="mt-2 opacity-75 whitespace-pre-wrap">{trace.agentLoop.traceSummary}</div> : null}

      {(trace.parsedArgs.length > 0 || trace.toolResults.length > 0) ? (
        <div className="mt-3 grid gap-3 lg:grid-cols-2">
          <div className="space-y-2">
            {trace.parsedArgs.map((item, index) => (
              <JsonPreview
                key={`arg-${item.name}-${index}`}
                title={`arguments · ${item.name}${item.reason ? ` — ${truncateText(item.reason, 90)}` : ''}`}
                value={item.args || 'argumentsJson unavailable'}
              />
            ))}
          </div>
          <div className="space-y-2">
            {trace.toolResults.map((item, index) => (
              <JsonPreview
                key={`result-${item?.status || 'tool'}-${index}`}
                title={`toolResult · ${item?.status || 'done'}`}
                value={{
                  summary: item?.summary,
                  navigateTo: item?.navigateTo,
                  jobId: item?.jobId,
                  batchId: item?.batchId,
                  draftId: item?.draftId,
                  assignmentId: item?.assignmentId,
                  debugInfo: item?.debugInfo || null,
                }}
              />
            ))}
          </div>
        </div>
      ) : null}
    </div>
  );
}

function ExperimentWorkbenchPanel({ session, messages, actionMode, instructionStrictness, developerView }) {
  if (!developerView) return null;
  const summary = buildSessionExperimentSummary(messages);
  const routeSnapshots = collectRouteSnapshots(messages);
  const lastDebug = routeSnapshots.length > 0 ? routeSnapshots[routeSnapshots.length - 1] : null;
  const lastRouting = lastDebug?.routing || {};
  const lastResolution = lastDebug?.resolution || {};

  return (
    <div className="rounded-3xl border border-dashed border-[rgba(var(--accent)/0.35)] bg-[rgba(var(--accent)/0.05)] px-4 py-4">
      <div className="flex flex-wrap items-center gap-2">
        <ExperimentChip label="dev sandbox" tone="success" />
        <ExperimentChip label={`режим: ${actionMode === 'multi' ? 'multi' : 'mono'}`} />
        <ExperimentChip label={`strictness: ${instructionStrictness}`} />
        {session?.memory?.messageCount ? <ExperimentChip label={`сообщений: ${session.memory.messageCount}`} /> : null}
        {lastRouting.mode ? <ExperimentChip label={`last route: ${lastRouting.mode}`} tone={lastRouting.mode === 'anchor-onboarding' ? 'success' : lastRouting.mode === 'pre-anchor' ? 'danger' : 'outline'} /> : null}
        {lastResolution.overrideReason && lastResolution.overrideReason !== 'none' ? <ExperimentChip label={`override: ${lastResolution.overrideReason}`} tone="success" /> : null}
      </div>
      <div className="mt-3 grid gap-3 xl:grid-cols-4 md:grid-cols-2">
        <div className="rounded-2xl bg-white/70 dark:bg-neutral-950/40 px-3 py-3">
          <div className="text-[11px] uppercase tracking-[0.16em] opacity-50">ветки / routes</div>
          <div className="mt-2 flex flex-wrap gap-2">
            {summary.modes.length === 0 ? <span className="opacity-55 text-xs">пока пусто</span> : summary.modes.map((item) => <Badge key={item} variant={item === 'anchor-onboarding' ? 'success' : item === 'pre-anchor' ? 'danger' : 'outline'}>{item}</Badge>)}
          </div>
          {summary.rawModes.length > 0 ? <div className="mt-2 text-xs opacity-65">raw: {summary.rawModes.join(', ')}</div> : null}
        </div>
        <div className="rounded-2xl bg-white/70 dark:bg-neutral-950/40 px-3 py-3">
          <div className="text-[11px] uppercase tracking-[0.16em] opacity-50">actions</div>
          <div className="mt-2 flex flex-wrap gap-2">
            {summary.toolCalls.length === 0 ? <span className="opacity-55 text-xs">ещё не ходили по tool-calls</span> : summary.toolCalls.map((item) => <Badge key={item} variant="outline">{item}</Badge>)}
          </div>
          {summary.actions.length > 0 ? <div className="mt-2 text-xs opacity-65">debug actions: {summary.actions.join(' → ')}</div> : null}
        </div>
        <div className="rounded-2xl bg-white/70 dark:bg-neutral-950/40 px-3 py-3">
          <div className="text-[11px] uppercase tracking-[0.16em] opacity-50">resolution</div>
          <div className="mt-2 flex flex-wrap gap-2">
            {summary.overrides.length === 0 ? <Badge variant="outline">без override</Badge> : summary.overrides.map((item) => <Badge key={item} variant="success">{item}</Badge>)}
            {summary.hints.length > 0 ? summary.hints.map((item) => <Badge key={item} variant="outline">{item}</Badge>) : null}
          </div>
        </div>
        <div className="rounded-2xl bg-white/70 dark:bg-neutral-950/40 px-3 py-3">
          <div className="text-[11px] uppercase tracking-[0.16em] opacity-50">статусы tool-result</div>
          <div className="mt-2 flex flex-wrap gap-2">
            {summary.toolResults.length === 0 ? <span className="opacity-55 text-xs">нет результатов</span> : summary.toolResults.map((item) => <Badge key={item} variant={String(item).toLowerCase() === 'done' ? 'success' : String(item).toLowerCase().includes('fail') ? 'danger' : 'outline'}>{item}</Badge>)}
          </div>
        </div>
      </div>
      <div className="mt-3 text-xs opacity-70 leading-5">
        Экспериментальный вид не меняет логику worker-а. Он просто показывает маршрут хода, tool-calls, tool-results, routing и override-решения так, чтобы чат ощущался как девелоперская лаборатория, а не как продуктовый помощник.
      </div>
    </div>
  );
}

function ToolDebugPanel({ debugInfo }) {
  if (!debugInfo || typeof debugInfo !== 'object') return null;
  const routing = debugInfo.routing || {};
  const memory = debugInfo.memory || {};
  const proposals = debugInfo.proposals || {};
  const validationIssues = debugArray(debugInfo.validationIssues);
  const latestMarkers = debugArray(routing.latestExplicitMarkers);
  const haystackMarkers = debugArray(routing.haystackExplicitMarkers);
  const preMarkers = debugArray(routing.preAnchorMarkers);
  const recentGoals = debugArray(memory.recentGoalsTail);
  const proposalTitles = debugArray(proposals.titles);
  const explicitTitles = debugArray(proposals.explicitAnchorTitles);
  const agentLoop = debugInfo.agentLoop || {};
  const requestedActions = debugArray(agentLoop.requestedActions);
  const autoActions = debugArray(agentLoop.autoActions);

  return (
    <details className="mt-3 rounded-2xl border border-dashed border-[rgba(var(--accent)/0.35)] px-3 py-3 text-xs bg-white/40 dark:bg-neutral-950/30" open>
      <summary className="cursor-pointer font-medium">Диагностика маршрутизации</summary>
      <div className="mt-3 space-y-3">
        <div className="flex flex-wrap gap-2">
          {debugInfo.actionName ? <Badge variant="outline">action: {debugInfo.actionName}</Badge> : null}
          {debugInfo.anchorConcept ? <Badge variant="outline">anchor: {debugInfo.anchorConcept}</Badge> : null}
          {routing.mode ? <Badge variant={routing.mode === 'pre-anchor' ? 'danger' : routing.mode === 'anchor-onboarding' ? 'success' : 'outline'}>mode: {routing.mode}</Badge> : null}
          {typeof routing.explicitStartLatest === 'boolean' ? <Badge variant={routing.explicitStartLatest ? 'success' : 'outline'}>latest explicit: {routing.explicitStartLatest ? 'yes' : 'no'}</Badge> : null}
          {typeof routing.explicitStartHaystack === 'boolean' ? <Badge variant={routing.explicitStartHaystack ? 'success' : 'outline'}>haystack explicit: {routing.explicitStartHaystack ? 'yes' : 'no'}</Badge> : null}
          {typeof routing.preAnchorDetected === 'boolean' ? <Badge variant={routing.preAnchorDetected ? 'danger' : 'outline'}>pre-anchor: {routing.preAnchorDetected ? 'yes' : 'no'}</Badge> : null}
          {typeof routing.allowsExplicitOnboarding === 'boolean' ? <Badge variant={routing.allowsExplicitOnboarding ? 'success' : 'outline'}>onboarding: {routing.allowsExplicitOnboarding ? 'yes' : 'no'}</Badge> : null}
        </div>

        {routing.preAnchorReason ? <div className="opacity-80">Причина pre-anchor: <span className="font-medium">{routing.preAnchorReason}</span></div> : null}
        {debugInfo.resolution ? (
          <div className="rounded-xl bg-neutral-50 dark:bg-neutral-900/60 px-3 py-2">
            <div className="font-medium opacity-85">Resolution</div>
            <div className="mt-1 flex flex-wrap gap-2 opacity-80">
              {debugInfo.resolution.hintMode ? <Badge variant="outline">hint: {debugInfo.resolution.hintMode}</Badge> : null}
              {debugInfo.resolution.hintConcept ? <Badge variant="outline">concept: {debugInfo.resolution.hintConcept}</Badge> : null}
              {debugInfo.resolution.overrideReason && debugInfo.resolution.overrideReason !== 'none' ? <Badge variant="success">override: {debugInfo.resolution.overrideReason}</Badge> : null}
              {typeof debugInfo.resolution.latestInstructionLooksLikeAssent === 'boolean' ? <Badge variant={debugInfo.resolution.latestInstructionLooksLikeAssent ? 'success' : 'outline'}>assent: {debugInfo.resolution.latestInstructionLooksLikeAssent ? 'yes' : 'no'}</Badge> : null}
              {typeof debugInfo.resolution.inferredProposalOnboarding === 'boolean' ? <Badge variant={debugInfo.resolution.inferredProposalOnboarding ? 'success' : 'outline'}>proposal evidence: {debugInfo.resolution.inferredProposalOnboarding ? 'yes' : 'no'}</Badge> : null}
            </div>
          </div>
        ) : null}
        {validationIssues.length > 0 ? (
          <div>
            <div className="font-medium opacity-85">Почему validation упал</div>
            <ul className="mt-1 list-disc pl-5 space-y-1 opacity-80">
              {validationIssues.map((item, index) => <li key={`issue-${index}`}>{item}</li>)}
            </ul>
          </div>
        ) : null}

        <div className="grid gap-3 md:grid-cols-2">
          <div className="rounded-xl bg-neutral-50 dark:bg-neutral-900/60 px-3 py-2">
            <div className="font-medium opacity-85">Latest explicit instruction</div>
            <div className="mt-1 whitespace-pre-wrap opacity-75">{memory.latestExplicitInstruction || '—'}</div>
          </div>
          <div className="rounded-xl bg-neutral-50 dark:bg-neutral-900/60 px-3 py-2">
            <div className="font-medium opacity-85">Recent goals tail</div>
            <div className="mt-1 space-y-1 opacity-75">
              {recentGoals.length === 0 ? <div>—</div> : recentGoals.map((item, index) => <div key={`goal-${index}`}>• {truncateText(item, 220)}</div>)}
            </div>
          </div>
        </div>

        {memory.instructionHaystack ? (
          <div className="rounded-xl bg-neutral-50 dark:bg-neutral-900/60 px-3 py-2">
            <div className="font-medium opacity-85">Haystack</div>
            <div className="mt-1 whitespace-pre-wrap opacity-75">{memory.instructionHaystack}</div>
          </div>
        ) : null}

        <div className="grid gap-3 md:grid-cols-3">
          <div className="rounded-xl bg-neutral-50 dark:bg-neutral-900/60 px-3 py-2">
            <div className="font-medium opacity-85">Latest explicit markers</div>
            <div className="mt-1 opacity-75">{latestMarkers.length === 0 ? '—' : latestMarkers.join(', ')}</div>
          </div>
          <div className="rounded-xl bg-neutral-50 dark:bg-neutral-900/60 px-3 py-2">
            <div className="font-medium opacity-85">Haystack explicit markers</div>
            <div className="mt-1 opacity-75">{haystackMarkers.length === 0 ? '—' : haystackMarkers.join(', ')}</div>
          </div>
          <div className="rounded-xl bg-neutral-50 dark:bg-neutral-900/60 px-3 py-2">
            <div className="font-medium opacity-85">Pre-anchor markers</div>
            <div className="mt-1 opacity-75">{preMarkers.length === 0 ? '—' : preMarkers.join(', ')}</div>
          </div>
        </div>

        {(proposalTitles.length > 0 || explicitTitles.length > 0) ? (
          <div className="grid gap-3 md:grid-cols-2">
            <div className="rounded-xl bg-neutral-50 dark:bg-neutral-900/60 px-3 py-2">
              <div className="font-medium opacity-85">Proposal titles</div>
              <div className="mt-1 space-y-1 opacity-75">
                {proposalTitles.length === 0 ? <div>—</div> : proposalTitles.map((item, index) => <div key={`pt-${index}`}>• {item}</div>)}
              </div>
            </div>
            <div className="rounded-xl bg-neutral-50 dark:bg-neutral-900/60 px-3 py-2">
              <div className="font-medium opacity-85">Explicit anchor titles</div>
              <div className="mt-1 space-y-1 opacity-75">
                {explicitTitles.length === 0 ? <div>—</div> : explicitTitles.map((item, index) => <div key={`et-${index}`}>• {item}</div>)}
              </div>
            </div>
          </div>
        ) : null}

        {(requestedActions.length > 0 || autoActions.length > 0 || agentLoop.traceSummary) ? (
          <div className="rounded-xl bg-neutral-50 dark:bg-neutral-900/60 px-3 py-2">
            <div className="font-medium opacity-85">Agent loop</div>
            {agentLoop.traceSummary ? <div className="mt-1 opacity-75">{agentLoop.traceSummary}</div> : null}
            {requestedActions.length > 0 ? <div className="mt-1 opacity-75">requested: {requestedActions.join(' → ')}</div> : null}
            {autoActions.length > 0 ? <div className="mt-1 opacity-75">auto: {autoActions.join(' → ')}</div> : null}
          </div>
        ) : null}
      </div>
    </details>
  );
}

function normalizeToolCalls(message) {

  if (Array.isArray(message?.toolCalls) && message.toolCalls.length > 0) return message.toolCalls;
  return message?.toolCall ? [message.toolCall] : [];
}

function normalizeToolResults(message) {
  if (Array.isArray(message?.toolResults) && message.toolResults.length > 0) return message.toolResults;
  return message?.toolResult ? [message.toolResult] : [];
}

function getAssistantQuickReplies(message) {
  const text = String(message?.content || '').toLowerCase();
  if (!text) return [];
  if (text.includes('сколько задач') || text.includes('сколько заданий') || text.includes('не указал количество')) {
    return [3, 5, 7, 10, 12].map((count) => ({
      key: `count-${count}`,
      label: `${count} задач`,
      message: `Запускай ${count} задач по этому плану.`,
    }));
  }
  if (text.includes('курс') && text.includes('не выбран')) {
    return [{ key: 'pick-course', label: 'Сначала выберу курс', message: '' }];
  }
  return [];
}

function triggerDownload(blob, fileName) {
  const url = window.URL.createObjectURL(blob);
  const link = document.createElement('a');
  link.href = url;
  link.download = fileName || 'ai-chat-history.md';
  document.body.appendChild(link);
  link.click();
  link.remove();
  window.URL.revokeObjectURL(url);
}

function FileChip({ file, onRemove, removable = false }) {
  const hasText = Boolean(file?.textExcerpt);
  const size = formatBytes(file?.size || file?.sizeBytes);
  return (
    <div className="inline-flex items-center gap-2 rounded-2xl border border-neutral-200/70 dark:border-neutral-800 px-3 py-2 text-xs bg-[rgb(var(--card))]">
      <Paperclip size={14} />
      <div className="flex flex-col">
        <span className="font-medium">{file?.originalName || file?.name || file?.fileKey}</span>
        <span className="opacity-60">{hasText ? 'текст извлечён' : (file?.mimeType || 'вложение')}{size ? ` · ${size}` : ''}</span>
      </div>
      {removable ? (
        <button type="button" className="opacity-60 hover:opacity-100" onClick={onRemove}>
          <X size={14} />
        </button>
      ) : null}
    </div>
  );
}

function renderTestPreviewList(title, tests) {
  if (!Array.isArray(tests) || tests.length === 0) return null;
  return (
    <div className="mt-3">
      <div className="text-xs uppercase tracking-[0.16em] opacity-55">{title}</div>
      <div className="mt-2 space-y-2">
        {tests.slice(0, 6).map((test, index) => (
          <div key={`${title}-${index}`} className="rounded-xl bg-neutral-50 dark:bg-neutral-900/60 px-3 py-2 text-xs font-mono">
            <div className="opacity-60">input</div>
            <div className="whitespace-pre-wrap break-words">{String(test?.input || '∅')}</div>
            <div className="mt-2 opacity-60">expected</div>
            <div className="whitespace-pre-wrap break-words">{String(test?.expectedOutput || '')}</div>
          </div>
        ))}
      </div>
    </div>
  );
}

function MemoryPanel({ memory, courseTitle }) {
  const facts = uniqueStrings(memory?.facts);
  const goals = uniqueStrings(memory?.recentGoals);
  const files = uniqueStrings(memory?.recentFiles);
  const actions = uniqueStrings(memory?.recentActions);
  const agentState = memory?.agentState || {};
  const placementCandidates = Array.isArray(agentState?.placementCandidates) ? agentState.placementCandidates : [];
  const subtasks = Array.isArray(agentState?.subtasks) ? agentState.subtasks : [];
  const planSteps = Array.isArray(agentState?.planSteps) ? agentState.planSteps : [];
  const evidenceLedger = uniqueStrings(agentState?.evidenceLedger);
  const openQuestions = uniqueStrings(agentState?.openQuestions);
  const riskFlags = uniqueStrings(agentState?.riskFlags);
  const completionCriteria = uniqueStrings(agentState?.completionCriteria);
  const decisionCandidates = Array.isArray(agentState?.decisionCandidates) ? agentState.decisionCandidates : [];
  const draftBlueprint = memory?.currentDraftBlueprint || null;
  const draftProposals = Array.isArray(draftBlueprint?.proposals) ? draftBlueprint.proposals : [];
  const hasMemory = Boolean(memory?.messageCount || facts.length || goals.length || files.length || actions.length || memory?.summary || agentState?.currentStage || agentState?.userIntentSummary || agentState?.objectiveSummary || subtasks.length || planSteps.length || draftProposals.length || evidenceLedger.length || openQuestions.length || riskFlags.length || decisionCandidates.length);

  return (
    <div className="rounded-3xl border border-neutral-200/70 dark:border-neutral-800 bg-[rgb(var(--card))] p-4">
      <div className="flex flex-wrap items-center gap-2">
        <Badge variant="outline">Память сессии</Badge>
        {memory?.messageCount ? <Badge variant="success">{memory.messageCount} сообщений</Badge> : null}
        {courseTitle ? <Badge variant="outline">курс: {courseTitle}</Badge> : null}
        {agentState?.workflowKind ? <Badge variant="outline">режим: {agentState.workflowKind}</Badge> : null}
        {agentState?.currentStage ? <Badge variant="outline">stage: {agentState.currentStage}</Badge> : null}
        {agentState?.readyForGeneration ? <Badge variant="success">готов к generation</Badge> : null}
      </div>

      {!hasMemory ? (
        <div className="mt-3 text-sm opacity-70">
          Пока это новая сессия. После первых сообщений AI начнёт собирать долговременную память: цели, файлы, прошлые действия и результаты.
        </div>
      ) : (
        <>
          <div className="mt-3 text-sm leading-6 opacity-90 whitespace-pre-wrap">
            {memory?.summary || 'AI уже держит в памяти ход разговора, прошлые действия и контекст файлов.'}
          </div>

          {draftProposals.length > 0 ? (
            <div className="mt-4 rounded-2xl border border-neutral-200/70 dark:border-neutral-800 p-3">
              <div className="text-xs uppercase tracking-[0.18em] opacity-50">Черновые условия из чата</div>
              {draftBlueprint?.summary ? <div className="mt-2 text-sm opacity-85 whitespace-pre-wrap">{draftBlueprint.summary}</div> : null}
              <div className="mt-3 space-y-3">
                {draftProposals.slice(0, 4).map((item, index) => {
                  const isFriendlyWalkthrough = String(item?.fullCondition || item?.conditionPreview || '').includes('Следуй шагам:');
                  return (
                  <div key={item?.id || index} className="rounded-2xl border border-neutral-200/70 dark:border-neutral-800 p-3">
                    <div className="flex flex-wrap items-center gap-2">
                      <Badge variant="outline">#{index + 1}</Badge>
                      <span className="font-medium">{item?.title || `Вариант ${index + 1}`}</span>
                      {!isFriendlyWalkthrough && item?.assignmentType ? <Badge variant="outline">{item.assignmentType}</Badge> : null}
                      {!isFriendlyWalkthrough && item?.difficulty ? <Badge variant="outline">сложность {item.difficulty}/5</Badge> : null}
                      {!isFriendlyWalkthrough && item?.status ? <Badge variant={item.status === 'queued' ? 'success' : 'outline'}>{item.status}</Badge> : null}
                    </div>
                    {item?.goal ? <div className="mt-2 text-sm opacity-80">Цель: {item.goal}</div> : null}
                    {item?.placementAfterTitle ? <div className="mt-2 text-xs opacity-70">После: {item.placementAfterTitle}</div> : null}
                    {item?.placementReason ? <div className="mt-1 text-xs opacity-65">Почему сюда: {item.placementReason}</div> : null}
                    {(item?.fullCondition || item?.conditionPreview) ? <div className="mt-2 text-sm whitespace-pre-wrap opacity-90">{item.fullCondition || item.conditionPreview}</div> : null}
                    {Array.isArray(item?.mustKeep) && item.mustKeep.length > 0 ? <div className="mt-3 text-xs opacity-75">Сохранить: {item.mustKeep.join(', ')}</div> : null}
                    {Array.isArray(item?.avoid) && item.avoid.length > 0 ? <div className="mt-1 text-xs opacity-70">Не добавлять: {item.avoid.join(', ')}</div> : null}
                    {renderTestPreviewList('Публичные тесты', item?.publicTests)}
                    {renderTestPreviewList('Скрытые тесты', item?.hiddenTests)}
                  </div>
                  );
                })}
              </div>
              <div className="mt-3 text-xs opacity-65">Сейчас можно продолжать обсуждение в чате, просить правки или командовать финализацию в черновик.</div>
            </div>
          ) : null}

          {(agentState?.userIntentSummary || agentState?.nextSuggestedAction || placementCandidates.length > 0) ? (
            <div className="mt-4 rounded-2xl border border-neutral-200/70 dark:border-neutral-800 p-3">
              <div className="text-xs uppercase tracking-[0.18em] opacity-50">Каноническое состояние агента</div>
              {agentState?.userIntentSummary ? <div className="mt-2 text-sm opacity-90">Запрос: {agentState.userIntentSummary}</div> : null}
              {agentState?.objectiveSummary ? <div className="mt-2 text-sm opacity-80">Стратегия: {agentState.objectiveSummary}</div> : null}
              <div className="mt-2 flex flex-wrap gap-2">
                {agentState?.objectiveKind ? <Badge variant="outline">objective: {agentState.objectiveKind}</Badge> : null}
                {agentState?.learnerAudience ? <Badge variant="outline">аудитория: {agentState.learnerAudience}</Badge> : null}
                {agentState?.pedagogyMode ? <Badge variant="outline">педагогика: {agentState.pedagogyMode}</Badge> : null}
                {typeof agentState?.confidencePercent === 'number' ? <Badge variant={agentState.confidencePercent >= 70 ? 'success' : 'outline'}>уверенность: {agentState.confidencePercent}%</Badge> : null}
                {agentState?.autonomyMode ? <Badge variant="outline">автономность: {agentState.autonomyMode}</Badge> : null}
                {agentState?.needsClarification ? <Badge variant="outline">нужно уточнение</Badge> : null}
                {(agentState?.activeConstraints || []).map((item) => <Badge key={item} variant="outline">{item}</Badge>)}
              </div>
              {agentState?.stageSummary ? <div className="mt-3 text-sm opacity-80">Текущий этап: {agentState.stageSummary}</div> : null}
              {agentState?.confidenceReason ? <div className="mt-2 text-sm opacity-75">Почему такая уверенность: {agentState.confidenceReason}</div> : null}
              {agentState?.selfCritique ? <div className="mt-2 text-sm opacity-75">Самокритика агента: {agentState.selfCritique}</div> : null}
              {agentState?.blockerSummary ? <div className="mt-2 text-sm opacity-80">Главный блокер: {agentState.blockerSummary}</div> : null}
              {agentState?.nextSuggestedAction ? <div className="mt-2 text-sm opacity-75">Следующий шаг: {agentState.nextSuggestedAction}</div> : null}
              {subtasks.length > 0 ? (
                <div className="mt-3 grid gap-2 md:grid-cols-2">
                  {subtasks.slice(0, 6).map((item, index) => (
                    <div key={`${item?.key || 'subtask'}-${index}`} className="rounded-2xl border border-neutral-200/70 dark:border-neutral-800 p-3">
                      <div className="flex flex-wrap items-center gap-2">
                        <span className="font-medium text-sm">{item?.title || `Шаг ${index + 1}`}</span>
                        <Badge variant={item?.status === 'done' ? 'success' : 'outline'}>{item?.status || 'pending'}</Badge>
                      </div>
                      {item?.summary ? <div className="mt-2 text-xs opacity-70 whitespace-pre-wrap">{item.summary}</div> : null}
                    </div>
                  ))}
                </div>
              ) : null}
              {planSteps.length > 0 ? (
                <div className="mt-3 rounded-2xl border border-neutral-200/70 dark:border-neutral-800 p-3">
                  <div className="text-xs uppercase tracking-[0.16em] opacity-50">Мини-план агента</div>
                  <div className="mt-2 grid gap-2 md:grid-cols-2">
                    {planSteps.slice(0, 8).map((item, index) => (
                      <div key={`${item?.key || 'plan'}-${index}`} className="rounded-2xl border border-neutral-200/70 dark:border-neutral-800 p-3">
                        <div className="flex flex-wrap items-center gap-2">
                          <span className="font-medium text-sm">{item?.title || `План ${index + 1}`}</span>
                          {item?.status ? <Badge variant={item.status === 'done' ? 'success' : 'outline'}>{item.status}</Badge> : null}
                          {item?.recommendedAction ? <Badge variant="outline">{item.recommendedAction}</Badge> : null}
                        </div>
                        {item?.summary ? <div className="mt-2 text-xs opacity-75 whitespace-pre-wrap">{item.summary}</div> : null}
                        {item?.successSignal ? <div className="mt-2 text-xs opacity-65">Сигнал успеха: {item.successSignal}</div> : null}
                        {item?.blockedBy ? <div className="mt-1 text-xs opacity-65">Блокер: {item.blockedBy}</div> : null}
                      </div>
                    ))}
                  </div>
                </div>
              ) : null}
              {evidenceLedger.length > 0 ? (
                <div className="mt-3 rounded-2xl border border-neutral-200/70 dark:border-neutral-800 p-3">
                  <div className="text-xs uppercase tracking-[0.16em] opacity-50">Опорные доказательства</div>
                  <div className="mt-2 grid gap-2 md:grid-cols-2">
                    {evidenceLedger.slice(0, 6).map((item, index) => (
                      <div key={`evidence-${index}`} className="rounded-xl bg-neutral-50 dark:bg-neutral-900/60 px-3 py-2 text-xs opacity-85">{item}</div>
                    ))}
                  </div>
                </div>
              ) : null}
              {(openQuestions.length > 0 || riskFlags.length > 0 || completionCriteria.length > 0) ? (
                <div className="mt-3 grid gap-3 lg:grid-cols-3">
                  <div className="rounded-2xl border border-neutral-200/70 dark:border-neutral-800 p-3">
                    <div className="text-xs uppercase tracking-[0.16em] opacity-50">Незакрытые вопросы</div>
                    <div className="mt-2 space-y-2 text-xs opacity-80">
                      {openQuestions.length === 0 ? <div className="opacity-50">Пока нет</div> : openQuestions.slice(0, 5).map((item, index) => <div key={`open-${index}`}>{item}</div>)}
                    </div>
                  </div>
                  <div className="rounded-2xl border border-neutral-200/70 dark:border-neutral-800 p-3">
                    <div className="text-xs uppercase tracking-[0.16em] opacity-50">Риски</div>
                    <div className="mt-2 space-y-2 text-xs opacity-80">
                      {riskFlags.length === 0 ? <div className="opacity-50">Пока нет</div> : riskFlags.slice(0, 5).map((item, index) => <div key={`risk-${index}`}>{item}</div>)}
                    </div>
                  </div>
                  <div className="rounded-2xl border border-neutral-200/70 dark:border-neutral-800 p-3">
                    <div className="text-xs uppercase tracking-[0.16em] opacity-50">Критерии завершения</div>
                    <div className="mt-2 space-y-2 text-xs opacity-80">
                      {completionCriteria.length === 0 ? <div className="opacity-50">Пока нет</div> : completionCriteria.slice(0, 5).map((item, index) => <div key={`done-${index}`}>{item}</div>)}
                    </div>
                  </div>
                </div>
              ) : null}
              {decisionCandidates.length > 0 ? (
                <div className="mt-3 rounded-2xl border border-neutral-200/70 dark:border-neutral-800 p-3">
                  <div className="text-xs uppercase tracking-[0.16em] opacity-50">Кандидаты на следующий шаг</div>
                  <div className="mt-2 grid gap-2 md:grid-cols-2">
                    {decisionCandidates.slice(0, 4).map((item, index) => (
                      <div key={`candidate-${index}`} className="rounded-2xl border border-neutral-200/70 dark:border-neutral-800 p-3">
                        <div className="flex flex-wrap items-center gap-2">
                          <span className="font-medium text-sm">{item?.name || `шаг ${index + 1}`}</span>
                          {item?.status ? <Badge variant={item.status === 'preferred' ? 'success' : 'outline'}>{item.status}</Badge> : null}
                        </div>
                        {item?.why ? <div className="mt-2 text-xs opacity-75 whitespace-pre-wrap">{item.why}</div> : null}
                      </div>
                    ))}
                  </div>
                </div>
              ) : null}
              {placementCandidates.length > 0 ? (
                <div className="mt-3 flex flex-wrap gap-2">
                  {placementCandidates.slice(0, 4).map((item, index) => (
                    <Badge key={`${item?.afterAssignmentId || item?.afterAssignmentTitle || item?.concept || 'placement'}-${index}`} variant="outline">
                      {(item?.afterAssignmentTitle || item?.concept || 'точка вставки')}
                    </Badge>
                  ))}
                </div>
              ) : null}
            </div>
          ) : null}

          <div className="mt-4 grid gap-3 md:grid-cols-2 xl:grid-cols-4">
            <div className="rounded-2xl border border-neutral-200/70 dark:border-neutral-800 p-3">
              <div className="text-xs uppercase tracking-[0.18em] opacity-50">Факты</div>
              <div className="mt-2 flex flex-wrap gap-2">
                {facts.length === 0 ? <span className="text-xs opacity-50">Пока пусто</span> : facts.map((item) => <Badge key={item} variant="outline">{item}</Badge>)}
              </div>
            </div>
            <div className="rounded-2xl border border-neutral-200/70 dark:border-neutral-800 p-3">
              <div className="text-xs uppercase tracking-[0.18em] opacity-50">Последние цели</div>
              <div className="mt-2 flex flex-wrap gap-2">
                {goals.length === 0 ? <span className="text-xs opacity-50">Пока пусто</span> : goals.map((item) => <Badge key={item} variant="outline">{item}</Badge>)}
              </div>
            </div>
            <div className="rounded-2xl border border-neutral-200/70 dark:border-neutral-800 p-3">
              <div className="text-xs uppercase tracking-[0.18em] opacity-50">Файлы</div>
              <div className="mt-2 flex flex-wrap gap-2">
                {files.length === 0 ? <span className="text-xs opacity-50">Пока пусто</span> : files.map((item) => <Badge key={item} variant="outline">{item}</Badge>)}
              </div>
            </div>
            <div className="rounded-2xl border border-neutral-200/70 dark:border-neutral-800 p-3">
              <div className="text-xs uppercase tracking-[0.18em] opacity-50">Действия AI</div>
              <div className="mt-2 flex flex-wrap gap-2">
                {actions.length === 0 ? <span className="text-xs opacity-50">Пока пусто</span> : actions.map((item) => <Badge key={item} variant="outline">{item}</Badge>)}
              </div>
            </div>
          </div>

          <div className="mt-4 flex flex-wrap gap-2 text-xs opacity-65">
            {memory?.lastUserMessageAtUtc ? <span>последний запрос: {formatDate(memory.lastUserMessageAtUtc)}</span> : null}
            {memory?.lastAssistantMessageAtUtc ? <span>· последний ответ AI: {formatDate(memory.lastAssistantMessageAtUtc)}</span> : null}
          </div>
        </>
      )}
    </div>
  );
}

// ── Live entity tracker ────────────────────────────────────

function useLiveEntity(entityType, entityId, active = true) {
  const [data, setData] = useState(null);
  const [loading, setLoading] = useState(false);
  const intervalRef = useRef(null);

  useEffect(() => {
    if (!entityId || !active) { setData(null); return undefined; }
    let cancelled = false;
    const fetcher = entityType === 'batch' ? getAiBatch
      : entityType === 'job' ? getAiJob
        : null;
    if (!fetcher) return undefined;

    const poll = async () => {
      try {
        setLoading(true);
        const result = await fetcher(entityId);
        if (!cancelled) setData(result);
      } catch { /* ignore */ } finally {
        if (!cancelled) setLoading(false);
      }
    };
    poll();
    intervalRef.current = setInterval(poll, 4000);
    return () => { cancelled = true; clearInterval(intervalRef.current); };
  }, [entityType, entityId, active]);

  // Stop polling once entity reaches terminal state
  useEffect(() => {
    if (!data) return;
    const status = String(data.status || data.batchStatus || '').toLowerCase();
    const terminal = ['done', 'completed', 'failed', 'cancelled', 'published'];
    if (terminal.some((s) => status.includes(s))) {
      clearInterval(intervalRef.current);
    }
  }, [data]);

  return { data, loading };
}

function LiveBatchCard({ batchId }) {
  const { data: batch, loading } = useLiveEntity('batch', batchId);
  if (!batch && !loading) return null;

  const items = Array.isArray(batch?.items) ? batch.items : [];
  const total = items.length || batch?.totalItems || 0;
  const completed = items.filter((i) => ['completed', 'done', 'published'].includes(String(i.status || '').toLowerCase())).length;
  const failed = items.filter((i) => String(i.status || '').toLowerCase() === 'failed').length;
  const processing = items.filter((i) => String(i.status || '').toLowerCase() === 'processing').length;
  const pct = total > 0 ? Math.round((completed / total) * 100) : 0;
  const batchStatus = String(batch?.status || batch?.batchStatus || 'unknown').toLowerCase();
  const isDone = ['done', 'completed', 'published'].some((s) => batchStatus.includes(s));

  return (
    <div className="mt-3 rounded-2xl border border-neutral-200/70 dark:border-neutral-800 px-3 py-3 text-sm bg-[rgb(var(--card))]">
      <div className="flex flex-wrap items-center gap-2">
        <Package size={14} />
        <span className="font-medium">Batch</span>
        <Badge variant={isDone ? 'success' : batchStatus === 'failed' ? 'danger' : 'outline'}>{batchStatus}</Badge>
        <span className="opacity-60">{completed}/{total} готово{failed > 0 ? `, ${failed} ошибок` : ''}{processing > 0 ? `, ${processing} в работе` : ''}</span>
        {loading && !isDone ? <LoaderCircle size={12} className="animate-spin opacity-50" /> : null}
      </div>
      {total > 0 && (
        <div className="mt-2 h-2 w-full rounded-full bg-neutral-200 dark:bg-neutral-800 overflow-hidden">
          <div className="h-full rounded-full bg-[rgb(var(--accent))] transition-all duration-500" style={{ width: `${pct}%` }} />
        </div>
      )}
      {items.length > 0 && (
        <div className="mt-3 flex flex-col gap-1">
          {items.map((item, idx) => {
            const st = String(item.status || '').toLowerCase();
            const variant = st === 'completed' || st === 'done' || st === 'published' ? 'success' : st === 'failed' ? 'danger' : st === 'processing' ? 'outline' : 'outline';
            const title = item.title || item.titleHint || item.targetSkill || `Задача ${idx + 1}`;
            return (
              <div key={item.id || idx} className="flex items-center gap-2 text-xs">
                <Badge variant={variant} className="min-w-[80px] justify-center">{st || 'pending'}</Badge>
                <span className="truncate">{title}</span>
                {item.draftId ? (
                  <Link to={`/admin/ai?tab=drafts&id=${item.draftId}`} className="opacity-60 hover:opacity-100">
                    <ExternalLink size={12} />
                  </Link>
                ) : null}
              </div>
            );
          })}
        </div>
      )}
    </div>
  );
}

function LiveJobCard({ jobId }) {
  const { data: job, loading } = useLiveEntity('job', jobId);
  if (!job && !loading) return null;

  const status = String(job?.status || 'unknown').toLowerCase();
  const isDone = ['completed', 'done'].includes(status);
  const variant = isDone ? 'success' : status === 'failed' ? 'danger' : 'outline';
  const draftTitle = job?.result?.draft?.title || job?.result?.title;
  const selfCheckScore = job?.result?.draftValidation?.score ?? job?.result?.selfCheckScore;

  return (
    <div className="mt-3 rounded-2xl border border-neutral-200/70 dark:border-neutral-800 px-3 py-3 text-sm bg-[rgb(var(--card))]">
      <div className="flex flex-wrap items-center gap-2">
        <FileText size={14} />
        <span className="font-medium">Job {String(job?.type || '').replace('assignment_', '')}</span>
        <Badge variant={variant}>{status}</Badge>
        {loading && !isDone ? <LoaderCircle size={12} className="animate-spin opacity-50" /> : null}
      </div>
      {draftTitle ? <div className="mt-2 opacity-90">Задача: {draftTitle}</div> : null}
      {selfCheckScore != null ? (
        <div className="mt-1 flex items-center gap-2">
          <span className="opacity-60">Self-check:</span>
          <Badge variant={selfCheckScore >= 0.8 ? 'success' : selfCheckScore >= 0.5 ? 'outline' : 'danger'}>
            {(selfCheckScore * 100).toFixed(0)}%
          </Badge>
        </div>
      ) : null}
    </div>
  );
}

function ToolResultCard({ sessionId, result, onConfirm, actionBusy, developerView }) {
  const tone = result?.status === 'done'
    ? 'success'
    : result?.status === 'confirmation_required'
      ? 'outline'
      : result?.status === 'failed'
        ? 'danger'
        : 'outline';

  return (
    <div className="mt-3 rounded-2xl border border-neutral-200/70 dark:border-neutral-800 px-3 py-3 text-sm bg-[rgb(var(--card))]">
      <div className="flex flex-wrap items-center gap-2">
        {result?.requiresConfirmation ? <ShieldCheck size={14} /> : <Sparkles size={14} />}
        <span className="font-medium">{result?.requiresConfirmation ? 'Нужно подтверждение' : 'Результат действия'}</span>
        <Badge variant={tone}>{result?.status || 'done'}</Badge>
      </div>
      {developerView ? <div className="mt-2 text-xs uppercase tracking-[0.16em] opacity-50">Сырой tool-result</div> : null}
      {result?.summary ? <div className="mt-2 opacity-90 whitespace-pre-wrap">{result.summary}</div> : null}
      <div className="mt-3 flex flex-wrap gap-2">
        {result?.jobId ? <Badge variant="outline">job: {String(result.jobId).slice(0, 8)}</Badge> : null}
        {result?.batchId ? <Badge variant="outline">batch: {String(result.batchId).slice(0, 8)}</Badge> : null}
        {result?.draftId ? <Badge variant="outline">draft: {String(result.draftId).slice(0, 8)}</Badge> : null}
        {result?.assignmentId ? <Badge variant="outline">assignment: {String(result.assignmentId).slice(0, 8)}</Badge> : null}
      </div>

      {/* Live tracking panels */}
      {result?.batchId ? <LiveBatchCard batchId={result.batchId} /> : null}
      {result?.jobId && !result?.batchId ? <LiveJobCard jobId={result.jobId} /> : null}

      {result?.debugInfo ? <ToolDebugPanel debugInfo={result.debugInfo} /> : null}
      {developerView ? (
        <div className="mt-3 grid gap-3 lg:grid-cols-2">
          <JsonPreview title="tool-result json" value={result} />
          {result?.confirmationToolCall ? <JsonPreview title="confirmation tool call" value={result.confirmationToolCall} /> : null}
        </div>
      ) : null}

      <div className="mt-3 flex flex-wrap gap-2">
        {result?.navigateTo ? (
          <Link to={result.navigateTo} className="btn-outline inline-flex">Открыть связанную страницу</Link>
        ) : null}
        {result?.requiresConfirmation && result?.confirmationToolCall && sessionId ? (
          <Button
            type="button"
            variant="outline"
            disabled={actionBusy}
            onClick={() => onConfirm(result)}
          >
            {actionBusy ? <LoaderCircle size={16} className="animate-spin" /> : <ShieldCheck size={16} />}
            Подтвердить и выполнить
          </Button>
        ) : null}
      </div>
    </div>
  );
}

function MessageBubble({ sessionId, message, onConfirm, onQuickReply, actionBusy, showTechnical, developerView }) {
  const isAssistant = String(message?.role || '').toLowerCase() === 'assistant';
  const isSystem = String(message?.role || '').toLowerCase() === 'system';
  const isBatchUpdate = message?.status === 'batch-update' || message?.status === 'needs-clarification';
  const isNeedsClarification = message?.status === 'needs-clarification';
  const attachments = Array.isArray(message?.attachments) ? message.attachments : [];
  const toolCalls = normalizeToolCalls(message);
  const toolResults = normalizeToolResults(message);
  const visibleToolCalls = (showTechnical || developerView) ? toolCalls : [];
  const visibleToolResults = (showTechnical || developerView)
    ? toolResults
    : toolResults.filter((result) => result?.requiresConfirmation
      || result?.draftId
      || result?.assignmentId
      || result?.jobId
      || result?.batchId
      || result?.debugInfo
      || ['failed', 'error', 'cancelled', 'partial', 'needs-revision'].includes(String(result?.status || '').toLowerCase()));
  const quickReplies = isAssistant && visibleToolCalls.length === 0 && visibleToolResults.length === 0 ? getAssistantQuickReplies(message) : [];

  // System/batch-update messages rendered as compact notifications
  if (isSystem || isBatchUpdate) {
    const borderColor = isNeedsClarification
      ? 'border-red-400/60 dark:border-red-500/50'
      : 'border-amber-400/50 dark:border-amber-500/40';
    const bgColor = isNeedsClarification
      ? 'bg-red-50/60 dark:bg-red-900/15'
      : 'bg-amber-50/60 dark:bg-amber-900/15';
    const textColor = isNeedsClarification
      ? 'text-red-800 dark:text-red-300'
      : 'text-amber-800 dark:text-amber-300';
    return (
      <div className="flex justify-center">
        <div className={`max-w-[85%] rounded-2xl border border-dashed ${borderColor} ${bgColor} px-4 py-2 text-xs ${textColor}`}>
          <span className="whitespace-pre-wrap">{message?.content || '—'}</span>
          {isNeedsClarification && (
            <div className="mt-1 text-[10px] opacity-70 italic">Ответьте в чате, чтобы уточнить запрос</div>
          )}
          <span className="ml-2 opacity-50">{formatDate(message?.createdAtUtc)}</span>
        </div>
      </div>
    );
  }

  return (
    <div className={`flex ${isAssistant ? 'justify-start' : 'justify-end'}`}>
      <div className={`max-w-[94%] rounded-3xl border px-4 py-3 shadow-soft ${isAssistant
        ? 'border-neutral-200/70 dark:border-neutral-800 bg-[rgb(var(--card))]'
        : 'border-[rgba(var(--accent)/0.22)] bg-[rgba(var(--accent)/0.12)]'}`}>
        <div className="mb-2 flex flex-wrap items-center gap-2 text-xs opacity-70">
          {isAssistant ? <Bot size={14} /> : <User size={14} />}
          <span>{isAssistant ? 'AI' : 'Вы'}</span>
          {message?.status === 'processing' ? <Badge variant="outline">обрабатывается</Badge> : null}
          {message?.status === 'failed' ? <Badge variant="danger">ошибка</Badge> : null}
          <span>{formatDate(message?.createdAtUtc)}</span>
        </div>

        {developerView && isAssistant ? <ExperimentTurnPanel message={message} /> : null}
        {developerView && isAssistant ? <div className="mb-2 text-[11px] uppercase tracking-[0.16em] opacity-45">Сырой текст ответа модели</div> : null}
        <div className="whitespace-pre-wrap break-words text-sm leading-6">{message?.content || '—'}</div>

        {attachments.length > 0 && (
          <div className="mt-3 flex flex-wrap gap-2">
            {attachments.map((file) => (
              <a
                key={file.fileKey}
                href={file.publicUrl || '#'}
                target="_blank"
                rel="noreferrer"
                className="inline-flex items-center gap-2 rounded-2xl border border-neutral-200/70 dark:border-neutral-800 px-3 py-2 text-xs hover:border-[rgba(var(--accent)/0.35)]"
              >
                <Paperclip size={14} />
                <span>{file.originalName || file.fileKey}</span>
              </a>
            ))}
          </div>
        )}

        {visibleToolCalls.map((tool, index) => (
          <div key={`${tool.name || 'tool'}-${index}`} className="mt-3 rounded-2xl border border-dashed border-[rgba(var(--accent)/0.35)] px-3 py-2 text-xs opacity-80">
            <div className="flex items-center gap-2 font-medium"><Wrench size={14} /> Действие: {tool.name}</div>
            {tool.reason ? <div className="mt-1">{tool.reason}</div> : null}
          </div>
        ))}

        {visibleToolResults.map((result, index) => (
          <ToolResultCard
            key={`${result.status || 'result'}-${index}`}
            sessionId={sessionId}
            result={result}
            onConfirm={onConfirm}
            actionBusy={actionBusy}
            developerView={developerView}
          />
        ))}

        {quickReplies.length > 0 && (
          <div className="mt-3 flex flex-wrap gap-2">
            {quickReplies.map((item) => (
              <Button
                key={item.key}
                type="button"
                variant="outline"
                onClick={() => onQuickReply?.(item.message)}
                disabled={actionBusy || !item.message}
              >
                {item.label}
              </Button>
            ))}
          </div>
        )}
      </div>
    </div>
  );
}


function ActionModeToggle({ actionMode, onChange, disabled = false, compact = false }) {
  const options = [
    {
      value: 'mono',
      title: 'Пошагово',
      subtitle: 'Один осознанный шаг за ход',
      hint: 'AI чаще останавливается после одного шага и ждёт следующего сообщения.',
    },
    {
      value: 'multi',
      title: 'Самостоятельно',
      subtitle: 'Несколько внутренних проходов подряд',
      hint: 'AI сама ищет anchor, открывает соседние задания, проверяет себя и только потом отвечает.',
    },
  ];

  return (
    <div className={`rounded-2xl border border-neutral-200/70 dark:border-neutral-800/70 ${compact ? 'px-2 py-2' : 'px-3 py-3'} bg-white/70 dark:bg-neutral-950/40`}>
      <div className="flex items-center justify-between gap-3">
        <div>
          <div className="text-[11px] uppercase tracking-[0.18em] opacity-55">Режим агента</div>
          <div className="mt-1 text-sm font-medium">{actionMode === 'multi' ? 'Самостоятельно' : 'Пошагово'}</div>
        </div>
        <div className="grid grid-cols-2 gap-1 rounded-2xl bg-neutral-100/80 p-1 dark:bg-neutral-900/80">
          {options.map((option) => {
            const active = actionMode === option.value;
            return (
              <button
                key={option.value}
                type="button"
                onClick={() => onChange(option.value)}
                disabled={disabled}
                title={option.hint}
                className={`rounded-xl px-3 py-2 text-left transition-colors ${
                  active
                    ? 'bg-primary text-primary-foreground shadow-sm'
                    : 'text-neutral-700 hover:bg-white dark:text-neutral-200 dark:hover:bg-neutral-800'
                } ${disabled ? 'cursor-not-allowed opacity-60' : ''}`}
              >
                <div className="text-xs font-semibold">{option.title}</div>
                <div className={`mt-0.5 text-[11px] leading-4 ${active ? 'text-primary-foreground/85' : 'opacity-70'}`}>{option.subtitle}</div>
              </button>
            );
          })}
        </div>
      </div>
      {!compact && (
        <div className="mt-2 text-xs opacity-70">
          {options.find((option) => option.value === actionMode)?.hint}
        </div>
      )}
    </div>
  );
}

export default function AdminAiChatPage() {
  const [searchParams] = useSearchParams();
  const isFullscreen = searchParams.get('fullscreen') === '1';
  const notify = useNotify();
  const [sessions, setSessions] = useState([]);
  const [sessionSearch, setSessionSearch] = useState('');
  const [courses, setCourses] = useState([]);
  const [selectedCourseId, setSelectedCourseId] = useState('');
  const [sessionId, setSessionId] = useState(null);
  const [session, setSession] = useState(null);
  const [message, setMessage] = useState('');
  const [files, setFiles] = useState([]);
  const [loading, setLoading] = useState(true);
  const [sending, setSending] = useState(false);
  const [refreshing, setRefreshing] = useState(false);
  const [actionBusy, setActionBusy] = useState(false);
  const [exporting, setExporting] = useState(false);
  const [dragActive, setDragActive] = useState(false);
  const [showMemory, setShowMemory] = useState(false);
  const [showTechnical, setShowTechnical] = useState(false);
  const [developerView, setDeveloperView] = useState(() => {
    try {
      return localStorage.getItem('aiChat_developerView') !== '0';
    } catch {
      return true;
    }
  });
  const [actionMode, setActionMode] = useState(() => {
    try {
      const raw = String(localStorage.getItem('aiChat_actionMode') || 'multi').toLowerCase();
      return raw === 'multi' ? 'multi' : 'mono';
    } catch {
      return 'multi';
    }
  });
  const [instructionStrictness, setInstructionStrictness] = useState(() => {
    try {
      const raw = Number(localStorage.getItem('aiChat_instructionStrictness') || '55');
      return Number.isFinite(raw) ? Math.max(0, Math.min(100, raw)) : 55;
    } catch {
      return 55;
    }
  });
  const fileInputRef = useRef(null);
  const listRef = useRef(null);

  const applyActionMode = useCallback((nextMode) => {
    const normalized = nextMode === 'multi' ? 'multi' : 'mono';
    setActionMode(normalized);
    try { localStorage.setItem('aiChat_actionMode', normalized); } catch { /* ignore */ }
  }, []);
  const previousPendingRef = useRef(false);

  useEffect(() => {
    try { localStorage.setItem('aiChat_developerView', developerView ? '1' : '0'); } catch { /* ignore */ }
  }, [developerView]);

  const currentMessages = Array.isArray(session?.messages) ? session.messages : [];
  const pending = useMemo(
    () => currentMessages.some((x) => x.role === 'assistant' && x.status === 'processing'),
    [currentMessages],
  );
  // Track active batches — poll for batch→chat feedback messages
  const hasActiveBatch = useMemo(() => {
    const toolResults = currentMessages.flatMap((m) => m.toolResults || []);
    const batchIds = toolResults.filter((r) => r.batchId).map((r) => r.batchId);
    if (batchIds.length === 0) return false;
    // Check if the last system message about batch is a terminal state
    const lastBatchMsg = [...currentMessages].reverse().find((m) => m.status === 'batch-update' || m.status === 'needs-clarification');
    if (lastBatchMsg && (lastBatchMsg.status === 'needs-clarification' || /завершён|готов|Все .* сгенерированы|Аудит публикации/i.test(lastBatchMsg.content || ''))) return false;
    return true;
  }, [currentMessages]);
  const lastAssistantMessage = useMemo(() => ([...currentMessages].reverse().find((x) => x.role === 'assistant' && x.status !== 'processing') || null), [currentMessages]);

  const strictnessLabel = useMemo(() => {
    if (instructionStrictness <= 20) return 'Свободно';
    if (instructionStrictness >= 85) return 'Максимум';
    return 'Баланс';
  }, [instructionStrictness]);

  const upsertSessionListItem = useCallback((full) => {
    const messages = Array.isArray(full?.messages) ? full.messages : [];
    const lastStable = [...messages].reverse().find((x) => x?.status !== 'processing') || messages[messages.length - 1];
    return {
      id: full.id,
      courseId: full.courseId,
      courseTitle: full.courseTitle,
      title: full.title,
      lastMessagePreview: lastStable?.content || null,
      memorySummary: full.memory?.summary || null,
      messageCount: full.memory?.messageCount || messages.length || 0,
      isPending: messages.some((x) => x.role === 'assistant' && x.status === 'processing'),
      createdAtUtc: full.createdAtUtc,
      updatedAtUtc: full.updatedAtUtc,
    };
  }, []);

  const selectSession = useCallback(async (nextSessionId, fallbackItem = null) => {
    if (!nextSessionId) {
      setSessionId(null);
      setSession(null);
      return null;
    }
    setSessionId(nextSessionId);
    try {
      const full = await getAiChatSession(nextSessionId);
      setSession(full);
      setSessions((prev) => prev.map((item) => (item.id === full.id ? upsertSessionListItem(full) : item)));
      return full;
    } catch (e) {
      const fallback = fallbackItem ? {
        id: fallbackItem.id,
        courseId: fallbackItem.courseId || null,
        courseTitle: fallbackItem.courseTitle || null,
        title: fallbackItem.title || 'Повреждённый AI-чат',
        createdAtUtc: fallbackItem.createdAtUtc,
        updatedAtUtc: fallbackItem.updatedAtUtc,
        memory: {
          summary: fallbackItem.memorySummary || 'Не удалось открыть этот чат. Его всё ещё можно удалить из списка или создать новый.',
          messageCount: fallbackItem.messageCount || 0,
          instructionStrictness,
        },
        messages: [],
      } : null;
      setSession(fallback);
      notify.error(handleApiError(e, 'Не удалось открыть этот чат. Его можно удалить из списка.'));
      return null;
    }
  }, [instructionStrictness, notify, upsertSessionListItem]);

  const deleteSessionFromList = useCallback(async (id) => {
    if (!id) return;
    if (!window.confirm('Удалить этот AI-чат?')) return;
    try {
      try {
        await deleteAiChatSession(id);
      } catch (e) {
        const status = e?.response?.status;
        if (status !== 404) throw e;
      }
      const nextList = sessions.filter((item) => item.id !== id);
      setSessions(nextList);
      if (sessionId === id) {
        const nextId = nextList[0]?.id || null;
        if (nextId) {
          await selectSession(nextId, nextList[0]);
        } else {
          setSessionId(null);
          setSession(null);
        }
      }
      notify.success('Чат удалён');
    } catch (e) {
      notify.error(handleApiError(e, 'Не удалось удалить чат'));
    }
  }, [notify, selectSession, sessionId, sessions]);

  const loadSessions = useCallback(async (preferredId) => {
    const list = await getAiChatSessions();
    setSessions(list);
    const nextId = preferredId || sessionId || list[0]?.id || null;
    if (nextId) {
      const fallbackItem = list.find((item) => item.id === nextId) || null;
      await selectSession(nextId, fallbackItem);
    } else {
      setSessionId(null);
      setSession(null);
    }
  }, [selectSession, sessionId]);

  useEffect(() => {
    let active = true;
    (async () => {
      try {
        setLoading(true);
        const [courseList] = await Promise.all([getCourses()]);
        if (!active) return;
        setCourses(Array.isArray(courseList) ? courseList : []);
        await loadSessions();
      } catch (e) {
        if (!active) return;
        notify.error(handleApiError(e, 'Не удалось открыть AI-чат'));
      } finally {
        if (active) setLoading(false);
      }
    })();
    return () => { active = false; };
  }, [loadSessions, notify]);

  useEffect(() => {
    if (!sessionId || !pending) return undefined;
    const timer = setInterval(async () => {
      try {
        const full = await getAiChatSession(sessionId);
        setSession(full);
        setSessions((prev) => prev.map((item) => (item.id === full.id ? upsertSessionListItem(full) : item)));
      } catch {
        // ignore transient polling issues
      }
    }, 3000);
    return () => clearInterval(timer);
  }, [pending, sessionId, upsertSessionListItem]);

  // Batch progress polling — slower interval, runs while batch is active
  useEffect(() => {
    if (!sessionId || !hasActiveBatch || pending) return undefined;
    const timer = setInterval(async () => {
      try {
        const full = await getAiChatSession(sessionId);
        setSession(full);
        setSessions((prev) => prev.map((item) => (item.id === full.id ? upsertSessionListItem(full) : item)));
      } catch {
        // ignore transient polling issues
      }
    }, 6000);
    return () => clearInterval(timer);
  }, [hasActiveBatch, pending, sessionId, upsertSessionListItem]);

  useEffect(() => {
    if (previousPendingRef.current && !pending && lastAssistantMessage?.status === 'done') {
      notify.success('AI ответила в текущем чате');
    }
    previousPendingRef.current = pending;
  }, [lastAssistantMessage, notify, pending]);

  useEffect(() => {
    const next = Number(session?.memory?.instructionStrictness);
    if (!Number.isFinite(next)) return;
    setInstructionStrictness((prev) => (prev === next ? prev : Math.max(0, Math.min(100, next))));
  }, [session?.id, session?.memory?.instructionStrictness]);

  useEffect(() => {
    try { localStorage.setItem('aiChat_instructionStrictness', String(instructionStrictness)); } catch { /* ignore */ }
  }, [instructionStrictness]);

  useEffect(() => {
    if (!sessionId || !session) return undefined;
    const currentStrictness = Number(session?.memory?.instructionStrictness);
    if (Number.isFinite(currentStrictness) && currentStrictness === instructionStrictness) return undefined;
    const timer = setTimeout(async () => {
      try {
        const updated = await updateAiChatSession(sessionId, {
          title: session?.title,
          courseId: session?.courseId || null,
          instructionStrictness,
        });
        setSession(updated);
        setSessions((prev) => prev.map((item) => (item.id === updated.id ? upsertSessionListItem(updated) : item)));
      } catch {
        // ignore slider sync errors; explicit send will still carry strictness
      }
    }, 250);
    return () => clearTimeout(timer);
  }, [instructionStrictness, sessionId, session, upsertSessionListItem]);

  useEffect(() => {
    const node = listRef.current;
    if (!node) return;
    node.scrollTop = node.scrollHeight;
  }, [session?.messages?.length, pending]);

  const createSession = useCallback(async () => {
    const created = await createAiChatSession({
      courseId: selectedCourseId || null,
      title: selectedCourseId ? `AI чат · ${courses.find((x) => String(x.id) === String(selectedCourseId))?.title || 'курс'}` : undefined,
      instructionStrictness,
    });
    setSessionId(created.id);
    setSession(created);
    setSessions((prev) => [upsertSessionListItem(created), ...prev]);
    return created;
  }, [courses, instructionStrictness, selectedCourseId, upsertSessionListItem]);

  const refreshCurrent = useCallback(async () => {
    if (!sessionId) return;
    try {
      setRefreshing(true);
      await selectSession(sessionId, sessions.find((item) => item.id === sessionId) || null);
      await loadSessions(sessionId);
    } catch (e) {
      notify.error(handleApiError(e, 'Не удалось обновить чат'));
    } finally {
      setRefreshing(false);
    }
  }, [loadSessions, notify, selectSession, sessionId, sessions]);

  const syncCurrentCourse = useCallback(async (nextCourseId) => {
    if (!sessionId) return;
    try {
      const updated = await updateAiChatSession(sessionId, {
        title: session?.title,
        courseId: nextCourseId || null,
        instructionStrictness,
      });
      setSession(updated);
      setSessions((prev) => prev.map((item) => (item.id === updated.id ? upsertSessionListItem(updated) : item)));
      notify.success(nextCourseId ? 'Курс для чата обновлён' : 'Привязка к курсу снята');
    } catch (e) {
      notify.error(handleApiError(e, 'Не удалось изменить курс чата'));
    }
  }, [instructionStrictness, notify, session?.title, sessionId, upsertSessionListItem]);

  const addFiles = useCallback((incoming) => {
    const next = Array.from(incoming || []).filter(Boolean);
    if (next.length === 0) return;
    setFiles((prev) => {
      const known = new Set(prev.map((file) => `${file.name}__${file.size}__${file.lastModified || 0}`));
      const unique = next.filter((file) => !known.has(`${file.name}__${file.size}__${file.lastModified || 0}`));
      return [...prev, ...unique];
    });
  }, []);

  const onSend = useCallback(async (overrideMessage) => {
    const text = String(overrideMessage ?? message ?? '').trim();
    if (!text && files.length === 0) return;
    try {
      setSending(true);
      let activeSessionId = sessionId;
      if (!activeSessionId) {
        const created = await createSession();
        activeSessionId = created.id;
      }

      const uploaded = [];
      for (const file of files) {
        // eslint-disable-next-line no-await-in-loop
        const meta = await uploadAiChatFile(file);
        uploaded.push(meta);
      }

      const response = await sendAiChatMessage(activeSessionId, {
        content: text || (uploaded.length ? 'Используй прикреплённые файлы в контексте.' : ''),
        attachments: uploaded,
        actionMode,
        instructionStrictness,
      });

      setSession(response.session);
      setSessionId(response.session.id);
      setSessions((prev) => {
        const item = upsertSessionListItem(response.session);
        const others = prev.filter((x) => x.id !== item.id);
        return [item, ...others];
      });
      setFiles([]);
      setMessage('');
      if (fileInputRef.current) fileInputRef.current.value = '';
    } catch (e) {
      notify.error(handleApiError(e, 'Не удалось отправить сообщение в AI-чат'));
    } finally {
      setSending(false);
    }
  }, [actionMode, createSession, files, instructionStrictness, message, notify, sessionId, upsertSessionListItem]);

  const onConfirmTool = useCallback(async (result) => {
    if (!sessionId || !result?.confirmationToolCall) return;
    try {
      setActionBusy(true);
      const response = await confirmAiChatTool(sessionId, {
        toolName: result.confirmationToolCall.name,
        argumentsJson: result.confirmationToolCall.argumentsJson,
        note: result.suggestedConfirmationMessage || `Подтверждаю выполнение действия ${result.confirmationToolCall.name}.`,
        instructionStrictness,
      });
      setSession(response.session);
      setSessions((prev) => prev.map((item) => (item.id === response.session.id ? upsertSessionListItem(response.session) : item)));
      notify.success('Действие подтверждено и выполнено');
    } catch (e) {
      notify.error(handleApiError(e, 'Не удалось подтвердить действие'));
    } finally {
      setActionBusy(false);
    }
  }, [notify, sessionId, upsertSessionListItem]);

  const onQuickReply = useCallback((nextMessage) => {
    if (!nextMessage) return;
    setMessage(nextMessage);
    onSend(nextMessage);
  }, [onSend]);

  const renameCurrent = async () => {
    if (!sessionId) return;
    const nextTitle = window.prompt('Новое название чата', session?.title || '');
    if (!nextTitle || !nextTitle.trim()) return;
    try {
      const updated = await updateAiChatSession(sessionId, { title: nextTitle.trim(), courseId: session?.courseId || null, instructionStrictness });
      setSession(updated);
      setSessions((prev) => prev.map((item) => (item.id === updated.id ? upsertSessionListItem(updated) : item)));
      notify.success('Название чата обновлено');
    } catch (e) {
      notify.error(handleApiError(e, 'Не удалось переименовать чат'));
    }
  };

  const deleteCurrent = async () => {
    if (!sessionId) return;
    await deleteSessionFromList(sessionId);
  };

  const exportCurrent = useCallback(async (format = 'md') => {
    if (!sessionId) return;
    try {
      setExporting(true);
      const { blob, fileName } = await downloadAiChatExport(sessionId, format);
      triggerDownload(blob, fileName);
      notify.success('История чата экспортирована');
    } catch (e) {
      notify.error(handleApiError(e, 'Не удалось экспортировать историю чата'));
    } finally {
      setExporting(false);
    }
  }, [notify, sessionId]);

  const filteredSessions = useMemo(() => {
    const q = sessionSearch.trim().toLowerCase();
    if (!q) return sessions;
    return sessions.filter((item) => [item.title, item.courseTitle, item.lastMessagePreview, item.memorySummary]
      .filter(Boolean)
      .some((value) => String(value).toLowerCase().includes(q)));
  }, [sessionSearch, sessions]);


  const openFullscreen = useCallback(() => {
    const url = `/admin/ai/chat?fullscreen=1${sessionId ? `&session=${sessionId}` : ''}`;
    window.open(url, '_blank');
  }, [sessionId]);

  // In fullscreen mode, auto-select session from URL param
  useEffect(() => {
    if (!isFullscreen) return;
    const urlSessionId = searchParams.get('session');
    if (urlSessionId && urlSessionId !== sessionId) {
      selectSession(urlSessionId, sessions.find((item) => item.id === urlSessionId) || null).catch(() => {});
    }
  }, [isFullscreen, searchParams, selectSession, sessionId, sessions]);

  // In fullscreen mode (new tab), apply theme classes that Layout normally handles
  useLayoutEffect(() => {
    if (!isFullscreen) return;
    const root = document.documentElement;
    const palettes = ['blue', 'pink', 'apple', 'red', 'honey', 'violet'];
    const readUi = () => { try { const r = localStorage.getItem('uiSettings'); return r ? JSON.parse(r) : null; } catch { return null; } };
    const apply = () => {
      const ui = readUi();
      const palette = palettes.includes(ui?.colorTheme) ? ui.colorTheme : (localStorage.getItem('colorTheme') || 'pink');
      const mode = ui?.mode || localStorage.getItem('mode') || 'dark';
      root.classList.remove(...palettes);
      root.classList.add(palette);
      if (mode === 'dark') root.classList.add('dark');
      else root.classList.remove('dark');
    };
    apply();
    const onStorage = (e) => { if (e.key && ['colorTheme', 'mode', 'uiSettings'].includes(e.key)) apply(); };
    window.addEventListener('storage', onStorage);
    return () => window.removeEventListener('storage', onStorage);
  }, [isFullscreen]);

  // Fullscreen: render without Layout, no sidebar, full viewport
  if (isFullscreen) {
    return (
      <div className="fixed inset-0 z-[9999] flex flex-col bg-[rgb(var(--background))] overflow-hidden">
        {/* Compact top bar */}
        <div className="flex-none border-b border-neutral-200/70 dark:border-neutral-800 px-4 py-2 flex items-center justify-between gap-3 bg-[rgb(var(--card))]">
          <div className="flex items-center gap-3 min-w-0">
            <Bot size={18} className="opacity-60 flex-none" />
            <span className="font-semibold truncate">{session?.title || 'Новый AI-чат'}</span>
            {session?.courseTitle ? <Badge variant="outline">{session.courseTitle}</Badge> : null}
            {pending ? <Badge variant="outline">AI думает…</Badge> : null}
          </div>
          <div className="flex items-center gap-2 flex-none">
            <Select
              value={session?.courseId || ''}
              onChange={(e) => syncCurrentCourse(e.target.value)}
              disabled={!sessionId || pending || actionBusy}
              className="w-[200px] text-xs"
            >
              <option value="">Курс не выбран</option>
              {courses.map((c) => <option key={c.id} value={c.id}>{c.title}</option>)}
            </Select>
            <Button type="button" variant="outline" onClick={() => setShowTechnical((v) => !v)} title="Тех. детали">
              <Code2 size={14} />
            </Button>
            <Button type="button" variant="outline" onClick={() => setDeveloperView((v) => !v)} title="Dev-вид">
              <Package size={14} />
            </Button>
            <Button type="button" variant="outline" onClick={refreshCurrent} disabled={!sessionId || refreshing}>
              <RefreshCcw size={14} className={refreshing ? 'animate-spin' : ''} />
            </Button>
            <Button type="button" variant="outline" onClick={() => window.close()} title="Закрыть">
              <Shrink size={14} />
            </Button>
          </div>
        </div>

        {/* Messages area */}
        <div ref={listRef} className="flex-1 px-4 py-4 space-y-3 overflow-y-auto">
          {currentMessages.length === 0 ? (
            <div className="h-full grid place-items-center opacity-60">Отправь сообщение чтобы начать чат</div>
          ) : (
            <>
              {pending ? (
                <div className="rounded-2xl border border-dashed border-[rgba(var(--accent)/0.35)] bg-[rgba(var(--accent)/0.05)] px-4 py-3 text-sm">
                  <div className="inline-flex items-center gap-2"><LoaderCircle size={16} className="animate-spin" /> AI обрабатывает запрос…</div>
                </div>
              ) : null}
              {currentMessages.map((item) => (
                <MessageBubble
                  key={item.id || `${item.role}-${item.createdAtUtc}`}
                  sessionId={sessionId}
                  message={item}
                  onConfirm={onConfirmTool}
                  onQuickReply={onQuickReply}
                  actionBusy={actionBusy || sending}
                  showTechnical={showTechnical}
                  developerView={developerView}
                />
              ))}
            </>
          )}
        </div>

        {/* Compact input bar */}
        <div className="flex-none border-t border-neutral-200/70 dark:border-neutral-800 px-4 py-3 bg-[rgb(var(--card))]">
          {files.length > 0 && (
            <div className="flex flex-wrap gap-2 mb-2">
              {files.map((file, idx) => (
                <FileChip key={`${file.name}-${idx}`} file={file} removable onRemove={() => setFiles((prev) => prev.filter((_, i) => i !== idx))} />
              ))}
            </div>
          )}
          <div className="flex items-end gap-2">
            <button type="button" className="opacity-60 hover:opacity-100 p-2" onClick={() => fileInputRef.current?.click()} disabled={sending || pending}>
              <Paperclip size={18} />
            </button>
            <input ref={fileInputRef} type="file" hidden multiple onChange={(e) => addFiles(e.target.files)} />
            <Textarea
              value={message}
              onChange={(e) => setMessage(e.target.value)}
              rows={2}
              placeholder="Сообщение для AI…"
              disabled={sending || pending || actionBusy}
              className="flex-1 min-h-[44px] max-h-[120px] resize-none"
              onKeyDown={(e) => {
                if ((e.ctrlKey || e.metaKey) && e.key === 'Enter') { e.preventDefault(); onSend(); }
              }}
            />
            <Button type="button" onClick={() => onSend()} disabled={sending || pending || actionBusy || (!message.trim() && files.length === 0)} className="flex-none">
              {sending ? <LoaderCircle size={16} className="animate-spin" /> : <Send size={16} />}
            </Button>
          </div>
          <div className="flex items-center gap-2 mt-2">
            <button type="button" className="text-xs opacity-60 hover:opacity-100" onClick={() => onSend(CONTINUE_MESSAGE)} disabled={sending || pending}>Продолжай</button>
            <button type="button" className="text-xs opacity-60 hover:opacity-100" onClick={() => onSend(GENERATE_MESSAGE)} disabled={sending || pending}>Генерация</button>
            <div className="ml-auto flex items-center gap-3">
              <div className="flex items-center gap-2 text-xs opacity-80">
                <span>Строгость</span>
                <input
                  type="range"
                  min={0}
                  max={100}
                  step={1}
                  value={instructionStrictness}
                  onChange={(e) => setInstructionStrictness(Number(e.target.value))}
                  disabled={sending || pending || actionBusy}
                  className="w-28 accent-[rgb(var(--accent))]"
                  title="0 = свободнее, 100 = максимально буквально следует пользовательской инструкции"
                />
                <span className="min-w-[5.5rem] text-right">{strictnessLabel} · {instructionStrictness}</span>
              </div>
            <div className="w-[22rem] max-w-full">
              <ActionModeToggle
                actionMode={actionMode}
                onChange={applyActionMode}
                disabled={sending || pending || actionBusy}
                compact
              />
            </div>
            </div>
          </div>
        </div>
      </div>
    );
  }

  return (
    <Layout fullWidth>
      <div className="grid gap-4 xl:grid-cols-[340px_minmax(0,1fr)]">
        <Card className="p-4 xl:sticky xl:top-24 h-fit max-h-[82vh] overflow-y-auto">
          <div className="flex items-center justify-between gap-3">
            <div>
              <div className="text-xs uppercase tracking-[0.18em] opacity-60">AI chat</div>
              <div className="mt-1 text-lg font-semibold">Сессии</div>
            </div>
            <Button type="button" variant="outline" onClick={createSession}>
              <Plus size={16} />
            </Button>
          </div>

          <Field label="Курс для нового чата" hint="Если выбрать курс заранее, AI увереннее работает с batch, draft, review и анализом.">
            <Select value={selectedCourseId} onChange={(e) => setSelectedCourseId(e.target.value)}>
              <option value="">Без привязки к курсу</option>
              {courses.map((course) => (
                <option key={course.id} value={course.id}>{course.title}</option>
              ))}
            </Select>
          </Field>

          <div className="mt-3 rounded-2xl border border-neutral-200/70 dark:border-neutral-800 px-3 py-2 flex items-center gap-2">
            <Search size={14} className="opacity-60" />
            <input
              value={sessionSearch}
              onChange={(e) => setSessionSearch(e.target.value)}
              placeholder="Поиск по чатам"
              className="w-full bg-transparent outline-none text-sm"
            />
          </div>

          <div className="mt-4 flex flex-col gap-2">
            {filteredSessions.length === 0 ? (
              <div className="rounded-2xl border border-dashed border-neutral-300/70 dark:border-neutral-700 p-4 text-sm opacity-70">
                Пока нет сессий. Создай новый чат и начни с запроса вроде: «сделай batch на 5 задач по строкам».
              </div>
            ) : filteredSessions.map((item) => (
              <div
                key={item.id}
                role="button"
                tabIndex={0}
                onClick={() => {
                  selectSession(item.id, item).catch(() => {});
                }}
                onKeyDown={(e) => {
                  if (e.key === 'Enter' || e.key === ' ') {
                    e.preventDefault();
                    selectSession(item.id, item).catch(() => {});
                  }
                }}
                className={`rounded-2xl border px-3 py-3 text-left transition cursor-pointer ${sessionId === item.id
                  ? 'border-[rgb(var(--accent))] bg-[rgba(var(--accent)/0.12)]'
                  : 'border-neutral-200/70 dark:border-neutral-800 hover:border-[rgba(var(--accent)/0.28)]'}`}
              >
                <div className="flex items-center justify-between gap-2">
                  <div className="font-medium line-clamp-1">{item.title || 'Новый AI-чат'}</div>
                  <div className="flex items-center gap-2">
                    {item.messageCount ? <Badge variant="outline">{item.messageCount}</Badge> : null}
                    {item.isPending ? <Badge variant="outline">AI думает</Badge> : null}
                    <button
                      type="button"
                      className="rounded-full p-1 opacity-50 hover:opacity-100 text-red-500"
                      title="Удалить чат"
                      onClick={(e) => {
                        e.stopPropagation();
                        deleteSessionFromList(item.id);
                      }}
                    >
                      <Trash2 size={14} />
                    </button>
                  </div>
                </div>
                {item.courseTitle ? <div className="mt-1 text-xs opacity-60 line-clamp-1">{item.courseTitle}</div> : null}
                {item.lastMessagePreview ? <div className="mt-1 text-xs opacity-70 line-clamp-2">{item.lastMessagePreview}</div> : null}
                {!item.lastMessagePreview && item.memorySummary ? <div className="mt-1 text-xs opacity-60 line-clamp-2">{item.memorySummary}</div> : null}
                <div className="mt-2 text-[11px] opacity-45">{formatDate(item.updatedAtUtc)}</div>
              </div>
            ))}
          </div>
        </Card>

        <Card className="p-0 overflow-hidden min-h-[72vh] flex flex-col">
          <div className="border-b border-neutral-200/70 dark:border-neutral-800 px-5 py-4 flex flex-wrap items-center justify-between gap-3">
            <div>
              <div className="text-xs uppercase tracking-[0.18em] opacity-60">TaskForge AI</div>
              <div className="mt-1 flex flex-wrap items-center gap-2 text-lg font-semibold">
                <span>{session?.title || 'Новый AI-чат'}</span>
                {session?.courseTitle ? <Badge variant="outline">{session.courseTitle}</Badge> : null}
              </div>
            </div>
            <div className="flex flex-wrap items-center gap-2">
              <Button type="button" variant="outline" onClick={createSession} disabled={pending || actionBusy}>
                <Plus size={16} /> Новый чат
              </Button>
              <Button type="button" variant="outline" onClick={() => exportCurrent('debug')} disabled={!sessionId || pending || actionBusy || exporting} title="Скачать расширенный debug-архив: transcript.md + session.json + memory.json + timeline.json">
                {exporting ? <LoaderCircle size={16} className="animate-spin" /> : <Download size={16} />} Экспорт debug
              </Button>
              <Button type="button" variant="outline" onClick={renameCurrent} disabled={!sessionId || pending || actionBusy}>
                <Pencil size={16} />
              </Button>
              <Button type="button" variant="outline" onClick={deleteCurrent} disabled={!sessionId || actionBusy} className="text-red-500">
                <Trash2 size={16} />
              </Button>
              <Button type="button" variant="outline" onClick={refreshCurrent} disabled={!sessionId || refreshing}>
                <RefreshCcw size={16} className={refreshing ? 'animate-spin' : ''} />
              </Button>
              <Button type="button" variant="outline" onClick={openFullscreen} title="Открыть в новой вкладке на весь экран">
                <Expand size={16} />
              </Button>
            </div>
          </div>

          <div className="px-5 py-3 border-b border-neutral-200/50 dark:border-neutral-800/80 bg-[rgba(var(--accent)/0.03)] space-y-3">
            <div className="flex flex-col gap-3 lg:flex-row lg:items-start lg:justify-between">
              <div className="min-w-0 flex-1">
                <div className="flex flex-wrap items-center gap-2">
                  <Badge variant="outline">Память сессии</Badge>
                  {session?.memory?.messageCount ? <Badge variant="success">{session.memory.messageCount} сообщений</Badge> : null}
                  {pending ? <Badge variant="outline">AI думает…</Badge> : null}
                  <Button type="button" variant="outline" onClick={() => setShowMemory((v) => !v)}>
                    {showMemory ? 'Скрыть память' : 'Показать память'}
                  </Button>
                  <Button type="button" variant="outline" onClick={() => setShowTechnical((v) => !v)}>
                    <Code2 size={16} /> {showTechnical ? 'Скрыть тех. детали' : 'Показать тех. детали'}
                  </Button>
                  <Button type="button" variant="outline" onClick={() => setDeveloperView((v) => !v)}>
                    <Package size={16} /> {developerView ? 'Скрыть dev-вид' : 'Показать dev-вид'}
                  </Button>
                </div>
                <div className="mt-2 text-sm leading-6 opacity-75 whitespace-pre-wrap">
                  {developerView
                    ? (lastAssistantMessage?.content || session?.memory?.summary || 'Это девелоперская лаборатория для AI-чата: здесь важны маршрут, ветки, tool-calls, routing и debug-снимки, а не продуктовая полировка ответа.')
                    : (lastAssistantMessage?.content || session?.memory?.summary || 'Это полноценный чат: AI помнит прошлые сообщения, файлы и действия в рамках этой сессии.')}
                </div>
                <div className="mt-3 flex flex-wrap gap-2">
                  {(developerView ? EXPERIMENT_SUGGESTIONS : SUGGESTIONS).slice(0, 4).map((item) => (
                    <button
                      key={item}
                      type="button"
                      onClick={() => setMessage(item)}
                      className="rounded-full border border-neutral-200/70 dark:border-neutral-800 px-3 py-1.5 text-xs hover:border-[rgba(var(--accent)/0.35)]"
                    >
                      {item}
                    </button>
                  ))}
                </div>
              </div>
              <div className="w-full lg:w-[280px]">
                <Field label="Курс текущего чата" hint="Можно менять на лету — следующий ответ уже будет с новым контекстом курса.">
                  <Select value={session?.courseId || ''} onChange={(e) => syncCurrentCourse(e.target.value)} disabled={!sessionId || pending || actionBusy}>
                    <option value="">Без привязки к курсу</option>
                    {courses.map((course) => (
                      <option key={course.id} value={course.id}>{course.title}</option>
                    ))}
                  </Select>
                </Field>
              </div>
            </div>

            <ExperimentWorkbenchPanel
              session={session}
              messages={currentMessages}
              actionMode={actionMode}
              instructionStrictness={instructionStrictness}
              developerView={developerView}
            />

            {showMemory ? (
              <MemoryPanel memory={session?.memory} courseTitle={session?.courseTitle} />
            ) : null}
          </div>

          <div ref={listRef} className="flex-1 px-5 py-5 space-y-4 overflow-y-auto bg-[rgba(var(--accent)/0.02)]">
            {loading ? (
              <div className="h-full min-h-[20rem] grid place-items-center opacity-70">
                <div className="inline-flex items-center gap-2"><LoaderCircle size={18} className="animate-spin" /> Загружаю AI-чат…</div>
              </div>
            ) : currentMessages.length === 0 ? (
              <div className="h-full min-h-[20rem] grid place-items-center">
                <div className="max-w-2xl text-center">
                  <div className="text-2xl font-semibold">Чат с реальной AI</div>
                  <div className="mt-3 opacity-75 text-sm leading-6">
                    {developerView
                      ? 'Здесь лучше думать как девелопер: смотреть route, tool-calls, auto-actions, debugInfo, worker-трассу и пробовать разные режимы/строгость/ветки, а не ждать красивый продуктовый текст.'
                      : 'Здесь можно прикреплять файлы, ставить batch в очередь, валидировать и публиковать draft’ы, запускать AI-review попыток и risk-review пользователей. Чат работает через MinIO-вложения, реальный AI worker и backend tool-calls, а память сессии сохраняет прошлые цели, файлы и действия.'}
                  </div>
                </div>
              </div>
            ) : (
              <>
                {pending ? (
                  <div className="rounded-2xl border border-dashed border-[rgba(var(--accent)/0.35)] bg-[rgba(var(--accent)/0.05)] px-4 py-3 text-sm">
                    <div className="inline-flex items-center gap-2"><LoaderCircle size={16} className="animate-spin" /> AI обрабатывает последний запрос. Ответ появится здесь автоматически.</div>
                  </div>
                ) : null}
                {currentMessages.map((item) => (
                  <MessageBubble
                    key={item.id || `${item.role}-${item.createdAtUtc}`}
                    sessionId={sessionId}
                    message={item}
                    onConfirm={onConfirmTool}
                    onQuickReply={onQuickReply}
                    actionBusy={actionBusy || sending}
                    showTechnical={showTechnical}
                    developerView={developerView}
                  />
                ))}
              </>
            )}
          </div>

          <div className="border-t border-neutral-200/70 dark:border-neutral-800 p-4 space-y-3">
            <div
              className={`rounded-3xl border-2 border-dashed p-4 transition ${dragActive
                ? 'border-[rgb(var(--accent))] bg-[rgba(var(--accent)/0.08)]'
                : 'border-neutral-200/70 dark:border-neutral-800 bg-[rgba(var(--accent)/0.02)]'}`}
              onDragEnter={(e) => { e.preventDefault(); setDragActive(true); }}
              onDragOver={(e) => { e.preventDefault(); setDragActive(true); }}
              onDragLeave={(e) => { e.preventDefault(); if (!e.currentTarget.contains(e.relatedTarget)) setDragActive(false); }}
              onDrop={(e) => {
                e.preventDefault();
                setDragActive(false);
                addFiles(e.dataTransfer.files);
              }}
            >
              <div className="flex flex-col gap-3 md:flex-row md:items-center md:justify-between">
                <div className="flex items-start gap-3">
                  <div className="rounded-2xl bg-[rgba(var(--accent)/0.12)] p-3">
                    <Upload size={18} />
                  </div>
                  <div>
                    <div className="font-medium">Перетащи файлы сюда или прикрепи вручную</div>
                    <div className="mt-1 text-sm opacity-70">TXT/MD/JSON/CSV читаются особенно хорошо. DOCX/XLSX/PPTX, PDF и ZIP-архивы тоже теперь пробуем извлекать в текстовый контекст.</div>
                  </div>
                </div>
                <Button type="button" variant="outline" disabled={sending || pending || actionBusy} onClick={() => fileInputRef.current?.click()}>
                  <Paperclip size={16} /> Прикрепить файл
                </Button>
              </div>
              <input
                ref={fileInputRef}
                type="file"
                hidden
                multiple
                onChange={(e) => addFiles(e.target.files)}
              />
            </div>

            {files.length > 0 && (
              <div className="flex flex-wrap gap-2">
                {files.map((file, idx) => (
                  <FileChip
                    key={`${file.name}-${idx}`}
                    file={file}
                    removable
                    onRemove={() => setFiles((prev) => prev.filter((_, i) => i !== idx))}
                  />
                ))}
              </div>
            )}

            <div className="flex flex-wrap items-center gap-2">
              <Button type="button" variant="outline" disabled={sending || pending || actionBusy} onClick={() => setMessage(CONTINUE_MESSAGE)}>
                Продолжай по памяти
              </Button>
              <Button type="button" variant="outline" disabled={sending || pending || actionBusy} onClick={() => setMessage(GENERATE_MESSAGE)}>
                Начать генерацию
              </Button>
              <span className="flex-1" />
              <div className="flex items-center gap-2 text-xs opacity-80">
                <span>Строгость</span>
                <input
                  type="range"
                  min={0}
                  max={100}
                  step={1}
                  value={instructionStrictness}
                  onChange={(e) => setInstructionStrictness(Number(e.target.value))}
                  disabled={sending || pending || actionBusy}
                  className="w-28 accent-[rgb(var(--accent))]"
                  title="0 = свободнее, 100 = максимально буквально следует пользовательской инструкции"
                />
                <span className="min-w-[5.5rem] text-right">{strictnessLabel} · {instructionStrictness}</span>
              </div>
              <div className="min-w-[19rem] max-w-full">
                <ActionModeToggle
                  actionMode={actionMode}
                  onChange={applyActionMode}
                  disabled={sending || pending || actionBusy}
                />
              </div>
            </div>

            <Field label="Сообщение для AI" hint="Enter — новая строка, Ctrl/Cmd+Enter — отправить. AI видит память сессии и может продолжать прошлую линию диалога без повторного описания контекста.">
              <Textarea
                value={message}
                onChange={(e) => setMessage(e.target.value)}
                rows={5}
                placeholder="Напиши, что нужно сделать AI…"
                disabled={sending || pending || actionBusy}
                onKeyDown={(e) => {
                  if ((e.ctrlKey || e.metaKey) && e.key === 'Enter') {
                    e.preventDefault();
                    onSend();
                  }
                }}
              />
            </Field>

            <div className="flex flex-col gap-3 md:flex-row md:items-center md:justify-between">
              <div className="flex flex-wrap items-center gap-2">
                {pending ? <Badge variant="outline">Ждём реальный AI worker…</Badge> : null}
                {developerView ? <Badge variant="success">dev sandbox</Badge> : null}
                {session?.courseTitle ? <Badge variant="success"><CheckCircle2 size={12} /> Курс задан</Badge> : null}
                {!session?.courseTitle ? <Badge variant="outline">Курс не выбран</Badge> : null}
                {actionBusy ? <Badge variant="outline">Выполняю действие…</Badge> : null}
                {session?.memory?.messageCount ? <Badge variant="outline">память: {session.memory.messageCount} сообщений</Badge> : null}
              </div>

              <Button type="button" onClick={() => onSend()} disabled={sending || pending || actionBusy || (!message.trim() && files.length === 0)}>
                {sending ? <LoaderCircle size={16} className="animate-spin" /> : <Send size={16} />} Отправить
              </Button>
            </div>
          </div>
        </Card>
      </div>
    </Layout>
  );
}
