import React, { useEffect, useState } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import Layout from '../components/Layout';
import { Badge, Button } from '../components/ui';
import {
  ArrowRight,
  BookOpen,
  Flame,
  Gamepad2,
  Newspaper,
  Server,
  Sparkles,
  Trophy,
  Zap,
} from 'lucide-react';
import { getUpdatesIndex } from '../api/updates';
import { getProfile } from '../api/profile';

const toneStyles = {
  pink: {
    glow: 'from-pink-500/20 via-fuchsia-500/8 to-violet-500/12',
    line: 'bg-gradient-to-r from-pink-500 via-fuchsia-500 to-violet-500',
    label: 'text-pink-600 dark:text-pink-300',
    icon: 'bg-pink-500/12 text-pink-600 dark:text-pink-300',
  },
  emerald: {
    glow: 'from-emerald-500/18 via-lime-500/7 to-cyan-500/12',
    line: 'bg-gradient-to-r from-emerald-500 via-lime-400 to-cyan-500',
    label: 'text-emerald-700 dark:text-emerald-300',
    icon: 'bg-emerald-500/12 text-emerald-700 dark:text-emerald-300',
  },
};

function UpdateCard({ item, index }) {
  const tone = toneStyles[item.tone] || toneStyles.pink;

  return (
    <article className="group relative overflow-hidden rounded-[30px] border border-black/5 bg-white/70 p-6 shadow-soft backdrop-blur-xl transition duration-300 hover:-translate-y-1 hover:shadow-2xl dark:border-white/10 dark:bg-white/[0.035] sm:p-8 lg:p-10">
      <div className={`pointer-events-none absolute inset-0 bg-gradient-to-br ${tone.glow}`} />
      <div className={`absolute inset-x-0 bottom-0 h-1 ${tone.line}`} />

      <div className="relative z-10 grid gap-7 lg:grid-cols-[minmax(0,1fr)_auto] lg:items-end">
        <div>
          <div className="flex items-start gap-4">
            <span className={`inline-flex h-12 w-12 shrink-0 items-center justify-center rounded-2xl ${tone.icon}`}>
              {item.tone === 'emerald' ? <Gamepad2 size={22} /> : <Zap size={22} />}
            </span>
            <div>
              <div className={`text-xs font-bold uppercase tracking-[0.22em] ${tone.label}`}>
                {item.eyebrow || 'Большое обновление'}
              </div>
              {item.featuredLabel ? (
                <div className="mt-1 text-xs text-neutral-500 dark:text-neutral-400">{item.featuredLabel}</div>
              ) : null}
            </div>
          </div>

          <div className="mt-6 text-xs font-semibold text-neutral-400 dark:text-neutral-500">
            ОБНОВЛЕНИЕ {String(index + 1).padStart(2, '0')}
          </div>
          <h2 className="mt-2 text-2xl font-semibold leading-tight tracking-tight sm:text-4xl">{item.title}</h2>
          {item.summary ? (
            <p className="mt-4 max-w-4xl text-sm leading-7 text-neutral-700 dark:text-neutral-200 sm:text-base sm:leading-8">
              {item.summary}
            </p>
          ) : null}

          {(item.highlights || []).length > 0 ? (
            <div className="mt-6 grid gap-2 sm:grid-cols-2">
              {item.highlights.map((highlight) => (
                <div
                  key={highlight}
                  className="rounded-2xl border border-black/5 bg-white/45 px-4 py-3 text-sm leading-6 text-neutral-700 dark:border-white/10 dark:bg-black/15 dark:text-neutral-200"
                >
                  {highlight}
                </div>
              ))}
            </div>
          ) : null}

          <div className="mt-6 flex flex-wrap gap-2">
            {(item.tags || []).map((tag) => (
              <Badge key={tag} variant="secondary">#{tag}</Badge>
            ))}
          </div>
        </div>

        <Link to={`/news/${item.id}`} className="btn-primary self-start lg:self-end">
          Читать полностью
          <ArrowRight size={18} />
        </Link>
      </div>
    </article>
  );
}

export default function NewsPage() {
  const nav = useNavigate();
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState(null);
  const [items, setItems] = useState([]);
  const [profile, setProfile] = useState(null);

  useEffect(() => {
    (async () => {
      try {
        setLoading(true);
        setError(null);
        const [index, currentProfile] = await Promise.all([
          getUpdatesIndex(),
          getProfile().catch(() => null),
        ]);
        setItems(index);
        setProfile(currentProfile);
      } catch {
        setError('Не удалось загрузить ленту');
      } finally {
        setLoading(false);
      }
    })();
  }, []);

  const score = profile?.score ?? profile?.rating ?? profile?.points;
  const fio = `${profile?.lastName || ''} ${profile?.firstName || ''}`.trim();
  const displayName = fio || profile?.displayName || profile?.login || profile?.username || profile?.email || '';

  return (
    <Layout>
      <div className="mx-auto flex max-w-[1280px] flex-col gap-6 pb-10">
        <section className="relative overflow-hidden rounded-[32px] border border-black/5 bg-white/70 p-6 shadow-soft backdrop-blur-xl dark:border-white/10 dark:bg-white/[0.035] sm:p-9 lg:p-11">
          <div className="pointer-events-none absolute inset-0 bg-[radial-gradient(circle_at_15%_0%,rgba(var(--accent)/0.22),transparent_38%),radial-gradient(circle_at_95%_25%,rgba(var(--accent2)/0.18),transparent_35%)]" />
          <div className="relative z-10 grid gap-7 lg:grid-cols-[1fr_auto] lg:items-end">
            <div className="max-w-4xl">
              <div className="flex items-center gap-2 text-sm font-medium text-neutral-500 dark:text-neutral-300">
                <Newspaper size={17} />
                <span>Лента</span>
              </div>
              <div className="mt-4 inline-flex items-center gap-2 rounded-full border border-black/5 bg-white/55 px-3 py-1.5 text-xs font-semibold dark:border-white/10 dark:bg-black/15">
                <Sparkles size={14} className="text-pink-500" />
                Главные изменения TaskForge
              </div>
              <h1 className="mt-4 text-3xl font-semibold leading-[1.05] tracking-tight sm:text-5xl lg:text-6xl">
                {displayName ? `${displayName}, история развития TaskForge` : 'История больших обновлений TaskForge'}
              </h1>
              <p className="mt-5 max-w-3xl text-sm leading-7 text-neutral-600 dark:text-neutral-300 sm:text-lg sm:leading-8">
                Здесь собраны только крупные изменения платформы. Без технического шума, дат и мелких правок — только понятное описание того, что стало доступно пользователям.
              </p>
              {score != null ? (
                <div className="mt-6 flex flex-wrap gap-2">
                  <Badge variant="outline" className="!px-3 !py-2">
                    <Flame size={15} className="mr-1" />
                    Ваш рейтинг: <b className="ml-1">{score}</b>
                  </Badge>
                  <Badge variant="outline" className="!px-3 !py-2">
                    <Server size={15} className="mr-1" />
                    mc.taskforge.by
                  </Badge>
                </div>
              ) : null}
            </div>

            <div className="grid gap-2 sm:grid-cols-2 lg:min-w-[250px] lg:grid-cols-1">
              <Button className="w-full" onClick={() => nav('/courses')}>
                <BookOpen size={18} />
                <span>Открыть курсы</span>
              </Button>
              <Button variant="outline" className="w-full" onClick={() => nav('/leaderboard')}>
                <Trophy size={18} />
                <span>Топ студентов</span>
              </Button>
            </div>
          </div>
        </section>

        {loading ? <div className="text-neutral-500">Загрузка…</div> : null}
        {error ? <div className="text-red-500">{error}</div> : null}

        {!loading && !error && items.length === 0 ? (
          <div className="rounded-3xl border border-black/5 bg-white/60 p-6 dark:border-white/10 dark:bg-white/[0.035]">
            В ленте пока нет обновлений.
          </div>
        ) : null}

        {!loading && !error && items.length > 0 ? (
          <div className="grid gap-6">
            {items.map((item, index) => (
              <UpdateCard key={item.id} item={item} index={index} />
            ))}
          </div>
        ) : null}
      </div>
    </Layout>
  );
}
