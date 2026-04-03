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
  { key: 'create', label: 'Batch Lab', icon: Wand2 },
  { key: 'queue', label: 'Jobs', icon: ListTodo },
  { key: 'batches', label: 'Batches', icon: Sparkles },
  { key: 'drafts', label: 'Drafts', icon: FileStack },
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

function statusTone(status) {
  switch ((status || '').toLowerCase()) {
    case 'done':
    case 'ready':
    case 'published':
      return 'success';
    case 'failed':
    case 'error':
      return 'danger';
    case 'running':
    case 'processing':
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

function extractSelfCheck(draftJson) {
  try {
    const parsed = typeof draftJson === 'string' ? JSON.parse(draftJson) : draftJson;
    return parsed?.meta?.selfCheck || parsed?.selfCheck || null;
  } catch {
    return null;
  }
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
      handleApiError(e, notify, 'Не удалось открыть batch');
    }
  };

  const openJob = async (id) => {
    try {
      const data = await getAiJob(id);
      setSelectedJob(data || null);
      setActiveTab('queue');
    } catch (e) {
      handleApiError(e, notify, 'Не удалось открыть AI job');
    }
  };

  const openBatch = async (id) => {
    try {
      const data = await getAiBatch(id);
      setSelectedBatch(data || null);
      setActiveTab('batches');
    } catch (e) {
      handleApiError(e, notify, 'Не удалось открыть batch');
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
      notify.success('Foundry batch поставлен в очередь');
      await loadAndOpenBatch(created?.id);
    } catch (e) {
      handleApiError(e, notify, 'Не удалось создать Foundry batch');
    } finally {
      setBusy(false);
    }
  };

  const reviewDraftNow = async (id, action) => {
    try {
      await reviewAiDraft(id, action);
      notify.success(action === 'approve' ? 'Черновик approved' : 'Черновик rejected');
      await load();
    } catch (e) {
      handleApiError(e, notify, 'Не удалось обновить статус черновика');
    }
  };

  const publishDraftNow = async (draft) => {
    try {
      setBusy(true);
      const data = await publishAiDraft(draft.id, { courseId: draft.courseId || courseScopeId || null });
      notify.success(`Черновик опубликован как задание: ${data?.assignmentId || ''}`.trim());
      await load();
    } catch (e) {
      handleApiError(e, notify, 'Не удалось опубликовать AI draft');
    } finally {
      setBusy(false);
    }
  };

  const validateDraftNow = async (draft) => {
    try {
      setBusy(true);
      const data = await validateAiDraft(draft.id, { usePythonSelfCheck: true });
      notify.success('AI self-check поставлен в очередь');
      await openJob(data?.id);
    } catch (e) {
      handleApiError(e, notify, 'Не удалось запустить AI self-check');
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
      if (created?.id) {
        setCourseScopeId(created.id);
      }
    } catch (e) {
      handleApiError(e, notify, 'Не удалось создать курс');
    } finally {
      setBusy(false);
    }
  };

  const renderCourseScopeCard = () => (
    <Card>
      <SectionTitle icon={FolderPlus} title="Курс для Foundry batch" subtitle="Выбери курс, в который будет создаваться новый batch. Старые single-shot генераторы убраны из UI." />
      <div className="grid lg:grid-cols-[1fr,auto,auto] gap-3 mt-4 items-end">
        <Field label="Текущий курс">
          <Select value={courseScopeId} onChange={(e) => setCourseScopeId(e.target.value)}>
            <option value="">Не выбран</option>
            {courses.map((course) => (
              <option key={course.id} value={course.id}>{course.title || course.id}</option>
            ))}
          </Select>
        </Field>
        <Button variant="outline" onClick={() => setShowCreateCourse((prev) => !prev)}>
          <FolderPlus size={16} />
          <span className="ml-1">{showCreateCourse ? 'Скрыть создание' : 'Создать новый курс'}</span>
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
          <label className="text-sm flex items-center gap-2 mt-7"><input type="checkbox" checked={!!courseCreateForm.isPublic} onChange={(e) => setCourseCreateForm((prev) => ({ ...prev, isPublic: e.target.checked }))} /> Public</label>
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
        <CompactStatCard title="Jobs" value={jobs.length} hint="Все AI jobs" />
        <CompactStatCard title="Batches" value={batches.length} hint="Foundry batch сущности" />
        <CompactStatCard title="Ready drafts" value={drafts.filter((d) => String(d.status || '').toLowerCase() === 'ready').length} hint="Готовы к review/publish" />
        <CompactStatCard title="Running jobs" value={jobs.filter((j) => String(j.status || '').toLowerCase() === 'running').length} hint="Сейчас обрабатываются worker'ом" />
      </div>
      <Card>
        <SectionTitle icon={Sparkles} title="Что осталось в этой странице" subtitle="Только новый маршрут: создать Foundry batch, смотреть батчи, смотреть очередь jobs и публиковать готовые drafts." />
        <div className="mt-4 text-sm opacity-80 space-y-2">
          <div>• legacy single-shot генераторы удалены из UI;</div>
          <div>• ручной generic enqueue удалён;</div>
          <div>• фронт теперь толкает только <code>/api/admin/ai/batches/generate</code>.</div>
        </div>
      </Card>
    </div>
  );

  const renderCreate = () => (
    <div className="space-y-6">
      {renderCourseScopeCard()}
      <Card>
        <SectionTitle icon={Wand2} title="Создать Foundry batch" subtitle="Эта форма создаёт только batch job и больше не может запустить старый assignment_generate_from_text / from_file путь." />
        <div className="grid md:grid-cols-2 gap-4 mt-4">
          <Field label="Course id">
            <Input value={batchForm.courseId} onChange={(e) => setBatchForm((p) => ({ ...p, courseId: e.target.value }))} placeholder="обязательно" />
          </Field>
          <Field label="Assignment type">
            <Select value={batchForm.assignmentType} onChange={(e) => setBatchForm((p) => ({ ...p, assignmentType: e.target.value }))}>
              <option value="math">math</option>
              <option value="test">test</option>
              <option value="code-test">code-test</option>
            </Select>
          </Field>
          <Field label="Mode">
            <Input value={batchForm.mode} onChange={(e) => setBatchForm((p) => ({ ...p, mode: e.target.value }))} placeholder="topic-pack" />
          </Field>
          <Field label="Difficulty">
            <Input type="number" min="1" max="5" value={batchForm.difficulty} onChange={(e) => setBatchForm((p) => ({ ...p, difficulty: e.target.value }))} />
          </Field>
          <Field label="Count">
            <Input type="number" min="1" max="20" value={batchForm.count} onChange={(e) => setBatchForm((p) => ({ ...p, count: e.target.value }))} />
          </Field>
          <Field label="Priority">
            <Input type="number" min="0" max="100" value={batchForm.priority} onChange={(e) => setBatchForm((p) => ({ ...p, priority: e.target.value }))} />
          </Field>
          <div className="md:col-span-2">
            <Field label="Prompt">
              <Textarea rows={6} value={batchForm.prompt} onChange={(e) => setBatchForm((p) => ({ ...p, prompt: e.target.value }))} />
            </Field>
          </div>
          <div className="md:col-span-2">
            <Field label="Notes">
              <Textarea rows={4} value={batchForm.notes} onChange={(e) => setBatchForm((p) => ({ ...p, notes: e.target.value }))} />
            </Field>
          </div>
        </div>
        <div className="mt-4 flex gap-2 flex-wrap">
          <Button disabled={busy || !batchForm.courseId} onClick={submitBatch}>Создать batch</Button>
          <Button variant="outline" onClick={() => setBatchForm({ ...batchEmpty, courseId: courseScopeId || '' })}>Сбросить</Button>
        </div>
      </Card>
    </div>
  );

  const renderJobsQueue = () => (
    <div className="grid xl:grid-cols-[0.92fr,1.08fr] gap-6">
      <div className="space-y-4">
        <Card>
          <SectionTitle icon={ListTodo} title="Очередь jobs" subtitle="Здесь должны появляться Foundry stage jobs вместо старых single-shot генераторов." />
          <div className="grid md:grid-cols-3 gap-4 mt-4">
            <Field label="Статус"><Input value={filters.status} onChange={(e) => setFilters((p) => ({ ...p, status: e.target.value }))} placeholder="pending / running / done / failed" /></Field>
            <Field label="Тип"><Input value={filters.type} onChange={(e) => setFilters((p) => ({ ...p, type: e.target.value }))} placeholder="assignment_batch_plan / ..." /></Field>
            <Field label="Поиск"><Input value={filters.query} onChange={(e) => setFilters((p) => ({ ...p, query: e.target.value }))} placeholder="search" /></Field>
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
                  <div className="text-sm opacity-80 mt-2">target: {job.targetEntityType || '—'} {job.targetEntityId || ''}</div>
                  <div className="text-xs opacity-60 mt-1">created: {job.createdAtUtc ? new Date(job.createdAtUtc).toLocaleString() : '—'}</div>
                </div>
                <div className="flex flex-wrap gap-2 items-center">
                  <Badge intent={statusTone(job.status)}>{job.status}</Badge>
                  <Button variant="outline" onClick={() => openJob(job.id)}>Открыть</Button>
                </div>
              </div>
            </Card>
          ))}
          {!loading && filteredJobs.length === 0 ? <Card>AI jobs пока нет.</Card> : null}
        </div>
      </div>

      <Card>
        <SectionTitle icon={Brain} title="Детали job" subtitle="Смотри input/result и проверяй, что stages идут по batch-цепочке." />
        {!selectedJob ? <div className="text-sm opacity-70 mt-4">Выбери job слева.</div> : (
          <div className="space-y-3 text-sm mt-4">
            <div className="flex flex-wrap gap-2">
              <Badge intent={statusTone(selectedJob.status)}>{selectedJob.status}</Badge>
              {selectedJob.modelName ? <Badge intent="secondary">model: {selectedJob.modelName}</Badge> : null}
              {selectedJob.workerId ? <Badge intent="secondary">worker: {selectedJob.workerId}</Badge> : null}
            </div>
            <div><span className="opacity-70">Type:</span> <span className="break-all">{selectedJob.type}</span></div>
            <div><span className="opacity-70">Target:</span> <span className="break-all">{selectedJob.targetEntityType || '—'} {selectedJob.targetEntityId || ''}</span></div>
            <div><span className="opacity-70">Created:</span> {selectedJob.createdAtUtc ? new Date(selectedJob.createdAtUtc).toLocaleString() : '—'}</div>
            <div><span className="opacity-70">Started:</span> {selectedJob.startedAtUtc ? new Date(selectedJob.startedAtUtc).toLocaleString() : '—'}</div>
            <div><span className="opacity-70">Completed:</span> {selectedJob.completedAtUtc ? new Date(selectedJob.completedAtUtc).toLocaleString() : '—'}</div>
            <Field label="InputJson"><Textarea rows={12} readOnly value={prettyJson(selectedJob.inputJson)} /></Field>
            <Field label="ResultJson"><Textarea rows={12} readOnly value={prettyJson(selectedJob.resultJson)} /></Field>
            <Field label="Error"><Textarea rows={4} readOnly value={selectedJob.errorText || ''} /></Field>
          </div>
        )}
      </Card>
    </div>
  );

  const renderBatches = () => (
    <div className="grid xl:grid-cols-[0.92fr,1.08fr] gap-6">
      <div className="space-y-4">
        <Card>
          <SectionTitle icon={Sparkles} title="Foundry batches" subtitle="Отдельный список batch-сущностей, чтобы видеть stage и прогресс без копания в jobs." />
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
                  <div className="text-xs opacity-60 mt-1">updated: {batch.updatedAtUtc ? new Date(batch.updatedAtUtc).toLocaleString() : '—'}</div>
                </div>
                <div className="flex flex-wrap gap-2 items-center">
                  <Badge intent={statusTone(batch.status)}>{batch.status}</Badge>
                  {batch.currentStage ? <Badge intent="secondary">{batch.currentStage}</Badge> : null}
                  <Button variant="outline" onClick={() => openBatch(batch.id)}>Открыть</Button>
                </div>
              </div>
            </Card>
          ))}
          {!loading && filteredBatches.length === 0 ? <Card>Foundry batches пока нет.</Card> : null}
        </div>
      </div>

      <Card>
        <SectionTitle icon={Brain} title="Детали batch" subtitle="Смотри items, review json и текущую стадию оркестрации." />
        {!selectedBatch ? <div className="text-sm opacity-70 mt-4">Выбери batch слева.</div> : (
          <div className="space-y-4 text-sm mt-4">
            <div className="flex flex-wrap gap-2">
              <Badge intent={statusTone(selectedBatch.status)}>{selectedBatch.status}</Badge>
              {selectedBatch.currentStage ? <Badge intent="secondary">{selectedBatch.currentStage}</Badge> : null}
              <Badge intent="secondary">items: {selectedBatch.itemsCount ?? selectedBatch.items?.length ?? 0}</Badge>
              <Badge intent="secondary">ready: {selectedBatch.readyItemsCount ?? 0}</Badge>
            </div>
            <div><span className="opacity-70">Id:</span> <span className="break-all">{selectedBatch.id}</span></div>
            <div><span className="opacity-70">Course:</span> <span className="break-all">{selectedBatch.courseId || '—'}</span></div>
            <div><span className="opacity-70">Assignment type:</span> {selectedBatch.assignmentType}</div>
            <div><span className="opacity-70">Mode:</span> {selectedBatch.mode || '—'}</div>
            <div><span className="opacity-70">Prompt:</span> {selectedBatch.prompt}</div>
            <Field label="PlannerFeedbackJson"><Textarea rows={8} readOnly value={prettyJson(selectedBatch.plannerFeedbackJson)} /></Field>
            <Field label="BatchReviewJson"><Textarea rows={8} readOnly value={prettyJson(selectedBatch.batchReviewJson)} /></Field>
            <Field label="QualityLedgerJson"><Textarea rows={8} readOnly value={prettyJson(selectedBatch.qualityLedgerJson)} /></Field>
            <Field label="ExportManifestJson"><Textarea rows={8} readOnly value={prettyJson(selectedBatch.exportManifestJson)} /></Field>
            <div className="space-y-3">
              {(selectedBatch.items || []).map((item) => (
                <div key={item.id} className="rounded-2xl border border-neutral-200/70 dark:border-neutral-800 p-3 space-y-2">
                  <div className="flex flex-wrap gap-2 items-center justify-between">
                    <div className="font-medium">#{item.index + 1} · {item.targetSkill || 'slot'}</div>
                    <div className="flex gap-2 flex-wrap">
                      <Badge intent={statusTone(item.status)}>{item.status}</Badge>
                      {item.draftId ? <Badge intent="secondary">draft linked</Badge> : null}
                    </div>
                  </div>
                  <div className="text-xs opacity-70">difficulty: {item.difficultyTarget} · repairs: {item.repairCount}</div>
                  {item.microGoal ? <div className="text-sm opacity-80">{item.microGoal}</div> : null}
                  <details>
                    <summary className="cursor-pointer opacity-80">JSON детали</summary>
                    <div className="mt-2 space-y-2">
                      <Field label="BriefJson"><Textarea rows={6} readOnly value={prettyJson(item.briefJson)} /></Field>
                      <Field label="ReferencePackJson"><Textarea rows={6} readOnly value={prettyJson(item.referencePackJson)} /></Field>
                      <Field label="ScorecardJson"><Textarea rows={6} readOnly value={prettyJson(item.scorecardJson)} /></Field>
                    </div>
                  </details>
                </div>
              ))}
            </div>
          </div>
        )}
      </Card>
    </div>
  );

  const renderDrafts = () => (
    <Card>
      <SectionTitle icon={FileStack} title="Drafts" subtitle="Готовые или проверяемые черновики из batch pipeline." />
      <div className="space-y-3 mt-4 max-h-[74vh] overflow-auto pr-1">
        {drafts.map((draft) => {
          const selfCheck = extractSelfCheck(draft.draftJson);
          return (
            <div key={draft.id} className="rounded-2xl border border-neutral-200/70 dark:border-neutral-800 p-4 space-y-2">
              <div className="flex flex-wrap gap-2 items-center justify-between">
                <div className="font-medium">{draft.title}</div>
                <div className="flex gap-2 flex-wrap">
                  <Badge intent="secondary">{draft.assignmentType}</Badge>
                  <Badge intent={statusTone(draft.status)}>{draft.status}</Badge>
                  {draft.batchId ? <Badge intent="secondary">batch</Badge> : null}
                  {selfCheck?.status ? <Badge intent={selfCheckTone(selfCheck.status)}>{selfCheck.status}</Badge> : null}
                </div>
              </div>
              <div className="text-xs opacity-70 break-all">draft id: {draft.id}</div>
              {draft.batchId ? <div className="text-xs opacity-70 break-all">batch id: {draft.batchId}</div> : null}
              <div className="text-sm opacity-80">{draft.summary || 'Без summary'}</div>
              <Field label="DraftJson"><Textarea rows={8} readOnly value={prettyJson(draft.draftJson)} /></Field>
              <div className="flex gap-2 flex-wrap">
                <Button variant="outline" disabled={busy} onClick={() => reviewDraftNow(draft.id, 'approve')}>Approve</Button>
                <Button variant="outline" disabled={busy} onClick={() => reviewDraftNow(draft.id, 'reject')}>Reject</Button>
                <Button variant="outline" disabled={busy} onClick={() => validateDraftNow(draft)}>Self-check</Button>
                <Button disabled={busy || !canPublishDraft(draft)} onClick={() => publishDraftNow(draft)}>Publish</Button>
                {draft.batchId ? <Button variant="outline" onClick={() => openBatch(draft.batchId)}>Открыть batch</Button> : null}
              </div>
            </div>
          );
        })}
        {!drafts.length ? <Card>Drafts пока нет.</Card> : null}
      </div>
    </Card>
  );

  return (
    <Layout title="Admin AI">
      <div className="space-y-6">
        <div className="flex flex-col gap-4 lg:flex-row lg:items-end lg:justify-between">
          <div>
            <h1 className="text-2xl font-semibold flex items-center gap-2"><Sparkles size={22} /> AI Foundry Admin</h1>
            <p className="text-sm opacity-70 mt-1">Фронт очищен от legacy single-shot генераторов. Эта страница работает только с новым batch pipeline.</p>
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
          <CompactStatCard title="Foundry stages seen" value={jobs.filter((job) => String(job.type || '').startsWith('assignment_batch_')).length} hint="Сколько stage jobs уже видно в очереди" />
          <CompactStatCard title="Batches ready" value={batches.filter((batch) => String(batch.status || '').toLowerCase() === 'ready').length} hint="Пакеты, дошедшие до ready" />
          <CompactStatCard title="Published drafts" value={drafts.filter((draft) => String(draft.status || '').toLowerCase() === 'published').length} hint="Уже ушли в assignment" />
        </div>
      </div>
    </Layout>
  );
}
