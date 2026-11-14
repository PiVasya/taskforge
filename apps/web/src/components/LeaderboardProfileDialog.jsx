import React, { Suspense, useEffect, useId, useRef } from 'react';
import { createPortal } from 'react-dom';
import { X } from 'lucide-react';
import './LeaderboardProfileDialog.css';

const PublicProfileContent = React.lazy(() => import('./profile/PublicProfileContent'));

const FOCUSABLE = 'a[href],button:not(:disabled),input:not(:disabled),select:not(:disabled),textarea:not(:disabled),[tabindex]:not([tabindex="-1"])';

export default function LeaderboardProfileDialog({ userId, onClose, returnFocusElement }) {
  const titleId = useId();
  const dialogRef = useRef(null);
  const closeRef = useRef(null);

  useEffect(() => {
    const previousOverflow = document.body.style.overflow;
    const previousFocus = document.activeElement;
    const scrollbarWidth = window.innerWidth - document.documentElement.clientWidth;
    const previousPaddingRight = document.body.style.paddingRight;
    // Preserve the rating scroll position and avoid a layout jump while opening.
    if (scrollbarWidth > 0) {
      const existingPadding = Number.parseFloat(window.getComputedStyle(document.body).paddingRight) || 0;
      document.body.style.paddingRight = `${existingPadding + scrollbarWidth}px`;
    }
    document.body.style.overflow = 'hidden';
    closeRef.current?.focus({ preventScroll: true });

    const handleKeyDown = (event) => {
      if (event.key === 'Escape') {
        event.preventDefault();
        event.stopPropagation();
        onClose();
        return;
      }
      if (event.key !== 'Tab') return;
      const dialog = dialogRef.current;
      if (!dialog) return;
      const focusable = Array.from(dialog.querySelectorAll(FOCUSABLE)).filter((element) => (
        !element.closest('[hidden],[inert],[aria-hidden="true"]') && element.getClientRects().length > 0
      ));
      const first = focusable[0];
      const last = focusable[focusable.length - 1];
      if (!first || !last) return;
      if (event.shiftKey && (document.activeElement === first || !dialog.contains(document.activeElement))) {
        event.preventDefault();
        last.focus();
      } else if (!event.shiftKey && (document.activeElement === last || !dialog.contains(document.activeElement))) {
        event.preventDefault();
        first.focus();
      }
    };
    document.addEventListener('keydown', handleKeyDown, true);
    return () => {
      document.removeEventListener('keydown', handleKeyDown, true);
      document.body.style.overflow = previousOverflow;
      document.body.style.paddingRight = previousPaddingRight;
      // A hovered top-three preview is removed when this opens. Return focus
      // to its original portrait if the preview button has disappeared.
      const fallback = Array.from(document.querySelectorAll('[data-leaderboard-user-id]'))
        .find((element) => element.dataset.leaderboardUserId === String(userId));
      let target = fallback;
      if (previousFocus?.isConnected && previousFocus !== document.body) target = previousFocus;
      if (returnFocusElement?.isConnected) target = returnFocusElement;
      target?.focus?.({ preventScroll: true });
    };
  }, [onClose, returnFocusElement, userId]);

  return createPortal(
    <div
      className="tf-modal-backdrop tf-leaderboard-profile-backdrop"
      onPointerDown={(event) => {
        if (event.currentTarget === event.target) onClose();
      }}
    >
      <section
        ref={dialogRef}
        role="dialog"
        aria-modal="true"
        aria-labelledby={titleId}
        className="tf-leaderboard-profile-dialog"
      >
        <header className="tf-leaderboard-profile-header">
          <h2 id={titleId}>Профиль пользователя</h2>
          <button
            type="button"
            ref={closeRef}
            className="tf-leaderboard-profile-close"
            aria-label="Закрыть профиль"
            onClick={onClose}
          >
            <X size={21} aria-hidden="true" />
          </button>
        </header>
        <div className="tf-leaderboard-profile-scroll">
          <div className="tf-leaderboard-profile-content">
            <Suspense fallback={<div role="status" className="text-sm text-neutral-500">Загрузка профиля…</div>}>
              <PublicProfileContent userId={userId} />
            </Suspense>
          </div>
        </div>
      </section>
    </div>,
    document.body,
  );
}
