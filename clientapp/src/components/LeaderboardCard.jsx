// Полностью переработанная карточка топа студентов
// Бейджи теперь отображаются рядом с именем, SVG без фона,
// размер крупнее, корректно отображаются в розовой/тёмной теме.

import React from 'react';
import { useNavigate } from 'react-router-dom';
import { Trophy, Clock } from 'lucide-react';

export default function LeaderboardCard({ entry }) {
  const nav = useNavigate();

  const handleOpenProfile = () => {
    nav(`/users/${entry.userId}`);
  };

  // Градиенты для позиции
  const rankColors = {
    1: 'from-amber-400 to-yellow-500',
    2: 'from-slate-300 to-slate-100',
    3: 'from-orange-400 to-amber-500',
  };

  const rankBg =
    rankColors[entry.rank] ||
    'from-slate-200 to-slate-300 dark:from-slate-700 dark:to-slate-800';

  return (
    <button
      type="button"
      onClick={handleOpenProfile}
      className="group relative w-full rounded-2xl border border-slate-200/70 dark:border-slate-800/70
                 bg-[rgb(var(--card))] shadow-soft hover:shadow-lg 
                 hover:-translate-y-0.5 transition-all p-4 flex gap-4 text-left"
    >

      {/* Ранг */}
      <div className="absolute -top-3 -left-3">
        <div
          className={`inline-flex items-center gap-1 rounded-2xl bg-gradient-to-br ${rankBg}
                      px-3 py-1 text-xs font-semibold text-slate-900 shadow-md`}
        >
          <Trophy size={14} />
          <span>#{entry.rank}</span>
        </div>
      </div>

      {/* Аватар */}
      <div className="shrink-0">
        {entry.avatarUrl ? (
          <img
            src={entry.avatarUrl}
            alt={entry.displayName}
            className="h-14 w-14 rounded-full object-cover border border-slate-300/60 dark:border-slate-700/60"
          />
        ) : (
          <div className="h-14 w-14 rounded-full bg-gradient-to-br from-fuchsia-500 to-pink-500
                          grid place-items-center text-white text-xl font-semibold">
            {entry.displayName?.[0]?.toUpperCase() || '?'}
          </div>
        )}
      </div>

      {/* Основной блок */}
      <div className="flex-1 min-w-0 space-y-2">

        {/* Имя + бейджи на одной строке */}
        <div className="flex items-center flex-wrap gap-2 min-w-0">

          {/* Имя */}
          <span className="font-semibold text-lg truncate max-w-[65%]">
            {entry.displayName || entry.email}
          </span>

          {/* Бейджи рядом с именем */}
          {entry.badges && entry.badges.length > 0 && (
            <div className="flex items-center flex-wrap gap-1">
              {entry.badges.map((badge) => (
                <img
                  key={badge.id || badge.name}
                  title={badge.name}
                  src={badge.imageUrl}
                  alt={badge.name}
                  className="
                    h-6 w-6 object-contain
                    drop-shadow-md
                  "
                />
              ))}
            </div>
          )}
        </div>

        {/* Статистика */}
        <div className="flex flex-wrap gap-2 text-xs">

          {/* решённых задач */}
          <span className="inline-flex items-center gap-1 rounded-full
                           bg-[rgb(var(--muted))] px-2 py-1">
            <span className="font-semibold text-slate-700 dark:text-slate-200">
              {entry.solvedAssignments}
            </span>
            <span className="text-slate-500 dark:text-slate-400">
              решённых&nbsp;заданий
            </span>
          </span>

          {/* попыток */}
          {typeof entry.totalAttempts === 'number' && (
            <span className="inline-flex items-center gap-1 rounded-full
                             bg-[rgb(var(--muted))] px-2 py-1">
              <span className="font-semibold text-slate-700 dark:text-slate-200">
                {entry.totalAttempts}
              </span>
              <span className="text-slate-500 dark:text-slate-400">
                попыток
              </span>
            </span>
          )}

          {/* Активность */}
          {entry.lastSubmitAt && (
            <span className="inline-flex items-center gap-1 rounded-full
                             bg-[rgb(var(--muted))] px-2 py-1">
              <Clock size={12} />
              <span className="text-slate-500 dark:text-slate-400">
                Активен:{' '}
                {new Date(entry.lastSubmitAt).toLocaleDateString(undefined, {
                  timeZone: Intl.DateTimeFormat().resolvedOptions().timeZone,
                })}
              </span>
            </span>
          )}
        </div>
      </div>
    </button>
  );
}
