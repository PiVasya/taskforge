import React, { useMemo, useState } from 'react';
import { Layers3, Loader2, Wand2 } from 'lucide-react';
import { createLearningCourse, updateLearningCourse } from '../api/learning';
import { CT_SECTIONS, EXAM_CODE, SUBJECT_CODE } from '../data/ctSections';

const ROOT_SLUG = `${SUBJECT_CODE}-${EXAM_CODE}`;
const ROOT_TITLE = 'Русский язык — ЦТ/ЦЭ';

function sectionSortOrder(section) {
  return section.partCode === 'A' ? section.number : 1000 + section.number;
}

export default function CtStructureBootstrapPanel({ allCourses, onDone }) {
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState('');
  const [error, setError] = useState('');

  const existingSectionCodes = useMemo(() => new Set((allCourses || []).map((course) => course.sectionCode).filter(Boolean)), [allCourses]);
  const missingSections = useMemo(() => CT_SECTIONS.filter((section) => !existingSectionCodes.has(section.code)), [existingSectionCodes]);
  const root = useMemo(() => (
    (allCourses || []).find((course) => course.slug === ROOT_SLUG)
    || (allCourses || []).find((course) => course.subjectCode === SUBJECT_CODE && course.examCode === EXAM_CODE && !course.sectionCode)
  ), [allCourses]);

  async function createBase() {
    setBusy(true);
    setMessage('');
    setError('');
    try {
      let parent = root;
      if (!parent) {
        parent = await createLearningCourse({
          parentCourseId: null,
          slug: ROOT_SLUG,
          title: ROOT_TITLE,
          shortTitle: 'ЦТ/ЦЭ',
          summary: 'Основа под подготовку к ЦТ/ЦЭ: A1-A30 и B1-B10.',
          description: 'Служебный корень для второго фронта. Обычный пользователь его не видит как дерево.',
          subjectCode: SUBJECT_CODE,
          examCode: EXAM_CODE,
          sectionCode: null,
          sortOrder: 10,
          isPublished: true,
        });
      }

      let created = 0;
      let fixed = 0;
      for (const section of missingSections) {
        const sameSlug = (allCourses || []).find((course) => course.slug === section.code.toLowerCase());
        if (sameSlug) {
          // Если такой slug уже был, но без sectionCode, дозаполняем метаданные вместо создания дубля.
          // eslint-disable-next-line no-await-in-loop
          await updateLearningCourse(sameSlug.id, {
            parentCourseId: sameSlug.parentCourseId || parent.id,
            slug: sameSlug.slug,
            title: sameSlug.title || section.code,
            shortTitle: sameSlug.shortTitle || section.code,
            summary: sameSlug.summary || `HTML-конспект и задания для ${section.code}.`,
            description: sameSlug.description || '',
            subjectCode: sameSlug.subjectCode || SUBJECT_CODE,
            examCode: sameSlug.examCode || EXAM_CODE,
            sectionCode: section.code,
            sortOrder: sameSlug.sortOrder ?? sectionSortOrder(section),
            isPublished: sameSlug.isPublished !== false,
          });
          fixed += 1;
          continue;
        }

        // eslint-disable-next-line no-await-in-loop
        await createLearningCourse({
          parentCourseId: parent.id,
          slug: section.code.toLowerCase(),
          title: section.code,
          shortTitle: section.code,
          summary: `HTML-конспект и задания для ${section.code}.`,
          description: '',
          subjectCode: SUBJECT_CODE,
          examCode: EXAM_CODE,
          sectionCode: section.code,
          sortOrder: sectionSortOrder(section),
          isPublished: true,
        });
        created += 1;
      }

      if (created || fixed) setMessage(`Основа готова: создано ${created}, обновлено ${fixed}.`);
      else setMessage('Основа уже есть: все разделы A/B найдены.');
      if (onDone) await onDone(parent.slug);
    } catch (e) {
      setError(e?.userMessage || e?.message || 'Не удалось создать основу ЦТ.');
    } finally {
      setBusy(false);
    }
  }

  return (
    <section className="rounded-[2rem] border border-brand-200 bg-brand-50/70 p-4 shadow-soft dark:border-brand-900 dark:bg-brand-950/20">
      <div className="mb-2 flex items-center gap-2 font-semibold text-brand-900 dark:text-brand-100">
        <Layers3 size={18} /> Основа ЦТ
      </div>
      <p className="text-sm leading-6 text-brand-900/80 dark:text-brand-100/80">
        Создаёт служебное дерево для второго фронта: корень ЦТ/ЦЭ и все номера A1-A30, B1-B10. После этого в каждом номере можно хранить HTML-конспект и задания.
      </p>
      <div className="mt-3 text-xs font-semibold text-brand-900/70 dark:text-brand-100/70">
        Не хватает разделов: {missingSections.length}
      </div>
      {message ? <div className="mt-3 rounded-2xl border border-emerald-200 bg-emerald-50 p-3 text-sm text-emerald-800 dark:border-emerald-900 dark:bg-emerald-950/30 dark:text-emerald-100">{message}</div> : null}
      {error ? <div className="mt-3 rounded-2xl border border-red-200 bg-red-50 p-3 text-sm text-red-800 dark:border-red-900 dark:bg-red-950/30 dark:text-red-100">{error}</div> : null}
      <button type="button" onClick={createBase} disabled={busy} className="btn-primary mt-4 inline-flex items-center gap-2 disabled:opacity-60">
        {busy ? <Loader2 size={18} className="animate-spin" /> : <Wand2 size={18} />}
        Создать / дозаполнить основу
      </button>
    </section>
  );
}
