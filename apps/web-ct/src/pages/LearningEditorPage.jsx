import React, { useCallback, useEffect, useMemo, useState } from 'react';
import { Link, Navigate, useNavigate, useParams } from 'react-router-dom';
import {
  AlertTriangle,
  ArrowLeft,
  CheckCircle2,
  Code2,
  Eye,
  FileText,
  HelpCircle,
  Loader2,
  Plus,
  RefreshCcw,
  Save,
} from 'lucide-react';
import Layout from '../components/Layout';
import CtStructureBootstrapPanel from '../components/CtStructureBootstrapPanel';
import SectionTaskAdminPanel from '../components/SectionTaskAdminPanel';
import {
  createLearningConspect,
  getLearningConspect,
  getLearningConspects,
  getLearningCourseTree,
  updateLearningConspect,
} from '../api/learning';
import { getApiErrorMessage } from '../api/http';
import { useEditorMode } from '../contexts/EditorModeContext';
import {
  CT_PARTS,
  EXAM_CODE,
  SUBJECT_CODE,
  getSectionPath,
  getSectionsByPart,
  isKnownSectionCode,
  normalizeSectionCode,
} from '../data/ctSections';

function slugify(text) {
  const map = { а:'a',б:'b',в:'v',г:'g',д:'d',е:'e',ё:'e',ж:'zh',з:'z',и:'i',й:'y',к:'k',л:'l',м:'m',н:'n',о:'o',п:'p',р:'r',с:'s',т:'t',у:'u',ф:'f',х:'h',ц:'c',ч:'ch',ш:'sh',щ:'sch',ы:'y',э:'e',ю:'yu',я:'ya',ь:'',ъ:'' };
  return String(text || '')
    .trim()
    .toLowerCase()
    .split('')
    .map((c) => map[c] ?? c)
    .join('')
    .replace(/[^a-z0-9]+/g, '-')
    .replace(/^-+|-+$/g, '') || `item-${Date.now()}`;
}

function flattenCourses(nodes, level = 0, result = []) {
  (nodes || []).forEach((node) => {
    result.push({ ...node, level });
    flattenCourses(node.children, level + 1, result);
  });
  return result;
}

function findCourseBySection(nodes, sectionCode) {
  const normalized = normalizeSectionCode(sectionCode);
  return flattenCourses(nodes).find((course) => normalizeSectionCode(course.sectionCode) === normalized) || null;
}

function findRootCourse(nodes) {
  return flattenCourses(nodes).find((course) => (
    course.subjectCode === SUBJECT_CODE
    && course.examCode === EXAM_CODE
    && !course.sectionCode
  )) || null;
}

function safeParseJson(raw, fallback = null) {
  if (!raw) return fallback;
  if (typeof raw === 'object') return raw;
  try { return JSON.parse(raw); } catch { return fallback; }
}

function htmlFromContent(raw) {
  const content = safeParseJson(raw, null);
  if (!content || typeof content !== 'object') return { html: '', isHtml: false };
  if (content.mode === 'html' && typeof content.html === 'string') return { html: content.html, isHtml: true };
  if (typeof content.html === 'string') return { html: content.html, isHtml: true };
  if (typeof content.rawHtml === 'string') return { html: content.rawHtml, isHtml: true };
  return { html: '', isHtml: false };
}

function contentFromHtml(html) {
  return {
    schemaVersion: 2,
    mode: 'html',
    html: html || '',
    updatedAtUtc: new Date().toISOString(),
  };
}

function badgesFromText(text) {
  return String(text || '').split(',').map((item) => item.trim()).filter(Boolean);
}

function getError(error) {
  return getApiErrorMessage(error, 'Не удалось выполнить действие');
}

function defaultHtml(sectionCode) {
  return `<section class="tf-conspect">
  <h1>${sectionCode}. Конспект</h1>
  <p>Здесь будет HTML-конспект для номера ${sectionCode}.</p>

  <h2>Теория</h2>
  <p>Кратко объясни правило и главный алгоритм решения.</p>

  <h2>Примеры</h2>
  <ul>
    <li>пример 1</li>
    <li>пример 2</li>
  </ul>
</section>`;
}

function makeNewConspectForm(sectionCode, course) {
  return {
    id: '',
    slug: `${sectionCode.toLowerCase()}-conspect`,
    title: `${sectionCode}. Конспект`,
    subtitle: 'Конспект',
    lead: `Теория и примеры для задания ${sectionCode}.`,
    subjectCode: course?.subjectCode || SUBJECT_CODE,
    examCode: course?.examCode || EXAM_CODE,
    sectionCode,
    sortOrder: '10',
    estimatedMinutes: '10',
    badgesText: `${sectionCode}, конспект, ЦТ/ЦЭ`,
    searchText: '',
    isPublished: true,
  };
}

function formFromConspect(details, sectionCode, course) {
  const c = details?.conspect || {};
  return {
    id: c.id || '',
    slug: c.slug || '',
    title: c.title || `${sectionCode}. Конспект`,
    subtitle: c.subtitle || 'Конспект',
    lead: c.lead || '',
    subjectCode: c.subjectCode || course?.subjectCode || SUBJECT_CODE,
    examCode: c.examCode || course?.examCode || EXAM_CODE,
    sectionCode: c.sectionCode || sectionCode,
    sortOrder: String(c.sortOrder ?? 10),
    estimatedMinutes: String(c.estimatedMinutes ?? 10),
    badgesText: safeParseJson(c.badgesJson, []).join(', '),
    searchText: details?.searchText || '',
    isPublished: c.isPublished !== false,
  };
}

function Field({ label, hint, children, required = false }) {
  return (
    <label className="block">
      <span className="text-sm font-semibold">
        {label}{required ? <span className="text-red-500"> *</span> : null}
      </span>
      <div className="mt-1">{children}</div>
      {hint ? <div className="mt-1 flex gap-1 text-xs leading-5 text-neutral-500 dark:text-neutral-400"><HelpCircle size={13} className="mt-0.5 shrink-0" />{hint}</div> : null}
    </label>
  );
}

function Input(props) {
  return <input {...props} className={`w-full rounded-2xl border border-neutral-200 bg-neutral-50 px-3 py-2.5 outline-none focus:border-brand-400 dark:border-neutral-800 dark:bg-neutral-950 ${props.className || ''}`} />;
}

function Textarea(props) {
  return <textarea {...props} className={`w-full rounded-2xl border border-neutral-200 bg-neutral-50 px-3 py-2.5 outline-none focus:border-brand-400 dark:border-neutral-800 dark:bg-neutral-950 ${props.className || ''}`} />;
}

function Alert({ type = 'info', children }) {
  const styles = type === 'error'
    ? 'border-red-200 bg-red-50 text-red-800 dark:border-red-900 dark:bg-red-950/30 dark:text-red-200'
    : type === 'success'
      ? 'border-emerald-200 bg-emerald-50 text-emerald-800 dark:border-emerald-900 dark:bg-emerald-950/30 dark:text-emerald-200'
      : type === 'warning'
        ? 'border-amber-200 bg-amber-50 text-amber-900 dark:border-amber-900 dark:bg-amber-950/30 dark:text-amber-100'
        : 'border-sky-200 bg-sky-50 text-sky-800 dark:border-sky-900 dark:bg-sky-950/30 dark:text-sky-200';
  return <div className={`whitespace-pre-line rounded-3xl border px-4 py-3 text-sm leading-6 ${styles}`}>{children}</div>;
}

function HtmlPreview({ html }) {
  const srcDoc = `<!doctype html><html><head><meta charset="utf-8" /><meta name="viewport" content="width=device-width, initial-scale=1" />
<style>
body{font-family:system-ui,-apple-system,BlinkMacSystemFont,"Segoe UI",sans-serif;margin:24px;color:#111827;background:white;line-height:1.6}
.tf-conspect{max-width:960px;margin:0 auto}.tf-conspect h1{font-size:40px;line-height:1.1;margin:0 0 16px}.tf-conspect h2{font-size:24px;margin:28px 0 10px}.tf-conspect table{border-collapse:collapse;width:100%;margin:16px 0}.tf-conspect td,.tf-conspect th{border:1px solid #e5e7eb;padding:10px;text-align:left}.tf-conspect .card{border:1px solid #dbeafe;background:#eff6ff;border-radius:18px;padding:16px;margin:12px 0}
</style></head><body>${html || '<p>HTML пока пустой.</p>'}</body></html>`;
  return (
    <iframe
      title="Предпросмотр HTML конспекта"
      className="h-[70vh] w-full rounded-3xl border border-neutral-200 bg-white dark:border-neutral-800"
      sandbox="allow-scripts allow-forms allow-popups allow-popups-to-escape-sandbox"
      srcDoc={srcDoc}
    />
  );
}

function SectionPicker({ active, onPick }) {
  return (
    <section className="rounded-[2rem] border border-neutral-200 bg-white p-4 shadow-soft dark:border-neutral-800 dark:bg-neutral-900">
      <div className="mb-3 font-semibold">Номер ЦТ/ЦЭ</div>
      <div className="space-y-4">
        {CT_PARTS.map((part) => (
          <div key={part.code}>
            <div className="mb-2 text-xs font-bold uppercase tracking-wide text-neutral-400">{part.title}</div>
            <div className="flex flex-wrap gap-2">
              {getSectionsByPart(part.code).map((section) => (
                <button
                  key={section.code}
                  type="button"
                  onClick={() => onPick(section.code)}
                  className={`rounded-2xl border px-3 py-2 text-sm font-bold transition ${section.code === active ? 'border-brand-500 bg-brand-600 text-white' : 'border-neutral-200 bg-neutral-50 hover:border-brand-300 dark:border-neutral-800 dark:bg-neutral-950'}`}
                >
                  {section.code}
                </button>
              ))}
            </div>
          </div>
        ))}
      </div>
    </section>
  );
}

export default function LearningEditorPage() {
  const params = useParams();
  const navigate = useNavigate();
  const { setEditorMode } = useEditorMode();
  const normalizedSectionCode = normalizeSectionCode(params.sectionCode || '');

  const [tree, setTree] = useState([]);
  const [conspects, setConspects] = useState([]);
  const [conspectForm, setConspectForm] = useState(makeNewConspectForm(normalizedSectionCode || 'A1', null));
  const [html, setHtml] = useState('');
  const [contentWarning, setContentWarning] = useState('');
  const [showPreview, setShowPreview] = useState(false);
  const [busy, setBusy] = useState('');
  const [error, setError] = useState('');
  const [success, setSuccess] = useState('');

  const allCourses = useMemo(() => flattenCourses(tree), [tree]);
  const sectionCourse = useMemo(() => findCourseBySection(tree, normalizedSectionCode), [tree, normalizedSectionCode]);
  const rootCourse = useMemo(() => findRootCourse(tree), [tree]);
  const taskCourse = sectionCourse || { sectionCode: normalizedSectionCode, subjectCode: SUBJECT_CODE, examCode: EXAM_CODE };
  const currentViewPath = getSectionPath(normalizedSectionCode || 'A1');

  useEffect(() => {
    setEditorMode(true);
  }, [setEditorMode]);

  const openConspectDetails = useCallback((details, courseForDefaults = null) => {
    const sectionCode = normalizeSectionCode(details?.conspect?.sectionCode || normalizedSectionCode);
    setConspectForm(formFromConspect(details, sectionCode, courseForDefaults));
    const parsedHtml = htmlFromContent(details?.contentJson);
    setHtml(parsedHtml.html);
    setContentWarning(parsedHtml.isHtml ? '' : 'Этот конспект хранится в старом JSON-формате, а не как HTML. Я не подставляю шаблон поверх реальных данных. Вставь HTML вручную и сохрани, если хочешь перевести его в новый HTML-формат.');
    setShowPreview(false);
  }, [normalizedSectionCode]);

  const startNewConspect = useCallback((courseForDefaults = sectionCourse) => {
    const form = makeNewConspectForm(normalizedSectionCode, courseForDefaults);
    setConspectForm(form);
    setHtml(defaultHtml(normalizedSectionCode));
    setContentWarning('');
    setShowPreview(false);
    setError('');
    setSuccess('Создана новая форма конспекта для этого номера. После сохранения она будет привязана к текущему sectionCode.');
  }, [normalizedSectionCode, sectionCourse]);

  const loadSection = useCallback(async () => {
    if (!normalizedSectionCode) return;
    setBusy('load');
    setError('');
    setSuccess('');
    setContentWarning('');
    try {
      const treeData = await getLearningCourseTree({ includeDraft: true });
      const nextTree = treeData || [];
      setTree(nextTree);

      const nextCourse = findCourseBySection(nextTree, normalizedSectionCode);
      const items = await getLearningConspects({
        subjectCode: SUBJECT_CODE,
        examCode: EXAM_CODE,
        sectionCode: normalizedSectionCode,
        includeDraft: true,
      });

      const sorted = [...(items || [])].sort((a, b) => (a.sortOrder ?? 0) - (b.sortOrder ?? 0));
      setConspects(sorted);

      if (sorted.length > 0) {
        const first = sorted[0];
        const details = await getLearningConspect(first.id || first.slug, { includeDraft: true });
        openConspectDetails(details, nextCourse);
      } else {
        const form = makeNewConspectForm(normalizedSectionCode, nextCourse);
        setConspectForm(form);
        setHtml(defaultHtml(normalizedSectionCode));
        setShowPreview(false);
      }
    } catch (e) {
      setError(getError(e));
    } finally {
      setBusy('');
    }
  }, [normalizedSectionCode, openConspectDetails]);

  useEffect(() => {
    loadSection();
  }, [loadSection]);

  async function openConspect(idOrSlug) {
    setBusy('conspect-load');
    setError('');
    setSuccess('');
    try {
      const details = await getLearningConspect(idOrSlug, { includeDraft: true });
      openConspectDetails(details, sectionCourse);
    } catch (e) {
      setError(getError(e));
    } finally {
      setBusy('');
    }
  }

  function setConspect(name, value) {
    setConspectForm((prev) => ({ ...prev, [name]: value }));
  }

  function validateConspect() {
    if (!conspectForm.title.trim()) return 'Заполни название конспекта.';
    if (!conspectForm.slug.trim()) return 'Заполни slug конспекта.';
    if (!conspectForm.sectionCode.trim()) return 'У конспекта должен быть sectionCode.';
    if (!html.trim()) return 'Вставь HTML конспекта.';
    if (!conspectForm.id && !sectionCourse?.id) {
      return `Для создания нового конспекта нужен служебный раздел ${normalizedSectionCode} в дереве LearningCourse. Нажми «Создать / дозаполнить основу» слева.`;
    }
    return '';
  }

  async function saveConspect() {
    const validation = validateConspect();
    if (validation) { setError(validation); return; }

    const sectionCode = normalizeSectionCode(conspectForm.sectionCode) || normalizedSectionCode;
    setBusy('conspect');
    setError('');
    setSuccess('');
    try {
      const payload = {
        slug: conspectForm.slug.trim() || slugify(conspectForm.title),
        title: conspectForm.title.trim(),
        subtitle: conspectForm.subtitle.trim() || null,
        lead: conspectForm.lead,
        subjectCode: conspectForm.subjectCode.trim() || SUBJECT_CODE,
        examCode: conspectForm.examCode.trim() || EXAM_CODE,
        sectionCode,
        kind: 'html-conspect',
        sortOrder: Number(conspectForm.sortOrder) || 0,
        estimatedMinutes: Number(conspectForm.estimatedMinutes) || 10,
        badges: badgesFromText(conspectForm.badgesText),
        content: contentFromHtml(html),
        searchText: conspectForm.searchText || `${conspectForm.title} ${conspectForm.lead} ${sectionCode}`,
        isPublished: conspectForm.isPublished,
      };

      const saved = conspectForm.id
        ? await updateLearningConspect(conspectForm.id, payload)
        : await createLearningConspect(sectionCourse.id, payload);

      const savedConspect = saved.conspect || {};
      setConspectForm((prev) => ({
        ...prev,
        id: savedConspect.id || prev.id,
        slug: savedConspect.slug || payload.slug,
        sectionCode: savedConspect.sectionCode || sectionCode,
      }));
      const message = conspectForm.id
        ? 'Конспект сохранён. Это именно тот HTML, который откроется на странице ученика по этому номеру.'
        : 'Конспект создан и сохранён для этого номера.';
      setContentWarning('');
      await loadSection();
      setSuccess(message);
    } catch (e) {
      setError(getError(e));
    } finally {
      setBusy('');
    }
  }

  if (!params.sectionCode) {
    return <Navigate to="/editor/a1" replace />;
  }

  if (!normalizedSectionCode || !isKnownSectionCode(normalizedSectionCode)) {
    return <Navigate to="/editor/a1" replace />;
  }

  return (
    <Layout fullWidth>
      <div className="container-app py-8">
        <div className="mb-6 flex flex-wrap items-center justify-between gap-3">
          <div>
            <Link to={currentViewPath} className="inline-flex items-center gap-2 text-sm text-neutral-600 hover:text-brand-700 dark:text-neutral-300 dark:hover:text-brand-200">
              <ArrowLeft size={16} /> Открыть {normalizedSectionCode} как ученик
            </Link>
            <h1 className="mt-3 text-3xl font-bold tracking-tight md:text-5xl">Редактор ЦТ/ЦЭ: {normalizedSectionCode}</h1>
            <p className="mt-2 max-w-3xl text-neutral-600 dark:text-neutral-300">
              Теперь редактор работает как ученическая страница: выбираешь номер, редактор ищет конспект по subjectCode, examCode и sectionCode, а не открывает случайный корень дерева.
            </p>
          </div>
          <div className="rounded-3xl border border-neutral-200 bg-white p-4 text-sm shadow-soft dark:border-neutral-800 dark:bg-neutral-900">
            <div className="mb-1 font-semibold">Что редактируется</div>
            <div className="text-neutral-500 dark:text-neutral-400">
              {SUBJECT_CODE} / {EXAM_CODE} / {normalizedSectionCode}
            </div>
            <div className="mt-1 text-xs text-neutral-400">
              {sectionCourse ? `LearningCourse: /${sectionCourse.slug}` : 'LearningCourse для номера не найден'}
            </div>
          </div>
        </div>

        {error ? <div className="mb-4"><Alert type="error">{error}</Alert></div> : null}
        {success ? <div className="mb-4"><Alert type="success">{success}</Alert></div> : null}
        {busy === 'load' ? <div className="mb-4"><Alert>Загружаю реальный конспект {normalizedSectionCode}...</Alert></div> : null}

        <div className="grid gap-6 lg:grid-cols-[330px_1fr]">
          <aside className="space-y-4">
            <SectionPicker active={normalizedSectionCode} onPick={(code) => navigate(`/editor/${code.toLowerCase()}`)} />

            <section className="rounded-[2rem] border border-neutral-200 bg-white p-4 text-sm shadow-soft dark:border-neutral-800 dark:bg-neutral-900">
              <div className="mb-2 font-semibold">Быстрые действия</div>
              <div className="flex flex-col gap-2">
                <Link to={currentViewPath} className="btn-outline inline-flex items-center justify-center gap-2"><Eye size={16} /> Просмотр ученика</Link>
                <button type="button" onClick={loadSection} disabled={busy === 'load'} className="btn-outline inline-flex items-center justify-center gap-2 disabled:opacity-60">
                  {busy === 'load' ? <Loader2 size={16} className="animate-spin" /> : <RefreshCcw size={16} />}
                  Перезагрузить данные
                </button>
              </div>
            </section>

            <CtStructureBootstrapPanel allCourses={allCourses} onDone={() => loadSection()} />

            <section className="rounded-[2rem] border border-neutral-200 bg-white p-4 text-sm shadow-soft dark:border-neutral-800 dark:bg-neutral-900">
              <div className="mb-2 font-semibold">Служебная привязка</div>
              {sectionCourse ? (
                <div className="space-y-1 text-neutral-600 dark:text-neutral-300">
                  <div>Курс найден: <span className="font-semibold">{sectionCourse.title}</span></div>
                  <div className="text-xs text-neutral-500">/{sectionCourse.slug}</div>
                  <div className="text-xs text-neutral-500">courseId: {sectionCourse.id}</div>
                </div>
              ) : (
                <div className="rounded-2xl border border-amber-200 bg-amber-50 p-3 text-amber-900 dark:border-amber-900 dark:bg-amber-950/30 dark:text-amber-100">
                  В дереве нет раздела {normalizedSectionCode}. Существующий конспект можно обновить, но новый создать нельзя, пока не будет LearningCourse.
                </div>
              )}
              {rootCourse ? <div className="mt-3 text-xs text-neutral-500">Корень экзамена: /{rootCourse.slug}</div> : null}
            </section>
          </aside>

          <main className="space-y-6">
            <section className="rounded-[2rem] border border-neutral-200 bg-white p-5 shadow-soft dark:border-neutral-800 dark:bg-neutral-900 md:p-6">
              <div className="mb-4 flex flex-wrap items-center justify-between gap-3">
                <div>
                  <div className="flex items-center gap-2 text-sm font-semibold uppercase tracking-wide text-brand-700 dark:text-brand-300">
                    <FileText size={16} /> {normalizedSectionCode} · HTML-конспект
                  </div>
                  <h2 className="mt-1 text-2xl font-bold tracking-tight">Конспект номера</h2>
                  <p className="mt-1 text-sm text-neutral-500 dark:text-neutral-400">
                    Список ниже загружен тем же способом, что и страница ученика: по sectionCode {normalizedSectionCode}.
                  </p>
                </div>
                <button type="button" onClick={() => startNewConspect(sectionCourse)} className="btn-outline inline-flex items-center gap-2">
                  <Plus size={16} /> Новый конспект
                </button>
              </div>

              <div className="mb-5 grid gap-3 md:grid-cols-2 xl:grid-cols-3">
                {conspects.map((item) => (
                  <button
                    key={item.id}
                    type="button"
                    onClick={() => openConspect(item.id || item.slug)}
                    className={`rounded-3xl border p-4 text-left transition hover:border-brand-300 ${conspectForm.id === item.id ? 'border-brand-300 bg-brand-50 dark:border-brand-800 dark:bg-brand-900/20' : 'border-neutral-200 bg-neutral-50 dark:border-neutral-800 dark:bg-neutral-950'}`}
                  >
                    <div className="flex items-start justify-between gap-2">
                      <div className="font-semibold">{item.title}</div>
                      {item.isPublished ? <CheckCircle2 size={16} className="shrink-0 text-emerald-600" /> : <span className="shrink-0 rounded-xl bg-amber-100 px-2 py-1 text-xs text-amber-800 dark:bg-amber-950/40 dark:text-amber-100">черновик</span>}
                    </div>
                    <div className="mt-1 text-xs text-neutral-500 dark:text-neutral-400">/{item.slug}</div>
                    {item.lead ? <div className="mt-2 line-clamp-2 text-sm text-neutral-600 dark:text-neutral-300">{item.lead}</div> : null}
                  </button>
                ))}
                {!conspects.length ? (
                  <div className="rounded-3xl border border-dashed border-neutral-200 p-5 text-sm text-neutral-500 dark:border-neutral-800 dark:text-neutral-400">
                    Для {normalizedSectionCode} пока нет конспекта. Заполни форму и сохрани.
                  </div>
                ) : null}
              </div>

              {contentWarning ? (
                <div className="mb-5">
                  <Alert type="warning">{contentWarning}</Alert>
                </div>
              ) : null}

              <div className="grid gap-4 md:grid-cols-2">
                <Field label="Название конспекта" required hint="Именно этот заголовок увидит ученик.">
                  <Input value={conspectForm.title} onChange={(e) => setConspect('title', e.target.value)} />
                </Field>
                <Field label="Slug" required hint="Адрес конспекта внутри БД. Страница ученика всё равно открывается по /a1, /a2 и т.п.">
                  <Input value={conspectForm.slug} onChange={(e) => setConspect('slug', e.target.value)} />
                </Field>
                <Field label="Подзаголовок" hint="Например: Конспект, Алгоритм, Словарь.">
                  <Input value={conspectForm.subtitle} onChange={(e) => setConspect('subtitle', e.target.value)} />
                </Field>
                <Field label="Минуты" hint="Примерное время чтения.">
                  <Input type="number" value={conspectForm.estimatedMinutes} onChange={(e) => setConspect('estimatedMinutes', e.target.value)} />
                </Field>
                <Field label="Код предмета" hint="Должен совпадать с просмотром.">
                  <Input value={conspectForm.subjectCode} onChange={(e) => setConspect('subjectCode', e.target.value)} />
                </Field>
                <Field label="Код экзамена" hint="Должен совпадать с просмотром.">
                  <Input value={conspectForm.examCode} onChange={(e) => setConspect('examCode', e.target.value)} />
                </Field>
                <Field label="Код раздела" required hint="Главная связь со страницей ученика. Для A1 должно быть A1.">
                  <Input value={conspectForm.sectionCode} onChange={(e) => setConspect('sectionCode', normalizeSectionCode(e.target.value) || e.target.value)} />
                </Field>
                <label className="flex items-center gap-2 pt-7">
                  <input type="checkbox" checked={conspectForm.isPublished} onChange={(e) => setConspect('isPublished', e.target.checked)} />
                  <span className="text-sm font-semibold">Опубликован</span>
                  <span className="text-xs text-neutral-500">виден ученику</span>
                </label>
                <div className="md:col-span-2">
                  <Field label="Краткое описание" hint="Покажется над HTML-конспектом.">
                    <Textarea rows={3} value={conspectForm.lead} onChange={(e) => setConspect('lead', e.target.value)} />
                  </Field>
                </div>
                <div className="md:col-span-2">
                  <Field label="Бейджи" hint="Через запятую: A1, орфография, ЦТ/ЦЭ. Это не JSON.">
                    <Input value={conspectForm.badgesText} onChange={(e) => setConspect('badgesText', e.target.value)} />
                  </Field>
                </div>
              </div>

              <div className="mt-6 rounded-[2rem] border border-neutral-200 bg-neutral-50 p-4 dark:border-neutral-800 dark:bg-neutral-950">
                <div className="mb-3 flex flex-wrap items-center justify-between gap-3">
                  <div>
                    <div className="flex items-center gap-2 font-semibold"><Code2 size={18} /> HTML конспекта</div>
                    <div className="mt-1 text-xs leading-5 text-neutral-500 dark:text-neutral-400">
                      Это поле сохраняется в LearningConspect.ContentJson как mode=html. Именно его читает /{normalizedSectionCode.toLowerCase()}.
                    </div>
                  </div>
                  <button type="button" onClick={() => setShowPreview((v) => !v)} className="btn-outline inline-flex items-center gap-2">
                    <Eye size={16} /> {showPreview ? 'Скрыть предпросмотр' : 'Предпросмотр'}
                  </button>
                </div>
                <Textarea rows={26} spellCheck={false} value={html} onChange={(e) => setHtml(e.target.value)} className="font-mono text-sm" />
                <div className="mt-3 flex gap-2 rounded-2xl border border-amber-200 bg-amber-50 p-3 text-xs leading-5 text-amber-900 dark:border-amber-900 dark:bg-amber-950/30 dark:text-amber-100">
                  <AlertTriangle size={15} className="mt-0.5 shrink-0" />
                  Не надо выбирать корень ЦТ/ЦЭ. Для редактирования A1 открывай /editor/a1, для B10 — /editor/b10. Сохранение останется привязанным к sectionCode.
                </div>
                {showPreview ? <div className="mt-4"><HtmlPreview html={html} /></div> : null}
              </div>

              <div className="mt-6 flex flex-wrap gap-3">
                <button type="button" onClick={saveConspect} disabled={busy === 'conspect'} className="btn-primary inline-flex items-center gap-2 disabled:opacity-60">
                  {busy === 'conspect' ? <Loader2 size={18} className="animate-spin" /> : <Save size={18} />}
                  Сохранить конспект {normalizedSectionCode}
                </button>
                <Link to={currentViewPath} className="btn-outline inline-flex items-center gap-2">
                  <Eye size={18} /> Проверить как ученик
                </Link>
              </div>
            </section>

            <SectionTaskAdminPanel selectedCourse={taskCourse} />
          </main>
        </div>
      </div>
    </Layout>
  );
}
