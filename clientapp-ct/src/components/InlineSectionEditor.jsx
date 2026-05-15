import React, { useCallback, useEffect, useMemo, useState } from 'react';
import {
  AlertTriangle,
  CheckCircle2,
  Code2,
  Eye,
  FileText,
  HelpCircle,
  Loader2,
  Plus,
  RefreshCcw,
  Save,
  Settings2,
} from 'lucide-react';
import CtStructureBootstrapPanel from './CtStructureBootstrapPanel';
import SectionTaskAdminPanel from './SectionTaskAdminPanel';
import {
  createLearningConspect,
  createLearningCourse,
  getLearningConspect,
  getLearningConspects,
  getLearningCourseTree,
  updateLearningConspect,
} from '../api/learning';
import { getApiErrorMessage } from '../api/http';
import { EXAM_CODE, SUBJECT_CODE, normalizeSectionCode, sectionSortOrder } from '../data/ctSections';


const ROOT_SLUG = `${SUBJECT_CODE}-${EXAM_CODE}`;
const ROOT_TITLE = 'Русский язык — ЦТ/ЦЭ';

function findRootCourse(courses) {
  return (courses || []).find((course) => course.slug === ROOT_SLUG)
    || (courses || []).find((course) => course.subjectCode === SUBJECT_CODE && course.examCode === EXAM_CODE && !course.sectionCode)
    || null;
}

function buildSectionCoursePayload(sectionCode, parentId) {
  return {
    parentCourseId: parentId,
    slug: sectionCode.toLowerCase(),
    title: sectionCode,
    shortTitle: sectionCode,
    summary: `HTML-конспект и задания для ${sectionCode}.`,
    description: '',
    subjectCode: SUBJECT_CODE,
    examCode: EXAM_CODE,
    sectionCode,
    sortOrder: sectionSortOrder(sectionCode),
    isPublished: true,
  };
}

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
      sandbox="allow-forms allow-popups"
      srcDoc={srcDoc}
    />
  );
}

export default function InlineSectionEditor({ sectionCode, onConspectSaved }) {
  const normalizedSectionCode = normalizeSectionCode(sectionCode || '');
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
  const taskCourse = sectionCourse || { sectionCode: normalizedSectionCode, subjectCode: SUBJECT_CODE, examCode: EXAM_CODE };

  const openConspectDetails = useCallback((details, courseForDefaults = null) => {
    const formSectionCode = normalizeSectionCode(details?.conspect?.sectionCode || normalizedSectionCode);
    setConspectForm(formFromConspect(details, formSectionCode, courseForDefaults));
    const parsedHtml = htmlFromContent(details?.contentJson);
    setHtml(parsedHtml.html);
    setContentWarning(parsedHtml.isHtml ? '' : 'Этот конспект хранится в старом JSON-формате. Я не подменяю его шаблоном: вставь HTML вручную и сохрани, если хочешь перевести его в новый формат.');
    setShowPreview(false);
  }, [normalizedSectionCode]);

  const startNewConspect = useCallback((courseForDefaults = sectionCourse) => {
    const form = makeNewConspectForm(normalizedSectionCode, courseForDefaults);
    setConspectForm(form);
    setHtml(defaultHtml(normalizedSectionCode));
    setContentWarning('');
    setShowPreview(false);
    setError('');
    setSuccess('Открыта новая форма конспекта. После сохранения она будет привязана к текущему номеру.');
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

  async function ensureSectionCourse() {
    if (sectionCourse?.id) return sectionCourse;

    setBusy('course');
    setError('');
    setSuccess('');
    try {
      const currentTree = tree?.length ? tree : (await getLearningCourseTree({ includeDraft: true }) || []);
      const currentCourses = flattenCourses(currentTree);
      let rootCourse = findRootCourse(currentCourses);

      if (!rootCourse) {
        rootCourse = await createLearningCourse({
          parentCourseId: null,
          slug: ROOT_SLUG,
          title: ROOT_TITLE,
          shortTitle: 'ЦТ/ЦЭ',
          summary: 'Основа под подготовку к ЦТ/ЦЭ. Номера A/B можно добавлять вручную без жёсткого лимита.',
          description: 'Служебный корень для второго фронта. Обычный пользователь его не видит как дерево.',
          subjectCode: SUBJECT_CODE,
          examCode: EXAM_CODE,
          sectionCode: null,
          sortOrder: 10,
          isPublished: true,
        });
      }

      const course = await createLearningCourse(buildSectionCoursePayload(normalizedSectionCode, rootCourse.id));
      await loadSection();
      setSuccess(`Служебный раздел ${normalizedSectionCode} создан. Теперь можно сохранять конспект и задания.`);
      return course;
    } catch (e) {
      setError(getError(e));
      return null;
    } finally {
      setBusy('');
    }
  }

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
    return '';
  }

  async function saveConspect() {
    const validation = validateConspect();
    if (validation) { setError(validation); return; }

    const formSectionCode = normalizeSectionCode(conspectForm.sectionCode) || normalizedSectionCode;
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
        sectionCode: formSectionCode,
        kind: 'html-conspect',
        sortOrder: Number(conspectForm.sortOrder) || 0,
        estimatedMinutes: Number(conspectForm.estimatedMinutes) || 10,
        badges: badgesFromText(conspectForm.badgesText),
        content: contentFromHtml(html),
        searchText: conspectForm.searchText || `${conspectForm.title} ${conspectForm.lead} ${formSectionCode}`,
        isPublished: conspectForm.isPublished,
      };

      const courseForSave = conspectForm.id ? sectionCourse : (sectionCourse || await ensureSectionCourse());
      if (!conspectForm.id && !courseForSave?.id) return;

      const saved = conspectForm.id
        ? await updateLearningConspect(conspectForm.id, payload)
        : await createLearningConspect(courseForSave.id, payload);

      const savedConspect = saved.conspect || {};
      setConspectForm((prev) => ({
        ...prev,
        id: savedConspect.id || prev.id,
        slug: savedConspect.slug || payload.slug,
        sectionCode: savedConspect.sectionCode || formSectionCode,
      }));
      setContentWarning('');
      await loadSection();
      setSuccess('Сохранено. Это тот же конспект, который ученик увидит на этой странице.');
      onConspectSaved?.();
    } catch (e) {
      setError(getError(e));
    } finally {
      setBusy('');
    }
  }

  return (
    <section className="mt-6 space-y-5 rounded-[2rem] border-2 border-brand-300 bg-brand-50/50 p-4 shadow-soft dark:border-brand-800 dark:bg-brand-950/20 md:p-6">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div>
          <div className="inline-flex rounded-full bg-brand-600 px-3 py-1 text-xs font-black uppercase tracking-wide text-white">
            Режим редактора включён
          </div>
          <h2 className="mt-3 text-2xl font-black tracking-tight md:text-3xl">Редактирование {normalizedSectionCode}</h2>
          <p className="mt-1 max-w-3xl text-sm leading-6 text-neutral-600 dark:text-neutral-300">
            Страница не перестраивается: сверху остаётся тот же номер, ниже тот же конспект и задания. Здесь меняется только содержимое текущего номера.
          </p>
        </div>
        <div className="flex flex-wrap gap-2">
          <button type="button" onClick={loadSection} disabled={busy === 'load'} className="btn-outline inline-flex items-center gap-2 bg-white disabled:opacity-60 dark:bg-neutral-950">
            {busy === 'load' ? <Loader2 size={16} className="animate-spin" /> : <RefreshCcw size={16} />}
            Обновить
          </button>
          <button type="button" onClick={saveConspect} disabled={busy === 'conspect'} className="btn-primary inline-flex items-center gap-2 disabled:opacity-60">
            {busy === 'conspect' ? <Loader2 size={18} className="animate-spin" /> : <Save size={18} />}
            Сохранить
          </button>
        </div>
      </div>

      {error ? <Alert type="error">{error}</Alert> : null}
      {success ? <Alert type="success">{success}</Alert> : null}
      {busy === 'load' ? <Alert>Загружаю данные редактора для {normalizedSectionCode}...</Alert> : null}
      {!sectionCourse ? (
        <Alert type="warning">
          В базе ещё нет служебного раздела {normalizedSectionCode}. Это нормально для новых A/B-номеров: нажми кнопку ниже или просто сохрани новый конспект, и раздел создастся автоматически.
          <button type="button" onClick={ensureSectionCourse} disabled={busy === 'course'} className="btn-primary mt-3 inline-flex items-center gap-2 disabled:opacity-60">
            {busy === 'course' ? <Loader2 size={16} className="animate-spin" /> : <Plus size={16} />}
            Создать раздел {normalizedSectionCode}
          </button>
        </Alert>
      ) : null}
      {contentWarning ? <Alert type="warning">{contentWarning}</Alert> : null}

      <div className="rounded-[1.75rem] border border-neutral-200 bg-white p-4 dark:border-neutral-800 dark:bg-neutral-900 md:p-5">
        <div className="mb-4 flex flex-wrap items-center justify-between gap-3">
          <div>
            <div className="flex items-center gap-2 text-sm font-bold uppercase tracking-wide text-brand-700 dark:text-brand-300"><FileText size={16} /> HTML-конспект</div>
            <p className="mt-1 text-sm text-neutral-500 dark:text-neutral-400">Основные поля сверху, служебные спрятаны ниже, чтобы не мешали.</p>
          </div>
          <button type="button" onClick={() => startNewConspect(sectionCourse)} className="btn-outline inline-flex items-center gap-2">
            <Plus size={16} /> Новый конспект
          </button>
        </div>

        {conspects.length > 0 ? (
          <div className="mb-5 flex flex-wrap gap-2">
            {conspects.map((item) => (
              <button
                key={item.id}
                type="button"
                onClick={() => openConspect(item.id || item.slug)}
                className={`rounded-2xl border px-3 py-2 text-left text-sm font-semibold transition hover:border-brand-300 ${conspectForm.id === item.id ? 'border-brand-400 bg-brand-50 text-brand-900 dark:border-brand-800 dark:bg-brand-900/20 dark:text-brand-100' : 'border-neutral-200 bg-neutral-50 dark:border-neutral-800 dark:bg-neutral-950'}`}
              >
                <span>{item.title}</span>
                {item.isPublished ? <CheckCircle2 size={14} className="ml-2 inline text-emerald-600" /> : <span className="ml-2 text-xs text-amber-700 dark:text-amber-200">черновик</span>}
              </button>
            ))}
          </div>
        ) : (
          <div className="mb-5 rounded-3xl border border-dashed border-neutral-200 p-4 text-sm text-neutral-500 dark:border-neutral-800 dark:text-neutral-400">
            Для {normalizedSectionCode} пока нет конспекта. Заполни форму и сохрани.
          </div>
        )}

        <div className="grid gap-4 md:grid-cols-2">
          <Field label="Название" required hint="Заголовок конспекта, который относится к текущему номеру.">
            <Input value={conspectForm.title} onChange={(e) => setConspect('title', e.target.value)} />
          </Field>
          <Field label="Время чтения" hint="Примерное время в минутах.">
            <Input type="number" min="1" value={conspectForm.estimatedMinutes} onChange={(e) => setConspect('estimatedMinutes', e.target.value)} />
          </Field>
          <div className="md:col-span-2">
            <Field label="Краткое описание" hint="Небольшой текст над конспектом.">
              <Textarea rows={2} value={conspectForm.lead} onChange={(e) => setConspect('lead', e.target.value)} />
            </Field>
          </div>
          <label className="flex items-center gap-2 md:col-span-2">
            <input type="checkbox" checked={conspectForm.isPublished} onChange={(e) => setConspect('isPublished', e.target.checked)} />
            <span className="text-sm font-semibold">Опубликован</span>
            <span className="text-xs text-neutral-500">если выключить, ученик его не увидит</span>
          </label>
        </div>

        <details className="mt-5 rounded-2xl border border-neutral-200 bg-neutral-50 p-4 dark:border-neutral-800 dark:bg-neutral-950">
          <summary className="cursor-pointer list-none text-sm font-bold"><Settings2 size={16} className="mr-2 inline" />Служебные поля</summary>
          <div className="mt-4 grid gap-4 md:grid-cols-2">
            <Field label="Slug" required hint="Технический адрес конспекта в базе.">
              <Input value={conspectForm.slug} onChange={(e) => setConspect('slug', e.target.value)} />
            </Field>
            <Field label="Порядок" hint="Если конспектов несколько, меньший порядок выбирается первым.">
              <Input type="number" value={conspectForm.sortOrder} onChange={(e) => setConspect('sortOrder', e.target.value)} />
            </Field>
            <Field label="Код предмета">
              <Input value={conspectForm.subjectCode} onChange={(e) => setConspect('subjectCode', e.target.value)} />
            </Field>
            <Field label="Код экзамена">
              <Input value={conspectForm.examCode} onChange={(e) => setConspect('examCode', e.target.value)} />
            </Field>
            <Field label="Код раздела" required hint="Главная связь: для страницы /a1 здесь должно быть A1.">
              <Input value={conspectForm.sectionCode} onChange={(e) => setConspect('sectionCode', normalizeSectionCode(e.target.value) || e.target.value)} />
            </Field>
            <Field label="Бейджи" hint="Через запятую.">
              <Input value={conspectForm.badgesText} onChange={(e) => setConspect('badgesText', e.target.value)} />
            </Field>
          </div>
        </details>

        <div className="mt-5 rounded-[1.5rem] border border-neutral-200 bg-neutral-50 p-4 dark:border-neutral-800 dark:bg-neutral-950">
          <div className="mb-3 flex flex-wrap items-center justify-between gap-3">
            <div>
              <div className="flex items-center gap-2 font-semibold"><Code2 size={18} /> HTML</div>
              <p className="mt-1 text-xs leading-5 text-neutral-500 dark:text-neutral-400">Просто вставь готовый HTML. Он сохраняется в ContentJson как mode=html.</p>
            </div>
            <button type="button" onClick={() => setShowPreview((v) => !v)} className="btn-outline inline-flex items-center gap-2 bg-white dark:bg-neutral-950">
              <Eye size={16} /> {showPreview ? 'Скрыть предпросмотр' : 'Предпросмотр'}
            </button>
          </div>
          <Textarea rows={22} spellCheck={false} value={html} onChange={(e) => setHtml(e.target.value)} className="font-mono text-sm" />
          {showPreview ? <div className="mt-4"><HtmlPreview html={html} /></div> : null}
        </div>
      </div>

      <SectionTaskAdminPanel selectedCourse={taskCourse} />

      <details className="rounded-[1.75rem] border border-dashed border-neutral-300 bg-white/70 p-4 dark:border-neutral-800 dark:bg-neutral-900/70">
        <summary className="cursor-pointer list-none text-sm font-bold"><AlertTriangle size={16} className="mr-2 inline" />Служебно: стартовая основа и ручные A/B-номера</summary>
        <div className="mt-4">
          <CtStructureBootstrapPanel allCourses={allCourses} onDone={() => loadSection()} />
        </div>
      </details>
    </section>
  );
}
