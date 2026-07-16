import React from 'react';
import { ArrowRight, CheckCircle2 } from 'lucide-react';

function TextSection({ section }) {
  return (
    <section className="changelog-section">
      <div className="max-w-4xl">
        {section.eyebrow ? <div className="changelog-kicker">{section.eyebrow}</div> : null}
        <h2 className="mt-2 text-2xl font-semibold leading-tight sm:text-3xl">{section.title}</h2>
        {(section.paragraphs || []).map((paragraph) => (
          <p key={paragraph} className="mt-4 text-sm leading-7 text-[rgb(var(--text-muted))] sm:text-base sm:leading-8">
            {paragraph}
          </p>
        ))}
      </div>

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
      <div className="max-w-4xl">
        {section.eyebrow ? <div className="changelog-kicker">{section.eyebrow}</div> : null}
        <h2 className="mt-2 text-2xl font-semibold leading-tight sm:text-3xl">{section.title}</h2>
        {section.text ? (
          <p className="mt-4 text-sm leading-7 text-[rgb(var(--text-muted))] sm:text-base sm:leading-8">
            {section.text}
          </p>
        ) : null}
      </div>

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

function CalloutSection({ section }) {
  return (
    <section className="changelog-callout">
      <div className="relative z-10 flex flex-col gap-6 lg:flex-row lg:items-center lg:justify-between">
        <div className="max-w-4xl">
          <div className="changelog-kicker">{section.eyebrow || 'Итог'}</div>
          <h2 className="mt-2 text-2xl font-semibold leading-tight sm:text-3xl">{section.title}</h2>
          <p className="mt-4 text-sm leading-7 text-[rgb(var(--text-muted))] sm:text-base sm:leading-8">
            {section.text}
          </p>
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
            {(hero.badges || meta?.tags || []).map((badge) => (
              <span key={badge} className="changelog-badge">{badge}</span>
            ))}
          </div>

          <div className="changelog-kicker mt-7">{hero.eyebrow || 'Обновление TaskForge'}</div>
          <h1 className="mt-3 text-3xl font-semibold leading-[1.05] tracking-tight sm:text-5xl lg:text-6xl">
            {hero.title || meta?.title}
          </h1>
          <p className="mt-6 max-w-4xl text-sm leading-7 text-[rgb(var(--text-muted))] sm:text-lg sm:leading-8">
            {hero.lead || meta?.summary}
          </p>

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
          if (section.type === 'features') {
            return <FeatureList key={`${section.type}-${section.title}`} section={section} />;
          }
          if (section.type === 'callout') {
            return <CalloutSection key={`${section.type}-${section.title}`} section={section} />;
          }
          return <TextSection key={`${section.type}-${section.title}`} section={section} />;
        })}
      </div>
    </article>
  );
}
