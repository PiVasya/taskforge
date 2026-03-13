import React, { useEffect, useMemo, useState } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import Layout from '../components/Layout';
import { Card, Badge, Button, Input } from '../components/ui';
import { Newspaper, ArrowRight, Search, Flame, BookOpen, Trophy, Pin } from 'lucide-react';
import { getUpdatesIndex } from '../api/updates';
import { getProfile } from '../api/profile';

function fmtDate(iso) {
  try {
    const d = new Date(iso);
    if (Number.isNaN(d.getTime())) return iso || '';
    return d.toLocaleDateString('ru-RU', { year: 'numeric', month: 'short', day: '2-digit' });
  } catch {
    return iso || '';
  }
}

export default function NewsPage() {
  const nav = useNavigate();

  const [loading, setLoading] = useState(true);
  const [error, setError] = useState(null);
  const [items, setItems] = useState([]);

  const [profile, setProfile] = useState(null);

  const [q, setQ] = useState('');
  const [tag, setTag] = useState('');

  useEffect(() => {
    (async () => {
      try {
        setLoading(true);
        setError(null);
        const [idx, prof] = await Promise.all([
          getUpdatesIndex().catch(() => []),
          getProfile().catch(() => null),
        ]);
        setItems(idx);
        setProfile(prof);
      } catch (e) {
        setError('Не удалось загрузить ленту');
      } finally {
        setLoading(false);
      }
    })();
  }, []);

  const tags = useMemo(() => {
    const set = new Set();
    for (const it of items) {
      const t = it?.tags;
      if (Array.isArray(t)) t.forEach((x) => x && set.add(String(x)));
    }
    return [''].concat([...set].sort((a, b) => a.localeCompare(b, 'ru')));
  }, [items]);

  const filtered = useMemo(() => {
    const qq = q.trim().toLowerCase();
    return items.filter((it) => {
      if (tag) {
        const t = Array.isArray(it?.tags) ? it.tags.map(String) : [];
        if (!t.includes(tag)) return false;
      }
      if (!qq) return true;
      const hay = `${it?.title || ''} ${it?.summary || ''} ${(it?.tags || []).join(' ')}`.toLowerCase();
      return hay.includes(qq);
    });
  }, [items, q, tag]);

  const pinned = filtered.filter((x) => x?.pinned);
  const rest = filtered.filter((x) => !x?.pinned);

  const score = profile?.score ?? profile?.rating ?? profile?.points;

  // Хотим показывать ФИО, а не почту.
  const fio = `${profile?.lastName || ''} ${profile?.firstName || ''}`.trim();
  const displayName = fio || profile?.displayName || profile?.username || profile?.email || '';

  return (
    <Layout>
      <div className="flex flex-col gap-6">
        {/* Hero */}
        <Card className="relative overflow-hidden">
          <div className="flex flex-col gap-4 sm:flex-row sm:items-center sm:justify-between">
            <div className="min-w-0">
              <div className="flex items-center gap-2 text-sm text-neutral-500 dark:text-neutral-400">
                <Newspaper size={16} />
                <span>Лента обновлений</span>
              </div>
              <div className="mt-1 text-2xl font-semibold leading-tight break-words sm:text-3xl">
                {displayName ? `Привет, ${displayName}!` : 'TaskForge'}
              </div>
              <div className="mt-2 text-sm text-neutral-600 dark:text-neutral-300 max-w-2xl">
                Новости платформы, новые курсы и важные объявления — всё в одном месте.
              </div>

              <div className="mt-4 flex flex-wrap gap-2">
                {score != null && (
                  <Badge variant="outline">
                    <Flame size={14} className="mr-1" />
                    Рейтинг: <b className="ml-1">{score}</b>
                  </Badge>
                )}
              </div>
            </div>

            <div className="flex flex-col gap-2 sm:min-w-[220px] sm:max-w-[240px]">
              <Button className="w-full" onClick={() => nav('/courses')}>
                <BookOpen size={18} />
                <span className="ml-2">Открыть курсы</span>
              </Button>
              <Button variant="outline" className="w-full" onClick={() => nav('/leaderboard')}>
                <Trophy size={18} />
                <span className="ml-2">Топ студентов</span>
              </Button>
            </div>
          </div>
        </Card>

        {/* Filters */}
        <div className="flex flex-col gap-2 sm:flex-row sm:items-center sm:justify-between">
          <div className="flex flex-col sm:flex-row gap-2 sm:items-center">
            <div className="relative">
              <Search size={16} className="absolute left-3 top-1/2 -translate-y-1/2 opacity-60" />
              <Input
                value={q}
                onChange={(e) => setQ(e.target.value)}
                placeholder="Поиск по новостям…"
                className="pl-9 w-full "
              />
            </div>

            <select
              className="select w-full sm:w-[180px]"
              value={tag}
              onChange={(e) => setTag(e.target.value)}
              title="Фильтр по тегу"
            >
              {tags.map((t) => (
                <option key={t || 'all'} value={t}>
                  {t ? `#${t}` : 'Все'}
                </option>
              ))}
            </select>
          </div>

          <div className="text-sm text-neutral-500 dark:text-neutral-400">
            {filtered.length} пост(ов)
          </div>
        </div>

        {/* Content */}
        {loading && <div className="text-neutral-500">Загрузка…</div>}
        {error && <div className="text-red-500">{error}</div>}

        {!loading && !error && filtered.length === 0 && (
          <Card>
            <div className="text-neutral-700 dark:text-neutral-200">Пока нет новостей по выбранному фильтру.</div>
            <div className="text-sm text-neutral-500 mt-2">Попробуйте снять фильтр или зайти позже.</div>
          </Card>
        )}

        {!loading && !error && pinned.length > 0 && (
          <div className="flex flex-col gap-3">
            <div className="text-sm font-semibold opacity-80">Закреплено</div>
            {pinned.map((it) => (
              <Card key={it.id} className="border border-brand-600/30">
                <div className="flex flex-col gap-2">
                  <div className="flex items-start justify-between gap-4">
                    <div className="min-w-0">
                      <div className="text-lg font-semibold truncate">{it.title}</div>
                      <div className="text-sm text-neutral-500 dark:text-neutral-400">{fmtDate(it.date)}</div>
                    </div>
                    <Badge variant="outline">
                      <Pin size={14} className="mr-1" />
                      Закреп
                    </Badge>
                  </div>

                  {it.summary && <div className="text-neutral-700 dark:text-neutral-200">{it.summary}</div>}

                  <div className="flex flex-wrap gap-2 items-center justify-between">
                    <div className="flex flex-wrap gap-2">
                      {(it.tags || []).map((t) => (
                        <Badge key={t} variant="secondary">#{t}</Badge>
                      ))}
                    </div>
                    <Link to={`/news/${it.id}`} className="btn-outline">
                      Читать <ArrowRight size={18} className="ml-2" />
                    </Link>
                  </div>
                </div>
              </Card>
            ))}
          </div>
        )}

        {!loading && !error && rest.length > 0 && (
          <div className="grid grid-cols-1 lg:grid-cols-2 gap-4">
            {rest.map((it) => (
              <Card key={it.id} className="hover:shadow-soft transition-shadow">
                <div className="flex flex-col gap-2">
                  <div className="flex items-start justify-between gap-4">
                    <div className="min-w-0">
                      <div className="text-lg font-semibold truncate">{it.title}</div>
                      <div className="text-sm text-neutral-500 dark:text-neutral-400">{fmtDate(it.date)}</div>
                    </div>
                  </div>

                  {it.summary && <div className="text-neutral-700 dark:text-neutral-200">{it.summary}</div>}

                  <div className="flex flex-wrap gap-2 items-center justify-between">
                    <div className="flex flex-wrap gap-2">
                      {(it.tags || []).slice(0, 4).map((t) => (
                        <Badge key={t} variant="secondary">#{t}</Badge>
                      ))}
                      {(it.tags || []).length > 4 && <Badge variant="outline">+{(it.tags || []).length - 4}</Badge>}
                    </div>
                    <Link to={`/news/${it.id}`} className="btn-outline">
                      Читать <ArrowRight size={18} className="ml-2" />
                    </Link>
                  </div>
                </div>
              </Card>
            ))}
          </div>
        )}
      </div>
    </Layout>
  );
}
