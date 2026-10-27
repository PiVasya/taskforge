import React from 'react';
import './LeaderboardMarquee.css';
import { measureLeaderboardMarquee } from './leaderboardMarqueeMotion.mjs';

/**
 * A one-line marquee that animates only when its actual content is wider than
 * the available space. The second copy is purely visual and is hidden from AT.
 */
export default function LeaderboardMarquee({ children, title, className = '' }) {
  const viewportRef = React.useRef(null);
  const contentRef = React.useRef(null);
  const [metrics, setMetrics] = React.useState({ overflow: false, distance: 0, duration: 0 });

  React.useEffect(() => {
    const viewport = viewportRef.current;
    const content = contentRef.current;
    if (!viewport || !content) return undefined;

    let active = true;
    const measure = () => {
      if (!active) return;
      const available = viewport.clientWidth;
      const required = Math.ceil(Math.max(content.getBoundingClientRect().width, content.scrollWidth));
      const next = measureLeaderboardMarquee(required, available);
      setMetrics((previous) => (
        previous.overflow === next.overflow && previous.distance === next.distance ? previous : next
      ));
    };

    measure();
    const observer = typeof ResizeObserver !== 'undefined' ? new ResizeObserver(measure) : null;
    if (observer) {
      observer.observe(viewport);
      observer.observe(content);
    } else {
      window.addEventListener('resize', measure);
    }

    // Fonts can finish loading after the first measurement.
    if (document.fonts?.ready) document.fonts.ready.then(measure).catch(() => {});

    return () => {
      active = false;
      observer?.disconnect();
      if (!observer) window.removeEventListener('resize', measure);
    };
  }, [children]);

  return (
    <span
      ref={viewportRef}
      className={`tf-leaderboard-marquee ${className}`.trim()}
      data-overflow={metrics.overflow ? 'true' : 'false'}
      title={title}
    >
      <span
        className="tf-leaderboard-marquee__track"
        style={metrics.overflow ? {
          '--tf-leaderboard-marquee-distance': `${metrics.distance}px`,
          '--tf-leaderboard-marquee-duration': `${metrics.duration}s`,
        } : undefined}
      >
        <span ref={contentRef} className="tf-leaderboard-marquee__copy">{children}</span>
        {metrics.overflow && (
          <span aria-hidden="true" className="tf-leaderboard-marquee__copy tf-leaderboard-marquee__duplicate">
            {children}
          </span>
        )}
      </span>
    </span>
  );
}
