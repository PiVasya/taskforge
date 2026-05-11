import React from 'react';
import { Link } from 'react-router-dom';
import { BookOpen, ChevronRight } from 'lucide-react';
import Layout from '../components/Layout';
import { CT_PARTS, getSectionPath, getSectionsByPart } from '../data/ctSections';

export default function SimpleHomePage() {
  return (
    <Layout fullWidth>
      <div className="min-h-[calc(100vh-57px)] bg-neutral-50 dark:bg-neutral-950">
        <div className="mx-auto max-w-6xl px-4 py-8 md:py-12">
          <section className="mb-8 rounded-[2rem] border border-neutral-200/80 bg-white p-6 text-center shadow-soft dark:border-neutral-800 dark:bg-neutral-900 md:p-10">
            <div className="mx-auto mb-4 grid h-14 w-14 place-items-center rounded-3xl bg-brand-50 text-brand-700 dark:bg-brand-900/20 dark:text-brand-100">
              <BookOpen size={28} />
            </div>
            <div className="text-sm font-bold uppercase tracking-[0.25em] text-brand-700 dark:text-brand-300">ЦТ / ЦЭ</div>
            <h1 className="mt-3 text-4xl font-black tracking-tight md:text-6xl">Выбери номер задания</h1>
            <p className="mx-auto mt-4 max-w-2xl text-lg text-neutral-600 dark:text-neutral-300">
              Без лишней платформы: номер → HTML-конспект → случайные задания по этому же номеру.
            </p>
          </section>

          <div className="space-y-6">
            {CT_PARTS.map((part) => (
              <section key={part.code} className="rounded-[2rem] border border-neutral-200/80 bg-white p-5 shadow-soft dark:border-neutral-800 dark:bg-neutral-900 md:p-6">
                <div className="mb-4 flex flex-wrap items-end justify-between gap-3">
                  <div>
                    <h2 className="text-3xl font-black tracking-tight">{part.title}</h2>
                    <p className="mt-1 max-w-3xl text-sm leading-6 text-neutral-600 dark:text-neutral-300">{part.description}</p>
                  </div>
                  <div className="text-sm font-semibold text-neutral-400">{part.count} номеров</div>
                </div>

                <div className="grid grid-cols-3 gap-3 sm:grid-cols-5 md:grid-cols-6 lg:grid-cols-10">
                  {getSectionsByPart(part.code).map((section) => (
                    <Link
                      key={section.code}
                      to={getSectionPath(section.code)}
                      className="group rounded-3xl border border-neutral-200 bg-neutral-50 p-4 text-center transition hover:-translate-y-0.5 hover:border-brand-300 hover:bg-white hover:shadow-md dark:border-neutral-800 dark:bg-neutral-950 dark:hover:border-brand-700 dark:hover:bg-neutral-900"
                    >
                      <div className="text-2xl font-black tracking-tight text-brand-700 dark:text-brand-200">{section.code}</div>
                      <div className="mt-2 inline-flex items-center gap-1 text-xs font-semibold text-neutral-500 group-hover:text-brand-700 dark:text-neutral-400 dark:group-hover:text-brand-200">
                        Открыть <ChevronRight size={13} />
                      </div>
                    </Link>
                  ))}
                </div>
              </section>
            ))}
          </div>
        </div>
      </div>
    </Layout>
  );
}
