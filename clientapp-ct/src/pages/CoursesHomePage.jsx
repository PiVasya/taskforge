import React, { useEffect, useMemo, useState } from 'react';
import { Link } from 'react-router-dom';
import { BookOpen, GraduationCap, Loader2, Search, Sparkles } from 'lucide-react';
import Layout from '../components/Layout';
import { getMainCourses } from '../api/courses';

export default function CoursesHomePage() {
  const [courses, setCourses] = useState([]);
  const [query, setQuery] = useState('');
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');

  useEffect(() => {
    let cancelled = false;
    async function load() {
      setLoading(true);
      setError('');
      try {
        const list = await getMainCourses();
        if (!cancelled) setCourses(Array.isArray(list) ? list : []);
      } catch (e) {
        if (!cancelled) setError(e?.userMessage || e?.message || 'Не удалось загрузить список курсов.');
      } finally {
        if (!cancelled) setLoading(false);
      }
    }
    load();
    return () => { cancelled = true; };
  }, []);

  const filtered = useMemo(() => {
    const q = query.trim().toLowerCase();
    if (!q) return courses;
    return courses.filter((course) => `${course.title || ''} ${course.description || ''}`.toLowerCase().includes(q));
  }, [courses, query]);

  return (
    <Layout>
      <section className="rounded-[2rem] border border-neutral-200/80 bg-white p-6 shadow-soft dark:border-neutral-800 dark:bg-neutral-900 md:p-8">
        <div className="flex flex-col gap-5 lg:flex-row lg:items-start lg:justify-between">
          <div className="max-w-3xl">
            <div className="inline-flex items-center gap-2 rounded-full border border-brand-200 bg-brand-50 px-3 py-1 text-sm font-semibold text-brand-700 dark:border-brand-900 dark:bg-brand-950/30 dark:text-brand-200">
              <GraduationCap size={16} />
              TaskForge CT
            </div>
            <h1 className="mt-4 text-3xl font-bold tracking-tight md:text-5xl">Курсы</h1>
            <p className="mt-3 max-w-2xl text-base leading-7 text-neutral-600 dark:text-neutral-300">
              Сначала выбираем обычный курс из TaskForge. Внутри курса открывается отдельная учебная часть: ЦТ/ЦЭ, разделы, конспекты и задания.
            </p>
          </div>
          <div className="rounded-3xl border border-neutral-200 bg-neutral-50 p-4 text-sm leading-6 text-neutral-600 dark:border-neutral-800 dark:bg-neutral-950 dark:text-neutral-300">
            <div className="flex items-center gap-2 font-semibold text-neutral-900 dark:text-neutral-100"><Sparkles size={17} /> Логика входа</div>
            <div className="mt-1">Курс → ЦТ/ЦЭ → A1/A2/... → конспекты и задания.</div>
          </div>
        </div>
      </section>

      <section className="mt-6 rounded-[2rem] border border-neutral-200/80 bg-white p-4 shadow-soft dark:border-neutral-800 dark:bg-neutral-900">
        <label className="relative block">
          <Search size={18} className="absolute left-4 top-1/2 -translate-y-1/2 text-neutral-400" />
          <input
            value={query}
            onChange={(e) => setQuery(e.target.value)}
            placeholder="Найти курс"
            className="w-full rounded-2xl border border-neutral-200 bg-neutral-50 py-3 pl-11 pr-4 outline-none focus:border-brand-400 dark:border-neutral-800 dark:bg-neutral-950"
          />
        </label>
      </section>

      {error && <div className="mt-6 rounded-3xl border border-red-200 bg-red-50 p-4 text-sm text-red-800 dark:border-red-900 dark:bg-red-950/20 dark:text-red-100">{error}</div>}

      {loading ? (
        <div className="mt-6 rounded-[2rem] border border-neutral-200 bg-white p-10 text-center shadow-soft dark:border-neutral-800 dark:bg-neutral-900">
          <Loader2 className="mx-auto animate-spin text-brand-600" />
          <div className="mt-3 text-neutral-600 dark:text-neutral-300">Загружаю курсы...</div>
        </div>
      ) : (
        <div className="mt-6 grid gap-4 md:grid-cols-2 xl:grid-cols-3">
          {filtered.map((course) => (
            <Link key={course.id} to={`/courses/${course.id}`} className="group rounded-[2rem] border border-neutral-200 bg-white p-5 shadow-soft transition hover:-translate-y-0.5 hover:border-brand-300 hover:shadow-lg dark:border-neutral-800 dark:bg-neutral-900">
              <div className="flex items-start justify-between gap-3">
                <div className="rounded-2xl bg-brand-50 p-3 text-brand-700 dark:bg-brand-950/30 dark:text-brand-200"><BookOpen size={22} /></div>
                {course.isCompletedForCurrentUser ? <span className="rounded-full bg-emerald-50 px-3 py-1 text-xs font-semibold text-emerald-700 dark:bg-emerald-950/30 dark:text-emerald-200">пройден</span> : null}
              </div>
              <h2 className="mt-4 text-xl font-bold leading-7 group-hover:text-brand-700 dark:group-hover:text-brand-200">{course.title}</h2>
              {course.description && <p className="mt-2 line-clamp-3 text-sm leading-6 text-neutral-600 dark:text-neutral-300">{course.description}</p>}
              <div className="mt-4 flex flex-wrap gap-2 text-xs text-neutral-500 dark:text-neutral-400">
                <span className="rounded-full border border-neutral-200 px-3 py-1 dark:border-neutral-800">Заданий: {course.assignmentCount ?? '—'}</span>
                <span className="rounded-full border border-neutral-200 px-3 py-1 dark:border-neutral-800">Тестов: {course.testCount ?? '—'}</span>
              </div>
            </Link>
          ))}
        </div>
      )}

      {!loading && filtered.length === 0 && <div className="mt-6 rounded-[2rem] border border-neutral-200 bg-white p-10 text-center text-neutral-500 shadow-soft dark:border-neutral-800 dark:bg-neutral-900">Курсов пока нет или они не найдены.</div>}
    </Layout>
  );
}
