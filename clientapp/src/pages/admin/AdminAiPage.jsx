import React, { useEffect, useMemo, useState } from 'react';
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
} from '../../api/aiAdmin';
import { createCourse, getCourses } from '../../api/courses';
import { handleApiError } from '../../utils/handleApiError';
import { useNotify } from '../../components/notify/NotifyProvider';
import {
  Brain,
  CheckCircle2,
  FileSearch,
  FileStack,
  FolderPlus,
  LayoutDashboard,
  ListTodo,
  RefreshCcw,
  Sparkles,
  Wand2,
} from 'lucide-react';

const AI_TABS = [
  { key: 'overview', label: 'Обзор', icon: LayoutDashboard },
  { key: 'create', label: 'Новый пакет', icon: Wand2 },
  { key: 'queue', label: 'Очередь', icon: ListTodo },
  { key: 'batches', label: 'Пакеты', icon: Sparkles },
  { key: 'drafts', label: 'Черновики', icon: FileStack },
];

const batchEmpty = {
  courseId: '',
  assignmentType: 'math',
  prompt: 'Собери пакет заданий для нового Foundry batch pipeline. Нужны логичные шаги, градация сложности и пригодность для текущего редактора TaskForge.',
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

const STATUS_LABELS = {
  pending: 'ожидает',
  queued: 'в очереди',
  running: 'в работе',
  processing: 'в работе',
  done: 'готово',
  ready: 'готово',
  published: 'опубликовано',
  approved: 'одобрено',
  reviewed: 'проверено',
  rejected: 'отклонено',
  failed: 'ошибка',
  error: 'ошибка',
  drafting: 'создание',
  draft_generate: 'генерация черновиков',
  reference_packaged: 'подготовлен контекст',
  reference-packaged: 'подготовлен контекст',
  repaired: 'исправлено',
  needs_review: 'нужна проверка',
  'needs-review': 'нужна проверка',
};

const STAGE_LABELS = {
  course_profile_build: 'Профиль курса',
  gap_analysis: 'Поиск пробелов',
  batch_plan: 'План пакета',
  brief_generate: 'Черновой план задания',
  brief_review: 'Проверка плана задания',
  reference_pack_build: 'Подготовка контекста',
  draft_generate: 'Генерация черновика',
  draft_review: 'Проверка черновика',
  repair: 'Исправление',
  batch_publish_prepare: 'Подготовка к публикации',
};

function statusTone(status) {
  switch ((status || '').toLowerCase()) {
    case 'done':
    case 'ready':
    case 'published':
    case 'approved':
      return 'success';
    case 'failed':
    case 'error':
    case 'rejected':
      return 'danger';
    case 'running':
    case 'processing':
    case 'needs-review':
    case 'needs_review':
      return 'outline';
    default:
      return 'secondary';
  }
}

function prettyJson(value) {
  if (!value) return '';
  try {
    return JSON.stringify(JSON.parse(value), null, 2);
  } catch {
    return value;
  }
}

function tryParseJson(value) {
  if (!value) return null;
  try {
    return typeof value === 'string' ? JSON.parse(value) : value;
  } catch {
    return null;
  }
}

function extractSelfCheck(draftJson) {
  const parsed = tryParseJson(draftJson);
  return parsed?.meta?.selfCheck || parsed?.selfCheck || null;
}

function selfCheckTone(status) {
  switch ((status || '').toLowerCase()) {
    case 'passed': return 'success';
    case 'failed': return 'danger';
    case 'needs-review': return 'outline';
    default: return 'secondary';
  }
}

function canPublishDraft(draft) {
  const selfCheck = extractSelfCheck(draft?.draftJson);
  return !!selfCheck && String(selfCheck.status || '').toLowerCase() === 'passed' && draft?.status !== 'published';
}

function toRuStatus(status) {
  const key = String(status || '').trim().toLowerCase();
  return STATUS_LABELS[key] || status || '—';
}

function toRuStage(stage) {
  const key = String(stage || '').trim();
  return STAGE_LABELS[key] || key || '—';
}

function shortText(text, max = 180) {
  const value = String(text || '').trim();
  if (!value) return '—';
  return value.length > max ? `${value.slice(0, max)}…` : value;
}

function extractDraftSummary(draft) {
  const parsed = tryParseJson(draft?.draftJson);
  return parsed?.summary || parsed?.description || draft?.summary || '';
}

function extractBatchItemPreview(item, linkedDraft) {
  const brief = tryParseJson(item?.briefJson);
  const ref = tryParseJson(item?.referencePackJson);
  return {
    title: linkedDraft?.title || brief?.titleHint || item?.targetSkill || 'Элемент пакета',
    summary: linkedDraft ? extractDraftSummary(linkedDraft) : (brief?.summary || item?.microGoal || ''),
    prompt: brief?.generationPrompt || '',
    sourceText: brief?.sourceText || '',
    notes: brief?.notes || '',
    policy: ref?.policyPack || null,
  };
}

function explainPublishBlocker(draft) {
  if (!draft) return 'Черновик ещё не найден.';
  if (String(draft.status || '').toLowerCase() === 'published') return 'Уже опубликовано.';
  const selfCheck = extractSelfCheck(draft?.draftJson);
  if (!selfCheck) return 'Сначала запусти самопроверку.';
  const status = String(selfCheck.status || '').toLowerCase();
  if (status !== 'passed') return `Самопроверка ещё не пройдена: ${toRuStatus(status)}.`;
  return '';
}

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
        <div className="font-medium flex items-center gap-2"><Icon size={18} /> {title}</div>
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

function JsonBlock({ label, value, rows = 8 }) {
  return (
    <Field label={label}>
      <Textarea rows={rows} readOnly value={prettyJson(value)} />
    </Field>
  );
}

export default function AdminAiPage() {
  const notify = useNotify();
  const [jobs, setJobs] = useState([]);
  const [batches, setBatches] = useState([]);
  const [drafts, setDrafts] = useState([]);
  const [courses, setCourses] = useState([]);
  const [selectedJob, setSelectedJob] = useState(null);
  const [selectedBatch, setSelectedBatch] = useState(null);
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState(false);
  const [pageError, setPageError] = useState('');
  const [activeTab, setActiveTab] = useState('overview');
  const [filters, setFilters] = useState({ status: '', type: '', query: '' });
  const [courseScopeId, setCourseScopeId] = useState('');
  const [showCreateCourse, setShowCreateCourse] = useState(false);
  const [courseCreateForm, setCourseCreateForm] = useState(courseCreateEmpty);
  const [batchForm, setBatchForm] = useState(batchEmpty);

  const draftById = useMemo(() => {
    const map = new Map();
    drafts.forEach((draft) => map.set(String(draft.id), draft));
    return map;
  }, [drafts]);

  const load = async () => {
    try {
      setLoading(true);
      const [jobsData, batchesData, draftsData, coursesData] = await Promise.all([
        getAiJobs({ status: filters.status || undefined, type: filters.type || undefined, page: 1, pageSize: 100 }),
        getAiBatches(),
        getAiDrafts(),
        getCourses(),
      ]);
      setJobs(Array.isArray(jobsData.items) ? jobsData.items : []);
      setBatches(Array.isArray(batchesData) ? batchesData : []);
      setDrafts(Array.isArray(draftsData) ? draftsData : []);
      setCourses(Array.isArray(coursesData) ? coursesData : []);
      setPageError('');
    } catch (e) {
      const parsed = handleApiError(e, notify, 'Не удалось загрузить AI-раздел');
      setPageError(parsed?.userMessage || 'Не удалось загрузить AI-раздел');
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => {
    load();
  }, []); // eslint-disable-line

  useEffect(() => {
    if (!courseScopeId) return;
    setBatchForm((prev) => ({ ...prev, courseId: courseScopeId }));
  }, [courseScopeId]);

  const selectedCourse = useMemo(
    () => courses.find((course) => String(course.id) === String(courseScopeId)) || null,
    [courses, courseScopeId],
  );

  const filteredJobs = useMemo(() => {
    const q = (filters.query || '').trim().toLowerCase();
    if (!q) return jobs;
    return jobs.filter((job) => {
      const blob = [job.id, job.type, job.status, job.targetEntityType, job.targetEntityId, job.workerId].join(' ').toLowerCase();
      return blob.includes(q);
    });
  }, [filters.query, jobs]);

  const filteredBatches = useMemo(() => {
    const q = (filters.query || '').trim().toLowerCase();
    if (!q) return batches;
    return batches.filter((batch) => {
      const blob = [batch.id, batch.status, batch.currentStage, batch.assignmentType, batch.mode, batch.prompt].join(' ').toLowerCase();
      return blob.includes(q);
    });
  }, [filters.query, batches]);

  const loadAndOpenBatch = async (batchId) => {
    await load();
    if (!batchId) return;
    try {
      const data = await getAiBatch(batchId);
      setSelectedBatch(data || null);
      setActiveTab('batches');
    } catch (e) {
      handleApiError(e, notify, 'Не удалось открыть пакет');
    }
  };

  const openJob = async (id) => {
    try {
      const data = await getAiJob(id);
      setSelectedJob(data || null);
      setActiveTab('queue');
    } catch (e) {
      handleApiError(e, notify, 'Не удалось открыть AI-задачу');
    }
  };

  const openBatch = async (id) => {
    try {
      const data = await getAiBatch(id);
      setSelectedBatch(data || null);
      setActiveTab('batches');
    } catch (e) {
      handleApiError(e, notify, 'Не удалось открыть пакет');
    }
  };

  const submitBatch = async () => {
    try {
      setBusy(true);
      const created = await generateAiBatch({
        ...batchForm,
        courseId: batchForm.courseId || null,
        count: Number(batchForm.count || 1),
        difficulty: Number(batchForm.difficulty || 2),
        priority: Number(batchForm.priority || 20),
      });
      notify.success('Пакет поставлен в очередь');
      await loadAndOpenBatch(created?.id);
    } catch (e) {
      handleApiError(e, notify, 'Не удалось создать пакет');
    } finally {
      setBusy(false);
    }
  };

  const reviewDraftNow = async (id, action) => {
    try {
      setBusy(true);
      await reviewAiDraft(id, action);
      notify.success(action === 'approve' ? 'Черновик одобрен' : 'Черновик отклонён');
      await load();
      if (selectedBatch?.id) {
        const refreshed = await getAiBatch(selectedBatch.id);
        setSelectedBatch(refreshed || null);
      }
    } catch (e) {
      handleApiError(e, notify, 'Не удалось обновить статус черновика');
    } finally {
      setBusy(false);
    }
  };

  const publishDraftNow = async (draft) => {
    try {
      setBusy(true);
      const data = await publishAiDraft(draft.id, { courseId: draft.courseId || courseScopeId || null });
      notify.success(`Черновик опубликован как задание ${data?.assignmentId ? `#${data.assignmentId}` : ''}`.trim());
      await load();
    } catch (e) {
      handleApiError(e, notify, 'Не удалось опубликовать черновик');
    } finally {
      setBusy(false);
    }
  };

  const validateDraftNow = async (draft) => {
    try {
      setBusy(true);
      const data = await validateAiDraft(draft.id, { usePythonSelfCheck: true });
      notify.success('Самопроверка поставлена в очередь');
      await openJob(data?.id);
    } catch (e) {
      handleApiError(e, notify, 'Не удалось запустить самопроверку');
    } finally {
      setBusy(false);
    }
  };

  const handleCreateCourse = async () => {
    try {
      setBusy(true);
      const created = await createCourse({
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
      if (created?.id) setCourseScopeId(created.id);
    } catch (e) {
      handleApiError(e, notify, 'Не удалось создать курс');
    } finally {
      setBusy(false);
    }
  };

  const renderCourseScopeCard = () => (
    <Card>
      <SectionTitle icon={FolderPlus} title="Курс для нового пакета" subtitle="Сначала выбери курс. Новый пакет будет создаваться именно в нём." />
      <div className="grid lg:grid-cols-[1fr,auto,auto] gap-3 mt-4 items-end">
        <Field label="Курс">
          <Select value={courseScopeId} onChange={(e) => setCourseScopeId(e.target.value)}>
            <option value="">Не выбран</option>
            {courses.map((course) => (
              <option key={course.id} value={course.id}>{course.title || course.id}</option>
            ))}
          </Select>
        </Field>
        <Button variant="outline" onClick={() => setShowCreateCourse((prev) => !prev)}>
          <FolderPlus size={16} />
          <span className="ml-1">{showCreateCourse ? 'Скрыть форму' : 'Создать курс'}</span>
        </Button>
        <Button variant="outline" onClick={load}><RefreshCcw size={16} /> <span className="ml-1">Обновить</span></Button>
      </div>
      {selectedCourse ? (
        <div className="mt-3 rounded-2xl border border-neutral-200/70 dark:border-neutral-800 p-4 text-sm space-y-1">
          <div className="font-medium">{selectedCourse.title || 'Без названия'}</div>
          <div className="opacity-70 break-all">{selectedCourse.id}</div>
          <div className="opacity-80">{selectedCourse.description || 'Без описания'}</div>
        </div>
      ) : null}
      {showCreateCourse ? (
        <div className="mt-4 rounded-2xl border border-dashed border-neutral-300 dark:border-neutral-700 p-4 grid md:grid-cols-2 gap-4">
          <Field label="Название курса">
            <Input value={courseCreateForm.title} onChange={(e) => setCourseCreateForm((prev) => ({ ...prev, title: e.target.value }))} />
          </Field>
          <label className="text-sm flex items-center gap-2 mt-7"><input type="checkbox" checked={!!courseCreateForm.isPublic} onChange={(e) => setCourseCreateForm((prev) => ({ ...prev, isPublic: e.target.checked }))} /> Публичный курс</label>
          <div className="md:col-span-2">
            <Field label="Описание">
              <Textarea rows={4} value={courseCreateForm.description} onChange={(e) => setCourseCreateForm((prev) => ({ ...prev, description: e.target.value }))} />
            </Field>
          </div>
          <div className="md:col-span-2 flex gap-2 flex-wrap">
            <Button disabled={busy} onClick={handleCreateCourse}>Создать курс</Button>
            <Button variant="outline" onClick={() => setCourseCreateForm(courseCreateEmpty)}>Сбросить</Button>
          </div>
        </div>
      ) : null}
    </Card>
  );

  const renderOverview = () => (
    <div className="space-y-6">
      {renderCourseScopeCard()}
      <div className="grid md:grid-cols-2 xl:grid-cols-4 gap-4">
        <CompactStatCard title="AI-задачи" value={jobs.length} hint="Все этапы обработки" />
        <CompactStatCard title="Пакеты" value={batches.length} hint="Созданные наборы заданий" />
        <CompactStatCard title="Черновики готовы" value={drafts.filter((d) => String(d.status || '').toLowerCase() === 'ready').length} hint="Можно проверять и публиковать" />
        <CompactStatCard title="Сейчас в работе" value={jobs.filter((j) => String(j.status || '').toLowerCase() === 'running').length} hint="Обрабатываются worker'ом" />
      </div>
      <Card>
        <SectionTitle icon={CheckCircle2} title="Как пользоваться разделом" subtitle="Теперь это не страница для разработчика, а понятный рабочий экран." />
        <div className="mt-4 text-sm opacity-80 space-y-2">
          <div>1. Выбери курс.</div>
          <div>2. Создай пакет в разделе «Новый пакет».</div>
          <div>3. В разделе «Пакеты» смотри, на каком шаге сейчас обработка.</div>
          <div>4. Когда появятся черновики, открой их или одобри прямо из карточки пакета.</div>
          <div>5. После самопроверки станет доступна публикация в обычное задание.</div>
        </div>
      </Card>
    </div>
  );

  const renderCreate = () => (
    <div className="space-y-6">
      {renderCourseScopeCard()}
      <Card>
        <SectionTitle icon={Wand2} title="Создать новый пакет заданий" subtitle="Эта форма запускает только новый пакетный AI-процесс. Старые генераторы больше не используются." />
        <div className="grid md:grid-cols-2 gap-4 mt-4">
          <Field label="ID курса">
            <Input value={batchForm.courseId} onChange={(e) => setBatchForm((p) => ({ ...p, courseId: e.target.value }))} placeholder="обязательно" />
          </Field>
          <Field label="Тип задания">
            <Select value={batchForm.assignmentType} onChange={(e) => setBatchForm((p) => ({ ...p, assignmentType: e.target.value }))}>
              <option value="math">math</option>
              <option value="test">test</option>
              <option value="code-test">code-test</option>
            </Select>
          </Field>
          <Field label="Режим">
            <Input value={batchForm.mode} onChange={(e) => setBatchForm((p) => ({ ...p, mode: e.target.value }))} placeholder="topic-pack" />
          </Field>
          <Field label="Сложность">
            <Input type="number" min="1" max="5" value={batchForm.difficulty} onChange={(e) => setBatchForm((p) => ({ ...p, difficulty: e.target.value }))} />
          </Field>
          <Field label="Сколько задач сделать">
            <Input type="number" min="1" max="20" value={batchForm.count} onChange={(e) => setBatchForm((p) => ({ ...p, count: e.target.value }))} />
          </Field>
          <Field label="Приоритет">
            <Input type="number" min="0" max="100" value={batchForm.priority} onChange={(e) => setBatchForm((p) => ({ ...p, priority: e.target.value }))} />
          </Field>
          <div className="md:col-span-2">
            <Field label="Что нужно сделать">
              <Textarea rows={6} value={batchForm.prompt} onChange={(e) => setBatchForm((p) => ({ ...p, prompt: e.target.value }))} />
            </Field>
          </div>
          <div className="md:col-span-2">
            <Field label="Дополнительные заметки">
              <Textarea rows={4} value={batchForm.notes} onChange={(e) => setBatchForm((p) => ({ ...p, notes: e.target.value }))} />
            </Field>
          </div>
        </div>
        <div className="mt-4 flex gap-2 flex-wrap">
          <Button disabled={busy || !batchForm.courseId} onClick={submitBatch}>Запустить пакет</Button>
          <Button variant="outline" onClick={() => setBatchForm({ ...batchEmpty, courseId: courseScopeId || '' })}>Сбросить</Button>
        </div>
      </Card>
    </div>
  );

  const renderJobsQueue = () => (
    <div className="grid xl:grid-cols-[0.92fr,1.08fr] gap-6">
      <div className="space-y-4">
        <Card>
          <SectionTitle icon={ListTodo} title="Очередь AI-задач" subtitle="Здесь видны все этапы: анализ, план, черновики, проверки и публикация." />
          <div className="grid md:grid-cols-3 gap-4 mt-4">
            <Field label="Статус"><Input value={filters.status} onChange={(e) => setFilters((p) => ({ ...p, status: e.target.value }))} placeholder="например: running" /></Field>
            <Field label="Тип этапа"><Input value={filters.type} onChange={(e) => setFilters((p) => ({ ...p, type: e.target.value }))} placeholder="например: brief" /></Field>
            <Field label="Поиск"><Input value={filters.query} onChange={(e) => setFilters((p) => ({ ...p, query: e.target.value }))} placeholder="id, тип, worker" /></Field>
          </div>
          <div className="mt-4 flex gap-2 flex-wrap">
            <Button variant="outline" onClick={load}>Обновить</Button>
            <Badge intent="secondary">показано: {filteredJobs.length}</Badge>
          </div>
        </Card>

        <div className="space-y-3 max-h-[72vh] overflow-auto pr-1">
          {loading ? <Card>Загрузка…</Card> : filteredJobs.map((job) => (
            <Card key={job.id} className={selectedJob?.id === job.id ? 'ring-2 ring-brand-500/30' : ''}>
              <div className="flex flex-col gap-3 lg:flex-row lg:items-start lg:justify-between">
                <div className="min-w-0">
                  <div className="font-medium break-all">{job.type}</div>
                  <div className="text-xs opacity-70 mt-1 break-all">{job.id}</div>
                  <div className="text-sm opacity-80 mt-2">Этап: {toRuStage(job.stageCode || job.type)}</div>
                  <div className="text-xs opacity-60 mt-1">создано: {job.createdAtUtc ? new Date(job.createdAtUtc).toLocaleString() : '—'}</div>
                </div>
                <div className="flex flex-wrap gap-2 items-center">
                  <Badge intent={statusTone(job.status)}>{toRuStatus(job.status)}</Badge>
                  <Button variant="outline" onClick={() => openJob(job.id)}>Открыть</Button>
                </div>
              </div>
            </Card>
          ))}
          {!loading && filteredJobs.length === 0 ? <Card>Пока пусто.</Card> : null}
        </div>
      </div>

      <Card>
        <SectionTitle icon={Brain} title="Подробности этапа" subtitle="Здесь виден вход, результат и возможная ошибка выбранной AI-задачи." />
        {!selectedJob ? <div className="text-sm opacity-70 mt-4">Выбери этап слева.</div> : (
          <div className="space-y-4 text-sm mt-4">
            <div className="flex flex-wrap gap-2">
              <Badge intent={statusTone(selectedJob.status)}>{toRuStatus(selectedJob.status)}</Badge>
              <Badge intent="secondary">{toRuStage(selectedJob.stageCode || selectedJob.type)}</Badge>
            </div>
            <div><span className="opacity-70">Тип:</span> {selectedJob.type}</div>
            <div><span className="opacity-70">Цель:</span> {selectedJob.targetEntityType} {selectedJob.targetEntityId}</div>
            <div><span className="opacity-70">Создано:</span> {selectedJob.createdAtUtc ? new Date(selectedJob.createdAtUtc).toLocaleString() : '—'}</div>
            <div><span className="opacity-70">Начато:</span> {selectedJob.startedAtUtc ? new Date(selectedJob.startedAtUtc).toLocaleString() : '—'}</div>
            <div><span className="opacity-70">Завершено:</span> {selectedJob.completedAtUtc ? new Date(selectedJob.completedAtUtc).toLocaleString() : '—'}</div>
            <JsonBlock label="Что пришло на вход" value={selectedJob.inputJson} rows={10} />
            <JsonBlock label="Что вернула нейросеть" value={selectedJob.resultJson} rows={10} />
            {selectedJob.errorText ? <JsonBlock label="Текст ошибки" value={selectedJob.errorText} rows={4} /> : null}
          </div>
        )}
      </Card>
    </div>
  );

  const renderBatchItemCard = (item, index) => {
    const linkedDraft = item?.draftId ? draftById.get(String(item.draftId)) : drafts.find((draft) => String(draft.batchItemId) === String(item.id));
    const preview = extractBatchItemPreview(item, linkedDraft);
    const selfCheck = extractSelfCheck(linkedDraft?.draftJson);
    const publishBlockedText = explainPublishBlocker(linkedDraft);

    return (
      <div key={item.id} className="rounded-2xl border border-neutral-200/70 dark:border-neutral-800 p-4 space-y-3">
        <div className="flex flex-wrap gap-2 items-center justify-between">
          <div>
            <div className="font-medium">Задача {index + 1}: {preview.title}</div>
            <div className="text-xs opacity-70 mt-1">Навык: {item.targetSkill || '—'}</div>
          </div>
          <div className="flex gap-2 flex-wrap items-center">
            <Badge intent={statusTone(item.status)}>{toRuStatus(item.status)}</Badge>
            {linkedDraft ? <Badge intent="secondary">черновик найден</Badge> : <Badge intent="secondary">черновика ещё нет</Badge>}
            {selfCheck?.status ? <Badge intent={selfCheckTone(selfCheck.status)}>{toRuStatus(selfCheck.status)}</Badge> : null}
          </div>
        </div>

        <div className="grid md:grid-cols-3 gap-3 text-sm">
          <div className="rounded-xl bg-black/5 dark:bg-white/5 p-3">
            <div className="text-xs opacity-60 uppercase tracking-wide">Что хотим получить</div>
            <div className="mt-2">{preview.summary || item.microGoal || '—'}</div>
          </div>
          <div className="rounded-xl bg-black/5 dark:bg-white/5 p-3">
            <div className="text-xs opacity-60 uppercase tracking-wide">Сложность и ремонт</div>
            <div className="mt-2">Сложность: {item.difficultyTarget ?? '—'}</div>
            <div className="mt-1">Исправлений: {item.repairCount ?? 0}</div>
          </div>
          <div className="rounded-xl bg-black/5 dark:bg-white/5 p-3">
            <div className="text-xs opacity-60 uppercase tracking-wide">Можно ли публиковать</div>
            <div className="mt-2">{canPublishDraft(linkedDraft) ? 'Да, публикация доступна.' : publishBlockedText || 'Пока нельзя.'}</div>
          </div>
        </div>

        <div className="space-y-2 text-sm">
          {preview.prompt ? <div><span className="opacity-70">Промпт генерации:</span> {shortText(preview.prompt, 260)}</div> : null}
          {preview.notes ? <div><span className="opacity-70">Заметки:</span> {shortText(preview.notes, 220)}</div> : null}
          {preview.policy ? <div><span className="opacity-70">Политика:</span> методы {((preview.policy.enforcedMethods || []).join(', ') || '—')} / запрещено {((preview.policy.forbiddenFunctions || []).join(', ') || '—')}</div> : null}
        </div>

        <div className="flex gap-2 flex-wrap">
          {linkedDraft ? (
            <>
              <Button variant="outline" disabled={busy} onClick={() => reviewDraftNow(linkedDraft.id, 'approve')}>Одобрить</Button>
              <Button variant="outline" disabled={busy} onClick={() => reviewDraftNow(linkedDraft.id, 'reject')}>Отклонить</Button>
              <Button variant="outline" disabled={busy} onClick={() => validateDraftNow(linkedDraft)}>Самопроверка</Button>
              <Button disabled={busy || !canPublishDraft(linkedDraft)} onClick={() => publishDraftNow(linkedDraft)}>Опубликовать</Button>
            </>
          ) : (
            <Badge intent="secondary">Для этого элемента ещё не появился отдельный черновик.</Badge>
          )}
        </div>

        <details>
          <summary className="cursor-pointer opacity-80">Показать технические JSON-данные</summary>
          <div className="mt-3 space-y-2">
            <JsonBlock label="JSON плана" value={item.briefJson} rows={6} />
            <JsonBlock label="JSON контекста" value={item.referencePackJson} rows={6} />
            <JsonBlock label="JSON оценки" value={item.scorecardJson} rows={6} />
          </div>
        </details>
      </div>
    );
  };

  const renderBatches = () => (
    <div className="grid xl:grid-cols-[0.92fr,1.08fr] gap-6">
      <div className="space-y-4">
        <Card>
          <SectionTitle icon={Sparkles} title="Пакеты заданий" subtitle="Отдельный список пакетов, чтобы видеть прогресс без копания в отдельных AI-задачах." />
          <div className="mt-4 flex gap-2 flex-wrap">
            <Button variant="outline" onClick={load}>Обновить</Button>
            <Badge intent="secondary">всего: {filteredBatches.length}</Badge>
          </div>
        </Card>

        <div className="space-y-3 max-h-[72vh] overflow-auto pr-1">
          {loading ? <Card>Загрузка…</Card> : filteredBatches.map((batch) => (
            <Card key={batch.id} className={selectedBatch?.id === batch.id ? 'ring-2 ring-brand-500/30' : ''}>
              <div className="flex flex-col gap-3 lg:flex-row lg:items-start lg:justify-between">
                <div className="min-w-0">
                  <div className="font-medium break-all">{batch.assignmentType} · {batch.mode || '—'}</div>
                  <div className="text-xs opacity-70 mt-1 break-all">{batch.id}</div>
                  <div className="text-sm opacity-80 mt-2 line-clamp-3">{batch.prompt}</div>
                  <div className="text-xs opacity-60 mt-1">обновлено: {batch.updatedAtUtc ? new Date(batch.updatedAtUtc).toLocaleString() : '—'}</div>
                </div>
                <div className="flex flex-wrap gap-2 items-center">
                  <Badge intent={statusTone(batch.status)}>{toRuStatus(batch.status)}</Badge>
                  {batch.currentStage ? <Badge intent="secondary">{toRuStage(batch.currentStage)}</Badge> : null}
                  <Button variant="outline" onClick={() => openBatch(batch.id)}>Открыть</Button>
                </div>
              </div>
            </Card>
          ))}
          {!loading && filteredBatches.length === 0 ? <Card>Пакетов пока нет.</Card> : null}
        </div>
      </div>

      <Card>
        <SectionTitle icon={Brain} title="Подробности пакета" subtitle="Здесь теперь видно человеческое описание, а не только сырые JSON-ы." />
        {!selectedBatch ? <div className="text-sm opacity-70 mt-4">Выбери пакет слева.</div> : (
          <div className="space-y-4 text-sm mt-4">
            <div className="flex flex-wrap gap-2">
              <Badge intent={statusTone(selectedBatch.status)}>{toRuStatus(selectedBatch.status)}</Badge>
              {selectedBatch.currentStage ? <Badge intent="secondary">{toRuStage(selectedBatch.currentStage)}</Badge> : null}
              <Badge intent="secondary">элементов: {selectedBatch.itemsCount ?? selectedBatch.items?.length ?? 0}</Badge>
              <Badge intent="secondary">готово к публикации: {selectedBatch.readyItemsCount ?? 0}</Badge>
            </div>
            <div><span className="opacity-70">ID пакета:</span> <span className="break-all">{selectedBatch.id}</span></div>
            <div><span className="opacity-70">Курс:</span> <span className="break-all">{selectedBatch.courseId || '—'}</span></div>
            <div><span className="opacity-70">Тип задания:</span> {selectedBatch.assignmentType}</div>
            <div><span className="opacity-70">Режим:</span> {selectedBatch.mode || '—'}</div>
            <div><span className="opacity-70">Запрос:</span> {selectedBatch.prompt}</div>

            <div className="grid md:grid-cols-3 gap-3">
              <div className="rounded-xl bg-black/5 dark:bg-white/5 p-3">
                <div className="text-xs opacity-60 uppercase tracking-wide">Что происходит сейчас</div>
                <div className="mt-2">{selectedBatch.currentStage ? toRuStage(selectedBatch.currentStage) : 'Ожидание следующего шага.'}</div>
              </div>
              <div className="rounded-xl bg-black/5 dark:bg-white/5 p-3">
                <div className="text-xs opacity-60 uppercase tracking-wide">Что уже сделано</div>
                <div className="mt-2">Элементов создано: {selectedBatch.itemsCount ?? selectedBatch.items?.length ?? 0}</div>
                <div className="mt-1">Готовых элементов: {selectedBatch.readyItemsCount ?? 0}</div>
              </div>
              <div className="rounded-xl bg-black/5 dark:bg-white/5 p-3">
                <div className="text-xs opacity-60 uppercase tracking-wide">Что дальше</div>
                <div className="mt-2">Когда у элемента появится черновик и он пройдёт самопроверку, ты сможешь одобрить и опубликовать его прямо здесь.</div>
              </div>
            </div>

            <details>
              <summary className="cursor-pointer opacity-80">Показать технические JSON-данные пакета</summary>
              <div className="mt-3 space-y-2">
                <JsonBlock label="Что сказал планировщик" value={selectedBatch.plannerFeedbackJson} rows={8} />
                <JsonBlock label="Итоговая проверка пакета" value={selectedBatch.batchReviewJson} rows={8} />
                <JsonBlock label="Журнал качества" value={selectedBatch.qualityLedgerJson} rows={8} />
                <JsonBlock label="Манифест публикации" value={selectedBatch.exportManifestJson} rows={8} />
              </div>
            </details>

            <div className="space-y-3">
              {(selectedBatch.items || []).map((item, index) => renderBatchItemCard(item, index))}
            </div>
          </div>
        )}
      </Card>
    </div>
  );

  const renderDrafts = () => (
    <Card>
      <SectionTitle icon={FileStack} title="Черновики заданий" subtitle="Здесь лежат готовые или проверяемые черновики. Их можно одобрить, проверить и опубликовать." />
      <div className="space-y-3 mt-4 max-h-[74vh] overflow-auto pr-1">
        {drafts.map((draft) => {
          const selfCheck = extractSelfCheck(draft.draftJson);
          const summary = extractDraftSummary(draft);
          const publishBlockedText = explainPublishBlocker(draft);
          return (
            <div key={draft.id} className="rounded-2xl border border-neutral-200/70 dark:border-neutral-800 p-4 space-y-3">
              <div className="flex flex-wrap gap-2 items-center justify-between">
                <div className="font-medium">{draft.title || 'Без названия'}</div>
                <div className="flex gap-2 flex-wrap">
                  <Badge intent="secondary">{draft.assignmentType}</Badge>
                  <Badge intent={statusTone(draft.status)}>{toRuStatus(draft.status)}</Badge>
                  {draft.batchId ? <Badge intent="secondary">из пакета</Badge> : null}
                  {selfCheck?.status ? <Badge intent={selfCheckTone(selfCheck.status)}>{toRuStatus(selfCheck.status)}</Badge> : null}
                </div>
              </div>
              <div className="text-xs opacity-70 break-all">ID черновика: {draft.id}</div>
              {draft.batchId ? <div className="text-xs opacity-70 break-all">ID пакета: {draft.batchId}</div> : null}
              <div className="text-sm opacity-85">{summary || 'Короткого описания пока нет.'}</div>
              <div className="text-sm opacity-75">{canPublishDraft(draft) ? 'Публикация уже доступна.' : publishBlockedText}</div>
              <div className="flex gap-2 flex-wrap">
                <Button variant="outline" disabled={busy} onClick={() => reviewDraftNow(draft.id, 'approve')}>Одобрить</Button>
                <Button variant="outline" disabled={busy} onClick={() => reviewDraftNow(draft.id, 'reject')}>Отклонить</Button>
                <Button variant="outline" disabled={busy} onClick={() => validateDraftNow(draft)}>Самопроверка</Button>
                <Button disabled={busy || !canPublishDraft(draft)} onClick={() => publishDraftNow(draft)}>Опубликовать</Button>
                {draft.batchId ? <Button variant="outline" onClick={() => openBatch(draft.batchId)}>Открыть пакет</Button> : null}
              </div>
              <details>
                <summary className="cursor-pointer opacity-80">Показать технический JSON</summary>
                <div className="mt-3">
                  <JsonBlock label="Содержимое черновика" value={draft.draftJson} rows={8} />
                </div>
              </details>
            </div>
          );
        })}
        {!drafts.length ? <Card>Черновиков пока нет.</Card> : null}
      </div>
    </Card>
  );

  return (
    <Layout title="AI Foundry">
      <div className="space-y-6">
        <div className="flex flex-col gap-4 lg:flex-row lg:items-end lg:justify-between">
          <div>
            <h1 className="text-2xl font-semibold flex items-center gap-2"><Sparkles size={22} /> AI-раздел пакетной генерации</h1>
            <p className="text-sm opacity-70 mt-1">Здесь запускаются пакеты заданий, отслеживаются шаги нейросети и публикуются готовые результаты.</p>
          </div>
          <div className="flex gap-2 flex-wrap">
            {AI_TABS.map((tab) => (
              <TabButton
                key={tab.key}
                active={activeTab === tab.key}
                icon={tab.icon}
                count={tab.key === 'queue' ? jobs.length : tab.key === 'batches' ? batches.length : tab.key === 'drafts' ? drafts.length : undefined}
                onClick={() => setActiveTab(tab.key)}
              >
                {tab.label}
              </TabButton>
            ))}
          </div>
        </div>

        {pageError ? <Card className="border-red-300/60 text-red-500">{pageError}</Card> : null}

        {activeTab === 'overview' ? renderOverview() : null}
        {activeTab === 'create' ? renderCreate() : null}
        {activeTab === 'queue' ? renderJobsQueue() : null}
        {activeTab === 'batches' ? renderBatches() : null}
        {activeTab === 'drafts' ? renderDrafts() : null}

        <div className="grid md:grid-cols-3 gap-4">
          <CompactStatCard title="Этапов увидели" value={jobs.filter((job) => String(job.type || '').startsWith('assignment_batch_')).length} hint="Сколько этапов пакетной схемы уже прошло через очередь" />
          <CompactStatCard title="Пакетов готово" value={batches.filter((batch) => String(batch.status || '').toLowerCase() === 'ready').length} hint="Пакеты, дошедшие до состояния «готово»" />
          <CompactStatCard title="Опубликовано" value={drafts.filter((draft) => String(draft.status || '').toLowerCase() === 'published').length} hint="Черновики, уже превращённые в обычные задания" />
        </div>
      </div>
    </Layout>
  );
}
