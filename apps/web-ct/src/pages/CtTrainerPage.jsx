import React, { useEffect, useMemo, useState } from 'react';
import { Link, useParams, useSearchParams } from 'react-router-dom';
import { BookOpen, Loader2, RefreshCcw, Search, Settings, Sparkles } from 'lucide-react';
import RichConspectRenderer from '../components/RichConspectRenderer';
import { getLearningConspect, getLearningConspects } from '../api/learning';

const fallbackContent = {
  schemaVersion: 1,
  layout: 'tabs',
  startTabId: 'theory',
  hero: {
    eyebrow: 'Русский язык · ЦТ/ЦЭ',
    title: 'A1. Гласная в корне слова',
    description: 'Локальный fallback-конспект. Backend может быть недоступен, но теория всё равно открывается.',
    stats: [
      { label: 'Формат', value: 'конспект' },
      { label: 'Блок', value: 'A1' },
      { label: 'Источник', value: 'fallback' },
    ],
  },
  tabs: [
    {
      id: 'theory',
      title: 'Теория',
      blocks: [
        {
          id: 'core-rule',
          type: 'rule-card',
          title: 'Три типа гласных в корне',
          items: [
            'Проверяемые: подбираем однокоренное слово с ударением.',
            'Непроверяемые: запоминаем словарное написание.',
            'Чередующиеся: применяем специальное правило корня.',
          ],
        },
        {
          type: 'warning',
          title: 'Не путай проверку и чередование',
          text: 'Если корень чередующийся, ударение часто не помогает. Нужно применить правило: лаг/лож, гар/гор, кас/кос и т.д.',
        },
      ],
    },
    {
      id: 'algorithm',
      title: 'Алгоритм',
      blocks: [
        {
          type: 'steps',
          title: 'Как решать',
          items: [
            'Выдели корень.',
            'Проверь, не является ли он чередующимся.',
            'Если чередования нет — подбери проверочное слово.',
            'Если проверить нельзя — вспоминай словарь.',
          ],
        },
      ],
    },
    {
      id: 'practice',
      title: 'Тренировка',
      blocks: [
        {
          type: 'practice-intro',
          title: 'Переход к заданиям',
          text: 'Кнопка ведёт на подборку заданий по A1.',
          cta: { label: 'Сделать задания A1', href: '/tasks?sectionCode=A1&type=vowel-choice' },
        },
      ],
    },
  ],
};

const fallbackDetails = {
  conspect: {
    id: 'fallback-a1',
    slug: 'a1-orthography-vowel-root',
    title: 'A1. Орфография: гласная в корне слова',
    lead: 'Fallback-версия для разработки интерфейса.',
    sectionCode: 'A1',
    estimatedMinutes: 12,
  },
  contentJson: JSON.stringify(fallbackContent),
  taskLinks: [
    {
      id: 'fallback-task-link',
      title: 'Мини-задания по A1',
      buttonText: 'Сделать задания A1',
      groupTitle: 'После конспекта',
      taskFilterJson: JSON.stringify({ subjectCode: 'russian', examCode: 'ct-ce-2026', sectionCode: 'A1', type: 'vowel-choice' }),
    },
  ],
};

function pickBadges(raw) {
  try {
    const data = typeof raw === 'string' ? JSON.parse(raw) : raw;
    return Array.isArray(data) ? data : [];
  } catch {
    return [];
  }
}

export default function CtTrainerPage() {
  const { slug } = useParams();
  const [searchParams] = useSearchParams();
  const sectionCode = searchParams.get('sectionCode') || 'A1';
  const [conspects, setConspects] = useState([]);
  const [details, setDetails] = useState(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  const [query, setQuery] = useState('');

  useEffect(() => {
    let cancelled = false;

    async function load() {
      setLoading(true);
      setError('');
      try {
        const list = await getLearningConspects({ subjectCode: 'russian', examCode: 'ct-ce-2026', sectionCode });
        if (cancelled) return;
        setConspects(list || []);

        const targetSlug = slug || list?.[0]?.slug || 'a1-orthography-vowel-root';
        const loadedDetails = await getLearningConspect(targetSlug, { courseSlug: list?.[0]?.courseSlug });
        if (!cancelled) setDetails(loadedDetails);
      } catch (e) {
        if (!cancelled) {
          setError(e?.userMessage || e?.message || 'Не удалось загрузить конспект с сервиса. Показана fallback-версия.');
          setDetails(fallbackDetails);
          setConspects([fallbackDetails.conspect]);
        }
      } finally {
        if (!cancelled) setLoading(false);
      }
    }

    load();
    return () => {
      cancelled = true;
    };
  }, [slug, sectionCode]);

  const filteredConspects = useMemo(() => {
    const q = query.trim().toLowerCase();
    if (!q) return conspects;
    return conspects.filter((item) => `${item.title || ''} ${item.subtitle || ''} ${item.lead || ''}`.toLowerCase().includes(q));
  }, [conspects, query]);

  return (
    <>
      <div className="min-h-[calc(100vh-57px)] bg-neutral-50 dark:bg-neutral-950">
        <div className="container-app py-6 lg:py-8">
          <div className="grid gap-6 lg:grid-cols-[320px_minmax(0,1fr)]">
            <aside className="space-y-4 lg:sticky lg:top-[77px] lg:self-start">
              <div className="rounded-[2rem] border border-neutral-200/80 dark:border-neutral-800 bg-white dark:bg-neutral-900 shadow-soft p-5">
                <div className="mb-4 flex items-center justify-between gap-3">
                  <div>
                    <div className="text-sm font-semibold text-brand-700 dark:text-brand-300">TaskForge CT</div>
                    <h2 className="text-2xl font-bold tracking-tight">Конспекты</h2>
                  </div>
                  <BookOpen className="text-brand-600" />
                </div>

                <label className="relative block">
                  <Search size={17} className="absolute left-3 top-1/2 -translate-y-1/2 text-neutral-400" />
                  <input
                    value={query}
                    onChange={(e) => setQuery(e.target.value)}
                    placeholder="Найти конспект"
                    className="w-full rounded-2xl border border-neutral-200 dark:border-neutral-800 bg-neutral-50 dark:bg-neutral-950 py-2.5 pl-10 pr-3 outline-none focus:border-brand-400"
                  />
                </label>

                <div className="mt-4 space-y-2">
                  {filteredConspects.map((item) => {
                    const active = item.slug === details?.conspect?.slug;
                    const badges = pickBadges(item.badgesJson);
                    return (
                      <Link
                        key={item.id || item.slug}
                        to={`/conspects/${item.slug}`}
                        className={`block rounded-2xl border p-4 transition ${active ? 'border-brand-400 bg-brand-50 dark:bg-brand-900/20' : 'border-neutral-200 dark:border-neutral-800 bg-neutral-50 dark:bg-neutral-950 hover:border-brand-300'}`}
                      >
                        <div className="font-semibold leading-6">{item.title}</div>
                        {item.lead && <p className="mt-1 line-clamp-2 text-sm text-neutral-500 dark:text-neutral-400">{item.lead}</p>}
                        {badges.length > 0 && (
                          <div className="mt-3 flex flex-wrap gap-1.5">
                            {badges.slice(0, 4).map((badge) => (
                              <span key={badge} className="rounded-full bg-white dark:bg-neutral-900 border border-neutral-200 dark:border-neutral-800 px-2 py-0.5 text-xs">
                                {badge}
                              </span>
                            ))}
                          </div>
                        )}
                      </Link>
                    );
                  })}
                </div>
              </div>

              <div className="rounded-[2rem] border border-neutral-200/80 dark:border-neutral-800 bg-white dark:bg-neutral-900 shadow-soft p-5">
                <div className="flex items-start gap-3">
                  <Sparkles size={20} className="mt-1 text-brand-600" />
                  <div>
                    <h3 className="font-semibold">Мега-идея</h3>
                    <p className="mt-1 text-sm leading-6 text-neutral-600 dark:text-neutral-300">
                      Конспект теперь хранится как JSON-блоки: вкладки, правила, таблицы, словари, слова по годам и CTA к заданиям.
                    </p>
                    <Link to="/admin/conspects" className="btn-outline mt-3 inline-flex items-center gap-2 text-sm">
                      <Settings size={16} />
                      Редактор
                    </Link>
                  </div>
                </div>
              </div>
            </aside>

            <main className="min-w-0">
              {loading && (
                <div className="rounded-[2rem] border border-neutral-200 dark:border-neutral-800 bg-white dark:bg-neutral-900 p-10 text-center shadow-soft">
                  <Loader2 className="mx-auto animate-spin text-brand-600" />
                  <div className="mt-3 text-neutral-600 dark:text-neutral-300">Загружаю конспект...</div>
                </div>
              )}

              {!loading && error && (
                <div className="mb-4 rounded-3xl border border-amber-200 dark:border-amber-900 bg-amber-50 dark:bg-amber-950/20 p-4 text-sm leading-6 text-amber-900 dark:text-amber-100">
                  <div className="flex items-center gap-2 font-semibold"><RefreshCcw size={16} /> Backend недоступен или ещё без миграции</div>
                  <div className="mt-1">{error}</div>
                </div>
              )}

              {!loading && details && <RichConspectRenderer details={details} />}
            </main>
          </div>
        </div>
      </div>
    </>
  );
}
