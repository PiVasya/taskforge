import React, { useEffect, useMemo, useState } from 'react';
import { Link, useParams } from 'react-router-dom';
import { ArrowLeft, BookOpen, ChevronRight, ClipboardList, FileText, Loader2, Settings } from 'lucide-react';
import Layout from '../components/Layout';
import { getLearningCourseOutline } from '../api/learning';

function parseBadges(raw) {
  try { const value = typeof raw === 'string' ? JSON.parse(raw) : raw; return Array.isArray(value) ? value : []; } catch { return []; }
}

export default function LearningCoursePage() {
  const { slug } = useParams();
  const [outline, setOutline] = useState(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');

  useEffect(() => {
    let cancelled = false;
    async function load() {
      setLoading(true); setError('');
      try { const data = await getLearningCourseOutline(slug, { includeDraft: false }); if (!cancelled) setOutline(data); }
      catch (e) { if (!cancelled) setError(e?.userMessage || e?.message || 'Не удалось загрузить учебный раздел.'); }
      finally { if (!cancelled) setLoading(false); }
    }
    load();
    return () => { cancelled = true; };
  }, [slug]);

  const sectionLabel = useMemo(() => {
    const c = outline?.course;
    if (!c) return '';
    return [c.subjectCode, c.examCode, c.sectionCode].filter(Boolean).join(' · ');
  }, [outline]);

  return (
    <Layout>
      <Link to="/" className="mb-5 inline-flex items-center gap-2 text-sm font-semibold text-brand-700 hover:underline dark:text-brand-300"><ArrowLeft size={16} />К списку курсов</Link>
      {error && <div className="mb-4 rounded-3xl border border-red-200 bg-red-50 p-4 text-sm text-red-800 dark:border-red-900 dark:bg-red-950/20 dark:text-red-100">{error}</div>}
      {loading ? (
        <div className="rounded-[2rem] border border-neutral-200 bg-white p-10 text-center shadow-soft dark:border-neutral-800 dark:bg-neutral-900"><Loader2 className="mx-auto animate-spin text-brand-600" /><div className="mt-3 text-neutral-600 dark:text-neutral-300">Загружаю раздел...</div></div>
      ) : outline ? (
        <>
          <section className="rounded-[2rem] border border-neutral-200/80 bg-white p-6 shadow-soft dark:border-neutral-800 dark:bg-neutral-900 md:p-8">
            {sectionLabel && <div className="text-sm font-semibold uppercase tracking-[0.2em] text-brand-700 dark:text-brand-300">{sectionLabel}</div>}
            <h1 className="mt-3 text-3xl font-bold tracking-tight md:text-5xl">{outline.course.title}</h1>
            {outline.course.summary && <p className="mt-3 max-w-3xl text-base leading-7 text-neutral-600 dark:text-neutral-300">{outline.course.summary}</p>}
            <div className="mt-5 flex flex-wrap gap-2"><Link to="/tasks" className="btn-outline inline-flex items-center gap-2"><ClipboardList size={17} /> Задания</Link><Link to="/admin/conspects" className="btn-outline inline-flex items-center gap-2"><Settings size={17} /> Редактор</Link></div>
          </section>

          {outline.children?.length > 0 && <section className="mt-6"><h2 className="mb-3 text-2xl font-bold tracking-tight">Подразделы</h2><div className="grid gap-4 md:grid-cols-2 xl:grid-cols-3">{outline.children.map((child) => (<Link key={child.id} to={`/learning/${child.slug}`} className="group rounded-[2rem] border border-neutral-200 bg-white p-5 shadow-soft transition hover:-translate-y-0.5 hover:border-brand-300 hover:shadow-lg dark:border-neutral-800 dark:bg-neutral-900"><div className="flex items-start justify-between gap-3"><div className="rounded-2xl bg-brand-50 p-3 text-brand-700 dark:bg-brand-950/30 dark:text-brand-200"><BookOpen size={22} /></div><ChevronRight className="text-neutral-400 transition group-hover:translate-x-1 group-hover:text-brand-600" /></div><h3 className="mt-4 text-xl font-bold leading-7 group-hover:text-brand-700 dark:group-hover:text-brand-200">{child.title}</h3>{child.summary && <p className="mt-2 line-clamp-3 text-sm leading-6 text-neutral-600 dark:text-neutral-300">{child.summary}</p>}</Link>))}</div></section>}

          {outline.conspects?.length > 0 && <section className="mt-6"><h2 className="mb-3 text-2xl font-bold tracking-tight">Конспекты</h2><div className="grid gap-4 lg:grid-cols-2">{outline.conspects.map((item) => { const badges = parseBadges(item.badgesJson); return (<Link key={item.id} to={`/learning/${outline.course.slug}/conspects/${item.slug}`} className="group rounded-[2rem] border border-neutral-200 bg-white p-5 shadow-soft transition hover:-translate-y-0.5 hover:border-brand-300 hover:shadow-lg dark:border-neutral-800 dark:bg-neutral-900"><div className="flex items-start justify-between gap-3"><div className="rounded-2xl bg-brand-50 p-3 text-brand-700 dark:bg-brand-950/30 dark:text-brand-200"><FileText size={22} /></div><ChevronRight className="text-neutral-400 transition group-hover:translate-x-1 group-hover:text-brand-600" /></div><h3 className="mt-4 text-xl font-bold leading-7 group-hover:text-brand-700 dark:group-hover:text-brand-200">{item.title}</h3>{item.lead && <p className="mt-2 line-clamp-3 text-sm leading-6 text-neutral-600 dark:text-neutral-300">{item.lead}</p>}<div className="mt-4 flex flex-wrap gap-2">{badges.slice(0, 5).map((badge) => <span key={badge} className="rounded-full border border-neutral-200 px-3 py-1 text-xs text-neutral-500 dark:border-neutral-800 dark:text-neutral-400">{badge}</span>)}<span className="rounded-full border border-neutral-200 px-3 py-1 text-xs text-neutral-500 dark:border-neutral-800 dark:text-neutral-400">≈ {item.estimatedMinutes} мин.</span></div></Link>); })}</div></section>}
          {outline.children?.length === 0 && outline.conspects?.length === 0 && <div className="mt-6 rounded-[2rem] border border-neutral-200 bg-white p-10 text-center text-neutral-500 shadow-soft dark:border-neutral-800 dark:bg-neutral-900">В этом разделе пока нет подразделов и конспектов.</div>}
        </>
      ) : null}
    </Layout>
  );
}
