import React, { useCallback, useEffect, useMemo, useState } from 'react';
import Layout from '../../components/Layout';
import { Badge, Button, Card, Field, Input, Select, Textarea } from '../../components/ui';
import {
  generateAiBatch,
  getAiBatch,
  getAiBatches,
  getAiDrafts,
  getAiJob,
  getAiJobs,
  publishAiDraft,
  reviewAiDraft,
  validateAiDraft,
  deleteAiDraft,
  deleteAiBatch,
  deleteAiJob,
  clearAiJobs,
  retryAiJob,
  cancelAiJob,
} from '../../api/aiAdmin';
import { createCourse, getCourses } from '../../api/courses';
import { handleApiError } from '../../utils/handleApiError';
import { useNotify } from '../../components/notify/NotifyProvider';
import {
  AlertTriangle,
  Brain,
  Check,
  ChevronDown,
  ChevronUp,
  Eye,
  FileStack,
  FolderPlus,
  LayoutDashboard,
  ListTodo,
  RefreshCcw,
  Sparkles,
  StopCircle,
  Trash2,
  Wand2,
  X,
} from 'lucide-react';

/* ── Tabs ───────────────────────────────────────── */
const AI_TABS = [
  { key: 'overview', label: 'Обзор', icon: LayoutDashboard },
  { key: 'create', label: 'Новый пакет', icon: Wand2 },
  { key: 'queue', label: 'Очередь', icon: ListTodo },
  { key: 'batches', label: 'Пакеты', icon: Sparkles },
  { key: 'drafts', label: 'Черновики', icon: FileStack },
];

/* ── Defaults ───────────────────────────────────── */
const batchEmpty = {
  courseId: '',
  assignmentType: 'code-test',
  prompt:
    'Собери пакет заданий для нового Foundry batch pipeline. Нужны логичные шаги, градация сложности и пригодность для текущего редактора TaskForge.',
  count: 5,
  mode: 'topic-pack',
  difficulty: 2,
  notes: '',
  priority: 20,
};

const courseCreateEmpty = {
  title: 'Новый курс',
  description: '',
  isPublic: false,
};

/* ── Helpers ─────────────────────────────────────── */
function statusTone(status) {
  switch ((status || '').toLowerCase()) {
    case 'done':
    case 'ready':
    case 'published':
    case 'approved':
      return 'success';
    case 'fallback-review':
      return 'outline';
    case 'failed':
    case 'error':
    case 'rejected':
      return 'danger';
    case 'running':
    case 'processing':
      return 'outline';
    default:
      return 'secondary';
  }
}

function statusLabel(status) {
  const s = (status || '').toLowerCase();
  const map = {
    pending: 'Ожидает',
    running: 'Выполняется',
    processing: 'Обработка',
    done: 'Готово',
    ready: 'Готов',
    published: 'Опубликован',
    approved: 'Одобрен',
    rejected: 'Отклонён',
    failed: 'Ошибка',
    error: 'Ошибка',
    draft: 'Черновик',
    reviewed: 'Проверен',
    'fallback-review': 'Fallback — перепроверить',
  };
  return map[s] || status || '—';
}

function prettyJson(value) {
  if (!value) return '';
  try {
    return JSON.stringify(JSON.parse(value), null, 2);
  } catch (e) {
    return value;
  }
}

function tryParse(value) {
  try {
    return typeof value === 'string' ? JSON.parse(value) : value;
  } catch (e) {
    return null;
  }
}

function extractSelfCheck(draftJson) {
  var parsed = tryParse(draftJson);
  return (parsed && (parsed.meta ? parsed.meta.selfCheck : null)) || (parsed ? parsed.selfCheck : null) || null;
}

function isFallbackDraft(draft) {
  if (!draft) return false;
  var parsed = tryParse(draft.draftJson);
  var title = String((parsed && parsed.title) || draft.title || '').toLowerCase();
  var tags = String((parsed && parsed.tags) || '').toLowerCase();
  var meta = parsed && parsed.meta ? parsed.meta : null;
  var source = String((meta && (meta.generationSource || meta.source)) || '').toLowerCase();
  return title.indexOf('ai fallback') === 0 || tags.indexOf('fallback') >= 0 || source === 'fallback';
}

function selfCheckTone(status) {
  switch ((status || '').toLowerCase()) {
    case 'passed':
      return 'success';
    case 'failed':
      return 'danger';
    case 'needs-review':
      return 'outline';
    default:
      return 'secondary';
  }
}

/**
 * Определяет, можно ли опубликовать черновик.
 * Publish доступен если:
 * - draft не уже published
 * - draft одобрен (approved) ИЛИ selfCheck passed —
 *   если ни того ни другого, будет force publish
 */
function getPublishability(draft) {
  if (!draft) return { can: false, reason: 'Нет черновика', force: false };
  var st = (draft.status || '').toLowerCase();
  if (st === 'published') return { can: false, reason: 'Уже опубликован', force: false };

  var selfCheck = extractSelfCheck(draft.draftJson);
  var selfCheckPassed = selfCheck && String(selfCheck.status || '').toLowerCase() === 'passed';
  var isApproved = st === 'approved';
  var fallbackDraft = isFallbackDraft(draft);

  if (fallbackDraft && !isApproved)
    return { can: false, reason: 'Fallback-черновик: сначала перегенерируй или одобри вручную', force: false };

  if (isApproved && selfCheckPassed)
    return { can: true, reason: 'Одобрен + self-check пройден', force: false };
  if (selfCheckPassed) return { can: true, reason: 'Self-check пройден', force: false };
  if (isApproved)
    return { can: true, reason: fallbackDraft ? 'Одобрен fallback-черновик — force' : 'Одобрен (self-check не пройден — force)', force: true };

  return { can: true, reason: 'Не проверен — будет force-публикация', force: true };
}

/** Вытаскивает человекочитаемую информацию из DraftJson */
function extractDraftPreview(draftJson) {
  var parsed = tryParse(draftJson);
  if (!parsed) return null;

  var info = {};
  info.title = parsed.title || '';
  info.difficulty = parsed.difficulty;
  info.description = (parsed.description || '').replace(/<[^>]*>/g, '').slice(0, 300);
  info.assignmentType = parsed.assignmentType;
  info.tags = parsed.tags;

  // code-test specifics
  if (parsed.publicTests) info.publicTests = Array.isArray(parsed.publicTests) ? parsed.publicTests.length : 0;
  if (parsed.hiddenTests) info.hiddenTests = Array.isArray(parsed.hiddenTests) ? parsed.hiddenTests.length : 0;
  if (parsed.allowedLanguages)
    info.languages = Array.isArray(parsed.allowedLanguages) ? parsed.allowedLanguages.join(', ') : parsed.allowedLanguages;
  if (parsed.forbiddenCalls)
    info.forbiddenCalls = Array.isArray(parsed.forbiddenCalls) ? parsed.forbiddenCalls.length : 0;
  var placement = extractPlacementSuggestion(draftJson);
  if (placement) {
    info.placementAfterAssignmentId = placement.afterAssignmentId;
    info.placementAfterTitle = placement.afterAssignmentTitle;
    info.placementReason = placement.placementReason;
  }

  // math specifics
  if (parsed.blocks) info.blocks = Array.isArray(parsed.blocks) ? parsed.blocks.length : 0;

  // test specifics
  if (parsed.questions) info.questions = Array.isArray(parsed.questions) ? parsed.questions.length : 0;

  return info;
}


function extractPlacementSuggestion(draftJson) {
  var parsed = tryParse(draftJson);
  if (!parsed) return null;
  var placement = parsed.placement || (parsed.meta && parsed.meta.recommendedPlacement) || null;
  var afterAssignmentId =
    parsed.placementAfterAssignmentId ||
    parsed.afterAssignmentId ||
    (placement && (placement.afterAssignmentId || placement.placementAfterAssignmentId)) ||
    null;
  var afterAssignmentTitle =
    parsed.placementAfterTitle ||
    parsed.afterAssignmentTitle ||
    (placement && (placement.afterAssignmentTitle || placement.placementAfterTitle || placement.anchorTitle || placement.title)) ||
    null;
  var placementReason =
    parsed.placementReason ||
    (placement && (placement.reason || placement.placementReason)) ||
    null;
  if (!afterAssignmentId && !afterAssignmentTitle && !placementReason) return null;
  return {
    afterAssignmentId: afterAssignmentId || null,
    afterAssignmentTitle: afterAssignmentTitle || null,
    placementReason: placementReason || null,
  };
}

/* ── Small UI components ─────────────────────────── */
function TabButton({ active, icon: Icon, children, onClick, count }) {
  return (
    <button
      type="button"
      onClick={onClick}
      className={[
        'rounded-2xl border px-3 py-2 text-sm transition flex items-center gap-2',
        active
          ? 'border-[rgb(var(--accent))] bg-[rgba(var(--accent)/0.12)] text-[rgb(var(--accent))]'
          : 'border-neutral-200/70 dark:border-neutral-800 hover:border-[rgba(var(--accent)/0.3)]',
      ].join(' ')}
    >
      {Icon ? <Icon size={16} /> : null}
      <span>{children}</span>
      {count != null ? <span className="opacity-70">{count}</span> : null}
    </button>
  );
}

function SectionTitle({ icon: Icon, title, subtitle }) {
  return (
    <div className="flex flex-col gap-1 md:flex-row md:items-center md:justify-between">
      <div>
        <div className="font-medium flex items-center gap-2">
          <Icon size={18} /> {title}
        </div>
        {subtitle ? <div className="text-sm opacity-70 mt-1">{subtitle}</div> : null}
      </div>
    </div>
  );
}

function CompactStatCard({ title, value, hint }) {
  return (
    <Card className="p-4">
      <div className="text-xs uppercase tracking-[0.18em] opacity-60">{title}</div>
      <div className="mt-2 text-2xl font-semibold">{value}</div>
      {hint ? <div className="mt-1 text-sm opacity-70">{hint}</div> : null}
    </Card>
  );
}

function DraftPreviewCard({ draft }) {
  var preview = extractDraftPreview(draft && draft.draftJson);
  if (!preview) return null;

  return (
    <div className="rounded-xl bg-neutral-50 dark:bg-neutral-900/50 p-3 text-sm space-y-1">
      {preview.title ? <div className="font-medium">{preview.title}</div> : null}
      {preview.description ? <div className="opacity-80 line-clamp-3">{preview.description}</div> : null}
      <div className="flex flex-wrap gap-x-4 gap-y-1 text-xs opacity-70">
        {preview.difficulty != null ? <span>Сложность: {preview.difficulty}</span> : null}
        {preview.languages ? <span>Языки: {preview.languages}</span> : null}
        {preview.publicTests != null ? <span>Открытых тестов: {preview.publicTests}</span> : null}
        {preview.hiddenTests != null ? <span>Скрытых тестов: {preview.hiddenTests}</span> : null}
        {preview.blocks != null ? <span>Блоков: {preview.blocks}</span> : null}
        {preview.questions != null ? <span>Вопросов: {preview.questions}</span> : null}
        {preview.forbiddenCalls != null && preview.forbiddenCalls > 0 ? (
          <span>Запрещённых вызовов: {preview.forbiddenCalls}</span>
        ) : null}
        {preview.tags ? <span>Теги: {preview.tags}</span> : null}
        {preview.placementAfterTitle ? <span>После: {preview.placementAfterTitle}</span> : null}
      </div>
      {preview.placementReason ? <div className="text-xs opacity-70">Позиция: {preview.placementReason}</div> : null}
    </div>
  );
}

/* ════════════════════════════════════════════════════
   Main page component
   ════════════════════════════════════════════════════ */
export default function AdminAiPage() {
  var notify = useNotify();
  var [jobs, setJobs] = useState([]);
  var [batches, setBatches] = useState([]);
  var [drafts, setDrafts] = useState([]);
  var [courses, setCourses] = useState([]);
  var [selectedJob, setSelectedJob] = useState(null);
  var [selectedBatch, setSelectedBatch] = useState(null);
  var [selectedBatchItem, setSelectedBatchItem] = useState(null);
  var [loading, setLoading] = useState(true);
  var [busy, setBusy] = useState(false);
  var [pageError, setPageError] = useState('');
  var [activeTab, setActiveTab] = useState('overview');
  var [filters, setFilters] = useState({ status: '', type: '', query: '' });
  var [courseScopeId, setCourseScopeId] = useState('');
  var [showCreateCourse, setShowCreateCourse] = useState(false);
  var [courseCreateForm, setCourseCreateForm] = useState(courseCreateEmpty);
  var [batchForm, setBatchForm] = useState(batchEmpty);
  var [expandedDrafts, setExpandedDrafts] = useState({});

  var load = useCallback(
    async function () {
      try {
        setLoading(true);
        var results = await Promise.all([
          getAiJobs({ status: filters.status || undefined, type: filters.type || undefined, page: 1, pageSize: 100 }),
          getAiBatches(),
          getAiDrafts(),
          getCourses(),
        ]);
        setJobs(Array.isArray(results[0].items) ? results[0].items : []);
        setBatches(Array.isArray(results[1]) ? results[1] : []);
        setDrafts(Array.isArray(results[2]) ? results[2] : []);
        setCourses(Array.isArray(results[3]) ? results[3] : []);
        setPageError('');
      } catch (e) {
        var parsed = handleApiError(e, notify, 'Не удалось загрузить AI-раздел');
        setPageError((parsed && parsed.userMessage) || 'Не удалось загрузить AI-раздел');
      } finally {
        setLoading(false);
      }
    },
    [filters.status, filters.type, notify],
  );

  useEffect(
    function () {
      load();
    },
    [], // eslint-disable-line
  );

  useEffect(
    function () {
      if (!courseScopeId) return;
      setBatchForm(function (prev) {
        return Object.assign({}, prev, { courseId: courseScopeId });
      });
    },
    [courseScopeId],
  );

  var selectedCourse = useMemo(
    function () {
      return courses.find(function (c) { return String(c.id) === String(courseScopeId); }) || null;
    },
    [courses, courseScopeId],
  );

  var filteredJobs = useMemo(
    function () {
      var q = (filters.query || '').trim().toLowerCase();
      if (!q) return jobs;
      return jobs.filter(function (job) {
        return [job.id, job.type, job.status, job.targetEntityType, job.targetEntityId, job.workerId]
          .join(' ')
          .toLowerCase()
          .includes(q);
      });
    },
    [filters.query, jobs],
  );

  var filteredBatches = useMemo(
    function () {
      var q = (filters.query || '').trim().toLowerCase();
      if (!q) return batches;
      return batches.filter(function (batch) {
        return [batch.id, batch.status, batch.currentStage, batch.assignmentType, batch.mode, batch.prompt]
          .join(' ')
          .toLowerCase()
          .includes(q);
      });
    },
    [filters.query, batches],
  );


  var selectedBatchJobs = useMemo(
    function () {
      if (!selectedBatch || !selectedBatch.id) return [];
      var itemIds = (selectedBatch.items || []).map(function (item) { return String(item.id); });
      return jobs
        .filter(function (job) {
          var targetId = String(job.targetEntityId || '');
          return targetId === String(selectedBatch.id) || itemIds.indexOf(targetId) >= 0;
        })
        .slice()
        .sort(function (a, b) {
          return new Date(b.createdAtUtc || 0).getTime() - new Date(a.createdAtUtc || 0).getTime();
        });
    },
    [jobs, selectedBatch],
  );

  var selectedBatchActiveJob = useMemo(
    function () {
      return selectedBatchJobs.find(function (job) {
        var st = String(job.status || '').toLowerCase();
        return st === 'retry' || st === 'pending' || st === 'running' || st === 'processing';
      }) || null;
    },
    [selectedBatchJobs],
  );

  /* ── Counters ───────────────────────────────────── */
  var draftCounts = useMemo(
    function () {
      var publishable = 0;
      var approved = 0;
      var rejected = 0;
      var published = 0;
      var total = drafts.length;
      for (var i = 0; i < drafts.length; i++) {
        var st = (drafts[i].status || '').toLowerCase();
        if (st === 'published') published++;
        else if (st === 'approved') approved++;
        else if (st === 'rejected') rejected++;
        if (getPublishability(drafts[i]).can) publishable++;
      }
      return { publishable: publishable, approved: approved, rejected: rejected, published: published, total: total };
    },
    [drafts],
  );

  /* ── Actions ────────────────────────────────────── */
  var loadAndOpenBatch = async function (batchId) {
    await load();
    if (!batchId) return;
    try {
      var data = await getAiBatch(batchId);
      setSelectedBatch(data || null);
      setActiveTab('batches');
    } catch (e) {
      handleApiError(e, notify, 'Не удалось открыть пакет');
    }
  };

  var openJob = async function (id) {
    try {
      var data = await getAiJob(id);
      setSelectedJob(data || null);
      setActiveTab('queue');
    } catch (e) {
      handleApiError(e, notify, 'Не удалось открыть задание');
    }
  };

  var openBatch = async function (id) {
    try {
      var data = await getAiBatch(id);
      setSelectedBatch(data || null);
      setActiveTab('batches');
    } catch (e) {
      handleApiError(e, notify, 'Не удалось открыть пакет');
    }
  };

  var submitBatch = async function () {
    if (!batchForm.courseId) {
      notify.error('Сначала выбери курс');
      return;
    }
    try {
      setBusy(true);
      var created = await generateAiBatch({
        courseId: batchForm.courseId || null,
        assignmentType: batchForm.assignmentType,
        prompt: batchForm.prompt,
        count: Number(batchForm.count || 1),
        difficulty: Number(batchForm.difficulty || 2),
        mode: batchForm.mode,
        notes: batchForm.notes,
        priority: Number(batchForm.priority || 20),
      });
      notify.success('Пакет создан и поставлен в очередь');
      await loadAndOpenBatch(created && created.id);
    } catch (e) {
      handleApiError(e, notify, 'Не удалось создать пакет');
    } finally {
      setBusy(false);
    }
  };

  var reviewDraftNow = async function (id, action) {
    try {
      setBusy(true);
      await reviewAiDraft(id, action);
      notify.success(action === 'approve' ? 'Черновик одобрен' : 'Черновик отклонён');
      await load();
    } catch (e) {
      handleApiError(e, notify, 'Не удалось обновить статус черновика');
    } finally {
      setBusy(false);
    }
  };

  var publishDraftNow = async function (draft, forcePublish) {
    var courseId = draft.courseId || courseScopeId || null;
    if (!courseId) {
      notify.error('Не указан курс. Выбери курс на вкладке «Обзор» перед публикацией.');
      return;
    }
    try {
      setBusy(true);
      var data = await publishAiDraft(draft.id, {
        courseId: courseId,
        forceWithoutPassedSelfCheck: !!forcePublish,
      });
      var placementSuffix = data && data.placementApplied && data.placementAfterTitle ? ' · после «' + data.placementAfterTitle + '»' : '';
      notify.success('Черновик опубликован как задание: ' + ((data && data.assignmentId) || '') + placementSuffix);
      await load();
    } catch (e) {
      handleApiError(e, notify, 'Не удалось опубликовать черновик');
    } finally {
      setBusy(false);
    }
  };

  var validateDraftNow = async function (draft) {
    try {
      setBusy(true);
      var data = await validateAiDraft(draft.id, { usePythonSelfCheck: true });
      notify.success('Self-check запущен');
      await openJob(data && data.id);
    } catch (e) {
      handleApiError(e, notify, 'Не удалось запустить self-check');
    } finally {
      setBusy(false);
    }
  };

  var handleCreateCourse = async function () {
    try {
      setBusy(true);
      var created = await createCourse({
        title: courseCreateForm.title || 'Новый курс',
        description: courseCreateForm.description || '',
        isPublic: !!courseCreateForm.isPublic,
        visibleGroupIds: [],
        ownerIds: [],
      });
      notify.success('Курс создан');
      setCourseCreateForm(courseCreateEmpty);
      setShowCreateCourse(false);
      await load();
      if (created && created.id) {
        setCourseScopeId(created.id);
      }
    } catch (e) {
      handleApiError(e, notify, 'Не удалось создать курс');
    } finally {
      setBusy(false);
    }
  };

  var toggleDraftExpanded = function (id) {
    setExpandedDrafts(function (prev) {
      var next = Object.assign({}, prev);
      next[id] = !prev[id];
      return next;
    });
  };

  /* ── Delete actions ─────────────────────────────── */
  var deleteDraftNow = async function (id) {
    if (!window.confirm('Удалить этот черновик навсегда?')) return;
    try {
      setBusy(true);
      await deleteAiDraft(id);
      notify.success('Черновик удалён');
      await load();
    } catch (e) {
      handleApiError(e, notify, 'Не удалось удалить черновик');
    } finally {
      setBusy(false);
    }
  };

  var deleteBatchNow = async function (id) {
    if (!window.confirm('Удалить пакет и все его элементы/черновики навсегда?')) return;
    try {
      setBusy(true);
      await deleteAiBatch(id);
      notify.success('Пакет удалён');
      setSelectedBatch(null);
      await load();
    } catch (e) {
      handleApiError(e, notify, 'Не удалось удалить пакет');
    } finally {
      setBusy(false);
    }
  };

  var deleteJobNow = async function (id) {
    if (!window.confirm('Удалить это задание из очереди?')) return;
    try {
      setBusy(true);
      await deleteAiJob(id);
      notify.success('Задание удалено');
      if (selectedJob && selectedJob.id === id) setSelectedJob(null);
      await load();
    } catch (e) {
      handleApiError(e, notify, 'Не удалось удалить задание');
    } finally {
      setBusy(false);
    }
  };

  var clearJobsNow = async function (statusFilter) {
    var label = statusFilter || 'завершённые и упавшие';
    if (!window.confirm('Очистить все ' + label + ' задания? Это необратимо.')) return;
    try {
      setBusy(true);
      var result = await clearAiJobs(statusFilter || undefined);
      notify.success('Удалено заданий: ' + ((result && result.deleted) || 0));
      setSelectedJob(null);
      await load();
    } catch (e) {
      handleApiError(e, notify, 'Не удалось очистить задания');
    } finally {
      setBusy(false);
    }
  };

  var retryJobNow = async function (id) {
    try {
      setBusy(true);
      await retryAiJob(id);
      notify.success('Задание перезапущено');
      await load();
    } catch (e) {
      handleApiError(e, notify, 'Не удалось перезапустить задание');
    } finally {
      setBusy(false);
    }
  };

  var cancelJobNow = async function (id) {
    if (!window.confirm('Отменить это задание?')) return;
    try {
      setBusy(true);
      await cancelAiJob(id);
      notify.success('Задание отменено');
      await load();
    } catch (e) {
      handleApiError(e, notify, 'Не удалось отменить задание');
    } finally {
      setBusy(false);
    }
  };

  var publishBatchDrafts = async function (batch) {
    var courseId = batch.courseId || courseScopeId;
    if (!courseId) {
      notify.error('Сначала выбери курс на вкладке «Обзор»');
      return;
    }
    var batchDrafts = drafts.filter(function (d) {
      return d.batchId === batch.id && (d.status || '').toLowerCase() !== 'published' && !isFallbackDraft(d);
    });
    if (batchDrafts.length === 0) {
      notify.error('Нет неопубликованных черновиков в этом пакете');
      return;
    }
    if (!window.confirm('Опубликовать все ' + batchDrafts.length + ' черновиков из этого пакета?')) return;
    try {
      setBusy(true);
      var published = 0;
      var errors = 0;
      var batchDetails = selectedBatch && selectedBatch.id === batch.id ? selectedBatch : null;
      var draftOrder = {};
      ((batchDetails && batchDetails.items) || []).forEach(function (item) {
        if (item && item.draftId) draftOrder[item.draftId] = typeof item.index === 'number' ? item.index : 0;
      });
      batchDrafts.sort(function (a, b) {
        return (draftOrder[a.id] ?? 0) - (draftOrder[b.id] ?? 0);
      });
      var chainedAfterAssignments = {};
      for (var i = 0; i < batchDrafts.length; i++) {
        try {
          var placement = extractPlacementSuggestion(batchDrafts[i].draftJson);
          var baseAfterId = placement && placement.afterAssignmentId ? placement.afterAssignmentId : null;
          var effectiveAfterId = baseAfterId ? (chainedAfterAssignments[baseAfterId] || baseAfterId) : null;
          var publishedDraft = await publishAiDraft(batchDrafts[i].id, {
            courseId: courseId,
            afterAssignmentId: effectiveAfterId,
            forceWithoutPassedSelfCheck: true,
          });
          if (baseAfterId && publishedDraft && publishedDraft.assignmentId && publishedDraft.placementApplied) {
            chainedAfterAssignments[baseAfterId] = publishedDraft.assignmentId;
          }
          published++;
        } catch (e) {
          errors++;
        }
      }
      notify.success('Опубликовано: ' + published + (errors ? ', ошибок: ' + errors : ''));
      await load();
    } catch (e) {
      handleApiError(e, notify, 'Ошибка при публикации пакета');
    } finally {
      setBusy(false);
    }
  };

  /* ── Course scope selector ──────────────────────── */
  var renderCourseScopeCard = function () {
    return (
      <Card>
        <SectionTitle
          icon={FolderPlus}
          title="Курс"
          subtitle="Выбери курс, в который будут создаваться задания из пакета."
        />
        <div className="grid lg:grid-cols-[1fr,auto,auto] gap-3 mt-4 items-end">
          <Field label="Текущий курс">
            <Select
              value={courseScopeId}
              onChange={function (e) {
                setCourseScopeId(e.target.value);
              }}
            >
              <option value="">— не выбран —</option>
              {courses.map(function (course) {
                return (
                  <option key={course.id} value={course.id}>
                    {course.title || course.id}
                  </option>
                );
              })}
            </Select>
          </Field>
          <Button
            variant="outline"
            onClick={function () {
              setShowCreateCourse(function (prev) {
                return !prev;
              });
            }}
          >
            <FolderPlus size={16} />
            <span className="ml-1">{showCreateCourse ? 'Скрыть' : 'Создать курс'}</span>
          </Button>
          <Button variant="outline" onClick={load}>
            <RefreshCcw size={16} /> <span className="ml-1">Обновить</span>
          </Button>
        </div>
        {selectedCourse ? (
          <div className="mt-3 rounded-2xl border border-neutral-200/70 dark:border-neutral-800 p-4 text-sm space-y-1">
            <div className="font-medium">{selectedCourse.title || 'Без названия'}</div>
            <div className="opacity-70 break-all text-xs">{selectedCourse.id}</div>
            {selectedCourse.description ? <div className="opacity-80">{selectedCourse.description}</div> : null}
          </div>
        ) : null}
        {showCreateCourse ? (
          <div className="mt-4 rounded-2xl border border-dashed border-neutral-300 dark:border-neutral-700 p-4 grid md:grid-cols-2 gap-4">
            <Field label="Название курса">
              <Input
                value={courseCreateForm.title}
                onChange={function (e) {
                  setCourseCreateForm(function (prev) {
                    return Object.assign({}, prev, { title: e.target.value });
                  });
                }}
              />
            </Field>
            <label className="text-sm flex items-center gap-2 mt-7">
              <input
                type="checkbox"
                checked={!!courseCreateForm.isPublic}
                onChange={function (e) {
                  setCourseCreateForm(function (prev) {
                    return Object.assign({}, prev, { isPublic: e.target.checked });
                  });
                }}
              />{' '}
              Публичный
            </label>
            <div className="md:col-span-2">
              <Field label="Описание">
                <Textarea
                  rows={4}
                  value={courseCreateForm.description}
                  onChange={function (e) {
                    setCourseCreateForm(function (prev) {
                      return Object.assign({}, prev, { description: e.target.value });
                    });
                  }}
                />
              </Field>
            </div>
            <div className="md:col-span-2 flex gap-2 flex-wrap">
              <Button disabled={busy} onClick={handleCreateCourse}>
                Создать курс
              </Button>
              <Button
                variant="outline"
                onClick={function () {
                  setCourseCreateForm(courseCreateEmpty);
                }}
              >
                Сбросить
              </Button>
            </div>
          </div>
        ) : null}
      </Card>
    );
  };

  /* ── Overview ───────────────────────────────────── */
  var renderOverview = function () {
    return (
      <div className="space-y-6">
        {renderCourseScopeCard()}
        <div className="grid md:grid-cols-2 xl:grid-cols-4 gap-4">
          <CompactStatCard title="Всего jobs" value={jobs.length} hint="Все AI-задания в очереди" />
          <CompactStatCard title="Пакеты" value={batches.length} hint="Foundry batch-сущности" />
          <CompactStatCard
            title="Готовы к публикации"
            value={draftCounts.publishable}
            hint={'Одобрено: ' + draftCounts.approved + ' · Отклонено: ' + draftCounts.rejected}
          />
          <CompactStatCard
            title="Опубликовано"
            value={draftCounts.published}
            hint={'Всего черновиков: ' + draftCounts.total}
          />
        </div>
        <Card>
          <SectionTitle
            icon={Sparkles}
            title="Как это работает"
            subtitle="Batch pipeline: создай пакет → AI пройдёт все стадии → черновики появятся → одобри и опубликуй."
          />
          <div className="mt-4 text-sm opacity-80 space-y-2">
            <div>1. Выбери курс наверху</div>
            <div>2. Перейди на вкладку «Новый пакет» и создай batch</div>
            <div>3. Следи за прогрессом на «Пакеты» и «Очередь»</div>
            <div>4. Когда черновики готовы — одобри и опубликуй на «Черновики»</div>
          </div>
        </Card>
      </div>
    );
  };

  /* ── Create batch ───────────────────────────────── */
  var renderCreate = function () {
    return (
      <div className="space-y-6">
        {renderCourseScopeCard()}
        <Card>
          <SectionTitle
            icon={Wand2}
            title="Создать Foundry-пакет"
            subtitle="Форма создаёт batch-задание, которое AI обработает в несколько стадий."
          />
          <div className="grid md:grid-cols-2 gap-4 mt-4">
            <Field label="Курс (id)">
              <Input
                value={batchForm.courseId}
                onChange={function (e) {
                  setBatchForm(function (p) {
                    return Object.assign({}, p, { courseId: e.target.value });
                  });
                }}
                placeholder="выбери курс наверху"
              />
            </Field>
            <Field label="Тип задания">
              <Select
                value={batchForm.assignmentType}
                onChange={function (e) {
                  setBatchForm(function (p) {
                    return Object.assign({}, p, { assignmentType: e.target.value });
                  });
                }}
              >
                <option value="code-test">code-test (программирование)</option>
                <option value="math">math (математика/блоки)</option>
                <option value="test">test (тест/квиз)</option>
              </Select>
            </Field>
            <Field label="Режим">
              <Input
                value={batchForm.mode}
                onChange={function (e) {
                  setBatchForm(function (p) {
                    return Object.assign({}, p, { mode: e.target.value });
                  });
                }}
                placeholder="topic-pack"
              />
            </Field>
            <Field label="Сложность (1–5)">
              <Input
                type="number"
                min="1"
                max="5"
                value={batchForm.difficulty}
                onChange={function (e) {
                  setBatchForm(function (p) {
                    return Object.assign({}, p, { difficulty: e.target.value });
                  });
                }}
              />
            </Field>
            <Field label="Количество заданий">
              <Input
                type="number"
                min="1"
                max="50"
                value={batchForm.count}
                onChange={function (e) {
                  setBatchForm(function (p) {
                    return Object.assign({}, p, { count: e.target.value });
                  });
                }}
              />
            </Field>
            <Field label="Приоритет">
              <Input
                type="number"
                min="0"
                max="100"
                value={batchForm.priority}
                onChange={function (e) {
                  setBatchForm(function (p) {
                    return Object.assign({}, p, { priority: e.target.value });
                  });
                }}
              />
            </Field>
            <div className="md:col-span-2">
              <Field label="Промпт (что генерировать)">
                <Textarea
                  rows={6}
                  value={batchForm.prompt}
                  onChange={function (e) {
                    setBatchForm(function (p) {
                      return Object.assign({}, p, { prompt: e.target.value });
                    });
                  }}
                />
              </Field>
            </div>
            <div className="md:col-span-2">
              <Field label="Заметки (необязательно)">
                <Textarea
                  rows={3}
                  value={batchForm.notes}
                  onChange={function (e) {
                    setBatchForm(function (p) {
                      return Object.assign({}, p, { notes: e.target.value });
                    });
                  }}
                />
              </Field>
            </div>
          </div>
          <div className="mt-4 flex gap-2 flex-wrap items-center">
            <Button disabled={busy || !batchForm.courseId} onClick={submitBatch}>
              Создать пакет
            </Button>
            <Button
              variant="outline"
              onClick={function () {
                setBatchForm(Object.assign({}, batchEmpty, { courseId: courseScopeId || '' }));
              }}
            >
              Сбросить
            </Button>
            {!batchForm.courseId ? (
              <span className="text-sm text-amber-600 flex items-center gap-1">
                <AlertTriangle size={14} /> Сначала выбери курс
              </span>
            ) : null}
          </div>
        </Card>
      </div>
    );
  };

  /* ── Jobs queue ─────────────────────────────────── */
  var renderJobsQueue = function () {
    return (
      <div className="grid xl:grid-cols-[0.92fr,1.08fr] gap-6">
        <div className="space-y-4">
          <Card>
            <SectionTitle icon={ListTodo} title="Очередь AI-заданий" subtitle="Foundry-стадии и другие AI-задания." />
            <div className="grid md:grid-cols-3 gap-4 mt-4">
              <Field label="Статус">
                <Input
                  value={filters.status}
                  onChange={function (e) {
                    setFilters(function (p) {
                      return Object.assign({}, p, { status: e.target.value });
                    });
                  }}
                  placeholder="pending / running / done / failed"
                />
              </Field>
              <Field label="Тип">
                <Input
                  value={filters.type}
                  onChange={function (e) {
                    setFilters(function (p) {
                      return Object.assign({}, p, { type: e.target.value });
                    });
                  }}
                  placeholder="assignment_batch_plan / ..."
                />
              </Field>
              <Field label="Поиск">
                <Input
                  value={filters.query}
                  onChange={function (e) {
                    setFilters(function (p) {
                      return Object.assign({}, p, { query: e.target.value });
                    });
                  }}
                  placeholder="id, тип, worker..."
                />
              </Field>
            </div>
            <div className="mt-4 flex gap-2 flex-wrap items-center">
              <Button variant="outline" onClick={load}>
                <RefreshCcw size={14} /> Обновить
              </Button>
              <Button variant="outline" disabled={busy} onClick={function () { clearJobsNow(); }} className="text-red-600 dark:text-red-400">
                <Trash2 size={14} /> Очистить историю
              </Button>
              <Badge intent="secondary">показано: {filteredJobs.length}</Badge>
            </div>
          </Card>

          <div className="space-y-3 max-h-[72vh] overflow-auto pr-1">
            {loading ? (
              <Card className="text-center opacity-70 py-8">Загрузка…</Card>
            ) : (
              filteredJobs.map(function (job) {
                return (
                  <Card
                    key={job.id}
                    className={selectedJob && selectedJob.id === job.id ? 'ring-2 ring-brand-500/30' : ''}
                  >
                    <div className="flex flex-col gap-3 lg:flex-row lg:items-start lg:justify-between">
                      <div className="min-w-0">
                        <div className="font-medium break-all">{job.type}</div>
                        <div className="text-xs opacity-50 mt-1 break-all font-mono">{job.id}</div>
                        {job.targetEntityType ? (
                          <div className="text-sm opacity-80 mt-1">
                            {job.targetEntityType}{' '}
                            {job.targetEntityId ? (
                              <span className="font-mono text-xs">{job.targetEntityId}</span>
                            ) : null}
                          </div>
                        ) : null}
                        <div className="text-xs opacity-50 mt-1">
                          {job.createdAtUtc ? new Date(job.createdAtUtc).toLocaleString() : '—'}
                        </div>
                      </div>
                      <div className="flex flex-wrap gap-2 items-center">
                        <Badge intent={statusTone(job.status)}>{statusLabel(job.status)}</Badge>
                        <Button
                          variant="outline"
                          onClick={function () {
                            openJob(job.id);
                          }}
                        >
                          Открыть
                        </Button>
                        {(job.status === 'failed' || job.status === 'error') ? (
                          <Button
                            variant="outline"
                            disabled={busy}
                            onClick={function () {
                              retryJobNow(job.id);
                            }}
                            className="text-yellow-600 hover:bg-yellow-50 dark:hover:bg-yellow-950/30"
                          >
                            <RefreshCcw size={14} />
                          </Button>
                        ) : null}
                        {(job.status === 'pending' || job.status === 'running') ? (
                          <Button
                            variant="outline"
                            disabled={busy}
                            onClick={function () {
                              cancelJobNow(job.id);
                            }}
                            className="text-orange-600 hover:bg-orange-50 dark:hover:bg-orange-950/30"
                          >
                            <StopCircle size={14} />
                          </Button>
                        ) : null}
                        <Button
                          variant="outline"
                          disabled={busy}
                          onClick={function () {
                            deleteJobNow(job.id);
                          }}
                          className="text-red-500 hover:bg-red-50 dark:hover:bg-red-950/30"
                        >
                          <Trash2 size={14} />
                        </Button>
                      </div>
                    </div>
                  </Card>
                );
              })
            )}
            {!loading && filteredJobs.length === 0 ? (
              <Card className="text-center opacity-70 py-8">AI-заданий пока нет.</Card>
            ) : null}
          </div>
        </div>

        <Card>
          <SectionTitle icon={Brain} title="Детали задания" subtitle="Input, результат и статус обработки." />
          {!selectedJob ? (
            <div className="text-sm opacity-70 mt-4">Выбери задание слева.</div>
          ) : (
            <div className="space-y-3 text-sm mt-4">
              <div className="flex flex-wrap gap-2">
                <Badge intent={statusTone(selectedJob.status)}>{statusLabel(selectedJob.status)}</Badge>
                {selectedJob.modelName ? <Badge intent="secondary">{selectedJob.modelName}</Badge> : null}
                {selectedJob.workerId ? <Badge intent="secondary">{selectedJob.workerId}</Badge> : null}
                {(selectedJob.status === 'failed' || selectedJob.status === 'error') ? (
                  <Button
                    variant="outline"
                    disabled={busy}
                    onClick={function () {
                      retryJobNow(selectedJob.id);
                    }}
                    className="text-yellow-600 hover:bg-yellow-50 dark:hover:bg-yellow-950/30 ml-auto"
                  >
                    <RefreshCcw size={14} /> Перезапустить
                  </Button>
                ) : (selectedJob.status === 'pending' || selectedJob.status === 'running') ? (
                  <Button
                    variant="outline"
                    disabled={busy}
                    onClick={function () {
                      cancelJobNow(selectedJob.id);
                    }}
                    className="text-orange-600 hover:bg-orange-50 dark:hover:bg-orange-950/30 ml-auto"
                  >
                    <StopCircle size={14} /> Отменить
                  </Button>
                ) : <span className="ml-auto" />}
                <Button
                  variant="outline"
                  disabled={busy}
                  onClick={function () {
                    deleteJobNow(selectedJob.id);
                  }}
                  className="text-red-500 hover:bg-red-50 dark:hover:bg-red-950/30"
                >
                  <Trash2 size={14} /> Удалить
                </Button>
              </div>
              <div>
                <span className="opacity-70">Тип:</span> <span className="break-all">{selectedJob.type}</span>
              </div>
              <div>
                <span className="opacity-70">Цель:</span>{' '}
                <span className="break-all">
                  {selectedJob.targetEntityType || '—'} {selectedJob.targetEntityId || ''}
                </span>
              </div>
              <div>
                <span className="opacity-70">Создано:</span>{' '}
                {selectedJob.createdAtUtc ? new Date(selectedJob.createdAtUtc).toLocaleString() : '—'}
              </div>
              <div>
                <span className="opacity-70">Начато:</span>{' '}
                {selectedJob.startedAtUtc ? new Date(selectedJob.startedAtUtc).toLocaleString() : '—'}
              </div>
              <div>
                <span className="opacity-70">Завершено:</span>{' '}
                {selectedJob.completedAtUtc ? new Date(selectedJob.completedAtUtc).toLocaleString() : '—'}
              </div>
              {selectedJob.errorText ? (
                <div className="rounded-xl bg-red-50 dark:bg-red-950/30 border border-red-200/50 dark:border-red-800/30 p-3">
                  <div className="font-medium text-red-600 dark:text-red-400 flex items-center gap-1 mb-1">
                    <AlertTriangle size={14} /> Ошибка
                  </div>
                  <pre className="text-xs whitespace-pre-wrap break-all">{selectedJob.errorText}</pre>
                </div>
              ) : null}
              <Field label="InputJson">
                <Textarea rows={12} readOnly value={prettyJson(selectedJob.inputJson)} className="font-mono text-xs" />
              </Field>
              <Field label="ResultJson">
                <Textarea
                  rows={12}
                  readOnly
                  value={prettyJson(selectedJob.resultJson)}
                  className="font-mono text-xs"
                />
              </Field>
              {selectedJob.artifacts && selectedJob.artifacts.length > 0 ? (
                <div className="space-y-2">
                  <div className="font-medium">Артефакты ({selectedJob.artifacts.length})</div>
                  {selectedJob.artifacts.map(function (art) {
                    return (
                      <details key={art.id} className="rounded-xl border p-3 text-xs">
                        <summary className="cursor-pointer flex items-center gap-2">
                          <Badge intent={art.status === 'ok' || art.status === 'done' ? 'success' : art.status === 'error' ? 'danger' : 'secondary'}>
                            {art.status || '—'}
                          </Badge>
                          <span className="font-medium">{art.stageCode || art.artifactType}</span>
                          {art.modelName ? <span className="opacity-50">({art.modelName})</span> : null}
                          <span className="opacity-50 ml-auto">{art.createdAtUtc ? new Date(art.createdAtUtc).toLocaleString() : ''}</span>
                        </summary>
                        <pre className="mt-2 whitespace-pre-wrap break-all max-h-[300px] overflow-auto font-mono">
                          {prettyJson(art.payloadJson)}
                        </pre>
                      </details>
                    );
                  })}
                </div>
              ) : null}
            </div>
          )}
        </Card>
      </div>
    );
  };

  /* ── Batches ────────────────────────────────────── */
  var renderBatchItemPreview = function (item) {
    var draft = item.draftId ? drafts.find(function (d) { return d.id === item.draftId; }) : null;
    var preview = draft ? extractDraftPreview(draft.draftJson) : null;
    var parsed = draft ? tryParse(draft.draftJson) : null;

    return (
      <div className="space-y-3">
        <div className="flex flex-wrap gap-2 items-center justify-between">
          <div className="font-medium text-base">
            #{item.index + 1} · {item.targetSkill || item.microGoal || 'слот'}
          </div>
          <div className="flex gap-2 flex-wrap">
            <Badge intent={statusTone(item.status)}>{statusLabel(item.status)}</Badge>
            {item.draftId ? <Badge intent="success">черновик создан</Badge> : null}
          </div>
        </div>
        <div className="text-xs opacity-70">
          сложность: {item.difficultyTarget} · починок: {item.repairCount}
        </div>
        {item.microGoal ? <div className="text-sm opacity-80">{item.microGoal}</div> : null}

        {/* Rich preview if draft exists */}
        {preview ? (
          <div className="rounded-xl border border-neutral-200/70 dark:border-neutral-800 bg-white dark:bg-neutral-900/60 p-4 space-y-3">
            <div className="text-lg font-semibold">{preview.title || 'Без названия'}</div>
            {parsed && parsed.description ? (
              <div
                className="text-sm opacity-90 prose prose-sm dark:prose-invert max-w-none"
                dangerouslySetInnerHTML={{ __html: parsed.description }}
              />
            ) : preview.description ? (
              <div className="text-sm opacity-90">{preview.description}</div>
            ) : null}
            <div className="flex flex-wrap gap-x-4 gap-y-1 text-xs opacity-70">
              {preview.assignmentType ? <span className="font-medium uppercase">{preview.assignmentType}</span> : null}
              {preview.difficulty != null ? <span>Сложность: {preview.difficulty}</span> : null}
              {preview.languages ? <span>Языки: {preview.languages}</span> : null}
              {preview.publicTests != null ? <span>Открытых тестов: {preview.publicTests}</span> : null}
              {preview.hiddenTests != null ? <span>Скрытых тестов: {preview.hiddenTests}</span> : null}
              {preview.blocks != null ? <span>Блоков: {preview.blocks}</span> : null}
              {preview.questions != null ? <span>Вопросов: {preview.questions}</span> : null}
              {preview.tags ? <span>Теги: {preview.tags}</span> : null}
              {preview.placementAfterTitle ? <span>После: {preview.placementAfterTitle}</span> : null}
            </div>
            {preview.placementReason ? <div className="text-xs opacity-70">Позиция в курсе: {preview.placementReason}</div> : null}

            {/* Show public tests for code-test */}
            {parsed && Array.isArray(parsed.publicTests) && parsed.publicTests.length > 0 ? (
              <div>
                <div className="text-xs font-medium opacity-70 mb-1">Примеры тестов:</div>
                <div className="space-y-1">
                  {parsed.publicTests.slice(0, 3).map(function (t, i) {
                    return (
                      <div key={i} className="flex gap-3 text-xs font-mono bg-neutral-50 dark:bg-neutral-800/50 rounded-lg px-3 py-1.5">
                        <span className="opacity-50">вход:</span>
                        <span className="whitespace-pre-wrap">{(t.input || '').trim()}</span>
                        <span className="opacity-50 ml-auto">→</span>
                        <span className="whitespace-pre-wrap">{(t.expectedOutput || '').trim()}</span>
                      </div>
                    );
                  })}
                </div>
              </div>
            ) : null}

            {/* Show questions for test */}
            {parsed && Array.isArray(parsed.questions) && parsed.questions.length > 0 ? (
              <div>
                <div className="text-xs font-medium opacity-70 mb-1">Вопросы:</div>
                <div className="space-y-1">
                  {parsed.questions.slice(0, 5).map(function (q, i) {
                    return (
                      <div key={i} className="text-xs bg-neutral-50 dark:bg-neutral-800/50 rounded-lg px-3 py-1.5">
                        <span className="opacity-50 mr-2">{i + 1}.</span>
                        <span>{q.prompt || q.text || '—'}</span>
                        {q.type ? <span className="opacity-40 ml-2">({q.type})</span> : null}
                      </div>
                    );
                  })}
                </div>
              </div>
            ) : null}

            {/* Show blocks for math */}
            {parsed && Array.isArray(parsed.blocks) && parsed.blocks.length > 0 ? (
              <div>
                <div className="text-xs font-medium opacity-70 mb-1">Блоки:</div>
                <div className="space-y-1">
                  {parsed.blocks.slice(0, 5).map(function (b, i) {
                    return (
                      <div key={i} className="text-xs bg-neutral-50 dark:bg-neutral-800/50 rounded-lg px-3 py-1.5">
                        <span className="font-medium mr-2">{b.blockType || 'block'}</span>
                        <span>{b.title || b.prompt || '—'}</span>
                      </div>
                    );
                  })}
                </div>
              </div>
            ) : null}

            {/* Draft action buttons */}
            {draft ? (
              <div className="flex gap-2 flex-wrap pt-2 border-t border-neutral-200/50 dark:border-neutral-800">
                {(draft.status || '').toLowerCase() !== 'published' ? (
                  <>
                    <Button variant="outline" disabled={busy} onClick={function () { publishDraftNow(draft, true); }} className="gap-1">
                      <Sparkles size={14} /> Опубликовать
                    </Button>
                    <Button variant="outline" disabled={busy} onClick={function () { reviewDraftNow(draft.id, 'approve'); }} className="gap-1">
                      <Check size={14} /> Одобрить
                    </Button>
                    <Button variant="outline" disabled={busy} onClick={function () { deleteDraftNow(draft.id); }} className="gap-1 text-red-500">
                      <Trash2 size={14} /> Удалить черновик
                    </Button>
                  </>
                ) : (
                  <span className="text-sm text-green-600 flex items-center gap-1"><Check size={14} /> Опубликован</span>
                )}
              </div>
            ) : null}
          </div>
        ) : (
          <div className="text-sm opacity-60 italic">Черновик ещё не создан для этого слота.</div>
        )}

        {/* Raw JSON: collapsed */}
        <details>
          <summary className="cursor-pointer opacity-60 text-xs hover:opacity-100 transition">JSON-детали</summary>
          <div className="mt-2 space-y-2">
            {item.briefJson ? (
              <Field label="BriefJson">
                <Textarea rows={5} readOnly value={prettyJson(item.briefJson)} className="font-mono text-xs" />
              </Field>
            ) : null}
            {item.referencePackJson ? (
              <Field label="ReferencePackJson">
                <Textarea rows={5} readOnly value={prettyJson(item.referencePackJson)} className="font-mono text-xs" />
              </Field>
            ) : null}
            {item.scorecardJson ? (
              <Field label="ScorecardJson">
                <Textarea rows={5} readOnly value={prettyJson(item.scorecardJson)} className="font-mono text-xs" />
              </Field>
            ) : null}
          </div>
        </details>
      </div>
    );
  };

  var renderBatches = function () {
    var batchMemory = selectedBatch ? tryParse(selectedBatch.batchMemoryJson) : null;
    var batchAgentState = batchMemory && batchMemory.agentState ? batchMemory.agentState : null;
    var batchPlacementPlan = batchMemory && Array.isArray(batchMemory.placementPlan) ? batchMemory.placementPlan : [];
    return (
      <div className="grid xl:grid-cols-[0.92fr,1.08fr] gap-6">
        <div className="space-y-4">
          <Card>
            <SectionTitle
              icon={Sparkles}
              title="Пакеты (batches)"
              subtitle="Batch-сущности и их прогресс через стадии pipeline."
            />
            <div className="mt-4 flex gap-2 flex-wrap items-center">
              <Button variant="outline" onClick={load}>
                <RefreshCcw size={14} /> Обновить
              </Button>
              <Badge intent="secondary">всего: {filteredBatches.length}</Badge>
            </div>
          </Card>

          <div className="space-y-3 max-h-[72vh] overflow-auto pr-1">
            {loading ? (
              <Card className="text-center opacity-70 py-8">Загрузка…</Card>
            ) : (
              filteredBatches.map(function (batch) {
                return (
                  <Card
                    key={batch.id}
                    className={selectedBatch && selectedBatch.id === batch.id ? 'ring-2 ring-brand-500/30' : ''}
                  >
                    <div className="flex flex-col gap-3 lg:flex-row lg:items-start lg:justify-between">
                      <div className="min-w-0">
                        <div className="font-medium break-all">
                          {batch.assignmentType} · {batch.mode || '—'}
                        </div>
                        <div className="text-xs opacity-50 mt-1 break-all font-mono">{batch.id}</div>
                        <div className="text-sm opacity-80 mt-1 line-clamp-2">{batch.prompt}</div>
                        <div className="text-xs opacity-50 mt-1">
                          {batch.updatedAtUtc ? new Date(batch.updatedAtUtc).toLocaleString() : '—'}
                        </div>
                      </div>
                      <div className="flex flex-wrap gap-2 items-center">
                        <Badge intent={statusTone(batch.status)}>{statusLabel(batch.status)}</Badge>
                        {batch.currentStage ? <Badge intent="secondary">{batch.currentStage}</Badge> : null}
                        <Button
                          variant="outline"
                          onClick={function () {
                            openBatch(batch.id);
                          }}
                        >
                          Открыть
                        </Button>
                        <Button
                          variant="outline"
                          disabled={busy}
                          onClick={function () {
                            deleteBatchNow(batch.id);
                          }}
                          className="text-red-500 hover:bg-red-50 dark:hover:bg-red-950/30"
                        >
                          <Trash2 size={14} />
                        </Button>
                      </div>
                    </div>
                  </Card>
                );
              })
            )}
            {!loading && filteredBatches.length === 0 ? (
              <Card className="text-center opacity-70 py-8">Пакетов пока нет.</Card>
            ) : null}
          </div>
        </div>

        <Card>
          <SectionTitle
            icon={Brain}
            title="Детали пакета"
            subtitle="Элементы пакета, review-данные и текущая стадия."
          />
          {!selectedBatch ? (
            <div className="text-sm opacity-70 mt-4">Выбери пакет слева.</div>
          ) : (
            <div className="space-y-4 text-sm mt-4">
              <div className="flex flex-wrap gap-2 items-center">
                <Badge intent={statusTone(selectedBatch.status)}>{statusLabel(selectedBatch.status)}</Badge>
                {selectedBatch.currentStage ? (
                  <Badge intent="secondary">стадия: {selectedBatch.currentStage}</Badge>
                ) : null}
                <Badge intent="secondary">
                  элементов: {selectedBatch.itemsCount || (selectedBatch.items ? selectedBatch.items.length : 0)}
                </Badge>
                <Badge intent="secondary">готово: {selectedBatch.readyItemsCount || 0}</Badge>
                <Button
                  variant="outline"
                  disabled={busy}
                  onClick={function () { publishBatchDrafts(selectedBatch); }}
                  className="gap-1 ml-auto"
                >
                  <Sparkles size={14} /> Опубликовать все
                </Button>
                <Button
                  variant="outline"
                  disabled={busy}
                  onClick={function () { deleteBatchNow(selectedBatch.id); }}
                  className="gap-1 text-red-500"
                >
                  <Trash2 size={14} /> Удалить пакет
                </Button>
              </div>
              <div>
                <span className="opacity-70">Id:</span>{' '}
                <span className="break-all font-mono text-xs">{selectedBatch.id}</span>
              </div>
              <div>
                <span className="opacity-70">Курс:</span>{' '}
                <span className="break-all font-mono text-xs">{selectedBatch.courseId || '—'}</span>
              </div>
              <div>
                <span className="opacity-70">Тип:</span> {selectedBatch.assignmentType}
              </div>
              <div>
                <span className="opacity-70">Режим:</span> {selectedBatch.mode || '—'}
              </div>
              <div>
                <span className="opacity-70">Промпт:</span> {selectedBatch.prompt}
              </div>

              {batchAgentState ? (
                <div className="rounded-2xl border border-neutral-200/70 dark:border-neutral-800 p-4 space-y-3 bg-[rgb(var(--card))]">
                  <div className="flex flex-wrap items-center gap-2">
                    <div className="font-medium">Каноническое состояние batch-агента</div>
                    {batchAgentState.workflowKind ? <Badge intent="secondary">{batchAgentState.workflowKind}</Badge> : null}
                    {batchAgentState.currentStage ? <Badge intent="secondary">{batchAgentState.currentStage}</Badge> : null}
                    {batchAgentState.readyForGeneration ? <Badge intent="success">ready</Badge> : null}
                  </div>
                  {batchAgentState.userIntentSummary ? (
                    <div>
                      <span className="opacity-70">Цель:</span> {batchAgentState.userIntentSummary}
                    </div>
                  ) : null}
                  <div className="flex flex-wrap gap-2">
                    {batchAgentState.learnerAudience ? <Badge intent="secondary">аудитория: {batchAgentState.learnerAudience}</Badge> : null}
                    {batchAgentState.pedagogyMode ? <Badge intent="secondary">режим: {batchAgentState.pedagogyMode}</Badge> : null}
                    {(batchAgentState.activeConstraints || []).map(function (item) {
                      return <Badge key={item} intent="secondary">{item}</Badge>;
                    })}
                  </div>
                  {batchAgentState.nextSuggestedAction ? (
                    <div className="text-xs opacity-70">Следующий шаг: {batchAgentState.nextSuggestedAction}</div>
                  ) : null}
                  {batchPlacementPlan.length > 0 ? (
                    <div className="space-y-2">
                      <div className="text-xs uppercase tracking-[0.18em] opacity-50">Точки вставки</div>
                      <div className="flex flex-wrap gap-2">
                        {batchPlacementPlan.slice(0, 6).map(function (item, index) {
                          var label = item.afterAssignmentTitle || item.concept || ('slot ' + (index + 1));
                          return <Badge key={String(label) + '-' + index} intent="secondary">{label}</Badge>;
                        })}
                      </div>
                    </div>
                  ) : null}
                </div>
              ) : null}

              {selectedBatchActiveJob ? (
                <div className="rounded-2xl border border-amber-300/60 bg-amber-50/60 dark:bg-amber-950/20 dark:border-amber-700/40 p-3 space-y-1">
                  <div className="font-medium">Текущий job пайплайна</div>
                  <div className="text-xs break-all">
                    {selectedBatchActiveJob.type} · статус: {selectedBatchActiveJob.status}
                    {selectedBatchActiveJob.stageCode ? ' · стадия: ' + selectedBatchActiveJob.stageCode : ''}
                  </div>
                  {selectedBatchActiveJob.retryCount ? (
                    <div className="text-xs opacity-80">Retry count: {selectedBatchActiveJob.retryCount}</div>
                  ) : null}
                  {selectedBatchActiveJob.nextAttemptAtUtc ? (
                    <div className="text-xs opacity-80">Следующая попытка: {new Date(selectedBatchActiveJob.nextAttemptAtUtc).toLocaleString()}</div>
                  ) : null}
                  {selectedBatchActiveJob.errorText ? (
                    <div className="text-xs text-red-600 break-words">Последняя ошибка: {selectedBatchActiveJob.errorText}</div>
                  ) : null}
                </div>
              ) : null}

              {/* Pipeline data — collapsed */}
              <details>
                <summary className="cursor-pointer opacity-60 text-xs hover:opacity-100 transition">Pipeline JSON-данные</summary>
                <div className="mt-2 space-y-2">
                  {selectedBatch.batchMemoryJson ? (
                    <Field label="BatchMemoryJson">
                      <Textarea rows={8} readOnly value={prettyJson(selectedBatch.batchMemoryJson)} className="font-mono text-xs" />
                    </Field>
                  ) : null}
                  {selectedBatch.plannerFeedbackJson ? (
                    <Field label="PlannerFeedbackJson">
                      <Textarea rows={6} readOnly value={prettyJson(selectedBatch.plannerFeedbackJson)} className="font-mono text-xs" />
                    </Field>
                  ) : null}
                  {selectedBatch.batchReviewJson ? (
                    <Field label="BatchReviewJson">
                      <Textarea rows={6} readOnly value={prettyJson(selectedBatch.batchReviewJson)} className="font-mono text-xs" />
                    </Field>
                  ) : null}
                  {selectedBatch.qualityLedgerJson ? (
                    <Field label="QualityLedgerJson">
                      <Textarea rows={6} readOnly value={prettyJson(selectedBatch.qualityLedgerJson)} className="font-mono text-xs" />
                    </Field>
                  ) : null}
                  {selectedBatch.exportManifestJson ? (
                    <Field label="ExportManifestJson">
                      <Textarea rows={6} readOnly value={prettyJson(selectedBatch.exportManifestJson)} className="font-mono text-xs" />
                    </Field>
                  ) : null}
                </div>
              </details>

              {/* Batch items — rich view */}
              <div className="space-y-3 mt-2">
                <div className="font-medium flex items-center gap-2">
                  Элементы пакета
                  {selectedBatch.items && selectedBatch.items.length > 0 ? (
                    <Badge intent="secondary">{selectedBatch.items.length}</Badge>
                  ) : null}
                </div>
                {(!selectedBatch.items || selectedBatch.items.length === 0) ? (
                  <div className="opacity-70">Элементов пока нет (pipeline ещё не создал слоты).</div>
                ) : null}
                {(selectedBatch.items || []).map(function (item) {
                  var isSelected = selectedBatchItem && selectedBatchItem.id === item.id;
                  return (
                    <div
                      key={item.id}
                      className={'rounded-2xl border p-4 transition cursor-pointer ' +
                        (isSelected ? 'border-[rgb(var(--accent))] bg-[rgba(var(--accent)/0.04)] ring-1 ring-[rgba(var(--accent)/0.2)]' : 'border-neutral-200/70 dark:border-neutral-800 hover:border-[rgba(var(--accent)/0.3)]')
                      }
                      onClick={function () { setSelectedBatchItem(isSelected ? null : item); }}
                    >
                      {isSelected ? (
                        renderBatchItemPreview(item)
                      ) : (
                        <div className="flex flex-wrap gap-2 items-center justify-between">
                          <div className="flex items-center gap-2 min-w-0">
                            <Eye size={14} className="opacity-40 shrink-0" />
                            <span className="font-medium">#{item.index + 1} · {item.targetSkill || item.microGoal || 'слот'}</span>
                          </div>
                          <div className="flex gap-2 flex-wrap items-center">
                            <span className="text-xs opacity-60">сл. {item.difficultyTarget}</span>
                            <Badge intent={statusTone(item.status)}>{statusLabel(item.status)}</Badge>
                            {item.draftId ? <Badge intent="success">черновик</Badge> : null}
                          </div>
                        </div>
                      )}
                    </div>
                  );
                })}
              </div>
            </div>
          )}
        </Card>
      </div>
    );
  };

  /* ── Drafts (the main thing!) ───────────────────── */
  var renderDrafts = function () {
    return (
      <Card>
        <SectionTitle
          icon={FileStack}
          title="Черновики"
          subtitle="Готовые черновики из batch pipeline. Одобри и опубликуй."
        />
        <div className="flex gap-2 flex-wrap text-xs mt-3 mb-2">
          <Badge intent="secondary">Всего: {draftCounts.total}</Badge>
          <Badge intent="success">Одобрено: {draftCounts.approved}</Badge>
          <Badge intent="danger">Отклонено: {draftCounts.rejected}</Badge>
          <Badge intent="success">Опубликовано: {draftCounts.published}</Badge>
        </div>
        {!courseScopeId ? (
          <div className="rounded-xl bg-amber-50 dark:bg-amber-950/30 border border-amber-200/50 dark:border-amber-800/30 p-3 text-sm flex items-center gap-2 mb-4">
            <AlertTriangle size={16} className="text-amber-600 shrink-0" />
            <span>Курс не выбран. Для публикации нужно выбрать курс на вкладке «Обзор».</span>
          </div>
        ) : null}
        <div className="space-y-4 mt-4 max-h-[76vh] overflow-auto pr-1">
          {drafts.length === 0 ? (
            <div className="text-center opacity-70 py-8">
              Черновиков пока нет. Создай пакет, чтобы AI начал генерировать.
            </div>
          ) : null}
          {drafts.map(function (draft) {
            var selfCheck = extractSelfCheck(draft.draftJson);
            var pub = getPublishability(draft);
            var isExpanded = !!expandedDrafts[draft.id];
            var isPublished = (draft.status || '').toLowerCase() === 'published';

            return (
              <div
                key={draft.id}
                className={
                  'rounded-2xl border p-4 space-y-3 ' +
                  (isPublished
                    ? 'border-green-300/50 bg-green-50/30 dark:border-green-800/30 dark:bg-green-950/10'
                    : 'border-neutral-200/70 dark:border-neutral-800')
                }
              >
                {/* Header */}
                <div className="flex flex-wrap gap-2 items-center justify-between">
                  <div className="font-medium text-base">{draft.title || 'Без названия'}</div>
                  <div className="flex gap-2 flex-wrap items-center">
                    <Badge intent="secondary">{draft.assignmentType}</Badge>
                    <Badge intent={statusTone(draft.status)}>{statusLabel(draft.status)}</Badge>
                    {selfCheck && selfCheck.status ? (
                      <Badge intent={selfCheckTone(selfCheck.status)}>
                        {selfCheck.status === 'passed'
                          ? 'self-check \u2713'
                          : selfCheck.status === 'failed'
                            ? 'self-check \u2717'
                            : selfCheck.status}
                      </Badge>
                    ) : null}
                  </div>
                </div>

                {/* IDs */}
                <div className="text-xs opacity-50 font-mono break-all">id: {draft.id}</div>
                {draft.batchId ? (
                  <div className="text-xs opacity-50 font-mono break-all">batch: {draft.batchId}</div>
                ) : null}

                {/* Preview */}
                <DraftPreviewCard draft={draft} />

                {/* Self-check details */}
                {selfCheck ? (
                  <div className="rounded-xl bg-neutral-50 dark:bg-neutral-900/50 p-3 text-sm">
                    <div className="font-medium mb-1">
                      Self-check: {selfCheck.status || '—'} (score: {selfCheck.score != null ? selfCheck.score : '—'})
                    </div>
                    {selfCheck.checks && selfCheck.checks.length ? (
                      <div className="space-y-1 text-xs">
                        {selfCheck.checks.map(function (c, i) {
                          return (
                            <div key={i} className="flex items-center gap-2">
                              {c.status === 'passed' ? (
                                <Check size={12} className="text-green-600" />
                              ) : (
                                <X size={12} className="text-red-500" />
                              )}
                              <span className="opacity-80">
                                {c.name}: {c.detail || c.status}
                              </span>
                            </div>
                          );
                        })}
                      </div>
                    ) : null}
                  </div>
                ) : null}

                {/* Publish info */}
                {!isPublished && pub.reason ? (
                  <div
                    className={
                      'text-xs flex items-center gap-1 ' +
                      (pub.can
                        ? 'text-emerald-700 dark:text-emerald-400'
                        : 'text-amber-600 dark:text-amber-400')
                    }
                  >
                    {pub.can ? <Check size={12} /> : <AlertTriangle size={12} />}
                    {pub.reason}
                  </div>
                ) : null}

                {/* Actions */}
                <div className="flex gap-2 flex-wrap items-center">
                  {!isPublished ? (
                    <>
                      <Button
                        variant="outline"
                        disabled={busy}
                        onClick={function () {
                          reviewDraftNow(draft.id, 'approve');
                        }}
                        className="gap-1"
                      >
                        <Check size={14} /> Одобрить
                      </Button>
                      <Button
                        variant="outline"
                        disabled={busy}
                        onClick={function () {
                          validateDraftNow(draft);
                        }}
                        className="gap-1"
                      >
                        Self-check
                      </Button>
                      <Button
                        disabled={busy || !pub.can || (!courseScopeId && !draft.courseId)}
                        onClick={function () {
                          publishDraftNow(draft, pub.force);
                        }}
                        className="gap-1"
                      >
                        <Sparkles size={14} /> Опубликовать{pub.force ? ' (force)' : ''}
                      </Button>
                      <Button
                        variant="outline"
                        disabled={busy}
                        onClick={function () {
                          deleteDraftNow(draft.id);
                        }}
                        className="gap-1 text-red-500 hover:bg-red-50 dark:hover:bg-red-950/30"
                      >
                        <Trash2 size={14} /> Удалить
                      </Button>
                    </>
                  ) : (
                    <>
                      <span className="text-sm text-green-600 dark:text-green-400 flex items-center gap-1">
                        <Check size={14} /> Опубликован
                      </span>
                      <Button
                        variant="outline"
                        disabled={busy}
                        onClick={function () {
                          deleteDraftNow(draft.id);
                        }}
                        className="gap-1 text-red-500 hover:bg-red-50 dark:hover:bg-red-950/30 ml-auto"
                      >
                        <Trash2 size={14} /> Удалить запись
                      </Button>
                    </>
                  )}
                  {draft.batchId ? (
                    <Button
                      variant="outline"
                      onClick={function () {
                        openBatch(draft.batchId);
                      }}
                    >
                      Открыть пакет
                    </Button>
                  ) : null}
                </div>

                {/* Expandable raw JSON */}
                <button
                  type="button"
                  onClick={function () {
                    toggleDraftExpanded(draft.id);
                  }}
                  className="text-xs opacity-60 flex items-center gap-1 hover:opacity-100 transition"
                >
                  {isExpanded ? <ChevronUp size={12} /> : <ChevronDown size={12} />}
                  {isExpanded ? 'Скрыть JSON' : 'Показать JSON'}
                </button>
                {isExpanded ? (
                  <Textarea rows={14} readOnly value={prettyJson(draft.draftJson)} className="font-mono text-xs" />
                ) : null}
              </div>
            );
          })}
        </div>
      </Card>
    );
  };

  /* ── Layout ─────────────────────────────────────── */
  return (
    <Layout title="AI Foundry">
      <div className="space-y-6">
        <div className="flex flex-col gap-4 lg:flex-row lg:items-end lg:justify-between">
          <div>
            <h1 className="text-2xl font-semibold flex items-center gap-2">
              <Sparkles size={22} /> AI Foundry
            </h1>
            <p className="text-sm opacity-70 mt-1">
              Создавай пакеты заданий, следи за стадиями pipeline и публикуй готовые черновики.
            </p>
          </div>
          <div className="flex gap-2 flex-wrap">
            {AI_TABS.map(function (tab) {
              return (
                <TabButton
                  key={tab.key}
                  active={activeTab === tab.key}
                  icon={tab.icon}
                  count={
                    tab.key === 'queue'
                      ? jobs.length
                      : tab.key === 'batches'
                        ? batches.length
                        : tab.key === 'drafts'
                          ? drafts.length
                          : undefined
                  }
                  onClick={function () {
                    setActiveTab(tab.key);
                  }}
                >
                  {tab.label}
                </TabButton>
              );
            })}
          </div>
        </div>

        {pageError ? (
          <Card className="border-red-300/60 bg-red-50/30 dark:bg-red-950/20 p-4 flex items-center gap-2 text-red-600 dark:text-red-400">
            <AlertTriangle size={18} /> {pageError}
          </Card>
        ) : null}

        {activeTab === 'overview' ? renderOverview() : null}
        {activeTab === 'create' ? renderCreate() : null}
        {activeTab === 'queue' ? renderJobsQueue() : null}
        {activeTab === 'batches' ? renderBatches() : null}
        {activeTab === 'drafts' ? renderDrafts() : null}

        <div className="grid md:grid-cols-3 gap-4">
          <CompactStatCard
            title="Foundry-стадии"
            value={
              jobs.filter(function (job) {
                return String(job.type || '').startsWith('assignment_');
              }).length
            }
            hint="AI-задания типа assignment_*"
          />
          <CompactStatCard
            title="Выполняется"
            value={
              jobs.filter(function (j) {
                return (j.status || '').toLowerCase() === 'running';
              }).length
            }
            hint="Сейчас обрабатывает worker"
          />
          <CompactStatCard
            title="Ошибки"
            value={
              jobs.filter(function (j) {
                return (j.status || '').toLowerCase() === 'failed';
              }).length
            }
            hint="Упавшие задания"
          />
        </div>
      </div>
    </Layout>
  );
}
