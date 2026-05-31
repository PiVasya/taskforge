import React, { useMemo, useState } from 'react';
import { Layers3, Loader2, Plus, Wand2 } from 'lucide-react';
import { createLearningCourse, updateLearningCourse } from '../api/learning';
import { CT_SECTIONS, EXAM_CODE, SUBJECT_CODE, normalizeSectionCode, getSectionSortOrder } from '../data/ctSections';

const ROOT_SLUG = `${SUBJECT_CODE}-${EXAM_CODE}`;
const ROOT_TITLE = 'Русский язык — ЦТ/ЦЭ';

function buildSectionPayload(sectionCode, parentId, sameSlug = null) {
  return {
    parentCourseId: sameSlug?.parentCourseId || parentId,
    slug: sameSlug?.slug || sectionCode.toLowerCase(),
    title: sameSlug?.title || sectionCode,
    shortTitle: sameSlug?.shortTitle || sectionCode,
    summary: sameSlug?.summary || `HTML-конспект и задания для ${sectionCode}.`,
    description: sameSlug?.description || '',
    subjectCode: sameSlug?.subjectCode || SUBJECT_CODE,
    examCode: sameSlug?.examCode || EXAM_CODE,
    sectionCode,
    sortOrder: sameSlug?.sortOrder ?? getSectionSortOrder(sectionCode),
    isPublished: sameSlug?.isPublished !== false,
  };
}

export default function CtStructureBootstrapPanel({ allCourses, onDone }) {
  const [busy, setBusy] = useState('');
  const [message, setMessage] = useState('');
  const [error, setError] = useState('');
  const [customSection, setCustomSection] = useState('');

  const existingSectionCodes = useMemo(() => new Set((allCourses || []).map((course) => normalizeSectionCode(course.sectionCode)).filter(Boolean)), [allCourses]);
  const missingSections = useMemo(() => CT_SECTIONS.filter((section) => !existingSectionCodes.has(section.code)), [existingSectionCodes]);
  const root = useMemo(() => (
    (allCourses || []).find((course) => course.slug === ROOT_SLUG)
    || (allCourses || []).find((course) => course.subjectCode === SUBJECT_CODE && course.examCode === EXAM_CODE && !course.sectionCode)
  ), [allCourses]);

  async function ensureRoot() {
    if (root) return root;
    return createLearningCourse({
      parentCourseId: null,
      slug: ROOT_SLUG,
      title: ROOT_TITLE,
      shortTitle: 'ЦТ/ЦЭ',
      summary: 'Основа под подготовку к ЦТ/ЦЭ. Стартовая сетка создаёт A1-A30 и B1-B10, но редактор может вручную добавлять A/B дальше.',
      description: 'Служебный корень для второго фронта. Обычный пользователь его не видит как дерево.',
      subjectCode: SUBJECT_CODE,
      examCode: EXAM_CODE,
      sectionCode: null,
      sortOrder: 10,
      isPublished: true,
    });
  }

  async function createSection(sectionCode, parent) {
    const sameSlug = (allCourses || []).find((course) => course.slug === sectionCode.toLowerCase());
    if (sameSlug) {
      await updateLearningCourse(sameSlug.id, buildSectionPayload(sectionCode, parent.id, sameSlug));
      return 'updated';
    }
    await createLearningCourse(buildSectionPayload(sectionCode, parent.id));
    return 'created';
  }

  async function createBase() {
    setBusy('base');
    setMessage('');
    setError('');
    try {
      const parent = await ensureRoot();
      let created = 0;
      let fixed = 0;
      for (const section of missingSections) {
        const result = await createSection(section.code, parent);
        if (result === 'updated') fixed += 1;
        else created += 1;
      }
      if (created || fixed) setMessage(`Основа готова: создано ${created}, обновлено ${fixed}.`);
      else setMessage('Стартовая основа уже есть: все A1-A30 и B1-B10 найдены. Дополнительные A/B можно добавить вручную ниже.');
      if (onDone) await onDone(parent.slug);
    } catch (e) {
      setError(e?.userMessage || e?.message || 'Не удалось создать основу ЦТ.');
    } finally {
      setBusy('');
    }
  }

  async function createCustom() {
    const normalized = normalizeSectionCode(customSection);
    if (!normalized) {
      setError('Введи номер в формате A31, A32, B11, B12 и т.п. Разрешены только части A и B.');
      return;
    }
    setBusy('custom');
    setMessage('');
    setError('');
    try {
      const parent = await ensureRoot();
      const result = await createSection(normalized, parent);
      setMessage(result === 'updated' ? `Раздел ${normalized} найден и обновлён.` : `Раздел ${normalized} создан. Теперь для него можно делать конспект и задания.`);
      setCustomSection('');
      if (onDone) await onDone(parent.slug);
    } catch (e) {
      setError(e?.userMessage || e?.message || `Не удалось создать раздел ${normalized}.`);
    } finally {
      setBusy('');
    }
  }

  return (
    <section className="rounded-[2rem] border border-brand-200 bg-brand-50/70 p-4 shadow-soft dark:border-brand-900 dark:bg-brand-950/20">
      <div className="mb-2 flex items-center gap-2 font-semibold text-brand-900 dark:text-brand-100">
        <Layers3 size={18} /> Основа ЦТ
      </div>
      <p className="text-sm leading-6 text-brand-900/80 dark:text-brand-100/80">
        Стартовая кнопка создаёт A1-A30 и B1-B10. Это больше не жёсткий лимит: ниже можно вручную добавить любой новый номер части A или B, например A31 или B11.
      </p>
      <div className="mt-3 text-xs font-semibold text-brand-900/70 dark:text-brand-100/70">
        Не хватает стартовых разделов: {missingSections.length}
      </div>
      {message ? <div className="mt-3 rounded-2xl border border-emerald-200 bg-emerald-50 p-3 text-sm text-emerald-800 dark:border-emerald-900 dark:bg-emerald-950/30 dark:text-emerald-100">{message}</div> : null}
      {error ? <div className="mt-3 rounded-2xl border border-red-200 bg-red-50 p-3 text-sm text-red-800 dark:border-red-900 dark:bg-red-950/30 dark:text-red-100">{error}</div> : null}
      <div className="mt-4 flex flex-wrap gap-2">
        <button type="button" onClick={createBase} disabled={busy !== ''} className="btn-primary inline-flex items-center gap-2 disabled:opacity-60">
          {busy === 'base' ? <Loader2 size={18} className="animate-spin" /> : <Wand2 size={18} />}
          Создать / дозаполнить стартовую основу
        </button>
      </div>
      <div className="mt-4 rounded-3xl border border-brand-200 bg-white p-3 dark:border-brand-900 dark:bg-neutral-950">
        <div className="text-sm font-black">Добавить новый номер вручную</div>
        <div className="mt-2 flex flex-col gap-2 sm:flex-row">
          <input
            value={customSection}
            onChange={(e) => setCustomSection(e.target.value.toUpperCase())}
            placeholder="A31 или B11"
            className="min-w-[180px] flex-1 rounded-2xl border border-neutral-200 bg-neutral-50 px-3 py-2.5 outline-none focus:border-brand-400 dark:border-neutral-800 dark:bg-neutral-950"
          />
          <button type="button" onClick={createCustom} disabled={busy !== ''} className="btn-outline inline-flex items-center justify-center gap-2 bg-white disabled:opacity-60 dark:bg-neutral-950">
            {busy === 'custom' ? <Loader2 size={16} className="animate-spin" /> : <Plus size={16} />}
            Создать номер
          </button>
        </div>
        <p className="mt-2 text-xs leading-5 text-neutral-500 dark:text-neutral-400">
          После создания открой адрес вида /a31 или /b11. Заданий в каждом номере может быть сколько угодно.
        </p>
      </div>
    </section>
  );
}
