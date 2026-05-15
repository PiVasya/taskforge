import React, { useEffect, useMemo, useState } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import { BookOpen, ChevronRight, Loader2, PencilLine, Plus } from 'lucide-react';
import Layout from '../components/Layout';
import { getLearningCourseTree } from '../api/learning';
import { CT_PARTS, getSectionPath, getSectionsByPart, normalizeSectionCode } from '../data/ctSections';
import { useEditorMode } from '../contexts/EditorModeContext';

function flattenCourses(nodes, result = []) {
  (nodes || []).forEach((node) => {
    result.push(node);
    flattenCourses(node.children, result);
  });
  return result;
}

function ManualSectionOpener() {
  const [value, setValue] = useState('');
  const [error, setError] = useState('');
  const navigate = useNavigate();

  function openSection() {
    const normalized = normalizeSectionCode(value);
    if (!normalized) {
      setError('Введи номер в формате A31, A32, B11, B12 и т.п. Разрешены только части A и B.');
      return;
    }
    setError('');
    navigate(getSectionPath(normalized));
  }

  return (
    <section className="mb-6 rounded-[1.5rem] border border-brand-200 bg-brand-50 p-4 text-brand-900 shadow-soft dark:border-brand-900 dark:bg-brand-950/30 dark:text-brand-100">
      <div className="flex items-start gap-3">
        <PencilLine size={20} className="mt-0.5 shrink-0" />
        <div className="min-w-0 flex-1">
          <div className="font-black">Режим редактора включён</div>
          <p className="mt-1 text-sm leading-6">
            Открой любой существующий номер или введи новый вручную. Жёсткого лимита A1-A30 / B1-B10 больше нет: можно делать A31, A32, B11, B12 и дальше.
          </p>
          <div className="mt-3 flex flex-col gap-2 sm:flex-row">
            <input
              value={value}
              onChange={(e) => setValue(e.target.value.toUpperCase())}
              onKeyDown={(e) => { if (e.key === 'Enter') openSection(); }}
              placeholder="A31 или B11"
              className="min-w-[180px] flex-1 rounded-2xl border border-brand-200 bg-white px-3 py-2.5 text-neutral-900 outline-none focus:border-brand-500 dark:border-brand-900 dark:bg-neutral-950 dark:text-neutral-100"
            />
            <button type="button" onClick={openSection} className="btn-primary inline-flex items-center justify-center gap-2">
              <Plus size={16} /> Открыть / создать номер
            </button>
          </div>
          {error ? <div className="mt-2 rounded-2xl border border-red-200 bg-red-50 p-2 text-sm text-red-800 dark:border-red-900 dark:bg-red-950/30 dark:text-red-100">{error}</div> : null}
        </div>
      </div>
    </section>
  );
}

export default function SimpleHomePage() {
  const { canEdit, isEditorMode } = useEditorMode();
  const [tree, setTree] = useState([]);
  const [loadingTree, setLoadingTree] = useState(false);

  useEffect(() => {
    let cancelled = false;
    async function loadTree() {
      setLoadingTree(true);
      try {
        const data = await getLearningCourseTree({ includeDraft: canEdit && isEditorMode });
        if (!cancelled) setTree(data || []);
      } catch {
        if (!cancelled) setTree([]);
      } finally {
        if (!cancelled) setLoadingTree(false);
      }
    }
    loadTree();
    return () => { cancelled = true; };
  }, [canEdit, isEditorMode]);

  const extraSections = useMemo(() => (
    flattenCourses(tree)
      .map((course) => normalizeSectionCode(course.sectionCode))
      .filter(Boolean)
  ), [tree]);

  return (
    <Layout fullWidth>
      <div className="min-h-[calc(100vh-57px)] bg-neutral-50 dark:bg-neutral-950">
        <div className="mx-auto max-w-6xl px-4 py-8 md:py-12">
          <section className="mb-8 rounded-[2rem] border border-neutral-200/80 bg-white p-6 text-center shadow-soft dark:border-neutral-800 dark:bg-neutral-900 md:p-10">
            <div className="mx-auto mb-4 grid h-14 w-14 place-items-center rounded-3xl bg-brand-50 text-brand-700 dark:bg-brand-900/20 dark:text-brand-100">
              <BookOpen size={28} />
            </div>
            <div className="text-sm font-bold uppercase tracking-[0.25em] text-brand-700 dark:text-brand-300">ЦТ / ЦЭ</div>
            <h1 className="mt-3 text-4xl font-black tracking-tight md:text-6xl">Выбери номер задания</h1>
            <p className="mx-auto mt-4 max-w-2xl text-lg text-neutral-600 dark:text-neutral-300">
              Без лишней платформы: номер → HTML-конспект → случайные задания по этому же номеру.
            </p>
          </section>

          {canEdit && isEditorMode ? <ManualSectionOpener /> : null}

          <div className="space-y-6">
            {CT_PARTS.map((part) => {
              const sections = getSectionsByPart(part.code, extraSections);
              return (
                <section key={part.code} className="rounded-[2rem] border border-neutral-200/80 bg-white p-5 shadow-soft dark:border-neutral-800 dark:bg-neutral-900 md:p-6">
                  <div className="mb-4 flex flex-wrap items-end justify-between gap-3">
                    <div>
                      <h2 className="text-3xl font-black tracking-tight">{part.title}</h2>
                      <p className="mt-1 max-w-3xl text-sm leading-6 text-neutral-600 dark:text-neutral-300">{part.description}</p>
                    </div>
                    <div className="text-sm font-semibold text-neutral-400">
                      {loadingTree ? <span className="inline-flex items-center gap-1"><Loader2 size={14} className="animate-spin" /> номера</span> : `${sections.length} номеров`}
                    </div>
                  </div>

                  <div className="grid grid-cols-3 gap-3 sm:grid-cols-5 md:grid-cols-6 lg:grid-cols-10">
                    {sections.map((section) => (
                      <Link
                        key={section.code}
                        to={getSectionPath(section.code)}
                        className={`group rounded-3xl border p-4 text-center transition hover:-translate-y-0.5 hover:border-brand-300 hover:bg-white hover:shadow-md dark:hover:border-brand-700 dark:hover:bg-neutral-900 ${section.isDefault ? 'border-neutral-200 bg-neutral-50 dark:border-neutral-800 dark:bg-neutral-950' : 'border-brand-200 bg-brand-50 dark:border-brand-900 dark:bg-brand-950/20'}`}
                      >
                        <div className="text-2xl font-black tracking-tight text-brand-700 dark:text-brand-200">{section.code}</div>
                        <div className="mt-2 inline-flex items-center gap-1 text-xs font-semibold text-neutral-500 group-hover:text-brand-700 dark:text-neutral-400 dark:group-hover:text-brand-200">
                          {canEdit && isEditorMode ? 'Открыть и править' : 'Открыть'} <ChevronRight size={13} />
                        </div>
                      </Link>
                    ))}
                  </div>
                </section>
              );
            })}
          </div>
        </div>
      </div>
    </Layout>
  );
}
