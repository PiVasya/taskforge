import React, { useEffect, useMemo, useState } from 'react';
import Layout from '../../components/Layout';
import { Badge, Button, Card, Field, Input, Select, Textarea } from '../../components/ui';
import {
  analyzeAiAssignment,
  createAiJob,
  generateAiAssignmentFromFile,
  generateAiAssignmentFromText,
  getAiAssignmentInsights,
  getAiDrafts,
  getAiJob,
  getAiJobs,
  getAiRiskReports,
  getAiSubmissionReviews,
  reviewAiDraft,
  reviewAiSubmission,
  reviewAiUser,
  publishAiDraft,
  validateAiDraft,
} from '../../api/aiAdmin';
import { createCourse, getCourses } from '../../api/courses';
import { handleApiError } from '../../utils/handleApiError';
import { useNotify } from '../../components/notify/NotifyProvider';
import {
  Bot,
  Brain,
  CheckCircle2,
  Clock3,
  FileStack,
  FolderPlus,
  LayoutDashboard,
  ListTodo,
  RefreshCcw,
  Search,
  ShieldAlert,
  Sparkles,
  Wand2,
} from 'lucide-react';

const JOB_TYPES = [
  { value: 'assignment_generate_from_text', label: 'Генерация задания из текста' },
  { value: 'assignment_generate_from_file', label: 'Генерация задания из файла' },
  { value: 'assignment_improve_existing', label: 'Улучшение существующего задания' },
  { value: 'assignment_analyze_existing', label: 'Анализ существующего задания' },
  { value: 'submission_review', label: 'Проверка решения пользователя' },
  { value: 'user_risk_review', label: 'Проверка пользователя / risk review' },
  { value: 'support_message_review', label: 'Анализ тикета/поддержки' },
  { value: 'minecraft_chat_review', label: 'Анализ Minecraft-чата' },
];

const AI_TABS = [
  { key: 'overview', label: 'Обзор', icon: LayoutDashboard },
  { key: 'create', label: 'Создать', icon: Wand2 },
  { key: 'queue', label: 'Jobs', icon: ListTodo },
  { key: 'drafts', label: 'Drafts', icon: FileStack },
  { key: 'reviews', label: 'Reviews', icon: Brain },
  { key: 'risk', label: 'Risk', icon: ShieldAlert },
  { key: 'insights', label: 'Insights', icon: Search },
];

const CREATOR_TABS = [
  { key: 'from-text', label: 'Из текста' },
  { key: 'from-file', label: 'Из файла' },
  { key: 'analyze', label: 'Анализ задания' },
  { key: 'submission', label: 'Review решения' },
  { key: 'user', label: 'Risk review' },
  { key: 'generic', label: 'Ручной job' },
];

const genericEmpty = {
  type: 'assignment_generate_from_text',
  targetEntityType: '',
  targetEntityId: '',
  courseId: '',
  priority: 10,
  inputJson: JSON.stringify({ prompt: '', assignmentType: 'math', notes: '' }, null, 2),
  fileKey: '',
  fileName: '',
  fileMime: '',
  fileUrl: '',
};

const fromTextEmpty = {
  courseId: '',
  assignmentType: 'math',
  prompt: 'Сгенерируй 1-2 задания пригодных для текущего редактора TaskForge.',
  sourceText: '',
  titleHint: '',
  difficulty: 2,
  count: 1,
  notes: '',
  priority: 20,
  enableSelfCheck: true,
};

const fromFileEmpty = {
  courseId: '',
  assignmentType: 'math',
  prompt: 'Извлеки материал из файла и собери задания пригодные для текущего редактора TaskForge.',
  fileKey: '',
  originalName: '',
  mimeType: '',
  publicUrl: '',
  titleHint: '',
  difficulty: 2,
  count: 1,
  notes: '',
  priority: 20,
  enableSelfCheck: true,
};

const analyzeEmpty = {
  assignmentId: '',
  includeStats: true,
  includeAttempts: true,
  prompt: 'Проанализируй качество задания, найди слабые места, неудачные distractors и предложи улучшения.',
  priority: 12,
};

const reviewSubmissionEmpty = {
  sourceType: 'math',
  sourceAttemptId: '',
  prompt: 'Оцени попытку, найди suspicious patterns, объясни странные места и сформируй короткий verdict.',
  priority: 10,
};

const reviewUserEmpty = {
  userId: '',
  includeSupport: true,
  includeMinecraft: true,
  includeRecentAttempts: true,
  prompt: 'Проверь пользователя на suspicious activity, резкие скачки качества, токсичность и аномалии.',
  priority: 10,
};

const courseCreateEmpty = {
  title: 'Новый курс',
  description: '',
  isPublic: false,
};

function statusTone(status) {
  switch ((status || '').toLowerCase()) {
    case 'done': return 'success';
    case 'failed': return 'danger';
    case 'running': return 'outline';
    case 'retry': return 'outline';
    default: return 'secondary';
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
  const [total, setTotal] = useState(0);
  const [drafts, setDrafts] = useState([]);
  const [submissionReviews, setSubmissionReviews] = useState([]);
  const [riskReports, setRiskReports] = useState([]);
  const [assignmentInsights, setAssignmentInsights] = useState([]);
  const [courses, setCourses] = useState([]);
  const [selectedJob, setSelectedJob] = useState(null);
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState(false);
  const [pageError, setPageError] = useState('');
  const [activeTab, setActiveTab] = useState('overview');
  const [creatorTab, setCreatorTab] = useState('from-text');
  const [filters, setFilters] = useState({ status: '', type: '', query: '' });
  const [courseScopeId, setCourseScopeId] = useState('');
  const [showCreateCourse, setShowCreateCourse] = useState(false);
  const [courseCreateForm, setCourseCreateForm] = useState(courseCreateEmpty);
  const [genericForm, setGenericForm] = useState(genericEmpty);
  const [fromTextForm, setFromTextForm] = useState(fromTextEmpty);
  const [fromFileForm, setFromFileForm] = useState(fromFileEmpty);
  const [analyzeForm, setAnalyzeForm] = useState(analyzeEmpty);
  const [reviewSubmissionForm, setReviewSubmissionForm] = useState(reviewSubmissionEmpty);
  const [reviewUserForm, setReviewUserForm] = useState(reviewUserEmpty);

  const load = async () => {
    try {
      setLoading(true);
      const [jobsData, draftsData, reviewsData, risksData, insightsData, coursesData] = await Promise.all([
        getAiJobs({ status: filters.status || undefined, type: filters.type || undefined, page: 1, pageSize: 50 }),
        getAiDrafts(),
        getAiSubmissionReviews(),
        getAiRiskReports(),
        getAiAssignmentInsights(),
        getCourses(),
      ]);
      setJobs(Array.isArray(jobsData.items) ? jobsData.items : []);
      setTotal(Number(jobsData.total || 0));
      setDrafts(Array.isArray(draftsData) ? draftsData : []);
      setSubmissionReviews(Array.isArray(reviewsData) ? reviewsData : []);
      setRiskReports(Array.isArray(risksData) ? risksData : []);
      setAssignmentInsights(Array.isArray(insightsData) ? insightsData : []);
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

  const selectedCourse = useMemo(
    () => courses.find((course) => String(course.id) === String(courseScopeId)) || null,
    [courses, courseScopeId],
  );

  useEffect(() => {
    if (!courseScopeId) return;
    setFromTextForm((prev) => ({ ...prev, courseId: courseScopeId }));
    setFromFileForm((prev) => ({ ...prev, courseId: courseScopeId }));
    setGenericForm((prev) => ({ ...prev, courseId: courseScopeId }));
  }, [courseScopeId]);

  const filteredJobs = useMemo(() => {
    const q = (filters.query || '').trim().toLowerCase();
    if (!q) return jobs;
    return jobs.filter((job) => {
      const blob = [job.id, job.type, job.status, job.targetEntityType, job.targetEntityId, job.workerId].join(' ').toLowerCase();
      return blob.includes(q);
    });
  }, [filters.query, jobs]);

  const loadAndOpen = async (jobId) => {
    await load();
    if (jobId) {
      try {
        const data = await getAiJob(jobId);
        setSelectedJob(data || null);
        setActiveTab('queue');
      } catch (e) {
        handleApiError(e, notify, 'Не удалось открыть AI job');
      }
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

  const submitAndOpen = async (action, successMessage) => {
    try {
      setBusy(true);
      const created = await action();
      notify.success(successMessage);
      await loadAndOpen(created?.id);
    } catch (e) {
      handleApiError(e, notify, 'Не удалось создать AI job');
    } finally {
      setBusy(false);
    }
  };

  const genericSubmit = async () => {
    await submitAndOpen(async () => createAiJob({
      type: genericForm.type,
      targetEntityType: genericForm.targetEntityType || null,
      targetEntityId: genericForm.targetEntityId || null,
      courseId: genericForm.courseId || null,
      priority: Number(genericForm.priority || 0),
      inputJson: genericForm.inputJson || '{}',
      files: genericForm.fileKey
        ? [{ fileKey: genericForm.fileKey, originalName: genericForm.fileName || null, mimeType: genericForm.fileMime || null, publicUrl: genericForm.fileUrl || null }]
        : [],
    }), 'AI job поставлен в очередь');
  };

  const reviewDraft = async (id, action) => {
    try {
      await reviewAiDraft(id, action);
      notify.success(action === 'approve' ? 'Черновик отмечен как approved' : 'Черновик отмечен как rejected');
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
      await loadAndOpen(data?.id);
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

  const quickTemplates = useMemo(() => ([
    {
      title: 'Math из текста',
      apply: () => {
        setActiveTab('create');
        setCreatorTab('from-text');
        setFromTextForm((p) => ({ ...p, assignmentType: 'math', prompt: 'Сгенерируй 1-2 задания по математике с блоками info / number / set / single-choice и кратким разбором.' }));
      },
    },
    {
      title: 'Test из файла',
      apply: () => {
        setActiveTab('create');
        setCreatorTab('from-file');
        setFromFileForm((p) => ({ ...p, assignmentType: 'test', prompt: 'Из файла собери тест с богатым rich statement, вариантами ответов и короткими объяснениями.' }));
      },
    },
    {
      title: 'Risk review юзера',
      apply: () => {
        setActiveTab('create');
        setCreatorTab('user');
        setReviewUserForm((p) => ({ ...p, prompt: 'Проверь пользователя на suspicious activity, токсичность, coordinated abuse и резкие скачки качества.' }));
      },
    },
  ]), []);

  const renderCourseScopeCard = () => (
    <Card>
      <SectionTitle icon={FolderPlus} title="Курс для генерации" subtitle="Больше не нужно помнить course id. Выбери существующий курс или создай новый прямо отсюда." />
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
        <Button variant="outline" onClick={load}><RefreshCcw size={16} /> <span className="ml-1">Обновить курсы</span></Button>
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
              <Textarea rows={3} value={courseCreateForm.description} onChange={(e) => setCourseCreateForm((prev) => ({ ...prev, description: e.target.value }))} />
            </Field>
          </div>
          <div className="md:col-span-2 flex gap-2 flex-wrap">
            <Button disabled={busy} onClick={handleCreateCourse}>Создать курс и выбрать его</Button>
            <Button variant="outline" onClick={() => setShowCreateCourse(false)}>Отмена</Button>
          </div>
        </div>
      ) : null}
    </Card>
  );

  const renderCreatePanel = () => {
    const commonCourseHint = courseScopeId ? `Будет использован курс: ${selectedCourse?.title || courseScopeId}` : 'Выбери курс выше, чтобы не вставлять id руками.';

    if (creatorTab === 'from-text') {
      return (
        <Card>
          <SectionTitle icon={Sparkles} title="Генерация задания из текста" subtitle={commonCourseHint} />
          <div className="grid md:grid-cols-2 gap-4 mt-4">
            <Field label="Курс"><Input value={fromTextForm.courseId} onChange={(e) => setFromTextForm((p) => ({ ...p, courseId: e.target.value }))} placeholder="course id подставится автоматически" /></Field>
            <Field label="Тип задания"><Select value={fromTextForm.assignmentType} onChange={(e) => setFromTextForm((p) => ({ ...p, assignmentType: e.target.value }))}><option value="math">math</option><option value="test">test</option><option value="code-test">code-test</option></Select></Field>
            <label className="text-sm flex items-center gap-2"><input type="checkbox" checked={!!fromTextForm.enableSelfCheck} onChange={(e) => setFromTextForm((p) => ({ ...p, enableSelfCheck: e.target.checked }))} /> Self-check</label>
            <Field label="Title hint"><Input value={fromTextForm.titleHint} onChange={(e) => setFromTextForm((p) => ({ ...p, titleHint: e.target.value }))} /></Field>
            <Field label="Difficulty"><Input type="number" min="1" max="5" value={fromTextForm.difficulty} onChange={(e) => setFromTextForm((p) => ({ ...p, difficulty: e.target.value }))} /></Field>
            <Field label="Count"><Input type="number" min="1" max="10" value={fromTextForm.count} onChange={(e) => setFromTextForm((p) => ({ ...p, count: e.target.value }))} /></Field>
            <Field label="Priority"><Input type="number" min="0" max="100" value={fromTextForm.priority} onChange={(e) => setFromTextForm((p) => ({ ...p, priority: e.target.value }))} /></Field>
            <div className="md:col-span-2"><Field label="Промпт для AI"><Textarea rows={5} value={fromTextForm.prompt} onChange={(e) => setFromTextForm((p) => ({ ...p, prompt: e.target.value }))} /></Field></div>
            <div className="md:col-span-2"><Field label="Исходный текст / конспект"><Textarea rows={8} value={fromTextForm.sourceText} onChange={(e) => setFromTextForm((p) => ({ ...p, sourceText: e.target.value }))} /></Field></div>
            <div className="md:col-span-2"><Field label="Notes"><Textarea rows={3} value={fromTextForm.notes} onChange={(e) => setFromTextForm((p) => ({ ...p, notes: e.target.value }))} /></Field></div>
          </div>
          <div className="mt-4 flex gap-2 flex-wrap">
            <Button disabled={busy} onClick={() => submitAndOpen(() => generateAiAssignmentFromText({ ...fromTextForm, difficulty: Number(fromTextForm.difficulty || 2), count: Number(fromTextForm.count || 1), priority: Number(fromTextForm.priority || 20) }), 'AI job на генерацию из текста поставлен в очередь')}>Создать AI job</Button>
            <Button variant="outline" onClick={() => setFromTextForm({ ...fromTextEmpty, courseId: courseScopeId || '' })}>Сбросить форму</Button>
          </div>
        </Card>
      );
    }

    if (creatorTab === 'from-file') {
      return (
        <Card>
          <SectionTitle icon={FileStack} title="Генерация задания из файла" subtitle={commonCourseHint} />
          <div className="grid md:grid-cols-2 gap-4 mt-4">
            <Field label="Курс"><Input value={fromFileForm.courseId} onChange={(e) => setFromFileForm((p) => ({ ...p, courseId: e.target.value }))} placeholder="course id подставится автоматически" /></Field>
            <Field label="Тип задания"><Select value={fromFileForm.assignmentType} onChange={(e) => setFromFileForm((p) => ({ ...p, assignmentType: e.target.value }))}><option value="math">math</option><option value="test">test</option><option value="code-test">code-test</option></Select></Field>
            <label className="text-sm flex items-center gap-2"><input type="checkbox" checked={!!fromFileForm.enableSelfCheck} onChange={(e) => setFromFileForm((p) => ({ ...p, enableSelfCheck: e.target.checked }))} /> Self-check</label>
            <Field label="File key"><Input value={fromFileForm.fileKey} onChange={(e) => setFromFileForm((p) => ({ ...p, fileKey: e.target.value }))} /></Field>
            <Field label="Original name"><Input value={fromFileForm.originalName} onChange={(e) => setFromFileForm((p) => ({ ...p, originalName: e.target.value }))} /></Field>
            <Field label="Mime type"><Input value={fromFileForm.mimeType} onChange={(e) => setFromFileForm((p) => ({ ...p, mimeType: e.target.value }))} /></Field>
            <Field label="Public URL"><Input value={fromFileForm.publicUrl} onChange={(e) => setFromFileForm((p) => ({ ...p, publicUrl: e.target.value }))} /></Field>
            <Field label="Title hint"><Input value={fromFileForm.titleHint} onChange={(e) => setFromFileForm((p) => ({ ...p, titleHint: e.target.value }))} /></Field>
            <Field label="Difficulty"><Input type="number" min="1" max="5" value={fromFileForm.difficulty} onChange={(e) => setFromFileForm((p) => ({ ...p, difficulty: e.target.value }))} /></Field>
            <Field label="Count"><Input type="number" min="1" max="10" value={fromFileForm.count} onChange={(e) => setFromFileForm((p) => ({ ...p, count: e.target.value }))} /></Field>
            <Field label="Priority"><Input type="number" min="0" max="100" value={fromFileForm.priority} onChange={(e) => setFromFileForm((p) => ({ ...p, priority: e.target.value }))} /></Field>
            <div className="md:col-span-2"><Field label="Промпт для AI"><Textarea rows={5} value={fromFileForm.prompt} onChange={(e) => setFromFileForm((p) => ({ ...p, prompt: e.target.value }))} /></Field></div>
            <div className="md:col-span-2"><Field label="Notes"><Textarea rows={3} value={fromFileForm.notes} onChange={(e) => setFromFileForm((p) => ({ ...p, notes: e.target.value }))} /></Field></div>
          </div>
          <div className="mt-4 flex gap-2 flex-wrap">
            <Button disabled={busy} onClick={() => submitAndOpen(() => generateAiAssignmentFromFile({ ...fromFileForm, difficulty: Number(fromFileForm.difficulty || 2), count: Number(fromFileForm.count || 1), priority: Number(fromFileForm.priority || 20) }), 'AI job на генерацию из файла поставлен в очередь')}>Создать AI job</Button>
            <Button variant="outline" onClick={() => setFromFileForm({ ...fromFileEmpty, courseId: courseScopeId || '' })}>Сбросить форму</Button>
          </div>
        </Card>
      );
    }

    if (creatorTab === 'analyze') {
      return (
        <Card>
          <SectionTitle icon={Brain} title="Анализ существующего задания" subtitle="Когда нужно понять, почему задание плохо решается, где слабые distractors и как его улучшить." />
          <div className="grid md:grid-cols-2 gap-4 mt-4">
            <Field label="Assignment id"><Input value={analyzeForm.assignmentId} onChange={(e) => setAnalyzeForm((p) => ({ ...p, assignmentId: e.target.value }))} /></Field>
            <Field label="Priority"><Input type="number" min="0" max="100" value={analyzeForm.priority} onChange={(e) => setAnalyzeForm((p) => ({ ...p, priority: e.target.value }))} /></Field>
            <Field label="Include stats"><Select value={String(analyzeForm.includeStats)} onChange={(e) => setAnalyzeForm((p) => ({ ...p, includeStats: e.target.value === 'true' }))}><option value="true">true</option><option value="false">false</option></Select></Field>
            <Field label="Include attempts"><Select value={String(analyzeForm.includeAttempts)} onChange={(e) => setAnalyzeForm((p) => ({ ...p, includeAttempts: e.target.value === 'true' }))}><option value="true">true</option><option value="false">false</option></Select></Field>
            <div className="md:col-span-2"><Field label="Промпт анализа"><Textarea rows={4} value={analyzeForm.prompt} onChange={(e) => setAnalyzeForm((p) => ({ ...p, prompt: e.target.value }))} /></Field></div>
          </div>
          <div className="mt-4"><Button disabled={busy} onClick={() => submitAndOpen(() => analyzeAiAssignment({ ...analyzeForm, priority: Number(analyzeForm.priority || 12) }), 'AI job на анализ задания поставлен в очередь')}>Проанализировать задание</Button></div>
        </Card>
      );
    }

    if (creatorTab === 'submission') {
      return (
        <Card>
          <SectionTitle icon={Clock3} title="Review решения пользователя" subtitle="Подходит для спорных сабмитов и быстрых подозрений на странный паттерн поведения." />
          <div className="grid md:grid-cols-2 gap-4 mt-4">
            <Field label="Source type"><Select value={reviewSubmissionForm.sourceType} onChange={(e) => setReviewSubmissionForm((p) => ({ ...p, sourceType: e.target.value }))}><option value="math">math</option><option value="test">test</option><option value="code">code</option><option value="image">image</option></Select></Field>
            <Field label="Attempt id"><Input value={reviewSubmissionForm.sourceAttemptId} onChange={(e) => setReviewSubmissionForm((p) => ({ ...p, sourceAttemptId: e.target.value }))} /></Field>
            <Field label="Priority"><Input type="number" min="0" max="100" value={reviewSubmissionForm.priority} onChange={(e) => setReviewSubmissionForm((p) => ({ ...p, priority: e.target.value }))} /></Field>
            <div className="md:col-span-2"><Field label="Промпт"><Textarea rows={4} value={reviewSubmissionForm.prompt} onChange={(e) => setReviewSubmissionForm((p) => ({ ...p, prompt: e.target.value }))} /></Field></div>
          </div>
          <div className="mt-4"><Button variant="outline" disabled={busy} onClick={() => submitAndOpen(() => reviewAiSubmission({ ...reviewSubmissionForm, priority: Number(reviewSubmissionForm.priority || 10) }), 'AI job на review решения поставлен в очередь')}>Review решения</Button></div>
        </Card>
      );
    }

    if (creatorTab === 'user') {
      return (
        <Card>
          <SectionTitle icon={ShieldAlert} title="Risk review пользователя" subtitle="Не нужно идти в другую секцию. Можно сразу собрать отчёт по пользователю отсюда." />
          <div className="grid md:grid-cols-2 gap-4 mt-4">
            <Field label="User id"><Input value={reviewUserForm.userId} onChange={(e) => setReviewUserForm((p) => ({ ...p, userId: e.target.value }))} /></Field>
            <Field label="Priority"><Input type="number" min="0" max="100" value={reviewUserForm.priority} onChange={(e) => setReviewUserForm((p) => ({ ...p, priority: e.target.value }))} /></Field>
            <label className="text-sm flex items-center gap-2"><input type="checkbox" checked={!!reviewUserForm.includeSupport} onChange={(e) => setReviewUserForm((p) => ({ ...p, includeSupport: e.target.checked }))} /> Include support</label>
            <label className="text-sm flex items-center gap-2"><input type="checkbox" checked={!!reviewUserForm.includeMinecraft} onChange={(e) => setReviewUserForm((p) => ({ ...p, includeMinecraft: e.target.checked }))} /> Include Minecraft</label>
            <label className="text-sm flex items-center gap-2 md:col-span-2"><input type="checkbox" checked={!!reviewUserForm.includeRecentAttempts} onChange={(e) => setReviewUserForm((p) => ({ ...p, includeRecentAttempts: e.target.checked }))} /> Include recent attempts</label>
            <div className="md:col-span-2"><Field label="Промпт"><Textarea rows={4} value={reviewUserForm.prompt} onChange={(e) => setReviewUserForm((p) => ({ ...p, prompt: e.target.value }))} /></Field></div>
          </div>
          <div className="mt-4"><Button variant="outline" disabled={busy} onClick={() => submitAndOpen(() => reviewAiUser({ ...reviewUserForm, priority: Number(reviewUserForm.priority || 10) }), 'AI job на review пользователя поставлен в очередь')}>Review пользователя</Button></div>
        </Card>
      );
    }

    return (
      <Card>
        <SectionTitle icon={ListTodo} title="Ручной AI job" subtitle="Для редких кейсов и тестов пайплайна, когда нужен полный контроль над payload." />
        <div className="grid md:grid-cols-2 gap-4 mt-4">
          <Field label="Type"><Select value={genericForm.type} onChange={(e) => setGenericForm((p) => ({ ...p, type: e.target.value }))}>{JOB_TYPES.map((item) => <option key={item.value} value={item.value}>{item.label}</option>)}</Select></Field>
          <Field label="Course id"><Input value={genericForm.courseId} onChange={(e) => setGenericForm((p) => ({ ...p, courseId: e.target.value }))} /></Field>
          <Field label="Target type"><Input value={genericForm.targetEntityType} onChange={(e) => setGenericForm((p) => ({ ...p, targetEntityType: e.target.value }))} /></Field>
          <Field label="Target id"><Input value={genericForm.targetEntityId} onChange={(e) => setGenericForm((p) => ({ ...p, targetEntityId: e.target.value }))} /></Field>
          <Field label="Priority"><Input type="number" min="0" max="100" value={genericForm.priority} onChange={(e) => setGenericForm((p) => ({ ...p, priority: e.target.value }))} /></Field>
          <Field label="File key"><Input value={genericForm.fileKey} onChange={(e) => setGenericForm((p) => ({ ...p, fileKey: e.target.value }))} /></Field>
          <Field label="File name"><Input value={genericForm.fileName} onChange={(e) => setGenericForm((p) => ({ ...p, fileName: e.target.value }))} /></Field>
          <Field label="Mime type"><Input value={genericForm.fileMime} onChange={(e) => setGenericForm((p) => ({ ...p, fileMime: e.target.value }))} /></Field>
          <div className="md:col-span-2"><Field label="Public URL"><Input value={genericForm.fileUrl} onChange={(e) => setGenericForm((p) => ({ ...p, fileUrl: e.target.value }))} /></Field></div>
          <div className="md:col-span-2"><Field label="InputJson"><Textarea rows={12} value={genericForm.inputJson} onChange={(e) => setGenericForm((p) => ({ ...p, inputJson: e.target.value }))} /></Field></div>
        </div>
        <div className="mt-4 flex gap-2 flex-wrap">
          <Button disabled={busy} onClick={genericSubmit}>Поставить job в очередь</Button>
          <Button variant="outline" onClick={() => setGenericForm({ ...genericEmpty, courseId: courseScopeId || '' })}>Сбросить форму</Button>
        </div>
      </Card>
    );
  };

  const renderJobsQueue = () => (
    <div className="grid xl:grid-cols-[0.92fr,1.08fr] gap-6">
      <div className="space-y-4">
        <Card>
          <SectionTitle icon={ListTodo} title="Очередь jobs" subtitle="Фильтруй и открывай задачи без бесконечной прокрутки по всей странице." />
          <div className="grid md:grid-cols-3 gap-4 mt-4">
            <Field label="Статус"><Input value={filters.status} onChange={(e) => setFilters((p) => ({ ...p, status: e.target.value }))} placeholder="pending / running / done / failed" /></Field>
            <Field label="Тип"><Input value={filters.type} onChange={(e) => setFilters((p) => ({ ...p, type: e.target.value }))} placeholder="assignment_generate / review / risk" /></Field>
            <Field label="Поиск по id / type / target"><Input value={filters.query} onChange={(e) => setFilters((p) => ({ ...p, query: e.target.value }))} placeholder="search" /></Field>
          </div>
          <div className="mt-4 flex gap-2 flex-wrap">
            <Button variant="outline" onClick={load}>Применить фильтры</Button>
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
                  {job.filesCount ? <Badge intent="secondary"><FileStack size={12} className="inline mr-1" /> {job.filesCount}</Badge> : null}
                  <Button variant="outline" onClick={() => openJob(job.id)}>Открыть</Button>
                </div>
              </div>
            </Card>
          ))}
          {!loading && filteredJobs.length === 0 ? <Card>AI jobs пока нет.</Card> : null}
        </div>
      </div>

      <Card>
        <SectionTitle icon={Brain} title="Детали job" subtitle="Выбранная задача всегда справа, без необходимости листать вниз до самого дна." />
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

  const renderDrafts = () => (
    <Card>
      <SectionTitle icon={FileStack} title="Drafts" subtitle="Черновики вынесены в отдельную секцию, чтобы не мешались с очередью jobs." />
      <div className="space-y-3 mt-4 max-h-[74vh] overflow-auto pr-1">
        {drafts.map((draft) => (
          <div key={draft.id} className="rounded-2xl border border-neutral-200/70 dark:border-neutral-800 p-4 space-y-2">
            <div className="flex flex-wrap gap-2 items-center justify-between">
              <div className="font-medium">{draft.title}</div>
              <div className="flex gap-2 flex-wrap">
                <Badge intent="secondary">{draft.assignmentType}</Badge>
                <Badge intent="secondary">{draft.status}</Badge>
              </div>
            </div>
            <div className="text-xs opacity-70 break-all">job: {draft.jobId}</div>
            <div className="text-xs opacity-70 break-all">course: {draft.courseId || '—'}</div>
            {(() => {
              const selfCheck = extractSelfCheck(draft.draftJson);
              return selfCheck ? (
                <div className="space-y-2">
                  <div className="flex flex-wrap gap-2 items-center">
                    <Badge intent={selfCheckTone(selfCheck.status)}>{selfCheck.status || 'unknown'}</Badge>
                    {selfCheck.score != null ? <Badge intent="secondary">score: {Math.round(Number(selfCheck.score || 0) * 100)}%</Badge> : null}
                  </div>
                  <div className="text-xs opacity-80">{selfCheck.summary || 'Self-check завершён.'}</div>
                </div>
              ) : <div className="text-xs opacity-70">self-check ещё не выполнялся</div>;
            })()}
            <Field label="DraftJson"><Textarea rows={8} readOnly value={prettyJson(draft.draftJson)} /></Field>
            <div className="flex gap-2 flex-wrap">
              <Button variant="outline" onClick={() => reviewDraft(draft.id, 'approve')}><CheckCircle2 size={16} /> <span className="ml-1">Approve</span></Button>
              <Button variant="outline" disabled={busy} onClick={() => validateDraftNow(draft)}>Self-check</Button>
              <Button disabled={busy || !canPublishDraft(draft)} onClick={() => publishDraftNow(draft)}>Опубликовать</Button>
              <Button variant="outline" onClick={() => reviewDraft(draft.id, 'reject')}>Reject</Button>
            </div>
            {!canPublishDraft(draft) && draft.status !== 'published' ? <div className="text-xs opacity-70">Публикация откроется после self-check со статусом passed.</div> : null}
          </div>
        ))}
        {drafts.length === 0 ? <div className="text-sm opacity-70">Drafts пока нет.</div> : null}
      </div>
    </Card>
  );

  const renderReviews = () => (
    <Card>
      <SectionTitle icon={Clock3} title="Submission reviews" subtitle="Отдельная вкладка с verdict и summary по решениям пользователей." />
      <div className="space-y-3 text-sm mt-4 max-h-[74vh] overflow-auto pr-1">
        {submissionReviews.map((item) => (
          <div key={item.id} className="rounded-2xl border border-neutral-200/70 dark:border-neutral-800 p-4 space-y-2">
            <div className="flex flex-wrap gap-2 items-center justify-between">
              <div className="font-medium">{item.verdict}</div>
              <Badge intent="secondary">{item.sourceType || 'submission'}</Badge>
            </div>
            <div className="text-xs opacity-70 break-all">attempt: {item.sourceAttemptId || '—'}</div>
            <div>{item.summary}</div>
          </div>
        ))}
        {submissionReviews.length === 0 ? <div className="text-sm opacity-70">Submission reviews пока нет.</div> : null}
      </div>
    </Card>
  );

  const renderRisk = () => (
    <Card>
      <SectionTitle icon={ShieldAlert} title="User risk reports" subtitle="Риск-репорты вынесены отдельно, чтобы не искать их между job и draft." />
      <div className="space-y-3 text-sm mt-4 max-h-[74vh] overflow-auto pr-1">
        {riskReports.map((item) => (
          <div key={item.id} className="rounded-2xl border border-neutral-200/70 dark:border-neutral-800 p-4 space-y-2">
            <div className="flex flex-wrap gap-2 items-center justify-between">
              <div className="font-medium">{item.riskLevel}</div>
              <Badge intent="secondary">score: {item.score}</Badge>
            </div>
            <div className="text-xs opacity-70 break-all">user: {item.userId}</div>
            <div>{item.summary}</div>
          </div>
        ))}
        {riskReports.length === 0 ? <div className="text-sm opacity-70">Risk reports пока нет.</div> : null}
      </div>
    </Card>
  );

  const renderInsights = () => (
    <Card>
      <SectionTitle icon={Brain} title="Assignment insights" subtitle="Инсайты по заданиям тоже живут отдельно, чтобы AI-центр стал действительно разветвлённым." />
      <div className="space-y-3 text-sm mt-4 max-h-[74vh] overflow-auto pr-1">
        {assignmentInsights.map((item) => (
          <div key={item.id} className="rounded-2xl border border-neutral-200/70 dark:border-neutral-800 p-4 space-y-2">
            <div className="flex flex-wrap gap-2 items-center justify-between">
              <div className="font-medium">{item.kind}</div>
              <Badge intent="secondary">{item.assignmentId}</Badge>
            </div>
            <div>{item.summary}</div>
            {item.suggestionsJson ? <Field label="Suggestions"><Textarea rows={6} readOnly value={prettyJson(item.suggestionsJson)} /></Field> : null}
          </div>
        ))}
        {assignmentInsights.length === 0 ? <div className="text-sm opacity-70">Assignment insights пока нет.</div> : null}
      </div>
    </Card>
  );

  return (
    <Layout>
      <div className="space-y-6">
        <div className="flex flex-col xl:flex-row xl:items-start xl:justify-between gap-4">
          <div className="max-w-3xl">
            <div className="text-xs font-semibold uppercase tracking-[0.22em] text-neutral-400">AI control center</div>
            <h1 className="mt-2 text-3xl font-semibold tracking-tight flex items-center gap-2"><Bot size={24} /> TaskForge AI</h1>
            <p className="mt-3 text-sm leading-6 text-neutral-500">AI-центр стал более разветвлённым: отдельные вкладки для создания, очереди, черновиков, review, risk и insights. Больше не нужно пролистывать одну гигантскую страницу до самого дна.</p>
          </div>
          <div className="flex gap-2 flex-wrap">
            <Button variant="outline" onClick={load}><RefreshCcw size={16} /> <span className="ml-1">Обновить</span></Button>
            <Badge intent="secondary">jobs: {total}</Badge>
            <Badge intent="secondary">drafts: {drafts.length}</Badge>
            <Badge intent="secondary">reviews: {submissionReviews.length}</Badge>
            <Badge intent="secondary">risks: {riskReports.length}</Badge>
          </div>
        </div>

        {pageError ? <Card className="border-rose-300 bg-rose-50 text-rose-700">{pageError}</Card> : null}

        <div className="flex gap-2 flex-wrap">
          {AI_TABS.map((tab) => (
            <TabButton
              key={tab.key}
              active={activeTab === tab.key}
              icon={tab.icon}
              onClick={() => setActiveTab(tab.key)}
              count={tab.key === 'queue' ? filteredJobs.length : tab.key === 'drafts' ? drafts.length : tab.key === 'reviews' ? submissionReviews.length : tab.key === 'risk' ? riskReports.length : tab.key === 'insights' ? assignmentInsights.length : null}
            >
              {tab.label}
            </TabButton>
          ))}
        </div>

        {(activeTab === 'overview' || activeTab === 'create') ? renderCourseScopeCard() : null}

        {activeTab === 'overview' ? (
          <div className="space-y-6">
            <div className="grid md:grid-cols-2 xl:grid-cols-4 gap-4">
              <CompactStatCard title="jobs" value={total} hint="Общая очередь и история" />
              <CompactStatCard title="drafts" value={drafts.length} hint="Черновики до публикации" />
              <CompactStatCard title="reviews" value={submissionReviews.length} hint="Review решений пользователей" />
              <CompactStatCard title="risk" value={riskReports.length} hint="Risk reports по пользователям" />
            </div>
            <Card>
              <SectionTitle icon={Sparkles} title="Быстрые сценарии" subtitle="Выбирай готовый шаблон — AI-центр сразу откроет нужную секцию и заполнит форму." />
              <div className="flex flex-wrap gap-2 mt-4">
                {quickTemplates.map((tpl) => (
                  <Button key={tpl.title} variant="outline" onClick={tpl.apply}>{tpl.title}</Button>
                ))}
              </div>
            </Card>
            {renderJobsQueue()}
          </div>
        ) : null}

        {activeTab === 'create' ? (
          <div className="space-y-4">
            <div className="flex gap-2 flex-wrap">
              {CREATOR_TABS.map((tab) => (
                <TabButton key={tab.key} active={creatorTab === tab.key} onClick={() => setCreatorTab(tab.key)}>{tab.label}</TabButton>
              ))}
            </div>
            {renderCreatePanel()}
          </div>
        ) : null}

        {activeTab === 'queue' ? renderJobsQueue() : null}
        {activeTab === 'drafts' ? renderDrafts() : null}
        {activeTab === 'reviews' ? renderReviews() : null}
        {activeTab === 'risk' ? renderRisk() : null}
        {activeTab === 'insights' ? renderInsights() : null}
      </div>
    </Layout>
  );
}
