// PublicProfilePage.jsx – публичный профиль пользователя с бейджами
import React, { useEffect, useState } from 'react';
import { useParams } from 'react-router-dom';
import Layout from '../components/Layout';
import { Card, Badge } from '../components/ui';
import { Github, Send, Globe2, MapPin, BookOpen, Trophy } from 'lucide-react';
import { api } from '../api/http';
import { getUserBadges } from '../api/badges';

export default function PublicProfilePage() {
  const { userId } = useParams();

  const [profile, setProfile] = useState(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState(null);

  const [badges, setBadges] = useState([]);
  const [badgesLoading, setBadgesLoading] = useState(false);

  // Загрузка публичного профиля
  useEffect(() => {
    (async () => {
      try {
        setLoading(true);
        setError(null);
        const { data } = await api.get(`/api/users/${userId}/public-profile`);
        setProfile(data);
      } catch (e) {
        console.error(e);
        setError('Профиль не найден');
      } finally {
        setLoading(false);
      }
    })();
  }, [userId]);

  // Загрузка бейджей пользователя
  useEffect(() => {
    if (!userId) return;
    (async () => {
      try {
        setBadgesLoading(true);
        const list = await getUserBadges(userId);
        setBadges(Array.isArray(list) ? list : []);
      } catch (e) {
        console.error('Failed to load user badges', e);
        setBadges([]);
      } finally {
        setBadgesLoading(false);
      }
    })();
  }, [userId]);

  return (
    <Layout>
      <div className="max-w-3xl mx-auto space-y-6">
        {loading && <div>Загрузка…</div>}

        {error && (
          <div className="text-sm text-red-500 bg-red-50 dark:bg-red-900/20 px-3 py-2 rounded-xl">
            {error}
          </div>
        )}

        {profile && (
          <>
            <Card className="p-6 flex gap-4">
              {/* Аватар */}
              <div className="shrink-0">
                {profile.avatarUrl ? (
                  <img
                    src={profile.avatarUrl}
                    alt={profile.displayName}
                    className="h-20 w-20 rounded-full object-cover border border-slate-300/60 dark:border-slate-700/60"
                  />
                ) : (
                  <div className="h-20 w-20 rounded-full bg-gradient-to-br from-fuchsia-500 to-pink-500 grid place-items-center text-white text-3xl font-semibold">
                    {profile.displayName?.[0]?.toUpperCase() || '?'}
                  </div>
                )}
              </div>

              <div className="flex-1 space-y-2 min-w-0">
                <div className="flex items-center gap-3 flex-wrap">
                  <h1 className="text-2xl font-semibold truncate">
                    {profile.displayName || profile.email}
                  </h1>

                  {typeof profile.rank === 'number' && (
                    <div className="inline-flex items-center gap-1 rounded-full bg-slate-100 dark:bg-slate-800 px-3 py-1 text-xs font-medium">
                      <Trophy size={14} />
                      <span>#{profile.rank} в топе</span>
                    </div>
                  )}
                </div>

                {profile.location && (
                  <div className="flex items-center gap-1 text-sm text-slate-500 dark:text-slate-400">
                    <MapPin size={14} />
                    <span>{profile.location}</span>
                  </div>
                )}

                {profile.education && (
                  <div className="flex items-center gap-1 text-sm text-slate-500 dark:text-slate-400">
                    <BookOpen size={14} />
                    <span>{profile.education}</span>
                  </div>
                )}

                {profile.bio && (
                  <p className="text-sm text-slate-600 dark:text-slate-300 mt-2 whitespace-pre-line">
                    {profile.bio}
                  </p>
                )}

                {/* Маленькая полоска бейджей сразу под именем */}
                {badges.length > 0 && (
                  <div className="flex flex-wrap items-center gap-1 mt-2">
                    {badges.map((b) => (
                      <span
                        key={b.id || b.name}
                        title={b.name}
                        className="inline-flex items-center justify-center"
                      >
                        {b.imageUrl && (
                          <img
                            src={b.imageUrl}
                            alt={b.name}
                            className="h-6 w-6 object-contain rounded border border-slate-200 dark:border-slate-700"
                          />
                        )}
                      </span>
                    ))}
                  </div>
                )}
              </div>
            </Card>

            <div className="grid gap-4 md:grid-cols-2">
              <Card className="p-4 space-y-2">
                <h2 className="font-semibold text-sm">Статистика</h2>
                <div className="text-sm space-y-1">
                  <div>
                    <span className="font-semibold">
                      {profile.solvedAssignments}
                    </span>{' '}
                    решённых заданий
                  </div>
                  <div>
                    <span className="font-semibold">
                      {profile.totalAttempts}
                    </span>{' '}
                    попыток отправки решений
                  </div>
                </div>
              </Card>

              <Card className="p-4 space-y-2">
                <h2 className="font-semibold text-sm">Ссылки</h2>
                <div className="flex flex-col gap-2 text-sm">
                  {(() => {
                    const hasGithub = !!profile.github?.trim();
                    const hasTelegram = !!profile.telegram?.trim();
                    const hasWebsite = !!profile.website?.trim();

                    return (
                      <>
                        {hasGithub && (
                          <a
                            href={profile.github}
                            target="_blank"
                            rel="noreferrer"
                            className="inline-flex items-center gap-2 text-slate-600 dark:text-slate-300 hover:text-brand-600"
                          >
                            <Github size={16} />
                            <span>GitHub</span>
                          </a>
                        )}

                        {hasTelegram && (
                          <a
                            href={
                              profile.telegram.startsWith('http')
                                ? profile.telegram
                                : `https://t.me/${profile.telegram.replace(
                                    /^@/,
                                    ''
                                  )}`
                            }
                            target="_blank"
                            rel="noreferrer"
                            className="inline-flex items-center gap-2 text-slate-600 dark:text-slate-300 hover:text-brand-600"
                          >
                            <Send size={16} />
                            <span>Telegram</span>
                          </a>
                        )}

                        {hasWebsite && (
                          <a
                            href={profile.website}
                            target="_blank"
                            rel="noreferrer"
                            className="inline-flex items-center gap-2 text-slate-600 dark:text-slate-300 hover:text-brand-600"
                          >
                            <Globe2 size={16} />
                            <span>Сайт / портфолио</span>
                          </a>
                        )}

                        {!hasGithub && !hasTelegram && !hasWebsite && (
                          <div className="text-xs text-slate-400">
                            Пользователь не добавил ссылки.
                          </div>
                        )}
                      </>
                    );
                  })()}
                </div>
              </Card>
            </div>

            {profile.skills && profile.skills.length > 0 && (
              <Card className="p-4 space-y-2">
                <h2 className="font-semibold text-sm">Навыки</h2>
                <div className="flex flex-wrap gap-2">
                  {profile.skills.map((s) => (
                    <Badge key={s}>{s}</Badge>
                  ))}
                </div>
              </Card>
            )}

            {(badgesLoading || badges.length > 0) && (
              <Card className="p-4 space-y-2">
                <h2 className="font-semibold text-sm">Бейджи</h2>
                {badgesLoading && (
                  <div className="text-xs text-slate-400">Загрузка…</div>
                )}
                {!badgesLoading && badges.length > 0 && (
                  <div className="flex flex-wrap items-center gap-2">
                    {badges.map((b) => (
                      <span
                        key={b.id || b.name}
                        title={b.name}
                        className="inline-flex items-center justify-center"
                      >
                        {b.imageUrl && (
                          <img
                            src={b.imageUrl}
                            alt={b.name}
                            className="h-8 w-8 object-contain rounded border border-slate-200 dark:border-slate-700"
                          />
                        )}
                      </span>
                    ))}
                  </div>
                )}
              </Card>
            )}
          </>
        )}
      </div>
    </Layout>
  );
}
