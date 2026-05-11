import React, { useCallback, useEffect, useMemo, useState } from 'react';
import { Link, Navigate, useNavigate, useParams } from 'react-router-dom';
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
  Wand2,
} from 'lucide-react';
import Layout from '../components/Layout';
import SectionTaskAdminPanel from '../components/SectionTaskAdminPanel';
import {
  createLearningConspect,
  createLearningCourse,
  getLearningConspect,
  getLearningConspects,
  getLearningCourseTree,
  updateLearningConspect,
  updateLearningCourse,
} from '../api/learning';
import { getApiErrorMessage } from '../api/http';
import { useEditorMode } from '../contexts/EditorModeContext';
import {
  CT_PARTS,
  CT_SECTIONS,
  EXAM_CODE,
  SUBJECT_CODE,
  getSectionPath,
  getSectionsByPart,
  isKnownSectionCode,
  normalizeSectionCode,
} from '../data/ctSections';

const ROOT_SLUG = `${SUBJECT_CODE}-${EXAM_CODE}`;
const ROOT_TITLE = 'Русский язык — ЦТ/ЦЭ';

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

function flatten(nodes, result = []) {
  (nodes || []).forEach((node) => {
    result.push(node);
    flatten(node.children, result);
  });
  return result;
}

function sectionSortOrder(section) {
  return section.partCode === 'A' ? section.number : 1000 + section.number;
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

function defaultHtml(sectionCode = 'A1') {
  return `<section class="tf-conspect">
  <h1>${sectionCode}. Конспект</h1>
  <p>Вставь сюда готовый HTML-конспект для ${sectionCode}.</p>

  <h2>Теория</h2>
  <p>Здесь будет объяснение правила.</p>

  <h2>Примеры</h2>
  <ul>
    <li>пример 1</li>
    <li>пример 2</li>
  </ul>
</section>`;
}

function makeConspectForm(sectionCode = 'A1') {
  return {
    id: '',
    slug: `${sectionCode.toLowerCase()}-conspect`,
    title: `${sectionCode}. Конспект`,
    subtitle: 'Конспект',
    lead: `Теория и примеры для задания ${sectionCode}.`,
    subjectCode: SUBJECT_CODE,
    examCode: EXAM_CODE,
    sectionCode,
    sortOrder: '10',
    estimatedMinutes: '10',
    badgesText: `${sectionCode}, конспект, ЦТ/ЦЭ`,
    searchText: '',
    isPublished: true,
  };
}

function findRootCourse(courses) {
  return (courses || []).find((course) => course.slug === ROOT_SLUG)
    || (courses || []).find((course) => course.subjectCode === SUBJECT_CODE && course.examCode === EXAM_CODE && !course.sectionCode);
}

function findSectionCourse(courses, sectionCode) {
  const normalized = normalizeSectionCode(sectionCode);
  return (courses || []).find((course) => normalizeSectionCode(course.sectionCode) === normalized)
    || (courses || []).find((course) => String(course.slug || '').toLowerCase() === normalized.toLowerCase());
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

function SectionPicker({ active, onSelect }) {
  return (
    <section className="rounded-[2rem] border border-neutral-200 bg-white p-4 shadow-soft dark:border-neutral-800 dark:bg-neutral-900">
      <div className="mb-3 text-sm font-bold uppercase tracking-[0.18em] text-neutral-400">Номера ЦТ</div>
      <div className="space-y-4">
        {CT_PARTS.map((part) => (
          <div key={part.code}>
            <div className="mb-2 flex items-center justify-between gap-2">
              <h2 className="font-bold">{part.title}</h2>
              <span className="text-xs text-neutral-400">{part.count}</span>
            </div>
            <div className="grid grid-cols-5 gap-2 sm:grid-cols-8 lg:grid-cols-5 xl:grid-cols-6">
              {getSectionsByPart(part.code).map((section) => (
                <button
                  key={section.code}
                  type="button"
                  onClick={() => onSelect(section.code)}
                  className={`rounded-2xl border px-3 py-2 text-sm font-black transition ${section.code === active ? 'border-brand-500 bg-brand-600 text-white shadow-md' : 'border-neutral-200 bg-neutral-50 hover:border-brand-300 hover:bg-white dark:border-neutral-800 dark:bg-neutral-950 dark:hover:border-brand-700 dark:hover:bg-neutral-900'}`}
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
  const { canEdit, setEditorMode } = useEditorMode();

  const selectedSection = normalizeSectionCode(params.sectionCode || 'A1');
  const [courses, setCourses] = useState([]);
  const [conspects, setConspects] = useState([]);
  const [sectionCourse, setSectionCourse] = useState(null);
  const [conspectForm, setConspectForm] = useState(makeConspectForm(selectedSection || 'A1'));
  const [html, setHtml] = useState(defaultHtml(selectedSection || 'A1'));
  const [showPreview, setShowPreview] = useState(false);
  const [busy, setBusy] = useState('');
  const [error, setError] = useState('');
  const [success, setSuccess] = useState('');

  const existingSectionCodes = useMemo(() => new Set((courses || []).map((course) => normalizeSectionCode(course.sectionCode)).filter(Boolean)), [courses]);
  const missingCount = useMemo(() => CT_SECTIONS.filter((section) => !existingSectionCodes.has(section.code)).length, [existingSectionCodes]);

  useEffect(() => { setEditorMode(true); }, [setEditorMode]);

  const loadCourses = useCallback(async () => {
    const tree = await getLearningCourseTree({ includeDraft: true });
    const flat = flatten(tree || []);
    setCourses(flat);
    return flat;
  }, []);

  const loadConspectsForSection = useCallback(async (sectionCode) => {
    const list = await getLearningConspects({
      subjectCode: SUBJECT_CODE,
      examCode: EXAM_CODE,
      sectionCode,
      includeDraft: true,
    });
    const sorted = [...(list || [])].sort((a, b) => (a.sortOrder ?? 0) - (b.sortOrder ?? 0));
    setConspects(sorted);
    return sorted;
  }, []);

  const ensureSectionCourse = useCallback(async (sectionCode, knownCourses = courses) => {
    let flat = knownCourses || [];
    let root = findRootCourse(flat);

    if (!root) {
      root = await createLearningCourse({
        parentCourseId: null,
        slug: ROOT_SLUG,
        title: ROOT_TITLE,
        shortTitle: 'ЦТ/ЦЭ',
        summary: 'Служебный корень для номеров ЦТ/ЦЭ.',
        description: 'Обычный пользователь не видит дерево. Он открывает конкретные номера A1, A2, B5 и т.п.',
        subjectCode: SUBJECT_CODE,
        examCode: EXAM_CODE,
        sectionCode: null,
        sortOrder: 10,
        isPublished: true,
      });
      flat = await loadCourses();
    }

    let course = findSectionCourse(flat, sectionCode);
    const sectionMeta = CT_SECTIONS.find((section) => section.code === sectionCode);

    if (course && normalizeSectionCode(course.sectionCode) !== sectionCode) {
      course = await updateLearningCourse(course.id, {
        parentCourseId: course.parentCourseId || root.id,
        slug: course.slug || sectionCode.toLowerCase(),
        title: course.title || sectionCode,
        shortTitle: course.shortTitle || sectionCode,
        summary: course.summary || `HTML-конспект и задания для ${sectionCode}.`,
        description: course.description || '',
        subjectCode: course.subjectCode || SUBJECT_CODE,
        examCode: course.examCode || EXAM_CODE,
        sectionCode,
        sortOrder: course.sortOrder ?? (sectionMeta ? sectionSortOrder(sectionMeta) : 10),
        isPublished: course.isPublished !== false,
      });
      await loadCourses();
      return course;
    }

    if (!course) {
      course = await createLearningCourse({
        parentCourseId: root.id,
        slug: sectionCode.toLowerCase(),
        title: sectionCode,
        shortTitle: sectionCode,
        summary: `HTML-конспект и задания для ${sectionCode}.`,
        description: '',
        subjectCode: SUBJECT_CODE,
        examCode: EXAM_CODE,
        sectionCode,
        sortOrder: sectionMeta ? sectionSortOrder(sectionMeta) : 10,
        isPublished: true,
      });
      await loadCourses();
    }

    return course;
  }, [courses, loadCourses]);

  const loadSelectedSection = useCallback(async (sectionCode) => {
    setBusy('load');
    setError('');
    setSuccess('');
    setShowPreview(false);
    try {
      const flat = await loadCourses();
      const course = findSectionCourse(flat, sectionCode);
      setSectionCourse(course || null);

      const list = await loadConspectsForSection(sectionCode);
      const first = list[0];

      if (!first) {
        setConspectForm(makeConspectForm(sectionCode));
        setHtml(defaultHtml(sectionCode));
        return;
      }

      const details = await getLearningConspect(first.slug || first.id, {
        courseSlug: course?.slug,
        includeDraft: true,
      });
      const c = details.conspect || first;
      setConspectForm({
        id: c.id,
        slug: c.slug || `${sectionCode.toLowerCase()}-conspect`,
        title: c.title || `${sectionCode}. Конспект`,
        subtitle: c.subtitle || 'Конспект',
        lead: c.lead || '',
        subjectCode: c.subjectCode || SUBJECT_CODE,
        examCode: c.examCode || EXAM_CODE,
        sectionCode: c.sectionCode || sectionCode,
        sortOrder: String(c.sortOrder ?? 10),
        estimatedMinutes: String(c.estimatedMinutes ?? 10),
        badgesText: safeParseJson(c.badgesJson, []).join(', '),
        searchText: details.searchText || '',
        isPublished: c.isPublished !== false,
      });
      const existingHtml = htmlFromContent(details.contentJson);
      setHtml(existingHtml || `<!-- Этот конспект был создан в старом JSON-блочном формате. Вставь новый HTML и сохрани. -->\n${defaultHtml(sectionCode)}`);
    } catch (e) {
      setError(getError(e));
    } finally {
      setBusy('');
    }
  }, [loadConspectsForSection, loadCourses]);

  useEffect(() => {
    if (!selectedSection || !isKnownSectionCode(selectedSection)) return;
    loadSelectedSection(selectedSection);
  }, [loadSelectedSection, selectedSection]);

  if (!canEdit) {
    return (
      <Layout fullWidth>
        <div className="container-app py-8">
          <Alert type="error">У тебя нет роли для редактора. Нужна роль Admin, Editor или LearningEditor.</Alert>
        </div>
      </Layout>
    );
  }

  if (!selectedSection || !isKnownSectionCode(selectedSection)) {
    return <Navigate to="/editor/a1" replace />;
  }

  function selectSection(sectionCode) {
    navigate(`/editor/${sectionCode.toLowerCase()}`);
  }

  function setConspect(name, value) {
    setConspectForm((prev) => ({ ...prev, [name]: value }));
  }

  function startNewConspect() {
    setConspectForm(makeConspectForm(selectedSection));
    setHtml(defaultHtml(selectedSection));
    setShowPreview(false);
    setError('');
    setSuccess('Новая форма готова. Вставь HTML и нажми «Сохранить конспект».');
  }

  async function openConspect(slug) {
    setBusy('conspect-load');
    setError('');
    setSuccess('');
    try {
      const details = await getLearningConspect(slug, {
        courseSlug: sectionCourse?.slug,
        includeDraft: true,
      });
      const c = details.conspect;
      setConspectForm({
        id: c.id,
        slug: c.slug || '',
        title: c.title || '',
        subtitle: c.subtitle || '',
        lead: c.lead || '',
        subjectCode: c.subjectCode || SUBJECT_CODE,
        examCode: c.examCode || EXAM_CODE,
        sectionCode: c.sectionCode || selectedSection,
        sortOrder: String(c.sortOrder ?? 10),
        estimatedMinutes: String(c.estimatedMinutes ?? 10),
        badgesText: safeParseJson(c.badgesJson, []).join(', '),
        searchText: details.searchText || '',
        isPublished: c.isPublished !== false,
      });
      const existingHtml = htmlFromContent(details.contentJson);
      setHtml(existingHtml || `<!-- Старый JSON-блочный формат. Вставь сюда HTML. -->\n${defaultHtml(selectedSection)}`);
      setShowPreview(false);
    } catch (e) {
      setError(getError(e));
    } finally {
      setBusy('');
    }
  }

  function validateConspect(form) {
    if (!form.title.trim()) return 'Заполни название конспекта.';
    if (!form.slug.trim()) return 'Заполни slug конспекта.';
    if (!html.trim()) return 'Вставь HTML конспекта.';
    return '';
  }

  async function saveConspect() {
    const validation = validateConspect(conspectForm);
    if (validation) { setError(validation); return; }
    setBusy('conspect');
    setError('');
    setSuccess('');
    try {
      const latestCourses = courses.length ? courses : await loadCourses();
      const course = sectionCourse || await ensureSectionCourse(selectedSection, latestCourses);
      setSectionCourse(course);

      const payload = {
        slug: conspectForm.slug.trim() || `${selectedSection.toLowerCase()}-${slugify(conspectForm.title)}`,
        title: conspectForm.title.trim(),
        subtitle: conspectForm.subtitle.trim() || null,
        lead: conspectForm.lead,
        subjectCode: SUBJECT_CODE,
        examCode: EXAM_CODE,
        sectionCode: selectedSection,
        kind: 'html-conspect',
        sortOrder: Number(conspectForm.sortOrder) || 0,
        estimatedMinutes: Number(conspectForm.estimatedMinutes) || 10,
        badges: badgesFromText(conspectForm.badgesText || `${selectedSection}, конспект, ЦТ/ЦЭ`),
        content: contentFromHtml(html),
        searchText: conspectForm.searchText || `${conspectForm.title} ${conspectForm.lead} ${selectedSection}`,
        isPublished: conspectForm.isPublished,
      };

      const saved = conspectForm.id
        ? await updateLearningConspect(conspectForm.id, payload)
        : await createLearningConspect(course.id, payload);

      setConspectForm((prev) => ({ ...prev, id: saved.conspect.id, slug: saved.conspect.slug, sectionCode: selectedSection }));
      setSuccess(conspectForm.id ? `Конспект ${selectedSection} сохранён.` : `Конспект ${selectedSection} создан.`);
      await loadConspectsForSection(selectedSection);
    } catch (e) {
      setError(getError(e));
    } finally {
      setBusy('');
    }
  }

  async function createSelectedSection() {
    setBusy('section');
    setError('');
    setSuccess('');
    try {
      const latestCourses = await loadCourses();
      const course = await ensureSectionCourse(selectedSection, latestCourses);
      setSectionCourse(course);
      setSuccess(`Раздел ${selectedSection} создан / готов.`);
    } catch (e) {
      setError(getError(e));
    } finally {
      setBusy('');
    }
  }

  async function createFullStructure() {
    setBusy('structure');
    setError('');
    setSuccess('');
    try {
      let flat = await loadCourses();
      let created = 0;
      let fixed = 0;
      for (const section of CT_SECTIONS) {
        const before = findSectionCourse(flat, section.code);
        // eslint-disable-next-line no-await-in-loop
        await ensureSectionCourse(section.code, flat);
        if (before) fixed += 1;
        else created += 1;
        // eslint-disable-next-line no-await-in-loop
        flat = await loadCourses();
      }
      setSectionCourse(findSectionCourse(flat, selectedSection) || null);
      setSuccess(`Основа ЦТ готова: создано ${created}, проверено/обновлено ${fixed}.`);
    } catch (e) {
      setError(getError(e));
    } finally {
      setBusy('');
    }
  }

  const syntheticCourse = sectionCourse || {
    subjectCode: SUBJECT_CODE,
    examCode: EXAM_CODE,
    sectionCode: selectedSection,
  };

  return (
    <Layout fullWidth>
      <div className="container-app py-8">
        <div className="mb-6 flex flex-wrap items-center justify-between gap-3">
          <div>
            <Link to={getSectionPath(selectedSection)} className="inline-flex items-center gap-2 text-sm text-neutral-600 hover:text-brand-700 dark:text-neutral-300 dark:hover:text-brand-200">
              <Eye size={16} /> Открыть {selectedSection} как ученик
            </Link>
            <h1 className="mt-3 text-3xl font-black tracking-tight md:text-5xl">Редактор ЦТ</h1>
            <p className="mt-2 max-w-3xl text-neutral-600 dark:text-neutral-300">
              Без старого дерева курсов: выбираешь номер A1, A2, B5 и редактируешь только его HTML-конспект и задания.
            </p>
          </div>
          <div className="rounded-3xl border border-neutral-200 bg-white p-4 text-sm shadow-soft dark:border-neutral-800 dark:bg-neutral-900">
            <div className="mb-1 flex items-center gap-2 font-semibold"><FileText size={16} /> Сейчас выбран</div>
            <div className="text-3xl font-black text-brand-700 dark:text-brand-200">{selectedSection}</div>
          </div>
        </div>

        {error ? <div className="mb-4"><Alert type="error">{error}</Alert></div> : null}
        {success ? <div className="mb-4"><Alert type="success">{success}</Alert></div> : null}
        {busy === 'load' ? <div className="mb-4"><Alert>Загружаю {selectedSection}...</Alert></div> : null}

        <div className="grid gap-6 lg:grid-cols-[360px_1fr]">
          <aside className="space-y-4">
            <SectionPicker active={selectedSection} onSelect={selectSection} />

            <section className="rounded-[2rem] border border-neutral-200 bg-white p-4 shadow-soft dark:border-neutral-800 dark:bg-neutral-900">
              <div className="mb-2 flex items-center gap-2 font-semibold"><Wand2 size={18} /> Служебная основа</div>
              <p className="text-sm leading-6 text-neutral-500 dark:text-neutral-400">
                Для сохранения конспекта нужен служебный раздел в БД. Если его нет, он создастся автоматически при сохранении.
              </p>
              <div className="mt-3 rounded-2xl bg-neutral-50 p-3 text-sm dark:bg-neutral-950">
                <div>Текущий раздел: <b>{sectionCourse ? 'есть в БД' : 'ещё не создан'}</b></div>
                <div>Не хватает номеров: <b>{missingCount}</b></div>
              </div>
              <div className="mt-3 flex flex-col gap-2">
                <button type="button" onClick={createSelectedSection} disabled={busy === 'section'} className="btn-outline inline-flex items-center justify-center gap-2 disabled:opacity-60">
                  {busy === 'section' ? <Loader2 size={16} className="animate-spin" /> : <Plus size={16} />}
                  Создать {selectedSection} в БД
                </button>
                <button type="button" onClick={createFullStructure} disabled={busy === 'structure'} className="btn-primary inline-flex items-center justify-center gap-2 disabled:opacity-60">
                  {busy === 'structure' ? <Loader2 size={16} className="animate-spin" /> : <RefreshCcw size={16} />}
                  Создать все A/B
                </button>
              </div>
            </section>
          </aside>

          <main className="space-y-6">
            <section className="rounded-[2rem] border border-neutral-200 bg-white p-5 shadow-soft dark:border-neutral-800 dark:bg-neutral-900 md:p-6">
              <div className="mb-4 flex flex-wrap items-center justify-between gap-3">
                <div>
                  <div className="flex items-center gap-2"><FileText className="text-brand-600" /><h2 className="text-2xl font-bold tracking-tight">HTML-конспект для {selectedSection}</h2></div>
                  <p className="mt-1 text-sm text-neutral-500 dark:text-neutral-400">Ученик видит этот HTML сверху на странице /{selectedSection.toLowerCase()}.</p>
                </div>
                <button type="button" onClick={startNewConspect} className="btn-outline inline-flex items-center gap-2"><Plus size={16} /> Новый конспект</button>
              </div>

              <div className="mb-5 grid gap-3 md:grid-cols-2 xl:grid-cols-3">
                {conspects.map((item) => (
                  <button key={item.id} type="button" onClick={() => openConspect(item.slug || item.id)} className={`rounded-3xl border p-4 text-left transition hover:border-brand-300 ${conspectForm.id === item.id ? 'border-brand-300 bg-brand-50 dark:border-brand-800 dark:bg-brand-900/20' : 'border-neutral-200 bg-neutral-50 dark:border-neutral-800 dark:bg-neutral-950'}`}>
                    <div className="font-semibold">{item.title}</div>
                    <div className="mt-1 text-xs text-neutral-500 dark:text-neutral-400">/{item.slug}</div>
                    {item.isPublished === false ? <div className="mt-2 inline-flex rounded-xl bg-amber-100 px-2 py-1 text-xs font-semibold text-amber-800 dark:bg-amber-950/40 dark:text-amber-100">черновик</div> : null}
                  </button>
                ))}
                {!conspects.length ? <div className="rounded-3xl border border-dashed border-neutral-200 p-5 text-sm text-neutral-500 dark:border-neutral-800 dark:text-neutral-400">Для {selectedSection} пока нет конспекта. Вставь HTML ниже и сохрани.</div> : null}
              </div>

              <div className="grid gap-4 md:grid-cols-2">
                <Field label="Название" required><Input value={conspectForm.title} onChange={(e) => setConspect('title', e.target.value)} /></Field>
                <Field label="Slug" required hint="Адрес самого конспекта. Страница ученика всё равно открывается по номеру: /a1, /b5."><Input value={conspectForm.slug} onChange={(e) => setConspect('slug', e.target.value)} /></Field>
                <Field label="Подзаголовок"><Input value={conspectForm.subtitle} onChange={(e) => setConspect('subtitle', e.target.value)} /></Field>
                <Field label="Минуты"><Input type="number" value={conspectForm.estimatedMinutes} onChange={(e) => setConspect('estimatedMinutes', e.target.value)} /></Field>
                <div className="md:col-span-2"><Field label="Краткое описание"><Textarea rows={3} value={conspectForm.lead} onChange={(e) => setConspect('lead', e.target.value)} /></Field></div>
                <Field label="Бейджи" hint="Через запятую: A1, орфография, ЦТ."><Input value={conspectForm.badgesText} onChange={(e) => setConspect('badgesText', e.target.value)} /></Field>
                <Field label="Порядок"><Input type="number" value={conspectForm.sortOrder} onChange={(e) => setConspect('sortOrder', e.target.value)} /></Field>
                <label className="flex items-center gap-2 md:col-span-2"><input type="checkbox" checked={conspectForm.isPublished} onChange={(e) => setConspect('isPublished', e.target.checked)} /><span className="text-sm font-semibold">Опубликован</span><span className="text-xs text-neutral-500">виден ученику</span></label>
              </div>

              <div className="mt-6 rounded-[2rem] border border-neutral-200 bg-neutral-50 p-4 dark:border-neutral-800 dark:bg-neutral-950">
                <div className="mb-3 flex flex-wrap items-center justify-between gap-3">
                  <div>
                    <div className="flex items-center gap-2 font-semibold"><Code2 size={18} /> HTML</div>
                    <div className="mt-1 text-xs leading-5 text-neutral-500 dark:text-neutral-400">Вставляй готовый HTML-фрагмент или целую HTML-разметку. Задания ниже подтягиваются отдельно по коду {selectedSection}.</div>
                  </div>
                  <button type="button" onClick={() => setShowPreview((v) => !v)} className="btn-outline inline-flex items-center gap-2"><Eye size={16} /> {showPreview ? 'Скрыть предпросмотр' : 'Предпросмотр'}</button>
                </div>
                <Textarea rows={24} spellCheck={false} value={html} onChange={(e) => setHtml(e.target.value)} className="font-mono text-sm" />
                <div className="mt-3 flex gap-2 rounded-2xl border border-amber-200 bg-amber-50 p-3 text-xs leading-5 text-amber-900 dark:border-amber-900 dark:bg-amber-950/30 dark:text-amber-100">
                  <AlertTriangle size={15} className="mt-0.5 shrink-0" />
                  Не вставляй задания в HTML. Задания создаются отдельным блоком ниже и автоматически показываются под конспектом у ученика.
                </div>
                {showPreview ? <div className="mt-4"><HtmlPreview html={html} /></div> : null}
              </div>

              <div className="mt-6 flex flex-wrap gap-3">
                <button type="button" onClick={saveConspect} disabled={busy === 'conspect'} className="btn-primary inline-flex items-center gap-2 disabled:opacity-60">
                  {busy === 'conspect' ? <Loader2 size={18} className="animate-spin" /> : <Save size={18} />} Сохранить конспект {selectedSection}
                </button>
                <Link to={getSectionPath(selectedSection)} className="btn-outline inline-flex items-center gap-2"><CheckCircle2 size={18} /> Открыть как ученик</Link>
              </div>
            </section>

            <SectionTaskAdminPanel selectedCourse={syntheticCourse} />
          </main>
        </div>
      </div>
    </Layout>
  );
}
