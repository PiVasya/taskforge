import React from 'react';
import { ArrowRight, CheckCircle2, Sparkles } from 'lucide-react';

const tones = {
  pink: {
    glow: 'from-pink-500/22 via-fuchsia-500/10 to-violet-500/14',
    border: 'border-pink-400/25 dark:border-pink-300/20',
    label: 'text-pink-600 dark:text-pink-300',
    marker: 'bg-pink-500/12 text-pink-600 dark:text-pink-300',
  },
  emerald: {
    glow: 'from-emerald-500/20 via-lime-500/8 to-cyan-500/14',
    border: 'border-emerald-400/25 dark:border-emerald-300/20',
    label: 'text-emerald-700 dark:text-emerald-300',
    marker: 'bg-emerald-500/12 text-emerald-700 dark:text-emerald-300',
  },
};

function TextSection({ section, tone }) {
  const current = tones[tone] || tones.pink;

  return (
    <section className="py-5 sm:py-8">
      <div className="max-w-4xl">
        {section.eyebrow ? (
          <div className={`text-xs font-bold uppercase tracking-[0.22em] ${current.label}`}>
            {section.eyebrow}
          </div>
        ) : null}
        <h2 className="mt-2 text-2xl font-semibold leading-tight sm:text-3xl">{section.title}</h2>
        {(section.paragraphs || []).map((paragraph) => (
          <p key={paragraph} className="mt-4 text-sm leading-7 text-neutral-600 dark:text-neutral-300 sm:text-base sm:leading-8">
            {paragraph}
          </p>
        ))}
      </div>

      {(section.bullets || []).length > 0 ? (
        <div className="mt-6 grid gap-3 lg:grid-cols-2">
          {section.bullets.map((bullet) => (
            <div
              key={bullet}
              className="flex gap-3 rounded-2xl border border-black/5 bg-white/55 px-4 py-4 dark:border-white/10 dark:bg-white/[0.035]"
            >
              <CheckCircle2 size={18} className={`mt-0.5 shrink-0 ${current.label}`} />
              <span className="text-sm leading-6 text-neutral-700 dark:text-neutral-200">{bullet}</span>
            </div>
          ))}
        </div>
      ) : null}
    </section>
  );
}

function FeatureList({ section, tone }) {
  const current = tones[tone] || tones.pink;

  return (
    <section className="py-5 sm:py-8">
      <div className="max-w-4xl">
        {section.eyebrow ? (
          <div className={`text-xs font-bold uppercase tracking-[0.22em] ${current.label}`}>
            {section.eyebrow}
          </div>
        ) : null}
        <h2 className="mt-2 text-2xl font-semibold leading-tight sm:text-3xl">{section.title}</h2>
        {section.text ? (
          <p className="mt-4 text-sm leading-7 text-neutral-600 dark:text-neutral-300 sm:text-base sm:leading-8">
            {section.text}
          </p>
        ) : null}
      </div>

      <div className="mt-6 divide-y divide-black/5 overflow-hidden rounded-[26px] border border-black/5 bg-white/55 dark:divide-white/10 dark:border-white/10 dark:bg-white/[0.035]">
        {(section.items || []).map((item, index) => (
          <article key={item.title} className="grid gap-3 p-5 sm:grid-cols-[52px_1fr] sm:p-6">
            <span className={`inline-flex h-10 w-10 items-center justify-center rounded-xl text-sm font-bold ${current.marker}`}>
              {String(index + 1).padStart(2, '0')}
            </span>
            <div>
              <h3 className="text-lg font-semibold">{item.title}</h3>
              <p className="mt-2 text-sm leading-6 text-neutral-600 dark:text-neutral-300">{item.text}</p>
            </div>
          </article>
        ))}
      </div>
    </section>
  );
}

function CalloutSection({ section, tone }) {
  const current = tones[tone] || tones.pink;

  return (
    <section className={`relative overflow-hidden rounded-[30px] border p-6 sm:p-9 ${current.border}`}>
      <div className={`pointer-events-none absolute inset-0 bg-gradient-to-br ${current.glow}`} />
      <div className="relative z-10 flex flex-col gap-6 lg:flex-row lg:items-center lg:justify-between">
        <div className="max-w-4xl">
          <div className={`text-xs font-bold uppercase tracking-[0.22em] ${current.label}`}>
            {section.eyebrow || 'Итог'}
          </div>
          <h2 className="mt-2 text-2xl font-semibold leading-tight sm:text-3xl">{section.title}</h2>
          <p className="mt-4 text-sm leading-7 text-neutral-700 dark:text-neutral-200 sm:text-base sm:leading-8">
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
  const tone = data?.tone || meta?.tone || 'pink';
  const current = tones[tone] || tones.pink;
  const hero = data?.hero || {};

  return (
    <article className="mx-auto max-w-[1180px] pb-10">
      <section className={`relative overflow-hidden rounded-[32px] border p-6 sm:p-9 lg:p-12 ${current.border}`}>
        <div className={`pointer-events-none absolute inset-0 bg-gradient-to-br ${current.glow}`} />
        <div className="pointer-events-none absolute -right-24 -top-28 h-80 w-80 rounded-full border border-white/10" />

        <div className="relative z-10 max-w-5xl">
          <div className="flex flex-wrap gap-2">
            {(hero.badges || meta?.tags || []).map((badge) => (
              <span
                key={badge}
                className="rounded-full border border-black/5 bg-white/55 px-3 py-1.5 text-xs font-medium backdrop-blur dark:border-white/10 dark:bg-black/15"
              >
                {badge}
              </span>
            ))}
          </div>

          <div className={`mt-7 flex items-center gap-2 text-xs font-bold uppercase tracking-[0.24em] ${current.label}`}>
            <Sparkles size={14} />
            {hero.eyebrow || 'Большое обновление'}
          </div>
          <h1 className="mt-3 text-3xl font-semibold leading-[1.05] tracking-tight sm:text-5xl lg:text-6xl">
            {hero.title || meta?.title}
          </h1>
          <p className="mt-6 max-w-4xl text-sm leading-7 text-neutral-700 dark:text-neutral-200 sm:text-lg sm:leading-8">
            {hero.lead || meta?.summary}
          </p>

          {hero.primaryValue ? (
            <div className="mt-7 inline-flex max-w-full flex-col rounded-2xl border border-black/5 bg-white/55 px-5 py-4 backdrop-blur dark:border-white/10 dark:bg-black/15">
              <span className="text-xs text-neutral-500 dark:text-neutral-400">{hero.primaryLabel}</span>
              <strong className="mt-1 break-all text-xl sm:text-2xl">{hero.primaryValue}</strong>
            </div>
          ) : null}
        </div>
      </section>

      {(data?.stats || []).length > 0 ? (
        <section className="relative z-20 mx-3 -mt-4 grid gap-3 sm:mx-6 sm:grid-cols-3 lg:mx-10">
          {data.stats.map((stat) => (
            <div
              key={stat.label}
              className="rounded-[22px] border border-black/5 bg-white/95 p-5 shadow-xl shadow-black/5 backdrop-blur-xl dark:border-white/10 dark:bg-neutral-950/90"
            >
              <div className={`text-2xl font-semibold sm:text-3xl ${current.label}`}>{stat.value}</div>
              <div className="mt-1 text-sm leading-6 text-neutral-600 dark:text-neutral-300">{stat.label}</div>
            </div>
          ))}
        </section>
      ) : null}

      <div className="px-1 pt-8 sm:px-4">
        {(data?.sections || []).map((section) => {
          if (section.type === 'features') {
            return <FeatureList key={`${section.type}-${section.title}`} section={section} tone={tone} />;
          }
          if (section.type === 'callout') {
            return <CalloutSection key={`${section.type}-${section.title}`} section={section} tone={tone} />;
          }
          return <TextSection key={`${section.type}-${section.title}`} section={section} tone={tone} />;
        })}
      </div>
    </article>
  );
}
