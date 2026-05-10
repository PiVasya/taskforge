import React, { useEffect, useMemo, useState } from 'react';
import { Link } from 'react-router-dom';
import { BookOpen, ChevronRight, Loader2, Search, Sparkles } from 'lucide-react';
import Layout from '../components/Layout';
import { getLearningCourseTree } from '../api/learning';

function flatten(nodes, level = 0, result = []) {
  (nodes || []).forEach((node) => {
    result.push({ ...node, level });
    flatten(node.children, level + 1, result);
  });
  return result;
}

function getKindLabel(course) {
  if (course.sectionCode) return 'Раздел экзамена';
  if (course.examCode) return 'Подготовка к экзамену';
  if (!course.parentCourseId) return 'Предмет';
  return 'Учебный раздел';
}

export default function LearningHomePage() {
  const [tree, setTree] = useState([]);
  const [query, setQuery] = useState('');
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');

  useEffect(() => {
    let cancelled = false;

    async function load() {
      setLoading(true);
      setError('');
      try {
        const data = await getLearningCourseTree();
        if (!cancelled) setTree(data || []);
      } catch (e) {
        if (!cancelled) setError(e?.userMessage || e?.message || 'Не удалось загрузить учебные курсы ЦТ/ЦЭ.');
      } finally {
        if (!cancelled) setLoading(false);
      }
    }

    load();
    return () => { cancelled = true; };
  }, []);

  const visibleRoots = useMemo(() => {
    const q = query.trim().toLowerCase();
    if (!q) return tree;

    const matches = (course) => {
      const text = `${course.title || ''} ${course.shortTitle || ''} ${course.summary || ''} ${course.sectionCode || ''}`.toLowerCase();
      return text.includes(q) || (course.children || []).some(matches);
    };

    return tree.filter(matches);
  }, [tree, query]);

  const flatCount = useMemo(() => flatten(tree).length, [tree]);

  return (
    <Layout fullWidth>
      <div className="min-h-[calc(100vh-57px)] bg-neutral-50 dark:bg-neutral-950">
        <div className="container-app py-6 lg:py-8">
          <section className="rounded-[2rem] border border-neutral-200/80 dark:border-neutral-800 bg-white dark:bg-neutral-900 shadow-soft p-6 md:p-8">
            <div className="flex flex-col gap-5 lg:flex-row lg:items-start lg:justify-between">
              <div>
                <div className="mb-4 inline-flex items-center gap-2 rounded-full border border-brand-200 bg-brand-50 px-3 py-1 text-sm font-semibold text-brand-800 dark:border-brand-800 dark:bg-brand-900/20 dark:text-brand-100">
                  <BookOpen size={16} />
                  TaskForge CT
                </div>
                <h1 className="text-4xl md:text-5xl font-bold tracking-tight">Учебные курсы ЦТ/ЦЭ</h1>
                <p className="mt-4 max-w-3xl text-lg leading-8 text-neutral-600 dark:text-neutral-300">
                  Это отдельная учебная ветка. Здесь нет старых курсов программирования TaskForge: только предметы, подготовка к экзамену, разделы, конспекты и мини-задания.
                </p>
              </div>
              <div className="rounded-3xl border border-neutral-200 dark:border-neutral-800 bg-neutral-50 dark:bg-neutral-950 p-5 lg:w-80">
                <div className="flex items-center gap-2 font-semibold"><Sparkles size={18} /> Логика</div>
                <p className="mt-2 text-sm leading-6 text-neutral-600 dark:text-neutral-300">
                  Предмет → ЦТ/ЦЭ → A1/A2/B... → конспекты, задания и прогресс.
                </p>
              </div>
            </div>
          </section>

          <section className="mt-6 rounded-[2rem] border border-neutral-200/80 dark:border-neutral-800 bg-white dark:bg-neutral-900 p-4 shadow-soft">
            <label className="relative block">
              <Search size={18} className="absolute left-4 top-1/2 -translate-y-1/2 text-neutral-400" />
              <input
                value={query}
                onChange={(e) => setQuery(e.target.value)}
                placeholder="Найти предмет, экзамен или раздел"
                className="w-full rounded-2xl border border-neutral-200 dark:border-neutral-800 bg-neutral-50 dark:bg-neutral-950 py-3 pl-12 pr-4 text-base outline-none focus:border-brand-400"
              />
            </label>
          </section>

          {error && (
            <div className="mt-6 rounded-3xl border border-red-200 bg-red-50 p-4 text-sm text-red-800 dark:border-red-900 dark:bg-red-950/20 dark:text-red-100">
              {error}
            </div>
          )}

          {loading ? (
            <div className="mt-6 rounded-[2rem] border border-neutral-200 dark:border-neutral-800 bg-white dark:bg-neutral-900 p-10 text-center shadow-soft">
              <Loader2 className="mx-auto animate-spin text-brand-600" />
              <div className="mt-3 text-neutral-600 dark:text-neutral-300">Загружаю учебное дерево...</div>
            </div>
          ) : (
            <>
              <div className="mt-5 text-sm text-neutral-500 dark:text-neutral-400">Найдено учебных узлов: {flatCount}</div>
              <div className="mt-4 grid gap-5 md:grid-cols-2 xl:grid-cols-3">
                {visibleRoots.map((course) => (
                  <Link
                    key={course.id}
                    to={`/courses/${course.slug}`}
                    className="group rounded-[2rem] border border-neutral-200/80 dark:border-neutral-800 bg-white dark:bg-neutral-900 p-6 shadow-soft transition hover:-translate-y-0.5 hover:border-brand-300 hover:shadow-xl"
                  >
                    <div className="mb-5 flex h-12 w-12 items-center justify-center rounded-2xl bg-brand-50 text-brand-700 dark:bg-brand-900/20 dark:text-brand-100">
                      <BookOpen />
                    </div>
                    <div className="text-sm font-semibold uppercase tracking-wide text-brand-700 dark:text-brand-300">{getKindLabel(course)}</div>
                    <h2 className="mt-2 text-2xl font-bold tracking-tight group-hover:text-brand-700 dark:group-hover:text-brand-200">{course.title}</h2>
                    {course.summary && <p className="mt-3 line-clamp-3 leading-7 text-neutral-600 dark:text-neutral-300">{course.summary}</p>}
                    <div className="mt-5 flex flex-wrap gap-2 text-sm text-neutral-500 dark:text-neutral-400">
                      <span className="rounded-full border border-neutral-200 dark:border-neutral-800 px-3 py-1">Внутри: {(course.children || []).length}</span>
                      {course.subjectCode && <span className="rounded-full border border-neutral-200 dark:border-neutral-800 px-3 py-1">{course.subjectCode}</span>}
                    </div>
                    <div className="mt-6 inline-flex items-center gap-2 font-semibold text-brand-700 dark:text-brand-200">
                      Открыть <ChevronRight size={18} />
                    </div>
                  </Link>
                ))}
              </div>

              {visibleRoots.length === 0 && (
                <div className="mt-6 rounded-[2rem] border border-neutral-200 dark:border-neutral-800 bg-white dark:bg-neutral-900 p-10 text-center shadow-soft text-neutral-600 dark:text-neutral-300">
                  Учебных курсов пока нет. Проверь seed `learning-content-service` или создай предмет в админке.
                </div>
              )}
            </>
          )}
        </div>
      </div>
    </Layout>
  );
}
