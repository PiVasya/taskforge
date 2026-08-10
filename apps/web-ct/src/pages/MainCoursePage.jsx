import React, { useEffect, useMemo, useState } from 'react';
import { Link, useParams } from 'react-router-dom';
import { ArrowLeft, BookOpen, ChevronRight, GraduationCap, Loader2 } from 'lucide-react';
import { getMainCourse } from '../api/courses';
import { getLearningCourseTree } from '../api/learning';

function flatten(nodes, result = []) {
  (nodes || []).forEach((node) => {
    result.push(node);
    flatten(node.children, result);
  });
  return result;
}

export default function MainCoursePage() {
  const { courseId } = useParams();
  const [course, setCourse] = useState(null);
  const [learningRoots, setLearningRoots] = useState([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');

  useEffect(() => {
    let cancelled = false;
    async function load() {
      setLoading(true);
      setError('');
      try {
        const [courseData, tree] = await Promise.all([getMainCourse(courseId), getLearningCourseTree({ includeDraft: false })]);
        if (cancelled) return;
        setCourse(courseData);
        setLearningRoots(Array.isArray(tree) ? tree : []);
      } catch (e) {
        if (!cancelled) setError(e?.userMessage || e?.message || 'Не удалось открыть курс.');
      } finally {
        if (!cancelled) setLoading(false);
      }
    }
    load();
    return () => { cancelled = true; };
  }, [courseId]);

  const allLearningItems = useMemo(() => flatten(learningRoots), [learningRoots]);
  const examModules = allLearningItems.filter((item) => item.examCode && !item.sectionCode);
  const directModules = examModules.length > 0 ? examModules : learningRoots;

  return (
    <>
      <Link to="/" className="mb-5 inline-flex items-center gap-2 text-sm font-semibold text-brand-700 hover:underline dark:text-brand-300"><ArrowLeft size={16} />Назад к курсам</Link>
      {error && <div className="mb-4 rounded-3xl border border-red-200 bg-red-50 p-4 text-sm text-red-800 dark:border-red-900 dark:bg-red-950/20 dark:text-red-100">{error}</div>}
      {loading ? (
        <div className="rounded-[2rem] border border-neutral-200 bg-white p-10 text-center shadow-soft dark:border-neutral-800 dark:bg-neutral-900"><Loader2 className="mx-auto animate-spin text-brand-600" /><div className="mt-3 text-neutral-600 dark:text-neutral-300">Открываю курс...</div></div>
      ) : (
        <>
          <section className="rounded-[2rem] border border-neutral-200/80 bg-white p-6 shadow-soft dark:border-neutral-800 dark:bg-neutral-900 md:p-8">
            <div className="inline-flex items-center gap-2 rounded-full border border-brand-200 bg-brand-50 px-3 py-1 text-sm font-semibold text-brand-700 dark:border-brand-900 dark:bg-brand-950/30 dark:text-brand-200"><GraduationCap size={16} />Курс TaskForge</div>
            <h1 className="mt-4 text-3xl font-bold tracking-tight md:text-5xl">{course?.title || 'Курс'}</h1>
            {course?.description && <p className="mt-3 max-w-3xl text-base leading-7 text-neutral-600 dark:text-neutral-300">{course.description}</p>}
          </section>

          <section className="mt-6">
            <div className="mb-3"><h2 className="text-2xl font-bold tracking-tight">Учебные направления</h2></div>
            <div className="grid gap-4 md:grid-cols-2 xl:grid-cols-3">
              {directModules.map((item) => (
                <Link key={item.id} to={`/learning/${item.slug}`} className="group rounded-[2rem] border border-neutral-200 bg-white p-5 shadow-soft transition hover:-translate-y-0.5 hover:border-brand-300 hover:shadow-lg dark:border-neutral-800 dark:bg-neutral-900">
                  <div className="flex items-start justify-between gap-3"><div className="rounded-2xl bg-brand-50 p-3 text-brand-700 dark:bg-brand-950/30 dark:text-brand-200"><BookOpen size={22} /></div><ChevronRight className="text-neutral-400 transition group-hover:translate-x-1 group-hover:text-brand-600" /></div>
                  <h3 className="mt-4 text-xl font-bold leading-7 group-hover:text-brand-700 dark:group-hover:text-brand-200">{item.title}</h3>
                  {item.summary && <p className="mt-2 line-clamp-3 text-sm leading-6 text-neutral-600 dark:text-neutral-300">{item.summary}</p>}
                  {item.children?.length ? <div className="mt-4 text-sm font-medium text-brand-700 dark:text-brand-300">Разделов: {item.children.length}</div> : null}
                </Link>
              ))}
            </div>
            {directModules.length === 0 && <div className="rounded-[2rem] border border-neutral-200 bg-white p-10 text-center text-neutral-500 shadow-soft dark:border-neutral-800 dark:bg-neutral-900">Учебных направлений пока нет.</div>}
          </section>
        </>
      )}
    </>
  );
}
