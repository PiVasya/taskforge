import React, { useCallback, useEffect, useLayoutEffect, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import { useNavigate } from 'react-router-dom';
import LeaderboardCard from './LeaderboardCard';
import { mountLeaderboardParticles } from './leaderboardParticles';
import { getLeaderboardName, getLeaderboardLogin } from './leaderboardPodiumModel.mjs';
import { positionLeaderboardInlinePreview } from './leaderboardInlinePreviewPosition.mjs';
import './LeaderboardPodium.css';

function PortraitAvatar({ entry }) {
  const name = getLeaderboardName(entry);
  return (
    <span className="tf-top3-avatar" data-place={entry.rank} aria-hidden="true">
      {name.charAt(0).toUpperCase()}
      {entry.avatarUrl && (
        <img
          src={entry.avatarUrl}
          alt=""
          onError={(event) => { event.currentTarget.style.display = 'none'; }}
        />
      )}
    </span>
  );
}

function Crown() {
  return (
    <svg className="tf-top3-crown" viewBox="0 0 48 39" aria-hidden="true">
      <path d="M5 10 15 19 24 5 33 19 43 10 39 30H9L5 10Z" />
      <path d="M9 31h30v4H9z" />
      <circle cx="5" cy="10" r="2" /><circle cx="24" cy="5" r="2" /><circle cx="43" cy="10" r="2" />
    </svg>
  );
}

export default function LeaderboardPodium({ entries, onOpenProfile, profileOpen = false }) {
  const navigate = useNavigate();
  const sceneRef = useRef(null);
  const canvasRef = useRef(null);
  const triggerRefs = useRef(new Map());
  const popoverRef = useRef(null);
  const hideTimerRef = useRef(0);
  const [activeRank, setActiveRank] = useState(null);
  const selected = entries.find((entry) => Number(entry.rank) === activeRank) || null;

  useEffect(() => {
    if (!sceneRef.current || !canvasRef.current) return undefined;
    const scene = sceneRef.current;
    const winners = Array.from(scene.querySelectorAll('.tf-top3-winner'));
    return mountLeaderboardParticles({ scene, canvas: canvasRef.current, winners });
  }, [entries]);

  const clearHide = useCallback(() => {
    if (hideTimerRef.current) window.clearTimeout(hideTimerRef.current);
    hideTimerRef.current = 0;
  }, []);

  const close = useCallback(() => {
    clearHide();
    setActiveRank(null);
  }, [clearHide]);

  const open = useCallback((entry) => {
    if (profileOpen) return;
    clearHide();
    setActiveRank(Number(entry.rank));
  }, [clearHide, profileOpen]);

  const scheduleHide = useCallback(() => {
    clearHide();
    hideTimerRef.current = window.setTimeout(() => {
      const pop = popoverRef.current;
      const focused = document.activeElement;
      const focusedTrigger = Array.from(triggerRefs.current.values()).some((element) => element === focused);
      if (!pop?.matches(':hover') && !pop?.contains(focused) && !focusedTrigger) {
        setActiveRank(null);
      }
    }, 220);
  }, [clearHide]);

  useEffect(() => () => clearHide(), [clearHide]);
  useEffect(() => { if (profileOpen) close(); }, [profileOpen, close]);

  // The same compact card used by the rows below is revealed beside the
  // *existing* portrait.  A clipped copy of its avatar is never shown, so
  // the levitating portrait does not jump or change on hover.
  useLayoutEffect(() => {
    if (!activeRank) return undefined;
    let frame = 0;
    let lastLayout = '';
    let lastViewportWidth = 0;
    let preferredWidth = 520;
    const reposition = () => {
      const trigger = triggerRefs.current.get(activeRank);
      const popup = popoverRef.current;
      if (!trigger || !popup) return;

      // CSS displays the complete compact row near the bottom on touch/small
      // screens, where there is no room for a rightward horizontal reveal.
      if (window.innerWidth <= 800) return;

      const viewportWidth = document.documentElement.clientWidth;
      if (lastViewportWidth !== viewportWidth) {
        lastViewportWidth = viewportWidth;
        const firstRow = document.querySelector('.tf-leaderboard-grid .tf-leaderboard-card');
        preferredWidth = firstRow?.getBoundingClientRect().width || 520;
      }
      const rect = trigger.getBoundingClientRect();
      const next = positionLeaderboardInlinePreview({
        avatar: rect,
        viewportWidth,
        preferredWidth,
      });
      const layout = `${next.left}|${next.top}|${next.width}`;
      if (layout !== lastLayout) {
        popup.style.left = `${next.left}px`;
        popup.style.top = `${next.top}px`;
        popup.style.width = `${next.width}px`;
        lastLayout = layout;
      }
    };
    const followLevitation = () => {
      reposition();
      frame = window.requestAnimationFrame(followLevitation);
    };
    reposition();
    frame = window.requestAnimationFrame(followLevitation);
    return () => window.cancelAnimationFrame(frame);
  }, [activeRank]);

  useEffect(() => {
    if (!activeRank) return undefined;
    const onPointerDown = (event) => {
      const withinStage = sceneRef.current?.contains(event.target);
      if (!withinStage && !popoverRef.current?.contains(event.target)) close();
    };
    const onKeyDown = (event) => { if (event.key === 'Escape') close(); };
    document.addEventListener('pointerdown', onPointerDown);
    document.addEventListener('keydown', onKeyDown);
    return () => {
      document.removeEventListener('pointerdown', onPointerDown);
      document.removeEventListener('keydown', onKeyDown);
    };
  }, [activeRank, close]);

  const handlePortraitClick = (entry, sourceElement) => {
    if (!entry.userId) return;
    close();
    if (onOpenProfile) onOpenProfile(entry, sourceElement);
    else navigate(`/users/${entry.userId}`);
  };

  return (
    <>
      <section className="tf-top3-stage" aria-label="Тройка лидеров">
        <div className="tf-top3-scene" ref={sceneRef}>
          <canvas className="tf-top3-canvas" ref={canvasRef} aria-hidden="true" />
          {entries.map((entry) => {
            const rank = Number(entry.rank);
            const name = getLeaderboardName(entry);
            const login = getLeaderboardLogin(entry);
            return (
              <div key={entry.userId || rank} className="tf-top3-winner" data-place={rank}>
                <button
                  type="button"
                  className="tf-top3-portrait"
                  aria-label={`Открыть профиль: ${name}`}
                  aria-expanded={activeRank === rank}
                  onFocus={() => open(entry)}
                  onBlur={() => scheduleHide()}
                  onClick={(event) => handlePortraitClick(entry, event.currentTarget)}
                  data-leaderboard-user-id={entry.userId}
                  onKeyDown={(event) => {
                    if (event.key === 'ArrowDown') {
                      event.preventDefault();
                      open(entry);
                      requestAnimationFrame(() => popoverRef.current?.querySelector('.tf-leaderboard-card')?.focus());
                    }
                  }}
                >
                  <span
                    className="tf-top3-avatar-wrap"
                    ref={(node) => { if (node) triggerRefs.current.set(rank, node); else triggerRefs.current.delete(rank); }}
                    onPointerEnter={(event) => { if (event.pointerType === 'mouse' || event.pointerType === 'pen') open(entry); }}
                    onPointerLeave={(event) => { if (event.pointerType === 'mouse' || event.pointerType === 'pen') scheduleHide(); }}
                  >
                    {rank === 1 && <Crown />}
                    <PortraitAvatar entry={entry} />
                  </span>
                  <span className="tf-top3-name">{name}</span>
                  {login && <span className="tf-top3-login">@{login}</span>}
                </button>
                <span className="tf-top3-ether-anchor" aria-hidden="true" />
              </div>
            );
          })}
        </div>
      </section>

      {selected && createPortal(
        <div
          className="tf-top3-inline-popover"
          ref={popoverRef}
          role="group"
          aria-label={`Карточка пользователя ${getLeaderboardName(selected)}`}
          onPointerEnter={clearHide}
          onPointerLeave={scheduleHide}
          onFocusCapture={clearHide}
          onBlurCapture={scheduleHide}
        >
          <LeaderboardCard entry={selected} onOpenProfile={onOpenProfile} />
        </div>,
        document.body,
      )}
    </>
  );
}
