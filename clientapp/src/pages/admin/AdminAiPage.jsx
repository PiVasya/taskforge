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
import { handleApiError } from '../../utils/handleApiError';
import { useNotify } from '../../components/notify/NotifyProvider';
import { Bot, Brain, CheckCircle2, Clock3, FileStack, RefreshCcw, ShieldAlert, Sparkles } from 'lucide-react';

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

export default function AdminAiPage() {
  const notify = useNotify();
  const [jobs, setJobs] = useState([]);
  const [total, setTotal] = useState(0);
  const [drafts, setDrafts] = useState([]);
  const [submissionReviews, setSubmissionReviews] = useState([]);
  const [riskReports, setRiskReports] = useState([]);
  const [assignmentInsights, setAssignmentInsights] = useState([]);
  const [selectedJob, setSelectedJob] = useState(null);
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState(false);
  const [pageError, setPageError] = useState('');
  const [filters, setFilters] = useState({ status: '', type: '' });
  const [genericForm, setGenericForm] = useState(genericEmpty);
  const [fromTextForm, setFromTextForm] = useState(fromTextEmpty);
  const [fromFileForm, setFromFileForm] = useState(fromFileEmpty);
  const [analyzeForm, setAnalyzeForm] = useState(analyzeEmpty);
  const [reviewSubmissionForm, setReviewSubmissionForm] = useState(reviewSubmissionEmpty);
  const [reviewUserForm, setReviewUserForm] = useState(reviewUserEmpty);

  const load = async () => {
    try {
      setLoading(true);
      const [jobsData, draftsData, reviewsData, risksData, insightsData] = await Promise.all([
        getAiJobs({ status: filters.status || undefined, type: filters.type || undefined, page: 1, pageSize: 30 }),
        getAiDrafts(),
        getAiSubmissionReviews(),
        getAiRiskReports(),
        getAiAssignmentInsights(),
      ]);
      setJobs(Array.isArray(jobsData.items) ? jobsData.items : []);
      setTotal(Number(jobsData.total || 0));
      setDrafts(Array.isArray(draftsData) ? draftsData : []);
      setSubmissionReviews(Array.isArray(reviewsData) ? reviewsData : []);
      setRiskReports(Array.isArray(risksData) ? risksData : []);
      setAssignmentInsights(Array.isArray(insightsData) ? insightsData : []);
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

  const openJob = async (id) => {
    try {
      const data = await getAiJob(id);
      setSelectedJob(data || null);
    } catch (e) {
      handleApiError(e, notify, 'Не удалось открыть AI job');
    }
  };

  const genericSubmit = async () => {
    try {
      setBusy(true);
      const payload = {
        type: genericForm.type,
        targetEntityType: genericForm.targetEntityType || null,
        targetEntityId: genericForm.targetEntityId || null,
        courseId: genericForm.courseId || null,
        priority: Number(genericForm.priority || 0),
        inputJson: genericForm.inputJson || '{}',
        files: genericForm.fileKey
          ? [{ fileKey: genericForm.fileKey, originalName: genericForm.fileName || null, mimeType: genericForm.fileMime || null, publicUrl: genericForm.fileUrl || null }]
          : [],
      };
      const created = await createAiJob(payload);
      notify.success('AI job поставлен в очередь');
      await load();
      if (created?.id) await openJob(created.id);
    } catch (e) {
      handleApiError(e, notify, 'Не удалось поставить AI job в очередь');
    } finally {
      setBusy(false);
    }
  };

  const submitAndOpen = async (action, successMessage) => {
    try {
      setBusy(true);
      const created = await action();
      notify.success(successMessage);
      await load();
      if (created?.id) await openJob(created.id);
    } catch (e) {
      handleApiError(e, notify, 'Не удалось создать AI job');
    } finally {
      setBusy(false);
    }
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

  const publishDraft = async (draft) => {
    try {
      setBusy(true);
      const data = await publishAiDraft(draft.id, { courseId: draft.courseId || null });
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
      await load();
      if (data?.id) await openJob(data.id);
    } catch (e) {
      handleApiError(e, notify, 'Не удалось запустить AI self-check');
    } finally {
      setBusy(false);
    }
  };

  const quickTemplates = useMemo(() => ([
    {
      title: 'Math из текста',
      icon: Sparkles,
      apply: () => setFromTextForm((p) => ({ ...p, assignmentType: 'math', prompt: 'Сгенерируй 1-2 задания по математике с блоками info / number / set / single-choice и кратким разбором.' })),
    },
    {
      title: 'Test из файла',
      icon: FileStack,
      apply: () => setFromFileForm((p) => ({ ...p, assignmentType: 'test', prompt: 'Из файла собери тест с богатым rich statement, вариантами ответов и короткими объяснениями.' })),
    },
    {
      title: 'Risk review юзера',
      icon: ShieldAlert,
      apply: () => setReviewUserForm((p) => ({ ...p, prompt: 'Проверь пользователя на suspicious activity, токсичность, coordinated abuse и резкие скачки качества.' })),
    },
  ]), []);

  return (
    <Layout>
      <div className="space-y-6">
        <div className="flex flex-col lg:flex-row lg:items-center lg:justify-between gap-3">
          <div>
            <h1 className="text-2xl font-semibold flex items-center gap-2"><Bot size={22} /> TaskForge AI</h1>
            <p className="text-sm text-neutral-500 mt-2">Минимально завершённый AI-поток: jobs → draft → self-check → approve → publish для test / math / code-test, плюс review решений, risk-review пользователей и анализ существующих заданий.</p>
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

        <Card>
          <div className="flex flex-wrap gap-2">
            {quickTemplates.map((tpl) => (
              <Button key={tpl.title} variant="outline" onClick={tpl.apply}><tpl.icon size={16} /> <span className="ml-1">{tpl.title}</span></Button>
            ))}
          </div>
        </Card>

        <div className="grid xl:grid-cols-[1.08fr,0.92fr] gap-6">
          <div className="space-y-4">
            <Card>
              <div className="font-medium mb-3 flex items-center gap-2"><Sparkles size={18} /> Генерация задания из текста</div>
              <div className="grid md:grid-cols-2 gap-4">
                <Field label="Course id"><Input value={fromTextForm.courseId} onChange={(e) => setFromTextForm((p) => ({ ...p, courseId: e.target.value }))} /></Field>
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
              <div className="mt-4"><Button disabled={busy} onClick={() => submitAndOpen(() => generateAiAssignmentFromText({ ...fromTextForm, difficulty: Number(fromTextForm.difficulty || 2), count: Number(fromTextForm.count || 1), priority: Number(fromTextForm.priority || 20) }), 'AI job на генерацию из текста поставлен в очередь')}>Создать AI job</Button></div>
            </Card>

            <Card>
              <div className="font-medium mb-3 flex items-center gap-2"><FileStack size={18} /> Генерация задания из файла</div>
              <div className="grid md:grid-cols-2 gap-4">
                <Field label="Course id"><Input value={fromFileForm.courseId} onChange={(e) => setFromFileForm((p) => ({ ...p, courseId: e.target.value }))} /></Field>
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
              <div className="mt-4"><Button disabled={busy} onClick={() => submitAndOpen(() => generateAiAssignmentFromFile({ ...fromFileForm, difficulty: Number(fromFileForm.difficulty || 2), count: Number(fromFileForm.count || 1), priority: Number(fromFileForm.priority || 20) }), 'AI job на генерацию из файла поставлен в очередь')}>Создать AI job</Button></div>
            </Card>

            <Card>
              <div className="font-medium mb-3 flex items-center gap-2"><Brain size={18} /> Анализ задания и review</div>
              <div className="grid md:grid-cols-2 gap-4">
                <Field label="Assignment id"><Input value={analyzeForm.assignmentId} onChange={(e) => setAnalyzeForm((p) => ({ ...p, assignmentId: e.target.value }))} /></Field>
                <Field label="Priority"><Input type="number" min="0" max="100" value={analyzeForm.priority} onChange={(e) => setAnalyzeForm((p) => ({ ...p, priority: e.target.value }))} /></Field>
                <Field label="Include stats"><Select value={String(analyzeForm.includeStats)} onChange={(e) => setAnalyzeForm((p) => ({ ...p, includeStats: e.target.value === 'true' }))}><option value="true">true</option><option value="false">false</option></Select></Field>
                <Field label="Include attempts"><Select value={String(analyzeForm.includeAttempts)} onChange={(e) => setAnalyzeForm((p) => ({ ...p, includeAttempts: e.target.value === 'true' }))}><option value="true">true</option><option value="false">false</option></Select></Field>
                <div className="md:col-span-2"><Field label="Промпт анализа"><Textarea rows={4} value={analyzeForm.prompt} onChange={(e) => setAnalyzeForm((p) => ({ ...p, prompt: e.target.value }))} /></Field></div>
              </div>
              <div className="mt-4"><Button disabled={busy} onClick={() => submitAndOpen(() => analyzeAiAssignment({ ...analyzeForm, priority: Number(analyzeForm.priority || 12) }), 'AI job на анализ задания поставлен в очередь')}>Проанализировать задание</Button></div>
              <div className="grid md:grid-cols-2 gap-4 mt-6 pt-4 border-t border-neutral-200/70 dark:border-neutral-800">
                <Field label="Source type"><Select value={reviewSubmissionForm.sourceType} onChange={(e) => setReviewSubmissionForm((p) => ({ ...p, sourceType: e.target.value }))}><option value="math">math</option><option value="test">test</option><option value="code">code</option><option value="image">image</option></Select></Field>
                <Field label="Attempt id"><Input value={reviewSubmissionForm.sourceAttemptId} onChange={(e) => setReviewSubmissionForm((p) => ({ ...p, sourceAttemptId: e.target.value }))} /></Field>
                <Field label="Priority"><Input type="number" min="0" max="100" value={reviewSubmissionForm.priority} onChange={(e) => setReviewSubmissionForm((p) => ({ ...p, priority: e.target.value }))} /></Field>
                <div className="md:col-span-2"><Field label="Промпт review решения"><Textarea rows={4} value={reviewSubmissionForm.prompt} onChange={(e) => setReviewSubmissionForm((p) => ({ ...p, prompt: e.target.value }))} /></Field></div>
              </div>
              <div className="mt-4"><Button variant="outline" disabled={busy} onClick={() => submitAndOpen(() => reviewAiSubmission({ ...reviewSubmissionForm, priority: Number(reviewSubmissionForm.priority || 10) }), 'AI job на review решения поставлен в очередь')}>Review решения</Button></div>
              <div className="grid md:grid-cols-2 gap-4 mt-6 pt-4 border-t border-neutral-200/70 dark:border-neutral-800">
                <Field label="User id"><Input value={reviewUserForm.userId} onChange={(e) => setReviewUserForm((p) => ({ ...p, userId: e.target.value }))} /></Field>
                <Field label="Priority"><Input type="number" min="0" max="100" value={reviewUserForm.priority} onChange={(e) => setReviewUserForm((p) => ({ ...p, priority: e.target.value }))} /></Field>
                <Field label="Include support"><Select value={String(reviewUserForm.includeSupport)} onChange={(e) => setReviewUserForm((p) => ({ ...p, includeSupport: e.target.value === 'true' }))}><option value="true">true</option><option value="false">false</option></Select></Field>
                <Field label="Include Minecraft"><Select value={String(reviewUserForm.includeMinecraft)} onChange={(e) => setReviewUserForm((p) => ({ ...p, includeMinecraft: e.target.value === 'true' }))}><option value="true">true</option><option value="false">false</option></Select></Field>
                <Field label="Include attempts"><Select value={String(reviewUserForm.includeRecentAttempts)} onChange={(e) => setReviewUserForm((p) => ({ ...p, includeRecentAttempts: e.target.value === 'true' }))}><option value="true">true</option><option value="false">false</option></Select></Field>
                <div className="md:col-span-2"><Field label="Промпт risk-review"><Textarea rows={4} value={reviewUserForm.prompt} onChange={(e) => setReviewUserForm((p) => ({ ...p, prompt: e.target.value }))} /></Field></div>
              </div>
              <div className="mt-4"><Button variant="outline" disabled={busy} onClick={() => submitAndOpen(() => reviewAiUser({ ...reviewUserForm, priority: Number(reviewUserForm.priority || 10) }), 'AI job на review пользователя поставлен в очередь')}>Review пользователя</Button></div>
            </Card>

            <Card>
              <div className="font-medium mb-3">Низкоуровневая постановка job</div>
              <div className="grid md:grid-cols-2 gap-4">
                <Field label="Тип job">
                  <Select value={genericForm.type} onChange={(e) => setGenericForm((p) => ({ ...p, type: e.target.value }))}>
                    {JOB_TYPES.map((item) => <option key={item.value} value={item.value}>{item.label}</option>)}
                  </Select>
                </Field>
                <Field label="Priority"><Input type="number" min="0" max="100" value={genericForm.priority} onChange={(e) => setGenericForm((p) => ({ ...p, priority: e.target.value }))} /></Field>
                <Field label="Target entity type"><Input placeholder="assignment / user / submission / support-ticket" value={genericForm.targetEntityType} onChange={(e) => setGenericForm((p) => ({ ...p, targetEntityType: e.target.value }))} /></Field>
                <Field label="Target entity id"><Input placeholder="guid" value={genericForm.targetEntityId} onChange={(e) => setGenericForm((p) => ({ ...p, targetEntityId: e.target.value }))} /></Field>
                <Field label="Course id"><Input placeholder="guid" value={genericForm.courseId} onChange={(e) => setGenericForm((p) => ({ ...p, courseId: e.target.value }))} /></Field>
                <Field label="File key"><Input value={genericForm.fileKey} onChange={(e) => setGenericForm((p) => ({ ...p, fileKey: e.target.value }))} /></Field>
                <Field label="Имя файла"><Input value={genericForm.fileName} onChange={(e) => setGenericForm((p) => ({ ...p, fileName: e.target.value }))} /></Field>
                <Field label="Mime type"><Input value={genericForm.fileMime} onChange={(e) => setGenericForm((p) => ({ ...p, fileMime: e.target.value }))} /></Field>
                <div className="md:col-span-2"><Field label="Public URL"><Input value={genericForm.fileUrl} onChange={(e) => setGenericForm((p) => ({ ...p, fileUrl: e.target.value }))} /></Field></div>
                <div className="md:col-span-2"><Field label="InputJson"><Textarea rows={14} value={genericForm.inputJson} onChange={(e) => setGenericForm((p) => ({ ...p, inputJson: e.target.value }))} /></Field></div>
              </div>
              <div className="mt-4"><Button variant="outline" disabled={busy} onClick={genericSubmit}>Поставить raw job</Button></div>
            </Card>

            <Card>
              <div className="grid md:grid-cols-2 gap-4 items-end">
                <Field label="Статус"><Input value={filters.status} onChange={(e) => setFilters((p) => ({ ...p, status: e.target.value }))} placeholder="pending / running / done / failed" /></Field>
                <Field label="Тип"><Input value={filters.type} onChange={(e) => setFilters((p) => ({ ...p, type: e.target.value }))} placeholder="assignment_generate / review / risk" /></Field>
              </div>
              <div className="mt-4"><Button variant="outline" onClick={load}>Применить фильтры</Button></div>
            </Card>

            <div className="space-y-3">
              {loading ? <Card>Загрузка…</Card> : jobs.map((job) => (
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
              {!loading && jobs.length === 0 ? <Card>AI jobs пока нет.</Card> : null}
            </div>
          </div>

          <div className="space-y-4">
            <Card>
              <div className="font-medium mb-3 flex items-center gap-2"><Brain size={18} /> Детали job</div>
              {!selectedJob ? <div className="text-sm opacity-70">Выбери job слева.</div> : (
                <div className="space-y-3 text-sm">
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

            <Card>
              <div className="font-medium mb-3 flex items-center gap-2"><Sparkles size={18} /> Drafts</div>
              <div className="space-y-3">
                {drafts.map((draft) => (
                  <div key={draft.id} className="rounded-2xl border border-neutral-200/70 dark:border-neutral-800 p-4 space-y-2">
                    <div className="flex flex-wrap gap-2 items-center justify-between">
                      <div className="font-medium">{draft.title}</div>
                      <Badge intent="secondary">{draft.assignmentType}</Badge>
                    </div>
                    <div className="text-xs opacity-70 break-all">job: {draft.jobId}</div>
                    <div className="text-xs opacity-70">status: {draft.status}</div>
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
                      <Button disabled={busy || !canPublishDraft(draft)} onClick={() => publishDraft(draft)}>Опубликовать</Button>
                      <Button variant="outline" onClick={() => reviewDraft(draft.id, 'reject')}>Reject</Button>
                    </div>
                    {!canPublishDraft(draft) && draft.status !== 'published' ? <div className="text-xs opacity-70">Публикация откроется после self-check со статусом passed.</div> : null}
                  </div>
                ))}
                {drafts.length === 0 ? <div className="text-sm opacity-70">Drafts пока нет.</div> : null}
              </div>
            </Card>

            <Card>
              <div className="font-medium mb-3 flex items-center gap-2"><Clock3 size={18} /> Submission reviews</div>
              <div className="space-y-3 text-sm">
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

            <Card>
              <div className="font-medium mb-3 flex items-center gap-2"><ShieldAlert size={18} /> User risk reports</div>
              <div className="space-y-3 text-sm">
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

            <Card>
              <div className="font-medium mb-3 flex items-center gap-2"><Brain size={18} /> Assignment insights</div>
              <div className="space-y-3 text-sm">
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
          </div>
        </div>
      </div>
    </Layout>
  );
}
