import React, { useMemo, useState } from 'react';
import { Link } from 'react-router-dom';
import { AlertTriangle, BookOpen, CheckCircle2, Clock, Layers, ListChecks, PlayCircle, Sparkles } from 'lucide-react';

function safeJson(value, fallback) {
  if (!value) return fallback;
  if (typeof value === 'object') return value;
  try {
    return JSON.parse(value);
  } catch {
    return fallback;
  }
}

function toTasksHref(link) {
  const filter = safeJson(link?.taskFilterJson, null);
  const params = new URLSearchParams();

  if (filter && typeof filter === 'object') {
    Object.entries(filter).forEach(([key, value]) => {
      if (value !== undefined && value !== null && value !== '') params.set(key, String(value));
    });
  }

  if (link?.taskSlug) params.set('task', link.taskSlug);
  if (!params.has('sectionCode') && link?.anchorBlockId) params.set('from', link.anchorBlockId);
  return `/tasks${params.toString() ? `?${params.toString()}` : ''}`;
}

function BlockShell({ block, icon, children, className = '' }) {
  return (
    <section id={block.id || undefined} className={`rounded-3xl border border-neutral-200/70 dark:border-neutral-800 bg-white dark:bg-neutral-900 shadow-soft p-5 ${className}`}>
      {block.title && (
        <div className="mb-4 flex items-center gap-2">
          {icon}
          <h3 className="text-xl font-semibold tracking-tight">{block.title}</h3>
        </div>
      )}
      {children}
    </section>
  );
}

function RenderBlock({ block }) {
  if (!block || typeof block !== 'object') return null;

  switch (block.type) {
    case 'rule-card':
      return (
        <BlockShell block={block} icon={<BookOpen size={20} className="text-brand-600" />}>
          <div className="grid gap-3 md:grid-cols-3">
            {(block.items || []).map((item, index) => (
              <div key={index} className="rounded-2xl bg-brand-50 dark:bg-brand-900/20 border border-brand-200/70 dark:border-brand-800 p-4 text-sm leading-6">
                <div className="mb-2 font-semibold text-brand-700 dark:text-brand-200">Правило {index + 1}</div>
                <div>{item}</div>
              </div>
            ))}
          </div>
        </BlockShell>
      );

    case 'warning':
      return (
        <BlockShell block={block} icon={<AlertTriangle size={20} className="text-amber-600" />} className="bg-amber-50 dark:bg-amber-950/20 border-amber-200 dark:border-amber-900">
          <p className="text-base leading-7 text-neutral-800 dark:text-neutral-200">{block.text}</p>
        </BlockShell>
      );

    case 'examples':
      return (
        <BlockShell block={block} icon={<Sparkles size={20} className="text-brand-600" />}>
          <div className="grid gap-3 md:grid-cols-3">
            {(block.items || []).map((item, index) => (
              <div key={index} className="rounded-2xl border border-neutral-200 dark:border-neutral-800 bg-neutral-50 dark:bg-neutral-950 p-4">
                <div className="text-sm text-neutral-500 dark:text-neutral-400">Пример</div>
                <div className="mt-1 text-lg font-semibold">{item.source}</div>
                <div className="mt-2 text-brand-700 dark:text-brand-200 font-medium">{item.answer}</div>
                {item.comment && <p className="mt-2 text-sm leading-6 text-neutral-600 dark:text-neutral-300">{item.comment}</p>}
              </div>
            ))}
          </div>
        </BlockShell>
      );

    case 'steps':
    case 'checklist':
      return (
        <BlockShell block={block} icon={block.type === 'steps' ? <Layers size={20} className="text-brand-600" /> : <ListChecks size={20} className="text-brand-600" />}>
          <ol className="space-y-3">
            {(block.items || []).map((item, index) => (
              <li key={index} className="flex gap-3 rounded-2xl bg-neutral-50 dark:bg-neutral-950 border border-neutral-200/70 dark:border-neutral-800 p-4">
                <span className="mt-0.5 flex h-7 w-7 shrink-0 items-center justify-center rounded-full bg-brand-600 text-white text-sm font-semibold">
                  {block.type === 'steps' ? index + 1 : <CheckCircle2 size={16} />}
                </span>
                <span className="leading-7">{item}</span>
              </li>
            ))}
          </ol>
        </BlockShell>
      );

    case 'table':
      return (
        <BlockShell block={block} icon={<BookOpen size={20} className="text-brand-600" />}>
          <div className="overflow-x-auto rounded-2xl border border-neutral-200 dark:border-neutral-800">
            <table className="min-w-full text-sm">
              <thead className="bg-neutral-100 dark:bg-neutral-950">
                <tr>
                  {(block.columns || []).map((col) => (
                    <th key={col} className="px-4 py-3 text-left font-semibold">{col}</th>
                  ))}
                </tr>
              </thead>
              <tbody className="divide-y divide-neutral-200 dark:divide-neutral-800">
                {(block.rows || []).map((row, rowIndex) => (
                  <tr key={rowIndex}>
                    {row.map((cell, cellIndex) => (
                      <td key={cellIndex} className="px-4 py-3 align-top leading-6">{cell}</td>
                    ))}
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </BlockShell>
      );

    case 'dictionary':
      return (
        <BlockShell block={block} icon={<BookOpen size={20} className="text-brand-600" />}>
          <div className="grid gap-4 md:grid-cols-3">
            {(block.groups || []).map((group) => (
              <div key={group.title} className="rounded-2xl border border-neutral-200 dark:border-neutral-800 bg-neutral-50 dark:bg-neutral-950 p-4">
                <div className="font-semibold text-brand-700 dark:text-brand-200">{group.title}</div>
                <div className="mt-3 flex flex-wrap gap-2">
                  {(group.words || []).map((word) => (
                    <span key={word} className="rounded-full bg-white dark:bg-neutral-900 border border-neutral-200 dark:border-neutral-800 px-3 py-1 text-sm">{word}</span>
                  ))}
                </div>
              </div>
            ))}
          </div>
        </BlockShell>
      );

    case 'year-words':
      return (
        <BlockShell block={block} icon={<Clock size={20} className="text-brand-600" />}>
          <div className="space-y-3">
            {(block.years || []).map((year) => (
              <div key={year.year} className="rounded-2xl border border-neutral-200 dark:border-neutral-800 bg-neutral-50 dark:bg-neutral-950 p-4">
                <div className="text-sm font-semibold text-neutral-500 dark:text-neutral-400">{year.year}</div>
                <div className="mt-2 flex flex-wrap gap-2">
                  {(year.words || []).map((word) => (
                    <span key={word} className="rounded-full bg-brand-50 dark:bg-brand-900/20 border border-brand-200 dark:border-brand-800 px-3 py-1 text-sm">{word}</span>
                  ))}
                </div>
              </div>
            ))}
          </div>
        </BlockShell>
      );

    case 'practice-intro':
      return (
        <BlockShell block={block} icon={<PlayCircle size={20} className="text-brand-600" />} className="bg-gradient-to-br from-brand-50 to-white dark:from-brand-900/20 dark:to-neutral-900">
          <p className="leading-7 text-neutral-700 dark:text-neutral-200">{block.text}</p>
          {block.cta?.href && (
            <Link to={block.cta.href} className="btn-primary mt-4 inline-flex items-center gap-2">
              <PlayCircle size={18} />
              {block.cta.label || 'К заданиям'}
            </Link>
          )}
        </BlockShell>
      );

    case 'note':
    default:
      return (
        <BlockShell block={block} icon={<BookOpen size={20} className="text-brand-600" />}>
          {block.text && <p className="leading-7 text-neutral-700 dark:text-neutral-200">{block.text}</p>}
        </BlockShell>
      );
  }
}

export default function RichConspectRenderer({ details }) {
  const content = useMemo(() => safeJson(details?.contentJson, {}), [details]);
  const tabs = Array.isArray(content.tabs) ? content.tabs : [];
  const initialTab = content.startTabId || tabs[0]?.id || 'main';
  const [activeTab, setActiveTab] = useState(initialTab);
  const currentTab = tabs.find((tab) => tab.id === activeTab) || tabs[0];
  const taskLinks = details?.taskLinks || [];
  const conspect = details?.conspect || {};

  return (
    <article className="space-y-6">
      <section className="relative overflow-hidden rounded-[2rem] border border-neutral-200/80 dark:border-neutral-800 bg-white dark:bg-neutral-900 shadow-soft p-6 md:p-8">
        <div className="absolute inset-0 bg-gradient-to-br from-brand-100/70 via-transparent to-transparent dark:from-brand-900/30 pointer-events-none" />
        <div className="relative">
          <div className="mb-3 text-sm font-semibold uppercase tracking-wide text-brand-700 dark:text-brand-300">
            {content.hero?.eyebrow || `${conspect.sectionCode || 'Раздел'} · Конспект`}
          </div>
          <h1 className="max-w-4xl text-3xl md:text-5xl font-bold tracking-tight">{content.hero?.title || conspect.title}</h1>
          {(content.hero?.description || conspect.lead) && (
            <p className="mt-4 max-w-3xl text-lg leading-8 text-neutral-700 dark:text-neutral-200">{content.hero?.description || conspect.lead}</p>
          )}
          <div className="mt-6 flex flex-wrap gap-3">
            {(content.hero?.stats || []).map((stat) => (
              <div key={`${stat.label}-${stat.value}`} className="rounded-2xl border border-white/70 dark:border-neutral-800 bg-white/80 dark:bg-neutral-950/70 px-4 py-3">
                <div className="text-xs text-neutral-500 dark:text-neutral-400">{stat.label}</div>
                <div className="font-semibold">{stat.value}</div>
              </div>
            ))}
            {conspect.estimatedMinutes ? (
              <div className="rounded-2xl border border-white/70 dark:border-neutral-800 bg-white/80 dark:bg-neutral-950/70 px-4 py-3">
                <div className="text-xs text-neutral-500 dark:text-neutral-400">Время</div>
                <div className="font-semibold">≈ {conspect.estimatedMinutes} мин.</div>
              </div>
            ) : null}
          </div>
        </div>
      </section>

      {tabs.length > 0 && (
        <div className="sticky top-[57px] z-20 -mx-4 overflow-x-auto border-y border-neutral-200/80 dark:border-neutral-800 bg-neutral-50/95 dark:bg-neutral-950/95 px-4 py-3 backdrop-blur md:mx-0 md:rounded-3xl md:border">
          <div className="flex min-w-max gap-2">
            {tabs.map((tab) => (
              <button
                key={tab.id}
                type="button"
                onClick={() => setActiveTab(tab.id)}
                className={`rounded-2xl px-4 py-2 text-sm font-medium transition ${tab.id === currentTab?.id ? 'bg-brand-600 text-white shadow-soft' : 'bg-white dark:bg-neutral-900 border border-neutral-200 dark:border-neutral-800 hover:border-brand-300'}`}
              >
                {tab.title}
              </button>
            ))}
          </div>
        </div>
      )}

      <div className="space-y-4">
        {(currentTab?.blocks || []).map((block, index) => (
          <RenderBlock key={block.id || `${block.type}-${index}`} block={block} />
        ))}
      </div>

      {taskLinks.length > 0 && (
        <section className="rounded-[2rem] border border-brand-200 dark:border-brand-800 bg-brand-50 dark:bg-brand-900/20 p-5 md:p-6">
          <div className="mb-4 flex items-center gap-2">
            <PlayCircle size={21} className="text-brand-700 dark:text-brand-200" />
            <h2 className="text-2xl font-semibold tracking-tight">Задания к конспекту</h2>
          </div>
          <div className="grid gap-3 md:grid-cols-2">
            {taskLinks.map((link) => (
              <Link key={link.id} to={toTasksHref(link)} className="group rounded-2xl border border-brand-200 dark:border-brand-800 bg-white dark:bg-neutral-900 p-4 transition hover:-translate-y-0.5 hover:shadow-soft">
                <div className="text-sm text-neutral-500 dark:text-neutral-400">{link.groupTitle || link.sourceService}</div>
                <div className="mt-1 font-semibold group-hover:text-brand-700 dark:group-hover:text-brand-200">{link.title}</div>
                <div className="mt-3 inline-flex items-center gap-2 text-sm font-semibold text-brand-700 dark:text-brand-200">
                  <PlayCircle size={16} />
                  {link.buttonText || 'К заданиям'}
                </div>
              </Link>
            ))}
          </div>
        </section>
      )}
    </article>
  );
}
