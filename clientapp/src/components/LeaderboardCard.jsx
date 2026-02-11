// Компонент карточки участника топа.
// Показывает аватар, имя, статистику и бейджи (иконки рядом с именем).

import React from 'react';
import { useNavigate } from 'react-router-dom';
import { Trophy, MapPin, BookOpen, Clock } from 'lucide-react';

export default function LeaderboardCard({ entry }) {
  const nav = useNavigate();
  const handleOpenProfile = () => {
    nav(`/users/${entry.userId}`);
  };

  // градиенты для топ-3 мест
  const rankColors = {
    1: 'from-amber-400 to-yellow-500',
    2: 'from-neutral-300 to-neutral-100',
    3: 'from-orange-400 to-amber-500',
  };
  const rankBg =
    rankColors[entry.rank] ||
    'from-neutral-200 to-neutral-300 dark:from-neutral-700 dark:to-neutral-800';

  const hasBadges = Array.isArray(entry.badges) && entry.badges.length > 0;

  return (
    <button
      type="button"
      onClick={handleOpenProfile}
      className="group relative w-full text-left rounded-2xl border border-neutral-200/70 dark:border-neutral-800/70 bg-[rgb(var(--card))] shadow-soft hover:shadow-lg hover:-translate-y-0.5 transition-all p-4 flex gap-4 cursor-pointer"
    >
      {/* Ранг */}
      <div className="absolute -top-3 left-0">
        <div
          className={`inline-flex items-center gap-1 rounded-2xl bg-gradient-to-br ${rankBg} px-3 py-1 text-xs font-semibold text-neutral-900 shadow-md`}
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
            className="h-14 w-14 rounded-full object-cover border border-neutral-300/60 dark:border-neutral-700/60"
          />
        ) : (
          <div className="h-14 w-14 rounded-full bg-gradient-to-br from-fuchsia-500 to-pink-500 grid place-items-center text-white text-xl font-semibold">
            {(entry.displayName || entry.email || '?')[0].toUpperCase()}
          </div>
        )}
      </div>

      {/* Основной блок */}
      <div className="flex-1 min-w-0 space-y-1">
        {/* Имя + бейджи в одной строке (с переносами) */}
        <div className="flex flex-wrap items-center gap-2">
          <div className="font-semibold break-words">
            {entry.displayName || entry.email}
          </div>

          {hasBadges && (
            <div className="flex flex-wrap items-center gap-1">
              {entry.badges.map((badge) => (
                <span
                  key={badge.id || badge.name}
                  title={badge.name}
                  className="inline-flex items-center justify-center drop-shadow-sm"
                >
                  {badge.imageUrl && (
                    <img
                      src={badge.imageUrl}
                      alt={badge.name}
                      className="h-5 w-5 object-contain"
                    />
                  )}
                  {/* скрытый текст для доступности */}
                  <span className="sr-only">{badge.name}</span>
                </span>
              ))}
            </div>
          )}
        </div>

        {/* Доп. инфа: локация / образование */}
        {entry.location && (
          <div className="flex items-center gap-1 text-xs text-neutral-500 dark:text-neutral-400">
            <MapPin size={12} />
            <span className="truncate">{entry.location}</span>
          </div>
        )}
        {entry.education && (
          <div className="flex items-center gap-1 text-xs text-neutral-500 dark:text-neutral-400">
            <BookOpen size={12} />
            <span className="truncate">{entry.education}</span>
          </div>
        )}

        {/* Статистика */}
        <div className="mt-2 flex flex-wrap gap-2 text-xs">
          {/* Решённые задания */}
          <span className="inline-flex items-center gap-1 rounded-full bg-[rgb(var(--muted))] px-2 py-1">
            <span className="font-semibold text-neutral-700 dark:text-neutral-200">
              {entry.solvedAssignments}
            </span>
            <span className="text-neutral-500 dark:text-neutral-400">
              решённых&nbsp;заданий
            </span>
          </span>

          {/* Всего попыток */}
          {typeof entry.totalAttempts === 'number' && (
            <span className="inline-flex items-center gap-1 rounded-full bg-[rgb(var(--muted))] px-2 py-1">
              <span className="font-semibold text-neutral-700 dark:text-neutral-200">
                {entry.totalAttempts}
              </span>
              <span className="text-neutral-500 dark:text-neutral-400">попыток</span>
            </span>
          )}

          {/* Последняя активность */}
          {entry.lastSubmitAt && (
            <span className="inline-flex items-center gap-1 rounded-full bg-[rgb(var(--muted))] px-2 py-1">
              <Clock size={12} />
              <span className="text-neutral-500 dark:text-neutral-400">
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
