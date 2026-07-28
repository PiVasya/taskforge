import React, { useEffect, useMemo, useState } from 'react';
import { Link } from 'react-router-dom';
import { ArrowLeft, BookOpen, CheckCircle2, Link2, Loader2, Plus, Save } from 'lucide-react';
import {
  createLearningConspect,
  createLearningConspectTaskLink,
  getLearningConspect,
  getLearningConspects,
  getLearningCourseTree,
  updateLearningConspect,
} from '../api/learning';

const templateContent = {
  schemaVersion: 1,
  layout: 'tabs',
  startTabId: 'theory',
  hero: {
    eyebrow: 'Русский язык · ЦТ/ЦЭ',
    title: 'Новый конспект',
    description: 'Краткое описание конспекта.',
    stats: [
      { label: 'Формат', value: 'конспект + задания' },
      { label: 'Время', value: '10–15 минут' },
    ],
  },
  tabs: [
    {
      id: 'theory',
      title: 'Теория',
      blocks: [
        {
          id: 'main-rule',
          type: 'rule-card',
          title: 'Главные правила',
          items: ['Правило 1', 'Правило 2', 'Правило 3'],
        },
        {
          type: 'examples',
          title: 'Примеры',
          items: [
            { source: 'пример', answer: 'ответ', comment: 'объяснение' },
          ],
        },
      ],
    },
    {
      id: 'practice',
      title: 'Тренировка',
      blocks: [
        {
          id: 'practice-link',
          type: 'practice-intro',
          title: 'Переход к заданиям',
          text: 'После изучения конспекта можно открыть задания по этому разделу.',
          cta: { label: 'Сделать задания', href: '/tasks?sectionCode=A1' },
        },
      ],
    },
  ],
};

function flattenCourses(nodes, level = 0, result = []) {
  (nodes || []).forEach((node) => {
    result.push({ ...node, level });
    flattenCourses(node.children, level + 1, result);
  });
  return result;
}

function defaultForm(course) {
  const section = course?.sectionCode || 'A1';
  return {
    id: '',
    courseId: course?.id || '',
    slug: `${String(section).toLowerCase()}-new-conspect`,
    title: `${section}. Новый конспект`,
    subtitle: 'Конспект уровня HTML-прототипа',
    lead: 'Краткое описание: что ученик изучит и зачем.',
    subjectCode: course?.subjectCode || 'russian',
    examCode: course?.examCode || 'ct-ce-2026',
    sectionCode: section,
    estimatedMinutes: 12,
    badgesJson: JSON.stringify([section, 'конспект', 'ЦТ/ЦЭ']),
    contentJson: JSON.stringify({ ...templateContent, hero: { ...templateContent.hero, title: `${section}. Новый конспект` } }, null, 2),
    searchText: '',
    isPublished: true,
  };
}

export default function AdminConspectsPage() {
  const [courses, setCourses] = useState([]);
  const [conspects, setConspects] = useState([]);
  const [selectedCourseId, setSelectedCourseId] = useState('');
  const [form, setForm] = useState(defaultForm(null));
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [linking, setLinking] = useState(false);
  const [message, setMessage] = useState('');
  const [error, setError] = useState('');

  const flatCourses = useMemo(() => flattenCourses(courses), [courses]);
  const selectedCourse = flatCourses.find((course) => course.id === selectedCourseId) || flatCourses[0];

  useEffect(() => {
    let cancelled = false;
    async function load() {
      setLoading(true);
      setError('');
      try {
        const tree = await getLearningCourseTree({ includeDraft: true });
        if (cancelled) return;
        setCourses(tree || []);
        const flat = flattenCourses(tree);
        const firstSection = flat.find((course) => course.sectionCode) || flat[0];
        if (firstSection) {
          setSelectedCourseId(firstSection.id);
          setForm(defaultForm(firstSection));
        }
      } catch (e) {
        if (!cancelled) setError(e?.userMessage || e?.message || 'Не удалось загрузить дерево курсов.');
      } finally {
        if (!cancelled) setLoading(false);
      }
    }
    load();
    return () => { cancelled = true; };
  }, []);

  useEffect(() => {
    if (!selectedCourse) return;
    let cancelled = false;
    async function loadConspects() {
      try {
        const list = await getLearningConspects({ courseSlug: selectedCourse.slug, includeDraft: true });
        if (!cancelled) setConspects(list || []);
      } catch {
        if (!cancelled) setConspects([]);
      }
    }
    loadConspects();
    return () => { cancelled = true; };
  }, [selectedCourse]);

  const changeField = (field, value) => setForm((prev) => ({ ...prev, [field]: value }));

  const startNew = () => {
    setMessage('');
    setError('');
    setForm(defaultForm(selectedCourse));
  };

  const loadExisting = async (item) => {
    setMessage('');
    setError('');
    try {
      const details = await getLearningConspect(item.slug, { courseSlug: selectedCourse?.slug, includeDraft: true });
      setForm({
        id: details.conspect.id,
        courseId: details.conspect.courseId,
        slug: details.conspect.slug,
        title: details.conspect.title,
        subtitle: details.conspect.subtitle || '',
        lead: details.conspect.lead || '',
        subjectCode: details.conspect.subjectCode || 'russian',
        examCode: details.conspect.examCode || 'ct-ce-2026',
        sectionCode: details.conspect.sectionCode || selectedCourse?.sectionCode || 'A1',
        estimatedMinutes: details.conspect.estimatedMinutes || 10,
        badgesJson: details.conspect.badgesJson || '[]',
        contentJson: JSON.stringify(JSON.parse(details.contentJson || '{}'), null, 2),
        searchText: details.searchText || '',
        isPublished: details.conspect.isPublished,
      });
    } catch (e) {
      setError(e?.userMessage || e?.message || 'Не удалось открыть конспект.');
    }
  };

  const validateJson = (value, label) => {
    try {
      return JSON.parse(value || '{}');
    } catch (e) {
      throw new Error(`${label}: неверный JSON (${e.message})`);
    }
  };

  const save = async () => {
    setSaving(true);
    setMessage('');
    setError('');
    try {
      const content = validateJson(form.contentJson, 'Контент конспекта');
      const badges = validateJson(form.badgesJson || '[]', 'Бейджи');
      const payload = {
        slug: form.slug,
        title: form.title,
        subtitle: form.subtitle,
        lead: form.lead,
        subjectCode: form.subjectCode,
        examCode: form.examCode,
        sectionCode: form.sectionCode,
        kind: 'conspect',
        sortOrder: 10,
        estimatedMinutes: Number(form.estimatedMinutes) || 10,
        badges,
        content,
        searchText: form.searchText,
        isPublished: Boolean(form.isPublished),
      };

      const result = form.id
        ? await updateLearningConspect(form.id, payload)
        : await createLearningConspect(selectedCourseId, payload);

      setForm((prev) => ({ ...prev, id: result.conspect.id, courseId: result.conspect.courseId }));
      setMessage('Конспект сохранён. Миграции не создавались — только код модели/API/фронта.');
      const list = await getLearningConspects({ courseSlug: selectedCourse?.slug, includeDraft: true });
      setConspects(list || []);
    } catch (e) {
      setError(e?.userMessage || e?.message || 'Не удалось сохранить конспект.');
    } finally {
      setSaving(false);
    }
  };

  const addTasksButton = async () => {
    if (!form.id) {
      setError('Сначала сохрани конспект, потом привяжи кнопку к заданиям.');
      return;
    }
    setLinking(true);
    setMessage('');
    setError('');
    try {
      await createLearningConspectTaskLink(form.id, {
        taskType: 'task-filter',
        sourceService: 'quiz-task-service',
        taskFilter: {
          subjectCode: form.subjectCode || 'russian',
          examCode: form.examCode || 'ct-ce-2026',
          sectionCode: form.sectionCode || 'A1',
        },
        title: `Задания ${form.sectionCode || ''}`.trim(),
        buttonText: 'Сделать задания',
        groupTitle: 'После конспекта',
        anchorBlockId: 'practice-link',
        isRequired: true,
        sortOrder: 10,
      });
      setMessage('Кнопка/ссылка на задания добавлена к конспекту.');
    } catch (e) {
      setError(e?.userMessage || e?.message || 'Не удалось добавить ссылку на задания.');
    } finally {
      setLinking(false);
    }
  };

  return (
    <>
      <div className="min-h-[calc(100vh-57px)] bg-neutral-50 dark:bg-neutral-950">
        <div className="container-app py-6 lg:py-8">
          <div className="mb-5 flex flex-wrap items-center justify-between gap-3">
            <div>
              <Link to="/" className="inline-flex items-center gap-2 text-sm font-medium text-brand-700 dark:text-brand-300 hover:underline">
                <ArrowLeft size={16} />
                Назад к учебным курсам
              </Link>
              <h1 className="mt-2 text-3xl font-bold tracking-tight">Редактор конспектов</h1>
              <p className="mt-1 text-neutral-600 dark:text-neutral-300">Здесь создаётся именно конспект: вкладки, блоки, таблицы, словари и кнопки к заданиям.</p>
            </div>
            <button type="button" onClick={startNew} className="btn-primary inline-flex items-center gap-2">
              <Plus size={18} />
              Новый конспект
            </button>
          </div>

          {message && <div className="mb-4 rounded-3xl border border-emerald-200 bg-emerald-50 p-4 text-sm text-emerald-800 dark:border-emerald-900 dark:bg-emerald-950/20 dark:text-emerald-100">{message}</div>}
          {error && <div className="mb-4 rounded-3xl border border-red-200 bg-red-50 p-4 text-sm text-red-800 dark:border-red-900 dark:bg-red-950/20 dark:text-red-100">{error}</div>}

          {loading ? (
            <div className="rounded-[2rem] border border-neutral-200 dark:border-neutral-800 bg-white dark:bg-neutral-900 p-10 text-center shadow-soft">
              <Loader2 className="mx-auto animate-spin text-brand-600" />
              <div className="mt-3 text-neutral-600 dark:text-neutral-300">Загружаю редактор...</div>
            </div>
          ) : (
            <div className="grid gap-6 lg:grid-cols-[320px_minmax(0,1fr)]">
              <aside className="space-y-4 lg:sticky lg:top-[77px] lg:self-start">
                <div className="rounded-[2rem] border border-neutral-200 dark:border-neutral-800 bg-white dark:bg-neutral-900 p-5 shadow-soft">
                  <label className="text-sm font-semibold">Курс / раздел</label>
                  <select
                    value={selectedCourseId}
                    onChange={(e) => {
                      const course = flatCourses.find((item) => item.id === e.target.value);
                      setSelectedCourseId(e.target.value);
                      setForm(defaultForm(course));
                    }}
                    className="mt-2 w-full rounded-2xl border border-neutral-200 dark:border-neutral-800 bg-neutral-50 dark:bg-neutral-950 px-3 py-2.5 outline-none focus:border-brand-400"
                  >
                    {flatCourses.map((course) => (
                      <option key={course.id} value={course.id}>{`${'— '.repeat(course.level)}${course.title}`}</option>
                    ))}
                  </select>
                </div>

                <div className="rounded-[2rem] border border-neutral-200 dark:border-neutral-800 bg-white dark:bg-neutral-900 p-5 shadow-soft">
                  <div className="mb-3 flex items-center gap-2 font-semibold"><BookOpen size={18} /> Уже есть</div>
                  <div className="space-y-2">
                    {conspects.map((item) => (
                      <button key={item.id} type="button" onClick={() => loadExisting(item)} className="w-full rounded-2xl border border-neutral-200 dark:border-neutral-800 bg-neutral-50 dark:bg-neutral-950 p-3 text-left text-sm hover:border-brand-300">
                        <div className="font-semibold">{item.title}</div>
                        <div className="text-neutral-500 dark:text-neutral-400">/{item.slug}</div>
                      </button>
                    ))}
                    {conspects.length === 0 && <div className="text-sm text-neutral-500 dark:text-neutral-400">В этом разделе пока нет конспектов.</div>}
                  </div>
                </div>
              </aside>

              <main className="rounded-[2rem] border border-neutral-200 dark:border-neutral-800 bg-white dark:bg-neutral-900 p-5 md:p-6 shadow-soft">
                <div className="grid gap-4 md:grid-cols-2">
                  <label className="block">
                    <span className="text-sm font-semibold">Slug</span>
                    <input value={form.slug} onChange={(e) => changeField('slug', e.target.value)} className="mt-1 w-full rounded-2xl border border-neutral-200 dark:border-neutral-800 bg-neutral-50 dark:bg-neutral-950 px-3 py-2.5 outline-none focus:border-brand-400" />
                  </label>
                  <label className="block">
                    <span className="text-sm font-semibold">Раздел</span>
                    <input value={form.sectionCode} onChange={(e) => changeField('sectionCode', e.target.value)} className="mt-1 w-full rounded-2xl border border-neutral-200 dark:border-neutral-800 bg-neutral-50 dark:bg-neutral-950 px-3 py-2.5 outline-none focus:border-brand-400" />
                  </label>
                  <label className="block md:col-span-2">
                    <span className="text-sm font-semibold">Название</span>
                    <input value={form.title} onChange={(e) => changeField('title', e.target.value)} className="mt-1 w-full rounded-2xl border border-neutral-200 dark:border-neutral-800 bg-neutral-50 dark:bg-neutral-950 px-3 py-2.5 outline-none focus:border-brand-400" />
                  </label>
                  <label className="block md:col-span-2">
                    <span className="text-sm font-semibold">Короткое описание</span>
                    <textarea value={form.lead} onChange={(e) => changeField('lead', e.target.value)} rows={3} className="mt-1 w-full rounded-2xl border border-neutral-200 dark:border-neutral-800 bg-neutral-50 dark:bg-neutral-950 px-3 py-2.5 outline-none focus:border-brand-400" />
                  </label>
                  <label className="block">
                    <span className="text-sm font-semibold">Минуты</span>
                    <input type="number" value={form.estimatedMinutes} onChange={(e) => changeField('estimatedMinutes', e.target.value)} className="mt-1 w-full rounded-2xl border border-neutral-200 dark:border-neutral-800 bg-neutral-50 dark:bg-neutral-950 px-3 py-2.5 outline-none focus:border-brand-400" />
                  </label>
                  <label className="flex items-center gap-2 pt-7">
                    <input type="checkbox" checked={form.isPublished} onChange={(e) => changeField('isPublished', e.target.checked)} />
                    <span className="text-sm font-semibold">Опубликован</span>
                  </label>
                  <label className="block md:col-span-2">
                    <span className="text-sm font-semibold">Бейджи JSON</span>
                    <input value={form.badgesJson} onChange={(e) => changeField('badgesJson', e.target.value)} className="mt-1 w-full rounded-2xl border border-neutral-200 dark:border-neutral-800 bg-neutral-50 dark:bg-neutral-950 px-3 py-2.5 font-mono text-sm outline-none focus:border-brand-400" />
                  </label>
                  <label className="block md:col-span-2">
                    <span className="text-sm font-semibold">ContentJson конспекта</span>
                    <textarea value={form.contentJson} onChange={(e) => changeField('contentJson', e.target.value)} rows={24} spellCheck={false} className="mt-1 w-full rounded-2xl border border-neutral-200 dark:border-neutral-800 bg-neutral-50 dark:bg-neutral-950 px-3 py-2.5 font-mono text-sm outline-none focus:border-brand-400" />
                  </label>
                  <label className="block md:col-span-2">
                    <span className="text-sm font-semibold">SearchText</span>
                    <textarea value={form.searchText} onChange={(e) => changeField('searchText', e.target.value)} rows={3} className="mt-1 w-full rounded-2xl border border-neutral-200 dark:border-neutral-800 bg-neutral-50 dark:bg-neutral-950 px-3 py-2.5 outline-none focus:border-brand-400" />
                  </label>
                </div>

                <div className="mt-6 flex flex-wrap gap-3">
                  <button type="button" onClick={save} disabled={saving} className="btn-primary inline-flex items-center gap-2 disabled:opacity-60">
                    {saving ? <Loader2 size={18} className="animate-spin" /> : <Save size={18} />}
                    Сохранить конспект
                  </button>
                  <button type="button" onClick={addTasksButton} disabled={linking || !form.id} className="btn-outline inline-flex items-center gap-2 disabled:opacity-60">
                    {linking ? <Loader2 size={18} className="animate-spin" /> : <Link2 size={18} />}
                    Сделать кнопку к заданиям
                  </button>
                  {form.id && (
                    <Link to={`/courses/${selectedCourse?.slug || form.sectionCode}/conspects/${form.slug}`} className="btn-outline inline-flex items-center gap-2">
                      <CheckCircle2 size={18} />
                      Открыть
                    </Link>
                  )}
                </div>
              </main>
            </div>
          )}
        </div>
      </div>
    </>
  );
}
