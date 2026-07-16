import React from 'react';
import {
  ArrowRight,
  BadgeCheck,
  Boxes,
  CheckCircle2,
  Clock3,
  Coins,
  Link2,
  MapPin,
  MessageSquareText,
  Navigation,
  PackageCheck,
  ShieldCheck,
  Skull,
  Sparkles,
  UsersRound,
} from 'lucide-react';

const ICONS = {
  link: Link2,
  verify: BadgeCheck,
  balance: Coins,
  death: Skull,
  chat: MessageSquareText,
  users: UsersRound,
  shield: ShieldCheck,
  map: MapPin,
  chest: Boxes,
  return: Navigation,
  drop: PackageCheck,
  time: Clock3,
  sparkles: Sparkles,
};

function SectionHeading({ section }) {
  return (
    <div className="max-w-4xl">
      {section.eyebrow ? <div className="changelog-kicker">{section.eyebrow}</div> : null}
      <h2 className="mt-2 text-2xl font-semibold leading-tight sm:text-3xl">{section.title}</h2>
      {section.text ? (
        <p className="mt-4 text-sm leading-7 text-[rgb(var(--text-muted))] sm:text-base sm:leading-8">
          {section.text}
        </p>
      ) : null}
    </div>
  );
}

function TextSection({ section }) {
  return (
    <section className="changelog-section">
      <SectionHeading section={section} />
      {(section.paragraphs || []).map((paragraph) => (
        <p key={paragraph} className="mt-4 max-w-4xl text-sm leading-7 text-[rgb(var(--text-muted))] sm:text-base sm:leading-8">
          {paragraph}
        </p>
      ))}

      {(section.bullets || []).length > 0 ? (
        <div className="mt-6 grid gap-3 lg:grid-cols-2">
          {section.bullets.map((bullet) => (
            <div key={bullet} className="changelog-bullet">
              <CheckCircle2 size={18} className="mt-0.5 shrink-0 text-[rgb(var(--accent))]" />
              <span className="text-sm leading-6">{bullet}</span>
            </div>
          ))}
        </div>
      ) : null}
    </section>
  );
}

function FeatureList({ section }) {
  return (
    <section className="changelog-section">
      <SectionHeading section={section} />
      <div className="changelog-feature-list mt-6">
        {(section.items || []).map((item, index) => (
          <article key={item.title} className="changelog-feature-row">
            <span className="changelog-number">{String(index + 1).padStart(2, '0')}</span>
            <div>
              <h3 className="text-lg font-semibold">{item.title}</h3>
              <p className="mt-2 text-sm leading-6 text-[rgb(var(--text-muted))]">{item.text}</p>
            </div>
          </article>
        ))}
      </div>
    </section>
  );
}

function BenefitGrid({ section }) {
  return (
    <section className="changelog-section">
      <SectionHeading section={section} />
      <div className="changelog-benefit-grid mt-7">
        {(section.items || []).map((item) => {
          const Icon = ICONS[item.icon] || Sparkles;
          return (
            <article key={item.title} className="changelog-benefit-card">
              <span className="changelog-benefit-icon"><Icon size={21} /></span>
              <div>
                <h3 className="text-base font-semibold sm:text-lg">{item.title}</h3>
                <p className="mt-2 text-sm leading-6 text-[rgb(var(--text-muted))]">{item.text}</p>
                {item.note ? <p className="mt-3 text-xs font-medium leading-5 text-[rgb(var(--accent))]">{item.note}</p> : null}
              </div>
            </article>
          );
        })}
      </div>
    </section>
  );
}

function DeathActions({ section }) {
  return (
    <section className="changelog-section">
      <SectionHeading section={section} />
      <div className="changelog-death-grid mt-7">
        {(section.items || []).map((item) => {
          const Icon = ICONS[item.icon] || Skull;
          return (
            <article key={item.title} className="changelog-death-card">
              <div className="flex items-start justify-between gap-4">
                <span className="changelog-death-icon"><Icon size={22} /></span>
                <span className="changelog-cost">{item.cost}</span>
              </div>
              <h3 className="mt-5 text-lg font-semibold">{item.title}</h3>
              <p className="mt-2 text-sm leading-6 text-[rgb(var(--text-muted))]">{item.text}</p>
              {(item.details || []).length > 0 ? (
                <div className="mt-4 space-y-2">
                  {item.details.map((detail) => (
                    <div key={detail} className="flex gap-2 text-xs leading-5 text-[rgb(var(--text-muted))]">
                      <CheckCircle2 size={14} className="mt-0.5 shrink-0 text-[rgb(var(--accent))]" />
                      <span>{detail}</span>
                    </div>
                  ))}
                </div>
              ) : null}
            </article>
          );
        })}
      </div>
      {section.footer ? <p className="mt-5 text-xs leading-5 text-[rgb(var(--text-muted))]">{section.footer}</p> : null}
    </section>
  );
}

function MobCard({ mob }) {
  const cardRef = React.useRef(null);
  const [animationFailed, setAnimationFailed] = React.useState(false);
  const [staticImage, setStaticImage] = React.useState(mob.image);
  const [imageVisible, setImageVisible] = React.useState(true);
  const [cardVisible, setCardVisible] = React.useState(false);
  const [pageVisible, setPageVisible] = React.useState(
    typeof document === 'undefined' || document.visibilityState === 'visible'
  );

  React.useEffect(() => {
    if (!mob.animatedImage || !cardRef.current || typeof IntersectionObserver === 'undefined') {
      setCardVisible(Boolean(mob.animatedImage));
      return undefined;
    }

    const observer = new IntersectionObserver(
      ([entry]) => setCardVisible(entry.isIntersecting),
      { rootMargin: '0px', threshold: 0.01 }
    );

    observer.observe(cardRef.current);
    return () => observer.disconnect();
  }, [mob.animatedImage]);

  React.useEffect(() => {
    if (!mob.animatedImage || typeof document === 'undefined') return undefined;

    const updatePageVisibility = () => {
      setPageVisible(!document.hidden);
    };
    const pausePageAnimation = () => setPageVisible(false);

    updatePageVisibility();
    document.addEventListener('visibilitychange', updatePageVisibility);
    window.addEventListener('pagehide', pausePageAnimation);
    window.addEventListener('pageshow', updatePageVisibility);

    return () => {
      document.removeEventListener('visibilitychange', updatePageVisibility);
      window.removeEventListener('pagehide', pausePageAnimation);
      window.removeEventListener('pageshow', updatePageVisibility);
    };
  }, [mob.animatedImage]);

  const showAnimation = Boolean(mob.animatedImage && !animationFailed && cardVisible && pageVisible);
  const imageSource = showAnimation ? mob.animatedImage : staticImage;

  const handleImageError = () => {
    if (showAnimation) {
      setAnimationFailed(true);
      return;
    }

    if (mob.fallbackImage && staticImage !== mob.fallbackImage) {
      setStaticImage(mob.fallbackImage);
      return;
    }

    setImageVisible(false);
  };

  return (
    <article ref={cardRef} className="changelog-mob-card">
      <div className="changelog-mob-visual" aria-hidden="true">
        <div className="changelog-mob-glow" />
        {imageVisible ? (
          <img
            src={imageSource}
            alt=""
            className="changelog-mob-image"
            loading="lazy"
            decoding="async"
            fetchPriority="low"
            draggable="false"
            referrerPolicy="no-referrer"
            onError={handleImageError}
          />
        ) : null}
        <span className="changelog-mob-index">{mob.index}</span>
      </div>
      <div className="changelog-mob-body">
        <div className="flex flex-wrap items-center gap-2">
          <h3 className="text-xl font-semibold">{mob.name}</h3>
          {mob.label ? <span className="changelog-mob-label">{mob.label}</span> : null}
        </div>
        <p className="mt-2 text-sm leading-6 text-[rgb(var(--text-muted))]">{mob.summary}</p>

        <div className="mt-5 space-y-2.5">
          {(mob.changes || []).map((change) => (
            <div key={change} className="changelog-mob-change">
              <CheckCircle2 size={15} className="mt-0.5 shrink-0 text-[rgb(var(--accent))]" />
              <span>{change}</span>
            </div>
          ))}
        </div>

        {(mob.loot || []).length > 0 ? (
          <div className="changelog-loot-box mt-5">
            <div className="changelog-loot-title">Дополнительный лут</div>
            <div className="mt-2 flex flex-wrap gap-2">
              {mob.loot.map((drop) => <span key={drop} className="changelog-loot-chip">{drop}</span>)}
            </div>
          </div>
        ) : null}

        {mob.note ? <p className="mt-4 text-xs leading-5 text-[rgb(var(--text-muted))]">{mob.note}</p> : null}
      </div>
    </article>
  );
}

function MobGrid({ section }) {
  return (
    <section className="changelog-section changelog-mobs-section">
      <SectionHeading section={section} />
      <div className="changelog-mob-grid mt-8">
        {(section.items || []).map((mob) => <MobCard key={mob.id} mob={mob} />)}
      </div>
      {section.source ? (
        <div className="changelog-source-note mt-6">
          <ShieldCheck size={17} className="shrink-0 text-[rgb(var(--accent))]" />
          <span>{section.source}</span>
        </div>
      ) : null}
    </section>
  );
}

function CalloutSection({ section }) {
  return (
    <section className="changelog-callout">
      <div className="relative z-10 flex flex-col gap-6 lg:flex-row lg:items-center lg:justify-between">
        <div className="max-w-4xl">
          <div className="changelog-kicker">{section.eyebrow || 'Итог'}</div>
          <h2 className="mt-2 text-2xl font-semibold leading-tight sm:text-3xl">{section.title}</h2>
          <p className="mt-4 text-sm leading-7 text-[rgb(var(--text-muted))] sm:text-base sm:leading-8">{section.text}</p>
        </div>
        {section.action ? (
          <a href={section.action.href} className="btn-primary shrink-0">
            {section.action.label}
            <ArrowRight size={18} />
          </a>
        ) : null}
      </div>
    </section>
  );
}

export default function ChangelogShowcase({ data, meta }) {
  const hero = data?.hero || {};

  return (
    <article className="mx-auto max-w-[1180px] pb-10">
      <section className="changelog-post-hero">
        <div className="relative z-10 max-w-5xl">
          <div className="flex flex-wrap gap-2">
            {(hero.badges || meta?.tags || []).map((badge) => <span key={badge} className="changelog-badge">{badge}</span>)}
          </div>
          <div className="changelog-kicker mt-7">{hero.eyebrow || 'Обновление TaskForge'}</div>
          <h1 className="mt-3 text-3xl font-semibold leading-[1.05] tracking-tight sm:text-5xl lg:text-6xl">{hero.title || meta?.title}</h1>
          <p className="mt-6 max-w-4xl text-sm leading-7 text-[rgb(var(--text-muted))] sm:text-lg sm:leading-8">{hero.lead || meta?.summary}</p>
          {hero.primaryValue ? (
            <div className="changelog-primary-value mt-7">
              <span className="text-xs text-[rgb(var(--text-muted))]">{hero.primaryLabel}</span>
              <strong className="mt-1 break-all text-xl sm:text-2xl">{hero.primaryValue}</strong>
            </div>
          ) : null}
        </div>
      </section>

      {(data?.stats || []).length > 0 ? (
        <section className="relative z-20 mx-3 -mt-4 grid gap-3 sm:mx-6 sm:grid-cols-3 lg:mx-10">
          {data.stats.map((stat) => (
            <div key={stat.label} className="changelog-stat">
              <div className="text-2xl font-semibold text-[rgb(var(--accent))] sm:text-3xl">{stat.value}</div>
              <div className="mt-1 text-sm leading-6 text-[rgb(var(--text-muted))]">{stat.label}</div>
            </div>
          ))}
        </section>
      ) : null}

      <div className="px-1 pt-8 sm:px-4">
        {(data?.sections || []).map((section) => {
          const key = `${section.type}-${section.title}`;
          if (section.type === 'features') return <FeatureList key={key} section={section} />;
          if (section.type === 'benefits') return <BenefitGrid key={key} section={section} />;
          if (section.type === 'death-actions') return <DeathActions key={key} section={section} />;
          if (section.type === 'mobs') return <MobGrid key={key} section={section} />;
          if (section.type === 'callout') return <CalloutSection key={key} section={section} />;
          return <TextSection key={key} section={section} />;
        })}
      </div>
    </article>
  );
}
