import React, { useEffect, useMemo, useState } from 'react';
import { Link, useParams } from 'react-router-dom';
import { ArrowLeft, Loader2 } from 'lucide-react';
import RichConspectRenderer from '../components/RichConspectRenderer';
import { getCourseConspects, getLearningConspect } from '../api/learning';

function parseBadges(raw) { try { const value = typeof raw === 'string' ? JSON.parse(raw) : raw; return Array.isArray(value) ? value : []; } catch { return []; } }

export default function ConspectPage() {
  const { courseSlug, slug } = useParams();
  const [conspects, setConspects] = useState([]);
  const [details, setDetails] = useState(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');

  useEffect(() => {
    let cancelled = false;
    async function load() {
      setLoading(true); setError('');
      try {
        const [list, loadedDetails] = await Promise.all([courseSlug ? getCourseConspects(courseSlug) : Promise.resolve([]), getLearningConspect(slug, courseSlug ? { courseSlug } : {})]);
        if (cancelled) return;
        setConspects(Array.isArray(list) ? list : []);
        setDetails(loadedDetails);
      } catch (e) { if (!cancelled) setError(e?.userMessage || e?.message || 'Не удалось открыть конспект.'); }
      finally { if (!cancelled) setLoading(false); }
    }
    load();
    return () => { cancelled = true; };
  }, [courseSlug, slug]);

  const currentSlug = details?.conspect?.slug || slug;
  const backHref = courseSlug ? `/learning/${courseSlug}` : '/';
  const title = details?.conspect?.title || 'Конспект';
  const badges = useMemo(() => parseBadges(details?.conspect?.badgesJson), [details]);

  return (
    <>
      <div className="min-h-[calc(100vh-57px)] bg-neutral-50 dark:bg-neutral-950"><div className="container-app py-6 lg:py-8"><div className="grid gap-6 lg:grid-cols-[320px_minmax(0,1fr)]">
        <aside className="space-y-4 lg:sticky lg:top-[77px] lg:self-start">
          <Link to={backHref} className="inline-flex items-center gap-2 text-sm font-semibold text-brand-700 hover:underline dark:text-brand-300"><ArrowLeft size={16} />Назад к разделу</Link>
          <div className="rounded-[2rem] border border-neutral-200/80 bg-white p-5 shadow-soft dark:border-neutral-800 dark:bg-neutral-900"><div className="text-sm font-semibold text-brand-700 dark:text-brand-300">Конспект</div><h2 className="mt-1 text-2xl font-bold tracking-tight">{title}</h2>{badges.length > 0 && <div className="mt-4 flex flex-wrap gap-2">{badges.map((badge) => <span key={badge} className="rounded-full border border-neutral-200 px-3 py-1 text-xs text-neutral-500 dark:border-neutral-800 dark:text-neutral-400">{badge}</span>)}</div>}</div>
          {conspects.length > 0 && <div className="rounded-[2rem] border border-neutral-200/80 bg-white p-5 shadow-soft dark:border-neutral-800 dark:bg-neutral-900"><div className="mb-3 font-semibold">Конспекты раздела</div><div className="space-y-2">{conspects.map((item) => (<Link key={item.id} to={`/learning/${courseSlug}/conspects/${item.slug}`} className={`block rounded-2xl border p-3 text-sm transition ${item.slug === currentSlug ? 'border-brand-400 bg-brand-50 dark:bg-brand-950/20' : 'border-neutral-200 bg-neutral-50 hover:border-brand-300 dark:border-neutral-800 dark:bg-neutral-950'}`}><div className="font-semibold">{item.title}</div>{item.lead && <div className="mt-1 line-clamp-2 text-neutral-500 dark:text-neutral-400">{item.lead}</div>}</Link>))}</div></div>}
        </aside>
        <main className="min-w-0">
          {error && <div className="mb-4 rounded-3xl border border-red-200 bg-red-50 p-4 text-sm text-red-800 dark:border-red-900 dark:bg-red-950/20 dark:text-red-100">{error}</div>}
          {loading ? <div className="rounded-[2rem] border border-neutral-200 bg-white p-10 text-center shadow-soft dark:border-neutral-800 dark:bg-neutral-900"><Loader2 className="mx-auto animate-spin text-brand-600" /><div className="mt-3 text-neutral-600 dark:text-neutral-300">Загружаю конспект...</div></div> : details ? <RichConspectRenderer details={details} /> : null}
        </main>
      </div></div></div>
    </>
  );
}
