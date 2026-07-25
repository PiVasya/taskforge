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
  Trophy,
  Wrench,
} from 'lucide-react';
import { getUpdatesIndex } from '../api/updates';
import { getProfile } from '../api/profile';
import { useAuth } from '../auth/AuthContext';

function UpdateCard({ item, index }) {
  const isMinecraft = String(item.id || '').startsWith('minecraft-');
  const Icon = isMinecraft ? Gamepad2 : Wrench;

  return (
    <article className="changelog-card group">
      <div className="changelog-card__line" />

      <div className="relative z-10 grid gap-7 lg:grid-cols-[minmax(0,1fr)_auto] lg:items-end">
        <div>
          <div className="flex items-start gap-4">
            <span className="changelog-icon">
              <Icon size={22} />
            </span>
            <div>
              <div className="changelog-kicker">
                {item.eyebrow || 'Обновление TaskForge'}
              </div>
              {item.featuredLabel ? (
                <div className="mt-1 text-xs text-[rgb(var(--text-muted))]">{item.featuredLabel}</div>
              ) : null}
            </div>
          </div>

          <div className="mt-6 text-xs font-semibold text-[rgb(var(--text-muted))] opacity-75">
            ОБНОВЛЕНИЕ {String(index + 1).padStart(2, '0')}
          </div>
          <h2 className="mt-2 text-2xl font-semibold leading-tight tracking-tight sm:text-4xl">{item.title}</h2>
          {item.summary ? (
            <p className="mt-4 max-w-4xl text-sm leading-7 text-[rgb(var(--text-muted))] sm:text-base sm:leading-8">
              {item.summary}
            </p>
          ) : null}

          {(item.highlights || []).length > 0 ? (
            <div className="mt-6 grid gap-2 sm:grid-cols-2">
              {item.highlights.map((highlight) => (
                <div key={highlight} className="changelog-highlight">
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
          Подробнее
          <ArrowRight size={18} />
        </Link>
      </div>
    </article>
  );
}

export default function NewsPage() {
  const nav = useNavigate();
  const { access } = useAuth();
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
          access ? getProfile().catch(() => null) : Promise.resolve(null),
        ]);
        setItems(index);
        setProfile(currentProfile);
      } catch {
        setError('Не удалось загрузить ленту');
      } finally {
        setLoading(false);
      }
    })();
  }, [access]);

  const score = profile?.score ?? profile?.rating ?? profile?.points;

  return (
    <Layout>
      <div className="mx-auto flex max-w-[1280px] flex-col gap-6 pb-10">
        <section className="changelog-hero">
          <div className="relative z-10 grid gap-7 lg:grid-cols-[1fr_auto] lg:items-end">
            <div className="max-w-4xl">
              <div className="flex items-center gap-2 text-sm font-medium text-[rgb(var(--text-muted))]">
                <Newspaper size={17} />
                <span>Лента</span>
              </div>
              <div className="changelog-pill mt-4">
                Крупные обновления TaskForge
              </div>
              <h1 className="mt-4 text-3xl font-semibold leading-[1.05] tracking-tight sm:text-5xl lg:text-6xl">
                Что изменилось в TaskForge
              </h1>
              <p className="mt-5 max-w-3xl text-sm leading-7 text-[rgb(var(--text-muted))] sm:text-lg sm:leading-8">
                Здесь собраны главные обновления платформы: без списка мелких правок и лишней технической информации — только новые возможности и заметные изменения для пользователей.
              </p>
              <div className="mt-6 flex flex-wrap gap-2">
                {score != null ? (
                  <Badge variant="outline" className="!px-3 !py-2">
                    <Flame size={15} className="mr-1" />
                    Ваш рейтинг: <b className="ml-1">{score}</b>
                  </Badge>
                ) : null}
                <Badge variant="outline" className="!px-3 !py-2">
                  <Server size={15} className="mr-1" />
                  mc.taskforge.by
                </Badge>
              </div>
            </div>

            <div className="grid gap-2 sm:grid-cols-2 lg:min-w-[250px] lg:grid-cols-1">
              <Button className="w-full" onClick={() => nav(access ? '/courses' : '/register')}>
                <BookOpen size={18} />
                <span>{access ? 'Открыть курсы' : 'Создать аккаунт'}</span>
              </Button>
              <Button variant="outline" className="w-full" onClick={() => nav(access ? '/leaderboard' : '/login')}>
                <Trophy size={18} />
                <span>{access ? 'Топ студентов' : 'Войти'}</span>
              </Button>
            </div>
          </div>
        </section>

        {loading ? <div className="text-[rgb(var(--text-muted))]">Загрузка…</div> : null}
        {error ? <div className="text-red-500">{error}</div> : null}

        {!loading && !error && items.length === 0 ? (
          <div className="changelog-empty">В ленте пока нет обновлений.</div>
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
