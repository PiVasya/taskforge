import React from 'react';
import { useNavigate } from 'react-router-dom';
import { Trophy, MapPin, BookOpen, Clock, AtSign, Copy, ExternalLink } from 'lucide-react';
import { ContextMenu, ContextMenuItem, ContextMenuLabel, ContextMenuSeparator, claimContextMenuEvent } from './ui/ContextMenu';
import { useNotify } from './notify/NotifyProvider';
import LeaderboardMarquee from './LeaderboardMarquee';
import './LeaderboardCard.css';

export default function LeaderboardCard({ entry }) {
  const nav = useNavigate();
  const notify = useNotify();
  const [contextMenu, setContextMenu] = React.useState({ open: false, x: 0, y: 0 });
  const solved = entry.solvedAssignments ?? entry.solvedCount ?? entry.solved ?? 0;
  const login = String(entry.login || entry.userLogin || entry.username || '').trim();
  const name = entry.displayName || entry.userName || entry.fullName || login || entry.maskedEmail || entry.email || 'Пользователь';
  const showLogin = Boolean(login && String(name).trim().toLowerCase() !== login.toLowerCase());
  const hasMetadata = showLogin || Boolean(entry.location) || Boolean(entry.education);
  const metadataTitle = [
    showLogin ? `@${login}` : null,
    entry.location,
    entry.education,
  ].filter(Boolean).join(' · ');
  const hasBadges = Array.isArray(entry.badges) && entry.badges.length > 0;

  const handleOpenProfile = () => nav(`/users/${entry.userId}`);

  const rankColors = {
    1: 'from-amber-400 to-yellow-500',
    2: 'from-neutral-300 to-neutral-100',
    3: 'from-orange-400 to-amber-500',
  };
  const rankBg = rankColors[entry.rank] || 'from-neutral-200 to-neutral-300 dark:from-neutral-700 dark:to-neutral-800';

  return (
    <>
      <button
        type="button"
        onClick={handleOpenProfile}
        onContextMenuCapture={(event) => {
          if (!claimContextMenuEvent(event)) return;
          setContextMenu({ open: true, x: event.clientX, y: event.clientY });
        }}
        className="tf-leaderboard-card group relative w-full text-left border border-neutral-200/70 dark:border-neutral-800/70 bg-[rgb(var(--card))] shadow-soft hover:shadow-lg hover:-translate-y-0.5 transition-all flex items-center gap-3 cursor-pointer"
      >
        {/* Keep the rank inside the card, clear of the avatar's tight top inset. */}
        <div className="pointer-events-none absolute right-1.5 top-1.5 z-10">
          <div className={`inline-flex items-center gap-1 rounded-lg bg-gradient-to-br ${rankBg} px-2 py-[2px] text-xs font-semibold ${entry.rank <= 3 ? 'text-neutral-900' : 'text-neutral-900 dark:text-neutral-100'} shadow-sm`}>
            <Trophy size={14} />
            <span>#{entry.rank}</span>
          </div>
        </div>

        <div className="tf-leaderboard-avatar shrink-0 overflow-hidden bg-[rgb(var(--muted))] shadow-sm ring-1 ring-inset ring-neutral-300/60 dark:ring-neutral-700/60">
          {entry.avatarUrl ? (
            <img
              src={entry.avatarUrl}
              alt={name}
              className="block h-full w-full object-cover"
            />
          ) : (
            <div className="grid h-full w-full place-items-center bg-gradient-to-br from-fuchsia-500 to-pink-500 text-2xl font-semibold text-white">
              {(name || '?')[0].toUpperCase()}
            </div>
          )}
        </div>

        {/* Three fixed, non-wrapping lines add up to 64px with their 2px gaps. */}
        <div className="tf-leaderboard-info flex-1 min-w-0 overflow-hidden flex flex-col justify-center">
          <div className="tf-leaderboard-name min-w-0 pr-[70px] font-semibold flex items-center">
            <LeaderboardMarquee title={[name, ...(hasBadges ? entry.badges.map((badge) => badge.name) : [])].filter(Boolean).join(' · ')}>
              <span className="inline-flex items-center gap-2 whitespace-nowrap">
                <span>{name}</span>
                {hasBadges && (
                  <span className="inline-flex shrink-0 items-center gap-1">
                    {entry.badges.map((badge) => (
                      <span key={badge.id || badge.name} title={badge.name} className="inline-flex items-center justify-center drop-shadow-sm">
                        {badge.imageUrl && <img src={badge.imageUrl} alt={badge.name} className="h-5 w-5 object-contain" />}
                        <span className="sr-only">{badge.name}</span>
                      </span>
                    ))}
                  </span>
                )}
              </span>
            </LeaderboardMarquee>
          </div>

          <div className="tf-leaderboard-meta min-w-0 text-xs text-neutral-500 dark:text-neutral-400">
            {hasMetadata && (
              <LeaderboardMarquee title={metadataTitle}>
                <span className="inline-flex items-center gap-3 whitespace-nowrap">
                  {showLogin && (
                    <span className="inline-flex items-center gap-1">
                      <AtSign size={12} className="shrink-0" />
                      <span>{login}</span>
                    </span>
                  )}
                  {entry.location && (
                    <span className="inline-flex items-center gap-1">
                      <MapPin size={12} className="shrink-0" />
                      <span>{entry.location}</span>
                    </span>
                  )}
                  {entry.education && (
                    <span className="inline-flex items-center gap-1">
                      <BookOpen size={12} className="shrink-0" />
                      <span>{entry.education}</span>
                    </span>
                  )}
                </span>
              </LeaderboardMarquee>
            )}
          </div>

          <div className="tf-leaderboard-stats min-w-0 text-xs">
            <LeaderboardMarquee>
              <span className="inline-flex items-center gap-1.5 whitespace-nowrap">
                <span className="inline-flex items-center gap-1 rounded-full bg-[rgb(var(--muted))] px-2 py-[2px]">
                  <span className="font-semibold text-neutral-700 dark:text-neutral-200">{solved}</span>
                  <span className="text-neutral-500 dark:text-neutral-400">решённых&nbsp;заданий</span>
                </span>

                {typeof entry.totalAttempts === 'number' && (
                  <span className="inline-flex items-center gap-1 rounded-full bg-[rgb(var(--muted))] px-2 py-[2px]">
                    <span className="font-semibold text-neutral-700 dark:text-neutral-200">{entry.totalAttempts}</span>
                    <span className="text-neutral-500 dark:text-neutral-400">попыток</span>
                  </span>
                )}

                {entry.lastSubmitAt && (
                  <span className="inline-flex items-center gap-1 rounded-full bg-[rgb(var(--muted))] px-2 py-[2px]">
                    <Clock size={12} />
                    <span className="text-neutral-500 dark:text-neutral-400">
                      Активен: {new Date(entry.lastSubmitAt).toLocaleDateString(undefined, {
                        timeZone: Intl.DateTimeFormat().resolvedOptions().timeZone,
                      })}
                    </span>
                  </span>
                )}
              </span>
            </LeaderboardMarquee>
          </div>
        </div>
      </button>

      <ContextMenu
        open={contextMenu.open}
        x={contextMenu.x}
        y={contextMenu.y}
        onClose={() => setContextMenu((current) => ({ ...current, open: false }))}
        ariaLabel="Действия участника рейтинга"
      >
        <ContextMenuLabel>Участник</ContextMenuLabel>
        <ContextMenuItem icon={ExternalLink} onClick={() => { setContextMenu((current) => ({ ...current, open: false })); handleOpenProfile(); }}>
          Открыть профиль
        </ContextMenuItem>
        <ContextMenuSeparator />
        <ContextMenuItem icon={Copy} disabled={!login} onClick={() => {
          setContextMenu((current) => ({ ...current, open: false }));
          navigator.clipboard.writeText(login).then(() => notify.success('Логин скопирован')).catch(() => notify.warn('Не удалось скопировать логин'));
        }}>
          Скопировать логин
        </ContextMenuItem>
        <ContextMenuItem icon={Copy} disabled={!entry.userId} onClick={() => {
          setContextMenu((current) => ({ ...current, open: false }));
          navigator.clipboard.writeText(String(entry.userId)).then(() => notify.success('ID скопирован')).catch(() => notify.warn('Не удалось скопировать ID'));
        }}>
          Скопировать ID
        </ContextMenuItem>
      </ContextMenu>
    </>
  );
}
