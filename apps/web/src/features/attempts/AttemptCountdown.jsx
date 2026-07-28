import React, { useEffect, useRef, useState } from 'react';
import { Badge } from '../../components/ui';

function formatSeconds(total) {
  if (total == null) return '';
  const value = Math.max(0, Math.floor(total));
  const minutes = Math.floor(value / 60);
  const seconds = value % 60;
  return `${minutes}:${String(seconds).padStart(2, '0')}`;
}

function calculateSecondsLeft(startedAt, timeLimitSeconds) {
  if (!startedAt || !timeLimitSeconds) return null;
  const startedAtMs = new Date(startedAt).getTime();
  if (!Number.isFinite(startedAtMs)) return null;
  return Math.max(0, Math.ceil(Number(timeLimitSeconds) - (Date.now() - startedAtMs) / 1000));
}

function AttemptCountdown({ startedAt, timeLimitSeconds, onExpire, disabled = false }) {
  const [secondsLeft, setSecondsLeft] = useState(() => calculateSecondsLeft(startedAt, timeLimitSeconds));
  const expireHandlerRef = useRef(onExpire);
  const disabledRef = useRef(disabled);
  const expiredRef = useRef(false);
  expireHandlerRef.current = onExpire;
  disabledRef.current = disabled;

  useEffect(() => {
    expiredRef.current = false;
    const update = () => {
      const next = calculateSecondsLeft(startedAt, timeLimitSeconds);
      setSecondsLeft(next);
      if (next === 0 && !disabledRef.current && !expiredRef.current) {
        expiredRef.current = true;
        expireHandlerRef.current?.();
      }
    };
    update();
    if (!startedAt || !timeLimitSeconds) return undefined;
    const timer = window.setInterval(update, 500);
    return () => window.clearInterval(timer);
  }, [startedAt, timeLimitSeconds]);

  if (!timeLimitSeconds) return null;
  return (
    <Badge variant={secondsLeft !== null && secondsLeft <= 10 ? 'destructive' : 'secondary'}>
      Таймер: {formatSeconds(secondsLeft)}
    </Badge>
  );
}

export default React.memo(AttemptCountdown);
