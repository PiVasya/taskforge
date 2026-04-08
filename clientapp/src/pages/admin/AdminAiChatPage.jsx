import React, { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { Link } from 'react-router-dom';
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
import { getCourses } from '../../api/courses';
import { handleApiError } from '../../utils/handleApiError';
import { useNotify } from '../../components/notify/NotifyProvider';
import {
  Bot,
  CheckCircle2,
  Download,
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
} from 'lucide-react';

const SUGGESTIONS = [
  'Сделай batch на 7 задач по массивам для выбранного курса.',
  'Используй прикреплённый файл и создай 3 code-test задания.',
  'Проверь последний draft и запусти self-check.',
  'Опубликуй последний approved draft в выбранный курс.',
  'Запусти AI-review последней попытки пользователя по курсу.',
  'Сделай risk-review пользователя с последними попытками и support-данными.',
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

function normalizeToolCalls(message) {
  if (Array.isArray(message?.toolCalls) && message.toolCalls.length > 0) return message.toolCalls;
  return message?.toolCall ? [message.toolCall] : [];
}

function normalizeToolResults(message) {
  if (Array.isArray(message?.toolResults) && message.toolResults.length > 0) return message.toolResults;
  return message?.toolResult ? [message.toolResult] : [];
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

function MemoryPanel({ memory, courseTitle }) {
  const facts = uniqueStrings(memory?.facts);
  const goals = uniqueStrings(memory?.recentGoals);
  const files = uniqueStrings(memory?.recentFiles);
  const actions = uniqueStrings(memory?.recentActions);
  const hasMemory = Boolean(memory?.messageCount || facts.length || goals.length || files.length || actions.length || memory?.summary);

  return (
    <div className="rounded-3xl border border-neutral-200/70 dark:border-neutral-800 bg-[rgb(var(--card))] p-4">
      <div className="flex flex-wrap items-center gap-2">
        <Badge variant="outline">Память сессии</Badge>
        {memory?.messageCount ? <Badge variant="success">{memory.messageCount} сообщений</Badge> : null}
        {courseTitle ? <Badge variant="outline">курс: {courseTitle}</Badge> : null}
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

function ToolResultCard({ sessionId, result, onConfirm, actionBusy }) {
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
      {result?.summary ? <div className="mt-2 opacity-90 whitespace-pre-wrap">{result.summary}</div> : null}
      <div className="mt-3 flex flex-wrap gap-2">
        {result?.jobId ? <Badge variant="outline">job: {String(result.jobId).slice(0, 8)}</Badge> : null}
        {result?.batchId ? <Badge variant="outline">batch: {String(result.batchId).slice(0, 8)}</Badge> : null}
        {result?.draftId ? <Badge variant="outline">draft: {String(result.draftId).slice(0, 8)}</Badge> : null}
        {result?.assignmentId ? <Badge variant="outline">assignment: {String(result.assignmentId).slice(0, 8)}</Badge> : null}
      </div>
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

function MessageBubble({ sessionId, message, onConfirm, actionBusy }) {
  const isAssistant = String(message?.role || '').toLowerCase() === 'assistant';
  const attachments = Array.isArray(message?.attachments) ? message.attachments : [];
  const toolCalls = normalizeToolCalls(message);
  const toolResults = normalizeToolResults(message);

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

        {toolCalls.map((tool, index) => (
          <div key={`${tool.name || 'tool'}-${index}`} className="mt-3 rounded-2xl border border-dashed border-[rgba(var(--accent)/0.35)] px-3 py-2 text-xs opacity-80">
            <div className="flex items-center gap-2 font-medium"><Wrench size={14} /> Действие: {tool.name}</div>
            {tool.reason ? <div className="mt-1">{tool.reason}</div> : null}
          </div>
        ))}

        {toolResults.map((result, index) => (
          <ToolResultCard
            key={`${result.status || 'result'}-${index}`}
            sessionId={sessionId}
            result={result}
            onConfirm={onConfirm}
            actionBusy={actionBusy}
          />
        ))}
      </div>
    </div>
  );
}

export default function AdminAiChatPage() {
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
  const fileInputRef = useRef(null);
  const listRef = useRef(null);

  const pending = useMemo(
    () => Array.isArray(session?.messages) && session.messages.some((x) => x.role === 'assistant' && x.status === 'processing'),
    [session],
  );

  const upsertSessionListItem = useCallback((full) => ({
    id: full.id,
    courseId: full.courseId,
    courseTitle: full.courseTitle,
    title: full.title,
    lastMessagePreview: full.messages?.[full.messages.length - 1]?.content || null,
    memorySummary: full.memory?.summary || null,
    messageCount: full.memory?.messageCount || full.messages?.length || 0,
    isPending: full.messages?.some((x) => x.role === 'assistant' && x.status === 'processing') || false,
    createdAtUtc: full.createdAtUtc,
    updatedAtUtc: full.updatedAtUtc,
  }), []);

  const loadSessions = useCallback(async (preferredId) => {
    const list = await getAiChatSessions();
    setSessions(list);
    const nextId = preferredId || sessionId || list[0]?.id || null;
    if (nextId) {
      setSessionId(nextId);
      const full = await getAiChatSession(nextId);
      setSession(full);
    } else {
      setSessionId(null);
      setSession(null);
    }
  }, [sessionId]);

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

  useEffect(() => {
    const node = listRef.current;
    if (!node) return;
    node.scrollTop = node.scrollHeight;
  }, [session?.messages?.length, pending]);

  const createSession = useCallback(async () => {
    const created = await createAiChatSession({
      courseId: selectedCourseId || null,
      title: selectedCourseId ? `AI чат · ${courses.find((x) => String(x.id) === String(selectedCourseId))?.title || 'курс'}` : undefined,
    });
    setSessionId(created.id);
    setSession(created);
    setSessions((prev) => [upsertSessionListItem(created), ...prev]);
    return created;
  }, [courses, selectedCourseId, upsertSessionListItem]);

  const refreshCurrent = useCallback(async () => {
    if (!sessionId) return;
    try {
      setRefreshing(true);
      const full = await getAiChatSession(sessionId);
      setSession(full);
      await loadSessions(sessionId);
    } catch (e) {
      notify.error(handleApiError(e, 'Не удалось обновить чат'));
    } finally {
      setRefreshing(false);
    }
  }, [loadSessions, notify, sessionId]);

  const syncCurrentCourse = useCallback(async (nextCourseId) => {
    if (!sessionId) return;
    try {
      const updated = await updateAiChatSession(sessionId, {
        title: session?.title,
        courseId: nextCourseId || null,
      });
      setSession(updated);
      setSessions((prev) => prev.map((item) => (item.id === updated.id ? upsertSessionListItem(updated) : item)));
      notify.success(nextCourseId ? 'Курс для чата обновлён' : 'Привязка к курсу снята');
    } catch (e) {
      notify.error(handleApiError(e, 'Не удалось изменить курс чата'));
    }
  }, [notify, session?.title, sessionId, upsertSessionListItem]);

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
  }, [createSession, files, message, notify, sessionId, upsertSessionListItem]);

  const onConfirmTool = useCallback(async (result) => {
    if (!sessionId || !result?.confirmationToolCall) return;
    try {
      setActionBusy(true);
      const response = await confirmAiChatTool(sessionId, {
        toolName: result.confirmationToolCall.name,
        argumentsJson: result.confirmationToolCall.argumentsJson,
        note: result.suggestedConfirmationMessage || `Подтверждаю выполнение действия ${result.confirmationToolCall.name}.`,
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

  const renameCurrent = async () => {
    if (!sessionId) return;
    const nextTitle = window.prompt('Новое название чата', session?.title || '');
    if (!nextTitle || !nextTitle.trim()) return;
    try {
      const updated = await updateAiChatSession(sessionId, { title: nextTitle.trim(), courseId: session?.courseId || null });
      setSession(updated);
      setSessions((prev) => prev.map((item) => (item.id === updated.id ? upsertSessionListItem(updated) : item)));
      notify.success('Название чата обновлено');
    } catch (e) {
      notify.error(handleApiError(e, 'Не удалось переименовать чат'));
    }
  };

  const deleteCurrent = async () => {
    if (!sessionId) return;
    if (!window.confirm('Удалить текущий AI-чат?')) return;
    try {
      await deleteAiChatSession(sessionId);
      const nextList = sessions.filter((item) => item.id !== sessionId);
      setSessions(nextList);
      const nextId = nextList[0]?.id || null;
      setSessionId(nextId);
      if (nextId) {
        const full = await getAiChatSession(nextId);
        setSession(full);
      } else {
        setSession(null);
      }
      notify.success('Чат удалён');
    } catch (e) {
      notify.error(handleApiError(e, 'Не удалось удалить чат'));
    }
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

  const currentMessages = Array.isArray(session?.messages) ? session.messages : [];

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
              <button
                key={item.id}
                type="button"
                onClick={async () => {
                  setSessionId(item.id);
                  const full = await getAiChatSession(item.id);
                  setSession(full);
                }}
                className={`rounded-2xl border px-3 py-3 text-left transition ${sessionId === item.id
                  ? 'border-[rgb(var(--accent))] bg-[rgba(var(--accent)/0.12)]'
                  : 'border-neutral-200/70 dark:border-neutral-800 hover:border-[rgba(var(--accent)/0.28)]'}`}
              >
                <div className="flex items-center justify-between gap-2">
                  <div className="font-medium line-clamp-1">{item.title || 'Новый AI-чат'}</div>
                  <div className="flex items-center gap-2">
                    {item.messageCount ? <Badge variant="outline">{item.messageCount}</Badge> : null}
                    {item.isPending ? <Badge variant="outline">AI думает</Badge> : null}
                  </div>
                </div>
                {item.courseTitle ? <div className="mt-1 text-xs opacity-60 line-clamp-1">{item.courseTitle}</div> : null}
                {item.lastMessagePreview ? <div className="mt-1 text-xs opacity-70 line-clamp-2">{item.lastMessagePreview}</div> : null}
                {!item.lastMessagePreview && item.memorySummary ? <div className="mt-1 text-xs opacity-60 line-clamp-2">{item.memorySummary}</div> : null}
                <div className="mt-2 text-[11px] opacity-45">{formatDate(item.updatedAtUtc)}</div>
              </button>
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
              <Button type="button" variant="outline" onClick={() => exportCurrent('md')} disabled={!sessionId || pending || actionBusy || exporting}>
                {exporting ? <LoaderCircle size={16} className="animate-spin" /> : <Download size={16} />} Экспорт
              </Button>
              <Button type="button" variant="outline" onClick={renameCurrent} disabled={!sessionId || pending || actionBusy}>
                <Pencil size={16} />
              </Button>
              <Button type="button" variant="outline" onClick={deleteCurrent} disabled={!sessionId || pending || actionBusy} className="text-red-500">
                <Trash2 size={16} />
              </Button>
              <Button type="button" variant="outline" onClick={refreshCurrent} disabled={!sessionId || refreshing}>
                <RefreshCcw size={16} className={refreshing ? 'animate-spin' : ''} />
              </Button>
            </div>
          </div>

          <div className="px-5 py-4 border-b border-neutral-200/50 dark:border-neutral-800/80 bg-[rgba(var(--accent)/0.03)] space-y-3">
            <div className="grid gap-3 lg:grid-cols-[minmax(0,1fr)_280px]">
              <div>
                <div className="text-xs uppercase tracking-[0.18em] opacity-60">Быстрые идеи</div>
                <div className="mt-2 flex flex-wrap gap-2">
                  {SUGGESTIONS.map((item) => (
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
              <Field label="Курс текущего чата" hint="Можно менять на лету — AI начнёт использовать новый контекст курса в следующих ходах.">
                <Select value={session?.courseId || ''} onChange={(e) => syncCurrentCourse(e.target.value)} disabled={!sessionId || pending || actionBusy}>
                  <option value="">Без привязки к курсу</option>
                  {courses.map((course) => (
                    <option key={course.id} value={course.id}>{course.title}</option>
                  ))}
                </Select>
              </Field>
            </div>

            <MemoryPanel memory={session?.memory} courseTitle={session?.courseTitle} />
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
                    Здесь можно прикреплять файлы, ставить batch в очередь, валидировать и публиковать draft’ы, запускать AI-review попыток и risk-review пользователей. Чат работает через MinIO-вложения, реальный AI worker и backend tool-calls, а память сессии сохраняет прошлые цели, файлы и действия.
                  </div>
                </div>
              </div>
            ) : currentMessages.map((item) => (
              <MessageBubble
                key={item.id || `${item.role}-${item.createdAtUtc}`}
                sessionId={sessionId}
                message={item}
                onConfirm={onConfirmTool}
                actionBusy={actionBusy}
              />
            ))}
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

            <div className="flex flex-wrap gap-2">
              <Button type="button" variant="outline" disabled={sending || pending || actionBusy} onClick={() => setMessage(CONTINUE_MESSAGE)}>
                Продолжай по памяти
              </Button>
              <Button type="button" variant="outline" disabled={sending || pending || actionBusy} onClick={() => setMessage(GENERATE_MESSAGE)}>
                Начать генерацию
              </Button>
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
