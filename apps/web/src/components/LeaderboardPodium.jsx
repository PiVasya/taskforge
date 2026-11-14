import React, { useCallback, useEffect, useLayoutEffect, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import { useNavigate } from 'react-router-dom';
import { Trophy, MapPin, BookOpen, ExternalLink } from 'lucide-react';
import { mountLeaderboardParticles } from './leaderboardParticles';
import { getLeaderboardName, getLeaderboardLogin, getLeaderboardSolved } from './leaderboardPodiumModel.mjs';
import './LeaderboardPodium.css';

const numberFormatter = new Intl.NumberFormat('ru-RU');

function formattedNumber(value) {
  const number = Number(value);
  return Number.isFinite(number) ? numberFormatter.format(number) : null;
}

function lastActivity(date) {
  if (!date) return 'нет данных';
  const elapsed = Date.now() - new Date(date).getTime();
  if (!Number.isFinite(elapsed)) return 'нет данных';
  if (elapsed < 86400000) return 'сегодня';
  const days = Math.floor(elapsed / 86400000);
  if (days === 1) return 'вчера';
  if (days < 30) return `${days} дн. назад`;
  return new Date(date).toLocaleDateString('ru-RU');
}

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

function popoverCoordinates(trigger, popup) {
  const rect = trigger.getBoundingClientRect();
  const width = popup?.offsetWidth || 340;
  const height = popup?.offsetHeight || 205;
  const margin = 12;
  const gap = 15;
  const viewportWidth = document.documentElement.clientWidth;
  const viewportHeight = window.innerHeight;

  let left = rect.right + gap;
  if (left + width > viewportWidth - margin) left = rect.left - width - gap;
  if (left < margin) left = Math.max(margin, Math.min(viewportWidth - width - margin, rect.left + (rect.width - width) / 2));

  let top = rect.bottom + gap;
  if (top + height > viewportHeight - margin) {
    top = rect.top - height - gap >= margin
      ? rect.top - height - gap
      : Math.max(margin, viewportHeight - height - margin);
  }
  return { left: Math.round(left), top: Math.round(top) };
}

export default function LeaderboardPodium({ entries }) {
  const navigate = useNavigate();
  const sceneRef = useRef(null);
  const canvasRef = useRef(null);
  const triggerRefs = useRef(new Map());
  const popoverRef = useRef(null);
  const hideTimerRef = useRef(0);
  const [activeRank, setActiveRank] = useState(null);
  const [position, setPosition] = useState({ left: -1000, top: -1000 });
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
    clearHide();
    setActiveRank(Number(entry.rank));
  }, [clearHide]);

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

  useLayoutEffect(() => {
    if (!activeRank) return undefined;
    const reposition = () => {
      const trigger = triggerRefs.current.get(activeRank);
      if (!trigger || !popoverRef.current) return;
      setPosition((previous) => {
        const next = popoverCoordinates(trigger, popoverRef.current);
        return next.left === previous.left && next.top === previous.top ? previous : next;
      });
    };
    reposition();
    window.addEventListener('scroll', reposition, true);
    window.addEventListener('resize', reposition);
    return () => {
      window.removeEventListener('scroll', reposition, true);
      window.removeEventListener('resize', reposition);
    };
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

  const handlePortraitClick = (entry) => {
    const touch = window.matchMedia('(hover: none)').matches;
    if (touch) open(entry);
    else if (entry.userId) navigate(`/users/${entry.userId}`);
  };

  const showProfile = () => {
    if (!selected?.userId) return;
    const userId = selected.userId;
    close();
    navigate(`/users/${userId}`);
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
                  ref={(node) => { if (node) triggerRefs.current.set(rank, node); else triggerRefs.current.delete(rank); }}
                  onPointerEnter={(event) => { if (event.pointerType === 'mouse' || event.pointerType === 'pen') open(entry); }}
                  onPointerLeave={(event) => { if (event.pointerType === 'mouse' || event.pointerType === 'pen') scheduleHide(); }}
                  onFocus={() => open(entry)}
                  onBlur={() => scheduleHide()}
                  onClick={() => handlePortraitClick(entry)}
                  onKeyDown={(event) => {
                    if (event.key === 'ArrowDown' || event.key === ' ') {
                      event.preventDefault();
                      open(entry);
                      requestAnimationFrame(() => popoverRef.current?.querySelector('.tf-top3-pop-link')?.focus());
                    }
                  }}
                >
                  <span className="tf-top3-avatar-wrap">
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
          className="tf-top3-popover"
          ref={popoverRef}
          style={position}
          role="group"
          aria-label={`Карточка пользователя ${getLeaderboardName(selected)}`}
          onPointerEnter={clearHide}
          onPointerLeave={scheduleHide}
          onFocusCapture={clearHide}
          onBlurCapture={scheduleHide}
        >
          <div className="tf-top3-pop-head">
            <PortraitAvatar entry={selected} />
            <div className="tf-top3-pop-identity">
              <div className="tf-top3-pop-name">{getLeaderboardName(selected)}</div>
              {getLeaderboardLogin(selected) && <div className="tf-top3-pop-login">@{getLeaderboardLogin(selected)}</div>}
            </div>
            <div className="tf-top3-pop-rank" data-place={selected.rank}>
              <Trophy size={13} aria-hidden="true" /> #{selected.rank}
            </div>
          </div>
          {(selected.location || selected.education) && (
            <div className="tf-top3-pop-meta">
              {selected.location && <span><MapPin size={13} aria-hidden="true" />{selected.location}</span>}
              {selected.education && <span><BookOpen size={13} aria-hidden="true" />{selected.education}</span>}
            </div>
          )}
          <div className="tf-top3-pop-chips">
            {formattedNumber(selected.totalScore ?? selected.score) !== null && (
              <span className="tf-top3-pop-chip">{formattedNumber(selected.totalScore ?? selected.score)} рейтинга</span>
            )}
            <span className="tf-top3-pop-chip">{formattedNumber(getLeaderboardSolved(selected))} решено</span>
            {selected.totalAttempts != null && <span className="tf-top3-pop-chip">{formattedNumber(selected.totalAttempts)} попыток</span>}
          </div>
          <div className="tf-top3-pop-divider" />
          <div className="tf-top3-pop-foot">
            <span className="tf-top3-pop-activity">Последняя активность: {lastActivity(selected.lastSubmitAt)}</span>
            <button type="button" className="tf-top3-pop-link" onClick={showProfile}>
              <ExternalLink size={13} aria-hidden="true" /> Профиль
            </button>
          </div>
        </div>,
        document.body,
      )}
    </>
  );
}
