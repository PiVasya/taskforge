import React, { useCallback, useEffect, useMemo, useState } from 'react';
import { Link, useNavigate, useParams } from 'react-router-dom';
import {
  AlertTriangle,
  BookOpen,
  CheckCircle2,
  ChevronRight,
  Code2,
  Eye,
  FileText,
  HelpCircle,
  Info,
  Layers3,
  Loader2,
  Plus,
  Save,
} from 'lucide-react';
import Layout from '../components/Layout';
import CtStructureBootstrapPanel from '../components/CtStructureBootstrapPanel';
import SectionTaskAdminPanel from '../components/SectionTaskAdminPanel';
import {
  createLearningConspect,
  createLearningCourse,
  getLearningConspect,
  getLearningCourseOutline,
  getLearningCourseTree,
  updateLearningConspect,
  updateLearningCourse,
} from '../api/learning';
import { getApiErrorMessage } from '../api/http';
import { useEditorMode } from '../contexts/EditorModeContext';

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

function flatten(nodes, level = 0, result = []) {
  (nodes || []).forEach((node) => {
    result.push({ ...node, level });
    flatten(node.children, level + 1, result);
  });
  return result;
}

function findCourse(nodes, slug) {
  for (const node of nodes || []) {
    if (node.slug === slug) return node;
    const child = findCourse(node.children, slug);
    if (child) return child;
  }
  return null;
}

function findPath(nodes, slug, parents = []) {
  for (const node of nodes || []) {
    const next = [...parents, node];
    if (node.slug === slug) return next;
    const child = findPath(node.children, slug, next);
    if (child.length) return child;
  }
  return [];
}

function courseType(course) {
  if (course?.sectionCode) return 'раздел';
  if (course?.examCode) return 'экзамен';
  if (!course?.parentCourseId) return 'предмет';
  return 'тема';
}

function makeCourseForm(course) {
  return {
    id: course?.id || '',
    parentCourseId: course?.parentCourseId || '',
    slug: course?.slug || '',
    title: course?.title || '',
    shortTitle: course?.shortTitle || '',
    summary: course?.summary || '',
    description: course?.description || '',
    subjectCode: course?.subjectCode || '',
    examCode: course?.examCode || '',
    sectionCode: course?.sectionCode || '',
    sortOrder: String(course?.sortOrder ?? 10),
    isPublished: course?.isPublished !== false,
  };
}

function makeChildForm(parent) {
  return {
    slug: '',
    title: '',
    shortTitle: '',
    summary: '',
    description: '',
    subjectCode: parent?.subjectCode || '',
    examCode: parent?.examCode || '',
    sectionCode: '',
    sortOrder: '10',
    isPublished: true,
  };
}

function defaultHtml(title = 'Новый конспект') {
  return `<section class="tf-conspect">
  <h1>${title}</h1>
  <p>Вставь сюда готовый HTML конспекта: вкладки, таблицы, карточки правил, словари и т.п.</p>

  <h2>Теория</h2>
  <p>Здесь будет объяснение темы.</p>

  <h2>Примеры</h2>
  <ul>
    <li>пример 1</li>
    <li>пример 2</li>
  </ul>
</section>`;
}

function makeConspectForm(course) {
  const title = course?.sectionCode ? `${course.sectionCode}. Новый конспект` : 'Новый конспект';
  return {
    id: '',
    slug: slugify(title),
    title,
    subtitle: 'Конспект',
    lead: 'Кратко опиши, что ученик поймёт после конспекта.',
    subjectCode: course?.subjectCode || '',
    examCode: course?.examCode || '',
    sectionCode: course?.sectionCode || '',
    sortOrder: '10',
    estimatedMinutes: '10',
    badgesText: [course?.sectionCode, 'конспект', 'ЦТ/ЦЭ'].filter(Boolean).join(', '),
    searchText: '',
    isPublished: true,
  };
}

function safeParseJson(raw, fallback = null) {
  if (!raw) return fallback;
  if (typeof raw === 'object') return raw;
  try { return JSON.parse(raw); } catch { return fallback; }
}

function htmlFromContent(raw) {
  const content = safeParseJson(raw, null);
  if (!content) return '';
  if (content.mode === 'html' && typeof content.html === 'string') return content.html;
  if (typeof content.html === 'string') return content.html;
  if (typeof content.rawHtml === 'string') return content.rawHtml;
  return '';
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
  return <input {...props} className={`w-full rounded-2xl border border-neutral-200 dark:border-neutral-800 bg-neutral-50 dark:bg-neutral-950 px-3 py-2.5 outline-none focus:border-brand-400 ${props.className || ''}`} />;
}

function Textarea(props) {
  return <textarea {...props} className={`w-full rounded-2xl border border-neutral-200 dark:border-neutral-800 bg-neutral-50 dark:bg-neutral-950 px-3 py-2.5 outline-none focus:border-brand-400 ${props.className || ''}`} />;
}

function Alert({ type = 'info', children }) {
  const styles = type === 'error'
    ? 'border-red-200 bg-red-50 text-red-800 dark:border-red-900 dark:bg-red-950/30 dark:text-red-200'
    : type === 'success'
      ? 'border-emerald-200 bg-emerald-50 text-emerald-800 dark:border-emerald-900 dark:bg-emerald-950/30 dark:text-emerald-200'
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

function TreeNode({ node, selected, open }) {
  const isActive = node.slug === selected;
  return (
    <div>
      <button
        type="button"
        onClick={() => open(node.slug)}
        className={`w-full rounded-2xl px-3 py-2 text-left transition ${isActive ? 'bg-brand-50 text-brand-900 ring-1 ring-brand-200 dark:bg-brand-900/30 dark:text-brand-100 dark:ring-brand-800' : 'hover:bg-neutral-100 dark:hover:bg-neutral-900'}`}
      >
        <div className="flex items-start justify-between gap-2">
          <div>
            <div className="font-semibold">{node.shortTitle || node.title}</div>
            <div className="text-xs text-neutral-500 dark:text-neutral-400">/{node.slug}</div>
          </div>
          <div className="text-xs text-neutral-400">{courseType(node)}</div>
        </div>
      </button>
      {node.children?.length ? (
        <div className="ml-5 border-l border-neutral-200 pl-2 dark:border-neutral-800">
          {node.children.map((child) => <TreeNode key={child.id} node={child} selected={selected} open={open} />)}
        </div>
      ) : null}
    </div>
  );
}

export default function LearningEditorPage() {
  const { courseSlug } = useParams();
  const navigate = useNavigate();
  const { setEditorMode } = useEditorMode();

  const [tree, setTree] = useState([]);
  const [selected, setSelected] = useState(courseSlug || '');
  const [outline, setOutline] = useState(null);
  const [courseForm, setCourseForm] = useState(makeCourseForm(null));
  const [childForm, setChildForm] = useState(makeChildForm(null));
  const [conspectForm, setConspectForm] = useState(makeConspectForm(null));
  const [html, setHtml] = useState(defaultHtml());
  const [showPreview, setShowPreview] = useState(false);
  const [busy, setBusy] = useState('');
  const [error, setError] = useState('');
  const [success, setSuccess] = useState('');

  const allCourses = useMemo(() => flatten(tree), [tree]);
  const selectedCourse = useMemo(() => findCourse(tree, selected), [tree, selected]);
  const breadcrumbs = useMemo(() => findPath(tree, selected), [tree, selected]);

  useEffect(() => { setEditorMode(true); }, [setEditorMode]);

  const loadTree = useCallback(async (preferredSlug = '') => {
    const data = await getLearningCourseTree({ includeDraft: true });
    setTree(data || []);
    const flat = flatten(data || []);
    const targetSlug = preferredSlug || courseSlug || '';
    const next = targetSlug && flat.some((x) => x.slug === targetSlug) ? targetSlug : flat[0]?.slug || '';
    setSelected(next);
    if (next && next !== courseSlug) navigate(`/editor/courses/${next}`, { replace: true });
    return next;
  }, [courseSlug, navigate]);

  async function loadOutline(slug) {
    if (!slug) return;
    const data = await getLearningCourseOutline(slug, { includeDraft: true });
    setOutline(data);
  }

  useEffect(() => {
    setBusy('init');
    setError('');
    loadTree(courseSlug)
      .catch((e) => setError(getError(e)))
      .finally(() => setBusy(''));
    
  }, [courseSlug, loadTree]);

  useEffect(() => {
    if (!selectedCourse) return;
    setCourseForm(makeCourseForm(selectedCourse));
    setChildForm(makeChildForm(selectedCourse));
    setConspectForm(makeConspectForm(selectedCourse));
    setHtml(defaultHtml(selectedCourse.sectionCode ? `${selectedCourse.sectionCode}. Новый конспект` : 'Новый конспект'));
    setShowPreview(false);
    setError('');
    setSuccess('');
    setBusy('outline');
    loadOutline(selectedCourse.slug)
      .catch((e) => setError(getError(e)))
      .finally(() => setBusy(''));
    
  }, [selectedCourse]);

  function open(slug) {
    setSelected(slug);
    navigate(`/editor/courses/${slug}`);
  }

  function setCourse(name, value) { setCourseForm((prev) => ({ ...prev, [name]: value })); }
  function setChild(name, value) { setChildForm((prev) => ({ ...prev, [name]: value })); }
  function setConspect(name, value) { setConspectForm((prev) => ({ ...prev, [name]: value })); }

  function validateCourse(form) {
    if (!form.title.trim()) return 'Заполни название.';
    if (!form.slug.trim()) return 'Заполни slug. Это часть адреса страницы.';
    return '';
  }

  function validateConspect(form) {
    if (!selectedCourse?.id) return 'Сначала выбери курс/раздел слева.';
    if (!form.title.trim()) return 'Заполни название конспекта.';
    if (!form.slug.trim()) return 'Заполни slug конспекта.';
    if (!html.trim()) return 'Вставь HTML конспекта.';
    return '';
  }

  async function saveCourse() {
    const validation = validateCourse(courseForm);
    if (validation) { setError(validation); return; }
    setBusy('course'); setError(''); setSuccess('');
    try {
      await updateLearningCourse(courseForm.id, {
        slug: courseForm.slug.trim(),
        title: courseForm.title.trim(),
        shortTitle: courseForm.shortTitle.trim() || null,
        summary: courseForm.summary,
        description: courseForm.description,
        subjectCode: courseForm.subjectCode.trim() || null,
        examCode: courseForm.examCode.trim() || null,
        sectionCode: courseForm.sectionCode.trim() || null,
        sortOrder: Number(courseForm.sortOrder) || 0,
        isPublished: courseForm.isPublished,
      });
      setSuccess('Курс / раздел сохранён.');
      const nextSlug = courseForm.slug.trim();
      await loadTree(nextSlug);
      await loadOutline(nextSlug);
    } catch (e) {
      setError(getError(e));
    } finally {
      setBusy('');
    }
  }

  async function createChildCourse() {
    const slug = childForm.slug.trim() || slugify(childForm.title);
    const validation = validateCourse({ ...childForm, slug });
    if (validation) { setError(validation); return; }
    setBusy('child'); setError(''); setSuccess('');
    try {
      const created = await createLearningCourse({
        parentCourseId: selectedCourse.id,
        slug,
        title: childForm.title.trim(),
        shortTitle: childForm.shortTitle.trim() || null,
        summary: childForm.summary,
        description: childForm.description,
        subjectCode: childForm.subjectCode.trim() || null,
        examCode: childForm.examCode.trim() || null,
        sectionCode: childForm.sectionCode.trim() || null,
        sortOrder: Number(childForm.sortOrder) || 0,
        isPublished: childForm.isPublished,
      });
      setSuccess(`Создано: ${created.title}`);
      await loadTree(created.slug);
    } catch (e) {
      setError(getError(e));
    } finally {
      setBusy('');
    }
  }

  function startNewConspect() {
    const form = makeConspectForm(selectedCourse);
    setConspectForm(form);
    setHtml(defaultHtml(form.title));
    setShowPreview(false);
    setError('');
    setSuccess('Создана черновая форма. Заполни поля и нажми «Сохранить конспект».');
  }

  async function openConspect(slug) {
    setBusy('conspect-load'); setError(''); setSuccess('');
    try {
      const details = await getLearningConspect(slug, { courseSlug: selectedCourse.slug, includeDraft: true });
      const c = details.conspect;
      setConspectForm({
        id: c.id,
        slug: c.slug || '',
        title: c.title || '',
        subtitle: c.subtitle || '',
        lead: c.lead || '',
        subjectCode: c.subjectCode || selectedCourse.subjectCode || '',
        examCode: c.examCode || selectedCourse.examCode || '',
        sectionCode: c.sectionCode || selectedCourse.sectionCode || '',
        sortOrder: String(c.sortOrder ?? 10),
        estimatedMinutes: String(c.estimatedMinutes ?? 10),
        badgesText: safeParseJson(c.badgesJson, []).join(', '),
        searchText: details.searchText || '',
        isPublished: c.isPublished !== false,
      });
      const existingHtml = htmlFromContent(details.contentJson);
      if (existingHtml) {
        setHtml(existingHtml);
      } else {
        setHtml(defaultHtml(c.title));
      }
      setShowPreview(false);
    } catch (e) {
      setError(getError(e));
    } finally {
      setBusy('');
    }
  }

  async function saveConspect() {
    const validation = validateConspect(conspectForm);
    if (validation) { setError(validation); return; }
    setBusy('conspect'); setError(''); setSuccess('');
    try {
      const payload = {
        slug: conspectForm.slug.trim(),
        title: conspectForm.title.trim(),
        subtitle: conspectForm.subtitle.trim() || null,
        lead: conspectForm.lead,
        subjectCode: conspectForm.subjectCode.trim() || selectedCourse.subjectCode || null,
        examCode: conspectForm.examCode.trim() || selectedCourse.examCode || null,
        sectionCode: conspectForm.sectionCode.trim() || selectedCourse.sectionCode || null,
        kind: 'html-conspect',
        sortOrder: Number(conspectForm.sortOrder) || 0,
        estimatedMinutes: Number(conspectForm.estimatedMinutes) || 10,
        badges: badgesFromText(conspectForm.badgesText),
        content: contentFromHtml(html),
        searchText: conspectForm.searchText || `${conspectForm.title} ${conspectForm.lead}`,
        isPublished: conspectForm.isPublished,
      };

      const saved = conspectForm.id
        ? await updateLearningConspect(conspectForm.id, payload)
        : await createLearningConspect(selectedCourse.id, payload);

      setConspectForm((prev) => ({ ...prev, id: saved.conspect.id, slug: saved.conspect.slug }));
      setSuccess(conspectForm.id ? 'Конспект сохранён.' : 'Конспект создан и сохранён.');
      await loadOutline(selectedCourse.slug);
    } catch (e) {
      setError(getError(e));
    } finally {
      setBusy('');
    }
  }

  return (
    <Layout fullWidth>
      <div className="container-app py-8">
        <div className="mb-6 flex flex-wrap items-center justify-between gap-3">
          <div>
            <Link to={selectedCourse ? `/courses/${selectedCourse.slug}` : '/'} className="inline-flex items-center gap-2 text-sm text-neutral-600 hover:text-brand-700 dark:text-neutral-300 dark:hover:text-brand-200">
              <Eye size={16} /> Выйти в просмотр
            </Link>
            <h1 className="mt-3 text-3xl font-bold tracking-tight md:text-5xl">Редактор CT/CE</h1>
            <p className="mt-2 max-w-3xl text-neutral-600 dark:text-neutral-300">
              Включён режим редактирования: слева выбираешь предмет, экзамен, раздел или тему, справа меняешь именно этот узел. Конспект теперь проще: вставляешь готовый HTML и сохраняешь.
            </p>
          </div>
          <div className="rounded-3xl border border-neutral-200 bg-white p-4 text-sm shadow-soft dark:border-neutral-800 dark:bg-neutral-900">
            <div className="mb-1 flex items-center gap-2 font-semibold"><Info size={16} /> Логика</div>
            <div className="text-neutral-500 dark:text-neutral-400">Дерево курсов отдельно, конспект HTML отдельно, задания отдельно.</div>
          </div>
        </div>

        {error ? <div className="mb-4"><Alert type="error">{error}</Alert></div> : null}
        {success ? <div className="mb-4"><Alert type="success">{success}</Alert></div> : null}
        {busy === 'init' ? <Alert>Загружаю дерево...</Alert> : null}

        <div className="grid gap-6 lg:grid-cols-[360px_1fr]">
          <aside className="space-y-4">
            <section className="rounded-[2rem] border border-neutral-200 bg-white p-4 shadow-soft dark:border-neutral-800 dark:bg-neutral-900">
              <div className="mb-3 flex items-center gap-2 font-semibold"><Layers3 size={18} /> Дерево</div>
              <div className="max-h-[72vh] space-y-1 overflow-auto pr-1">
                {tree.map((node) => <TreeNode key={node.id} node={node} selected={selected} open={open} />)}
                {!tree.length && <div className="rounded-2xl border border-dashed border-neutral-200 p-4 text-sm text-neutral-500 dark:border-neutral-800 dark:text-neutral-400">Дерево пустое.</div>}
              </div>
            </section>
            <CtStructureBootstrapPanel allCourses={allCourses} onDone={() => loadTree(selected)} />
            <section className="rounded-[2rem] border border-neutral-200 bg-white p-4 shadow-soft dark:border-neutral-800 dark:bg-neutral-900">
              <div className="mb-2 font-semibold">Быстрый переход</div>
              <select value={selected} onChange={(e) => open(e.target.value)} className="w-full rounded-2xl border border-neutral-200 bg-neutral-50 px-3 py-2.5 outline-none focus:border-brand-400 dark:border-neutral-800 dark:bg-neutral-950">
                {allCourses.map((item) => <option key={item.id} value={item.slug}>{`${'— '.repeat(item.level)}${item.title}`}</option>)}
              </select>
            </section>
          </aside>

          <main className="space-y-6">
            {selectedCourse ? (
              <>
                <nav className="flex flex-wrap items-center gap-2 text-sm text-neutral-500 dark:text-neutral-400">
                  <Link to="/editor">CT</Link>
                  {breadcrumbs.map((item) => (
                    <React.Fragment key={item.id}>
                      <ChevronRight size={15} />
                      <button type="button" onClick={() => open(item.slug)} className={item.slug === selected ? 'font-semibold text-neutral-900 dark:text-neutral-100' : 'hover:text-brand-700 dark:hover:text-brand-200'}>{item.shortTitle || item.title}</button>
                    </React.Fragment>
                  ))}
                </nav>

                <section className="rounded-[2rem] border border-neutral-200 bg-white p-5 shadow-soft dark:border-neutral-800 dark:bg-neutral-900 md:p-6">
                  <div className="mb-5 flex flex-wrap items-center justify-between gap-3">
                    <div>
                      <div className="flex items-center gap-2 text-sm font-semibold uppercase tracking-wide text-brand-700 dark:text-brand-300"><BookOpen size={16} /> {courseType(selectedCourse)}</div>
                      <h2 className="mt-1 text-2xl font-bold tracking-tight">Курс / раздел</h2>
                    </div>
                    <Link to={`/courses/${selectedCourse.slug}`} className="btn-outline inline-flex items-center gap-2"><Eye size={16} /> Просмотр</Link>
                  </div>

                  <div className="grid gap-4 md:grid-cols-2">
                    <Field label="Название" required hint="Человекочитаемое название в дереве и на странице."><Input value={courseForm.title} onChange={(e) => setCourse('title', e.target.value)} /></Field>
                    <Field label="Slug" required hint="Адресная часть страницы. Лучше латиница и дефисы, например russian-ct-ce-2026-a1."><Input value={courseForm.slug} onChange={(e) => setCourse('slug', e.target.value)} /></Field>
                    <Field label="Короткое название" hint="Показывается в дереве, если нужно короче полного названия. Например A1."><Input value={courseForm.shortTitle} onChange={(e) => setCourse('shortTitle', e.target.value)} /></Field>
                    <Field label="Порядок" hint="Чем меньше число, тем выше элемент в списке. Например 10, 20, 30."><Input type="number" value={courseForm.sortOrder} onChange={(e) => setCourse('sortOrder', e.target.value)} /></Field>
                    <Field label="Код предмета" hint="Связующий код предмета. Для русского можно russian. У детей обычно наследуется от родителя."><Input value={courseForm.subjectCode} onChange={(e) => setCourse('subjectCode', e.target.value)} /></Field>
                    <Field label="Код экзамена" hint="Связующий код экзамена. Например ct-ce-2026. У разделов обычно наследуется от экзамена."><Input value={courseForm.examCode} onChange={(e) => setCourse('examCode', e.target.value)} /></Field>
                    <Field label="Код раздела" hint="Не захардкожено. Это просто твой код раздела: A1, A2, B10 или любой другой."><Input value={courseForm.sectionCode} onChange={(e) => setCourse('sectionCode', e.target.value)} /></Field>
                    <label className="flex items-center gap-2 pt-7"><input type="checkbox" checked={courseForm.isPublished} onChange={(e) => setCourse('isPublished', e.target.checked)} /><span className="text-sm font-semibold">Опубликован</span><span className="text-xs text-neutral-500">виден в обычном режиме</span></label>
                    <div className="md:col-span-2"><Field label="Краткое описание" hint="Короткий текст для карточки."><Textarea rows={3} value={courseForm.summary} onChange={(e) => setCourse('summary', e.target.value)} /></Field></div>
                    <div className="md:col-span-2"><Field label="Полное описание" hint="Подробное описание раздела/курса. Можно оставить пустым."><Textarea rows={5} value={courseForm.description} onChange={(e) => setCourse('description', e.target.value)} /></Field></div>
                  </div>

                  <button type="button" onClick={saveCourse} disabled={busy === 'course'} className="btn-primary mt-5 inline-flex items-center gap-2 disabled:opacity-60">
                    {busy === 'course' ? <Loader2 size={18} className="animate-spin" /> : <Save size={18} />} Сохранить курс / раздел
                  </button>
                </section>

                <section className="rounded-[2rem] border border-neutral-200 bg-white p-5 shadow-soft dark:border-neutral-800 dark:bg-neutral-900 md:p-6">
                  <div className="mb-4 flex items-center gap-2"><Plus className="text-brand-600" /><h2 className="text-2xl font-bold tracking-tight">Добавить внутрь выбранного</h2></div>
                  <p className="mb-4 text-sm leading-6 text-neutral-500 dark:text-neutral-400">Так создаются дочерние элементы дерева: экзамен внутри предмета, раздел внутри экзамена, тема внутри раздела.</p>
                  <div className="grid gap-4 md:grid-cols-2">
                    <Field label="Название" required hint="Например: A1. Орфография"><Input value={childForm.title} onChange={(e) => setChild('title', e.target.value)} placeholder="A1. Орфография" /></Field>
                    <Field label="Slug" hint="Можно оставить пустым — сгенерируется из названия."><Input value={childForm.slug} onChange={(e) => setChild('slug', e.target.value)} /></Field>
                    <Field label="Короткое название" hint="Например A1"><Input value={childForm.shortTitle} onChange={(e) => setChild('shortTitle', e.target.value)} /></Field>
                    <Field label="Порядок" hint="Положение внутри родителя."><Input type="number" value={childForm.sortOrder} onChange={(e) => setChild('sortOrder', e.target.value)} /></Field>
                    <Field label="Код предмета" hint="Обычно уже заполнен от родителя."><Input value={childForm.subjectCode} onChange={(e) => setChild('subjectCode', e.target.value)} /></Field>
                    <Field label="Код экзамена" hint="Обычно уже заполнен от родителя."><Input value={childForm.examCode} onChange={(e) => setChild('examCode', e.target.value)} /></Field>
                    <Field label="Код раздела" hint="Заполняй для разделов: A1, A2, B10 и т.п. Для предмета/экзамена можно пусто."><Input value={childForm.sectionCode} onChange={(e) => setChild('sectionCode', e.target.value)} /></Field>
                    <label className="flex items-center gap-2 pt-7"><input type="checkbox" checked={childForm.isPublished} onChange={(e) => setChild('isPublished', e.target.checked)} /><span className="text-sm font-semibold">Опубликован</span></label>
                    <div className="md:col-span-2"><Field label="Краткое описание"><Textarea rows={3} value={childForm.summary} onChange={(e) => setChild('summary', e.target.value)} /></Field></div>
                  </div>
                  <button type="button" onClick={createChildCourse} disabled={busy === 'child'} className="btn-primary mt-5 inline-flex items-center gap-2 disabled:opacity-60">
                    {busy === 'child' ? <Loader2 size={18} className="animate-spin" /> : <Plus size={18} />} Создать внутри выбранного
                  </button>
                </section>

                <section className="rounded-[2rem] border border-neutral-200 bg-white p-5 shadow-soft dark:border-neutral-800 dark:bg-neutral-900 md:p-6">
                  <div className="mb-4 flex flex-wrap items-center justify-between gap-3">
                    <div>
                      <div className="flex items-center gap-2"><FileText className="text-brand-600" /><h2 className="text-2xl font-bold tracking-tight">Конспекты выбранного курса</h2></div>
                      <p className="mt-1 text-sm text-neutral-500 dark:text-neutral-400">Здесь конспект — это просто HTML. Задания не смешиваются с конспектом.</p>
                    </div>
                    <button type="button" onClick={startNewConspect} className="btn-outline inline-flex items-center gap-2"><Plus size={16} /> Новый конспект</button>
                  </div>

                  <div className="mb-5 grid gap-3 md:grid-cols-2 xl:grid-cols-3">
                    {(outline?.conspects || []).map((item) => (
                      <button key={item.id} type="button" onClick={() => openConspect(item.slug)} className={`rounded-3xl border p-4 text-left transition hover:border-brand-300 ${conspectForm.id === item.id ? 'border-brand-300 bg-brand-50 dark:border-brand-800 dark:bg-brand-900/20' : 'border-neutral-200 bg-neutral-50 dark:border-neutral-800 dark:bg-neutral-950'}`}>
                        <div className="font-semibold">{item.title}</div>
                        <div className="mt-1 text-xs text-neutral-500 dark:text-neutral-400">/{item.slug}</div>
                        {item.lead ? <div className="mt-2 line-clamp-2 text-sm text-neutral-600 dark:text-neutral-300">{item.lead}</div> : null}
                      </button>
                    ))}
                    {(outline?.conspects || []).length === 0 ? <div className="rounded-3xl border border-dashed border-neutral-200 p-5 text-sm text-neutral-500 dark:border-neutral-800 dark:text-neutral-400">Конспектов пока нет. Нажми «Новый конспект», заполни поля и сохрани.</div> : null}
                  </div>

                  <div className="grid gap-4 md:grid-cols-2">
                    <Field label="Название конспекта" required hint="Заголовок карточки и страницы."><Input value={conspectForm.title} onChange={(e) => setConspect('title', e.target.value)} /></Field>
                    <Field label="Slug" required hint="Адрес конспекта внутри курса."><Input value={conspectForm.slug} onChange={(e) => setConspect('slug', e.target.value)} /></Field>
                    <Field label="Подзаголовок" hint="Короткая пометка: Конспект, Алгоритм, Словарь."><Input value={conspectForm.subtitle} onChange={(e) => setConspect('subtitle', e.target.value)} /></Field>
                    <Field label="Минуты" hint="Примерное время чтения."><Input type="number" value={conspectForm.estimatedMinutes} onChange={(e) => setConspect('estimatedMinutes', e.target.value)} /></Field>
                    <Field label="Код предмета" hint="Для фильтрации и связи с заданиями."><Input value={conspectForm.subjectCode} onChange={(e) => setConspect('subjectCode', e.target.value)} /></Field>
                    <Field label="Код экзамена" hint="Например ct-ce-2026."><Input value={conspectForm.examCode} onChange={(e) => setConspect('examCode', e.target.value)} /></Field>
                    <Field label="Код раздела" hint="Например A1. По нему можно подтягивать задания раздела."><Input value={conspectForm.sectionCode} onChange={(e) => setConspect('sectionCode', e.target.value)} /></Field>
                    <label className="flex items-center gap-2 pt-7"><input type="checkbox" checked={conspectForm.isPublished} onChange={(e) => setConspect('isPublished', e.target.checked)} /><span className="text-sm font-semibold">Опубликован</span><span className="text-xs text-neutral-500">виден ученику</span></label>
                    <div className="md:col-span-2"><Field label="Краткое описание" hint="Покажется под заголовком и в карточке."><Textarea rows={3} value={conspectForm.lead} onChange={(e) => setConspect('lead', e.target.value)} /></Field></div>
                    <div className="md:col-span-2"><Field label="Бейджи" hint="Через запятую: A1, орфография, ЦТ/ЦЭ. Это не JSON."><Input value={conspectForm.badgesText} onChange={(e) => setConspect('badgesText', e.target.value)} /></Field></div>
                  </div>

                  <div className="mt-6 rounded-[2rem] border border-neutral-200 bg-neutral-50 p-4 dark:border-neutral-800 dark:bg-neutral-950">
                    <div className="mb-3 flex flex-wrap items-center justify-between gap-3">
                      <div>
                        <div className="flex items-center gap-2 font-semibold"><Code2 size={18} /> HTML конспекта</div>
                        <div className="mt-1 text-xs leading-5 text-neutral-500 dark:text-neutral-400">Вставь сюда готовую HTML-страницу или HTML-фрагмент. CSS можно писать внутри &lt;style&gt;. Это проще старого JSON-блочного редактора.</div>
                      </div>
                      <button type="button" onClick={() => setShowPreview((v) => !v)} className="btn-outline inline-flex items-center gap-2"><Eye size={16} /> {showPreview ? 'Скрыть предпросмотр' : 'Предпросмотр'}</button>
                    </div>
                    <Textarea rows={24} spellCheck={false} value={html} onChange={(e) => setHtml(e.target.value)} className="font-mono text-sm" />
                    <div className="mt-3 flex gap-2 rounded-2xl border border-amber-200 bg-amber-50 p-3 text-xs leading-5 text-amber-900 dark:border-amber-900 dark:bg-amber-950/30 dark:text-amber-100">
                      <AlertTriangle size={15} className="mt-0.5 shrink-0" />
                      HTML хранится отдельно от заданий. Кнопку к заданиям делать не нужно: фронт сам показывает задания под конспектом. Если ссылка нужна внутри HTML, веди на номер: &lt;a href=&quot;/{(selectedCourse.sectionCode || 'a1').toLowerCase()}&quot;&gt;Практика&lt;/a&gt;.
                    </div>
                    {showPreview ? <div className="mt-4"><HtmlPreview html={html} /></div> : null}
                  </div>

                  <div className="mt-6 flex flex-wrap gap-3">
                    <button type="button" onClick={saveConspect} disabled={busy === 'conspect'} className="btn-primary inline-flex items-center gap-2 disabled:opacity-60">
                      {busy === 'conspect' ? <Loader2 size={18} className="animate-spin" /> : <Save size={18} />} Сохранить конспект
                    </button>
                    {conspectForm.id ? <Link to={`/${(conspectForm.sectionCode || selectedCourse.sectionCode || 'a1').toLowerCase()}`} className="btn-outline inline-flex items-center gap-2"><CheckCircle2 size={18} /> Открыть как ученик</Link> : null}
                  </div>
                </section>

                <SectionTaskAdminPanel selectedCourse={selectedCourse} />
              </>
            ) : (
              <Alert>Выбери элемент дерева слева.</Alert>
            )}
          </main>
        </div>
      </div>
    </Layout>
  );
}
