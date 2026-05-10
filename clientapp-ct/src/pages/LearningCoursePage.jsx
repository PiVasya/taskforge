import React, { useEffect, useMemo, useState } from 'react';
import { Link, useParams } from 'react-router-dom';
import { ArrowLeft, BookOpen, ChevronRight, ClipboardList, FileText, Loader2, PlayCircle, Sparkles } from 'lucide-react';
import Layout from '../components/Layout';
import { getLearningCourseOutline, getLearningCourseTree } from '../api/learning';
import { getQuizTasks } from '../api/quiz';

function findPath(nodes, slug, path = []) {
  for (const node of nodes || []) {
    const next = [...path, node];
    if (node.slug === slug) return next;
    const child = findPath(node.children, slug, next);
    if (child) return child;
  }
  return null;
}

function typeLabel(course) {
  if (course?.sectionCode) return 'Раздел экзамена';
  if (course?.examCode) return 'Курс подготовки';
  if (!course?.parentCourseId) return 'Предмет';
  return 'Учебный раздел';
}

function makeTasksParams(course) {
  const params = new URLSearchParams();
  if (course?.subjectCode) params.set('subjectCode', course.subjectCode);
  if (course?.examCode) params.set('examCode', course.examCode);
  if (course?.sectionCode) params.set('sectionCode', course.sectionCode);
  return params.toString();
}

export default function LearningCoursePage() {
  const { courseSlug } = useParams();
  const [outline, setOutline] = useState(null);
  const [tree, setTree] = useState([]);
  const [quizTasks, setQuizTasks] = useState([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');

  useEffect(() => {
    let cancelled = false;

    async function load() {
      setLoading(true);
      setError('');
      try {
        const [outlineData, treeData] = await Promise.all([
          getLearningCourseOutline(courseSlug),
          getLearningCourseTree(),
        ]);
        if (cancelled) return;
        setOutline(outlineData);
        setTree(treeData || []);

        const course = outlineData?.course;
        if (course?.sectionCode) {
          try {
            const tasks = await getQuizTasks({
              subjectCode: course.subjectCode || 'russian',
              examCode: course.examCode || 'ct-ce-2026',
              sectionCode: course.sectionCode,
            });
            if (!cancelled) setQuizTasks(tasks || []);
          } catch {
            if (!cancelled) setQuizTasks([]);
          }
        } else {
          setQuizTasks([]);
        }
      } catch (e) {
        if (!cancelled) setError(e?.userMessage || e?.message || 'Не удалось открыть учебный курс.');
      } finally {
        if (!cancelled) setLoading(false);
      }
    }

    load();
    return () => { cancelled = true; };
  }, [courseSlug]);

  const course = outline?.course;
  const path = useMemo(() => findPath(tree, courseSlug) || [], [tree, courseSlug]);
  const tasksQuery = makeTasksParams(course);

  return (
    <Layout fullWidth>
      <div className="min-h-[calc(100vh-57px)] bg-neutral-50 dark:bg-neutral-950">
        <div className="container-app py-6 lg:py-8">
          <Link to="/" className="inline-flex items-center gap-2 text-sm font-semibold text-brand-700 dark:text-brand-300 hover:underline">
            <ArrowLeft size={16} />
            К учебным курсам
          </Link>

          {loading ? (
            <div className="mt-6 rounded-[2rem] border border-neutral-200 dark:border-neutral-800 bg-white dark:bg-neutral-900 p-10 text-center shadow-soft">
              <Loader2 className="mx-auto animate-spin text-brand-600" />
              <div className="mt-3 text-neutral-600 dark:text-neutral-300">Открываю раздел...</div>
            </div>
          ) : error ? (
            <div className="mt-6 rounded-3xl border border-red-200 bg-red-50 p-4 text-sm text-red-800 dark:border-red-900 dark:bg-red-950/20 dark:text-red-100">{error}</div>
          ) : course ? (
            <>
              <nav className="mt-5 flex flex-wrap items-center gap-2 text-sm text-neutral-500 dark:text-neutral-400">
                <Link to="/" className="hover:text-brand-700 dark:hover:text-brand-200">CT</Link>
                {path.map((item) => (
                  <React.Fragment key={item.id}>
                    <ChevronRight size={15} />
                    <Link to={`/courses/${item.slug}`} className={item.slug === courseSlug ? 'font-semibold text-neutral-900 dark:text-neutral-100' : 'hover:text-brand-700 dark:hover:text-brand-200'}>
                      {item.shortTitle || item.title}
                    </Link>
                  </React.Fragment>
                ))}
              </nav>

              <section className="mt-5 overflow-hidden rounded-[2rem] border border-neutral-200/80 dark:border-neutral-800 bg-white dark:bg-neutral-900 shadow-soft">
                <div className="bg-gradient-to-br from-brand-50 via-white to-white p-6 md:p-8 dark:from-brand-900/20 dark:via-neutral-900 dark:to-neutral-900">
                  <div className="mb-4 inline-flex items-center gap-2 rounded-full border border-brand-200 bg-white/80 px-3 py-1 text-sm font-semibold text-brand-800 dark:border-brand-800 dark:bg-neutral-950/60 dark:text-brand-100">
                    <Sparkles size={16} />
                    {typeLabel(course)}
                  </div>
                  <h1 className="max-w-5xl text-4xl md:text-5xl font-bold tracking-tight">{course.title}</h1>
                  {(course.summary || course.description) && (
                    <p className="mt-4 max-w-4xl text-lg leading-8 text-neutral-700 dark:text-neutral-200">{course.summary || course.description}</p>
                  )}
                  <div className="mt-6 flex flex-wrap gap-3 text-sm">
                    {course.subjectCode && <span className="rounded-full border border-neutral-200 bg-white/80 px-3 py-1 dark:border-neutral-800 dark:bg-neutral-950/60">Предмет: {course.subjectCode}</span>}
                    {course.examCode && <span className="rounded-full border border-neutral-200 bg-white/80 px-3 py-1 dark:border-neutral-800 dark:bg-neutral-950/60">Экзамен: {course.examCode}</span>}
                    {course.sectionCode && <span className="rounded-full border border-neutral-200 bg-white/80 px-3 py-1 dark:border-neutral-800 dark:bg-neutral-950/60">Раздел: {course.sectionCode}</span>}
                  </div>
                </div>
              </section>

              {outline.children?.length > 0 && (
                <section className="mt-6">
                  <div className="mb-3 flex items-center gap-2">
                    <BookOpen className="text-brand-600" />
                    <h2 className="text-2xl font-bold tracking-tight">Внутри курса</h2>
                  </div>
                  <div className="grid gap-4 md:grid-cols-2 xl:grid-cols-3">
                    {outline.children.map((child) => (
                      <Link key={child.id} to={`/courses/${child.slug}`} className="group rounded-[2rem] border border-neutral-200/80 dark:border-neutral-800 bg-white dark:bg-neutral-900 p-5 shadow-soft transition hover:-translate-y-0.5 hover:border-brand-300">
                        <div className="text-sm font-semibold uppercase tracking-wide text-brand-700 dark:text-brand-300">{typeLabel(child)}</div>
                        <h3 className="mt-2 text-2xl font-bold group-hover:text-brand-700 dark:group-hover:text-brand-200">{child.title}</h3>
                        {child.summary && <p className="mt-2 line-clamp-3 leading-7 text-neutral-600 dark:text-neutral-300">{child.summary}</p>}
                        <div className="mt-5 inline-flex items-center gap-2 font-semibold text-brand-700 dark:text-brand-200">Открыть <ChevronRight size={18} /></div>
                      </Link>
                    ))}
                  </div>
                </section>
              )}

              {(outline.conspects?.length > 0 || course.sectionCode) && (
                <section className="mt-6 grid gap-5 lg:grid-cols-[minmax(0,1.4fr)_minmax(320px,0.8fr)]">
                  <div className="rounded-[2rem] border border-neutral-200/80 dark:border-neutral-800 bg-white dark:bg-neutral-900 p-5 shadow-soft">
                    <div className="mb-4 flex items-center gap-2">
                      <FileText className="text-brand-600" />
                      <h2 className="text-2xl font-bold tracking-tight">Конспекты</h2>
                    </div>
                    {outline.conspects?.length > 0 ? (
                      <div className="space-y-3">
                        {outline.conspects.map((conspect) => (
                          <Link key={conspect.id} to={`/courses/${course.slug}/conspects/${conspect.slug}`} className="block rounded-3xl border border-neutral-200 dark:border-neutral-800 bg-neutral-50 dark:bg-neutral-950 p-4 transition hover:border-brand-300">
                            <div className="font-semibold text-lg">{conspect.title}</div>
                            {conspect.lead && <p className="mt-1 line-clamp-2 text-sm leading-6 text-neutral-600 dark:text-neutral-300">{conspect.lead}</p>}
                            <div className="mt-3 inline-flex items-center gap-2 text-sm font-semibold text-brand-700 dark:text-brand-200">Открыть конспект <ChevronRight size={16} /></div>
                          </Link>
                        ))}
                      </div>
                    ) : (
                      <div className="rounded-3xl border border-dashed border-neutral-200 dark:border-neutral-800 p-6 text-neutral-500 dark:text-neutral-400">Для этого раздела конспекты ещё не добавлены.</div>
                    )}
                  </div>

                  <div className="rounded-[2rem] border border-brand-200 dark:border-brand-800 bg-brand-50 dark:bg-brand-900/20 p-5 shadow-soft">
                    <div className="mb-4 flex items-center gap-2">
                      <ClipboardList className="text-brand-700 dark:text-brand-200" />
                      <h2 className="text-2xl font-bold tracking-tight">Задания</h2>
                    </div>
                    {course.sectionCode ? (
                      <>
                        <p className="leading-7 text-neutral-700 dark:text-neutral-200">Мини-задачи хранятся отдельно в `quiz-task-service`, а этот раздел только открывает нужную подборку.</p>
                        <div className="mt-4 rounded-2xl border border-brand-200 dark:border-brand-800 bg-white dark:bg-neutral-900 p-4">
                          <div className="text-sm text-neutral-500 dark:text-neutral-400">Сейчас в разделе</div>
                          <div className="text-3xl font-bold">{quizTasks.length}</div>
                          <div className="text-sm text-neutral-500 dark:text-neutral-400">опубликованных заданий</div>
                        </div>
                        <Link to={`/courses/${course.slug}/tasks${tasksQuery ? `?${tasksQuery}` : ''}`} className="btn-primary mt-4 inline-flex items-center gap-2">
                          <PlayCircle size={18} />
                          Перейти к заданиям
                        </Link>
                      </>
                    ) : (
                      <p className="leading-7 text-neutral-700 dark:text-neutral-200">Задания появляются внутри конкретных разделов A1/A2/B...</p>
                    )}
                  </div>
                </section>
              )}

              {(!outline.children?.length && !outline.conspects?.length && !course.sectionCode) && (
                <div className="mt-6 rounded-[2rem] border border-neutral-200 dark:border-neutral-800 bg-white dark:bg-neutral-900 p-10 text-center shadow-soft text-neutral-600 dark:text-neutral-300">
                  В этом учебном разделе пока нет дочерних тем и конспектов.
                </div>
              )}
            </>
          ) : null}
        </div>
      </div>
    </Layout>
  );
}
