import React, { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { useSearchParams } from 'react-router-dom';
import {
  Bot,
  ClipboardCopy,
  ChevronLeft,
  ChevronRight,
  GitCompare,
  Loader2,
  PanelRightOpen,
  Plus,
  RefreshCw,
} from 'lucide-react';
import AppErrorPanel from '../../components/AppErrorPanel';
import { Button, Badge } from '../../components/ui';
import { useAuth } from '../../auth/AuthContext';
import { useNotify } from '../../components/notify/NotifyProvider';
import { handleApiError } from '../../utils/handleApiError';
import { buildConversationTitle } from './agentModel';
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
} from '../../api/agent';
import { joinAgentConversation, leaveAgentConversation } from '../../realtime/agentHub';
import AgentComposer from './components/AgentComposer';
import {
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
} from './components/AgentViews';

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
  const [loadingList, setLoadingList] = useState(true);
  const [loadingConversation, setLoadingConversation] = useState(false);
  const [sending, setSending] = useState(false);
  const [error, setError] = useState(null);
  const [logsOpen, setLogsOpen] = useState(false);
  const [patchesOpen, setPatchesOpen] = useState(false);
  const [selectedPatchKey, setSelectedPatchKey] = useState(null);
  const [copyingDebugDump, setCopyingDebugDump] = useState(false);
  const [sidebarOpen, setSidebarOpen] = useState(true);
  const [realtimeEvents, setRealtimeEvents] = useState([]);
  const [polishingTasks, setPolishingTasks] = useState({});
  const [applyingArtifacts, setApplyingArtifacts] = useState({});
  const [artifactApplyResults, setArtifactApplyResults] = useState({});
  const [selectedDraftTasks, setSelectedDraftTasks] = useState({});
  const [uploadingFiles, setUploadingFiles] = useState(false);

  const bottomRef = useRef(null);
  const composerRef = useRef(null);
  const selectedIdRef = useRef(null);
  const sendingRef = useRef(false);

  const activeRun = useMemo(() => getLatestActiveRun(runs), [runs]);
  const latestRun = useMemo(() => getLatestRun(runs), [runs]);
  const patchSets = useMemo(() => collectPatchSets(messages, runs), [messages, runs]);
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
      return false;
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
            setRuns((prev) => {
              if (payload.run) return sortRunsDesc(upsertById(prev, payload.run));
              return prev.map((run) => (
                normalizeId(run.id) === normalizeId(payload.runId)
                  ? { ...run, status: payload.status || (type === 'run.failed' ? 'failed' : 'completed'), result: payload.result || run.result, error: payload.error || run.error, finishedAtUtc: nowIso() }
                  : run
              ));
            });
            refreshConversations({ silent: true });
            if (selectedIdRef.current) loadConversation(selectedIdRef.current, { silent: true });
          }
        };

        conn.on('AgentEvent', handler);
      } catch {
        
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

  const sendText = useCallback(async ({ text: requestedText = '', files = [] } = {}) => {
    const value = safeText(requestedText);
    const filesToUpload = [...files];
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
          title: buildConversationTitle(value),
          mode: 'course-assistant',
        });
        const conv = created?.conversation;
        if (!conv?.id) throw new Error('Сервис AI не вернул id чата.');
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
      setTimeout(() => scrollToBottom(), 0);

      const res = await sendAgentMessage(targetId, { text: value, clientMessageId, attachments: uploadedAttachments });
      if (res?.message) {
        setMessages((prev) => sortByTimeAsc(upsertMessage(prev, res.message)));
      }
      if (res?.run) setRuns((prev) => sortRunsDesc(upsertById(prev, res.run)));
      refreshConversations({ silent: true });
      return true;
    } catch (err) {
      const parsed = handleApiError(err, notify, 'Не удалось отправить сообщение AI');
      setError(parsed);
      return false;
    } finally {
      sendingRef.current = false;
      setSending(false);
      setUploadingFiles(false);
    }
  }, [assignmentId, courseId, notify, refreshConversations, scrollToBottom, selectedId, supportTicketId]);

  const stopActiveRun = async () => {
    if (!activeRun?.id) return;
    try {
      await cancelAgentRun(activeRun.id, 'user_requested');
      await loadConversation(selectedId, { silent: true });
      notify.info('Обработка остановлена');
    } catch (err) {
      handleApiError(err, notify, 'Не удалось остановить обработку');
    }
  };


  const handleApplyArtifact = async ({ message, persistedArtifact, artifactIndex, dryRun }) => {
    const artifactId = persistedArtifact?.id;
    const runId = persistedArtifact?.runId || message?.runId;
    if (!artifactId || !runId) {
      notify.warn('Материал ещё не готов к применению. Обнови чат после завершения обработки.');
      return;
    }

    const key = getArtifactStableKey({ persistedArtifact, message, artifactIndex });
    setApplyingArtifacts((prev) => ({ ...prev, [key]: dryRun ? 'check' : 'apply' }));
    try {
      const result = await applyAgentRunArtifact(runId, artifactId, {
        dryRun: !!dryRun,
        note: dryRun ? 'Пользователь запустил безопасную проверку предложения из интерфейса.' : 'Пользователь подтвердил применение предложения из интерфейса.',
      });
      setArtifactApplyResults((prev) => ({ ...prev, [key]: result }));
      if (result?.ok) {
        notify.success(dryRun ? 'Предложение проверено без сохранения' : 'Предложение применено');
        if (!dryRun && selectedId) await loadConversation(selectedId, { silent: true });
      } else {
        notify.warn(result?.message || 'Предложение не прошло проверку');
      }
    } catch (err) {
      const parsed = handleApiError(err, notify, dryRun ? 'Проверка предложения не прошла' : 'Не удалось применить предложение');
      setArtifactApplyResults((prev) => ({ ...prev, [key]: { ok: false, message: parsed?.message || 'Ошибка применения предложения' } }));
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
        note: 'Пользователь выбрал это задание для доработки и создания скрытого черновика.',
      });
      if (res?.message) setMessages((prev) => sortByTimeAsc(upsertMessage(prev, res.message)));
      if (res?.run) setRuns((prev) => sortRunsDesc(upsertById(prev, res.run)));
      notify.success('Ассистент начал дорабатывать задание и готовить скрытый черновик');
      setTimeout(() => scrollToBottom(), 0);
    } catch (err) {
      handleApiError(err, notify, 'Не удалось отправить задание на доработку');
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
      note: 'Пользователь выбрал это задание для доработки и создания скрытого черновика.',
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
        note: `Пакетная доработка ${chosen.length} заданий и создание скрытых черновиков.`,
        tasks: chosen.map(buildPolishPayload),
      });
      if (res?.message) setMessages((prev) => sortByTimeAsc(upsertMessage(prev, res.message)));
      if (Array.isArray(res?.runs)) setRuns((prev) => sortRunsDesc(mergeById(prev, res.runs)));
      setSelectedDraftTasks((prev) => {
        const next = { ...prev };
        keys.forEach((key) => delete next[key]);
        return next;
      });
      notify.success(`Ассистент поставил в очередь ${res?.count || chosen.length} черновиков`);
      setTimeout(() => scrollToBottom(), 0);
    } catch (err) {
      handleApiError(err, notify, 'Не удалось отправить выбранные задания на доработку');
    } finally {
      setPolishingTasks((prev) => {
        const next = { ...prev };
        keys.forEach((key) => { next[key] = false; });
        return next;
      });
    }
  };

  const copyAiDebugDump = useCallback(async () => {
    if (!selectedId) {
      notify.warn('Сначала открой AI-чат.');
      return;
    }

    const writeClipboard = async (text) => {
      if (navigator.clipboard?.writeText) {
        await navigator.clipboard.writeText(text);
        return;
      }

      const ta = document.createElement('textarea');
      ta.value = text;
      ta.setAttribute('readonly', 'readonly');
      ta.style.position = 'fixed';
      ta.style.opacity = '0';
      document.body.appendChild(ta);
      ta.select();
      document.execCommand('copy');
      document.body.removeChild(ta);
    };

    const buildClientDump = (backendDumpError = null) => ({
      generatedAtUtc: nowIso(),
      page: 'AgentPage',
      note: 'Этот отчёт можно целиком отправить разработчику/ассистенту для разбора поведения AI. Он содержит чат, run-ы, шаги, артефакты и события интерфейса.',
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
      pendingFiles: (composerRef.current?.getFiles?.() || []).map((file) => ({ name: file.name, size: file.size, type: file.type, lastModified: file.lastModified })),
      context: { courseId, assignmentId, supportTicketId },
      backendDumpError,
    });

    setCopyingDebugDump(true);
    try {
      const backendDump = await getAgentConversationDebugDump(selectedId, { format: 'text' });
      const textDump = [
        backendDump || 'BACKEND AI TRACE EMPTY',
        '',
        'CLIENT SIDE AI TRACE SNAPSHOT',
        '='.repeat(96),
        JSON.stringify(buildClientDump(), null, 2),
      ].join('\n');

      await writeClipboard(textDump);
      notify.success(`AI-отчёт скопирован (${Math.round(textDump.length / 1024)} KB).`);
    } catch (err) {
      const backendDumpError = {
        message: err?.message,
        status: err?.response?.status,
        data: err?.response?.data,
      };
      const textDump = [
        'BACKEND AI TRACE FAILED',
        '='.repeat(96),
        JSON.stringify(backendDumpError, null, 2),
        '',
        'CLIENT SIDE AI TRACE SNAPSHOT',
        '='.repeat(96),
        JSON.stringify(buildClientDump(backendDumpError), null, 2),
      ].join('\n');

      try {
        await writeClipboard(textDump);
        notify.warn(`Backend-отчёт не собрался, но локальный AI-отчёт скопирован (${Math.round(textDump.length / 1024)} KB).`);
      } catch {
        handleApiError(err, notify, 'Не удалось скопировать диагностику AI');
      }
    } finally {
      setCopyingDebugDump(false);
    }
  }, [activeRun, applyingArtifacts, artifactApplyResults, assignmentId, conversation, courseId, latestRun, messages, notify, polishingTasks, realtimeEvents, runs, selectedDraftTasks, selectedId, supportTicketId]);

  const contextBits = [
    courseId ? `курс ${String(courseId).slice(0, 8)}` : null,
    assignmentId ? `задание ${String(assignmentId).slice(0, 8)}` : null,
    supportTicketId ? `тикет ${String(supportTicketId).slice(0, 8)}` : null,
  ].filter(Boolean);

  return (
    <>
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
              {AI_DIAGNOSTICS_ENABLED && (
                <>
                  {patchSets.length > 0 && (
                    <button type="button" className="btn-outline !min-w-0" onClick={() => setPatchesOpen(true)} title="Открыть меню патчей курса">
                      <GitCompare size={16} />
                      <span className="hidden sm:inline">Патчи</span>
                      <span className="rounded-full bg-brand-600 px-1.5 py-0.5 text-[10px] text-white">{patchSets.length}</span>
                    </button>
                  )}
                  <button type="button" className="btn-outline !min-w-0" onClick={copyAiDebugDump} disabled={!selectedId || copyingDebugDump} title="Скопировать полный AI-отчёт">
                    {copyingDebugDump ? <Loader2 size={16} className="animate-spin" /> : <ClipboardCopy size={16} />}
                    <span className="hidden sm:inline">AI-отчёт</span>
                  </button>
                  <button type="button" className="btn-outline !min-w-0" onClick={() => setLogsOpen(true)} title="Открыть подробный журнал AI">
                    <PanelRightOpen size={16} />
                    <span className="hidden sm:inline">Логи</span>
                  </button>
                </>
              )}
            </div>
          </div>

          {error && <div className="p-4"><AppErrorPanel error={error} title="Проблема в AI-чате" /></div>}
          <div className="min-h-0 flex-1 overflow-y-auto p-3 sm:p-5 space-y-5">
            {loadingConversation ? (
              <div className="h-full grid place-items-center text-neutral-500">
                <div className="flex items-center gap-2"><Loader2 size={18} className="animate-spin" /> Загружаю AI-чат…</div>
              </div>
            ) : messages.length === 0 && !activeRun ? (
              <EmptyChat onTemplate={(template) => composerRef.current?.setText(template)} />
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
                    onOpenPatchSet={(key) => { setSelectedPatchKey(key); setPatchesOpen(true); }}
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

          {selectedDraftCount > 0 ? (
            <div className="border-t border-neutral-200/70 dark:border-neutral-800/70 bg-[rgb(var(--card))]/95 px-3 pt-3">
              <div className="mx-auto flex max-w-5xl flex-wrap items-center justify-between gap-2 rounded-2xl border border-brand-200 bg-brand-50/70 px-3 py-2 text-sm dark:border-brand-900 dark:bg-brand-950/20">
                <div className="font-medium text-brand-800 dark:text-brand-200">Выбрано задач для черновиков: {selectedDraftCount}</div>
                <div className="flex flex-wrap items-center gap-2">
                  <button type="button" className="btn-outline !min-w-0 !px-3 !py-1.5 text-xs" onClick={() => setSelectedDraftTasks({})}>Очистить</button>
                  <Button type="button" className="!min-w-0 !py-1.5 text-xs" onClick={() => handlePolishSelectedTasks()}>
                    Создать черновики выбранных
                  </Button>
                </div>
              </div>
            </div>
          ) : null}
          <AgentComposer
            ref={composerRef}
            sending={sending}
            uploading={uploadingFiles}
            activeRun={Boolean(activeRun)}
            onSend={sendText}
            onStop={stopActiveRun}
          />
        </section>
      </div>

      <PatchSetDrawer
        open={patchesOpen}
        onClose={() => setPatchesOpen(false)}
        patchSets={selectedPatchKey ? [...patchSets].sort((a, b) => (a.key === selectedPatchKey ? -1 : b.key === selectedPatchKey ? 1 : 0)) : patchSets}
        onApplyPatchSet={({ artifact, persistedArtifact, dryRun }) => handleApplyArtifact({ message: { runId: artifact?.runId || artifact?.RunId }, artifact, persistedArtifact, artifactIndex: 0, dryRun })}
        applyingArtifacts={applyingArtifacts}
        artifactApplyResults={artifactApplyResults}
      />

      {AI_DIAGNOSTICS_ENABLED && (
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
      )}
    </>
  );
}
