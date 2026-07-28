import React, { useEffect, useMemo, useState } from 'react';
import { Link, useParams } from 'react-router-dom';
import { ArrowLeft, BookOpen, ChevronRight, Loader2, Settings } from 'lucide-react';
import RichConspectRenderer from '../components/RichConspectRenderer';
import { getLearningConspect, getLearningCourseOutline } from '../api/learning';

export default function LearningConspectPage() {
  const { courseSlug, slug } = useParams();
  const [outline, setOutline] = useState(null);
  const [details, setDetails] = useState(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');

  useEffect(() => {
    let cancelled = false;

    async function load() {
      setLoading(true);
      setError('');
      try {
        const [outlineData, detailsData] = await Promise.all([
          courseSlug ? getLearningCourseOutline(courseSlug) : Promise.resolve(null),
          getLearningConspect(slug, courseSlug ? { courseSlug } : {}),
        ]);
        if (!cancelled) {
          setOutline(outlineData);
          setDetails(detailsData);
        }
      } catch (e) {
        if (!cancelled) setError(e?.userMessage || e?.message || 'Не удалось открыть конспект.');
      } finally {
        if (!cancelled) setLoading(false);
      }
    }

    load();
    return () => { cancelled = true; };
  }, [courseSlug, slug]);

  const course = outline?.course;
  const conspects = useMemo(() => outline?.conspects || [], [outline]);
  const tasksBasePath = courseSlug ? `/courses/${courseSlug}/tasks` : '/tasks';

  return (
    <>
      <div className="min-h-[calc(100vh-57px)] bg-neutral-50 dark:bg-neutral-950">
        <div className="container-app py-6 lg:py-8">
          <div className="mb-5 flex flex-wrap items-center justify-between gap-3">
            <Link to={courseSlug ? `/courses/${courseSlug}` : '/'} className="inline-flex items-center gap-2 text-sm font-semibold text-brand-700 dark:text-brand-300 hover:underline">
              <ArrowLeft size={16} />
              Назад в раздел{course?.title ? `: ${course.title}` : ''}
            </Link>
            <Link to="/admin/conspects" className="btn-outline inline-flex items-center gap-2 text-sm">
              <Settings size={16} />
              Редактор
            </Link>
          </div>

          {loading ? (
            <div className="rounded-[2rem] border border-neutral-200 dark:border-neutral-800 bg-white dark:bg-neutral-900 p-10 text-center shadow-soft">
              <Loader2 className="mx-auto animate-spin text-brand-600" />
              <div className="mt-3 text-neutral-600 dark:text-neutral-300">Загружаю конспект...</div>
            </div>
          ) : error ? (
            <div className="rounded-3xl border border-red-200 bg-red-50 p-4 text-sm text-red-800 dark:border-red-900 dark:bg-red-950/20 dark:text-red-100">{error}</div>
          ) : details ? (
            <div className="grid gap-6 lg:grid-cols-[320px_minmax(0,1fr)]">
              <aside className="space-y-4 lg:sticky lg:top-[77px] lg:self-start">
                <div className="rounded-[2rem] border border-neutral-200/80 dark:border-neutral-800 bg-white dark:bg-neutral-900 p-5 shadow-soft">
                  <div className="mb-4 flex items-center gap-2">
                    <BookOpen className="text-brand-600" />
                    <div>
                      <div className="text-sm font-semibold text-brand-700 dark:text-brand-300">{course?.title || 'Конспекты'}</div>
                      <h2 className="text-xl font-bold">Материалы раздела</h2>
                    </div>
                  </div>
                  <div className="space-y-2">
                    {conspects.map((item) => {
                      const active = item.slug === details?.conspect?.slug;
                      return (
                        <Link key={item.id} to={`/courses/${courseSlug}/conspects/${item.slug}`} className={`block rounded-2xl border p-3 text-sm transition ${active ? 'border-brand-400 bg-brand-50 dark:bg-brand-900/20' : 'border-neutral-200 dark:border-neutral-800 bg-neutral-50 dark:bg-neutral-950 hover:border-brand-300'}`}>
                          <div className="font-semibold leading-6">{item.title}</div>
                          {item.lead && <div className="mt-1 line-clamp-2 text-neutral-500 dark:text-neutral-400">{item.lead}</div>}
                        </Link>
                      );
                    })}
                    {conspects.length === 0 && <div className="text-sm text-neutral-500 dark:text-neutral-400">В разделе пока нет других конспектов.</div>}
                  </div>
                  {courseSlug && (
                    <Link to={`/courses/${courseSlug}`} className="mt-4 inline-flex items-center gap-2 text-sm font-semibold text-brand-700 dark:text-brand-200 hover:underline">
                      Карточка раздела <ChevronRight size={16} />
                    </Link>
                  )}
                </div>
              </aside>
              <main className="min-w-0">
                <RichConspectRenderer details={details} tasksBasePath={tasksBasePath} />
              </main>
            </div>
          ) : null}
        </div>
      </div>
    </>
  );
}
