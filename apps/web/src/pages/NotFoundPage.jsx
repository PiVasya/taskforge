import React from 'react';
import { ArrowLeft, SearchX } from 'lucide-react';
import { Link } from 'react-router-dom';
import { useAuth } from '../auth/AuthContext';

export default function NotFoundPage() {
  const { access } = useAuth();
  const primaryHref = access ? '/courses' : '/';
  const primaryLabel = access ? 'Перейти к курсам' : 'Вернуться на главную';

  return (
    <section className="mx-auto max-w-3xl rounded-[2rem] border border-neutral-200/70 bg-[rgb(var(--card))]/90 p-7 text-center shadow-soft dark:border-neutral-800/70 sm:p-10">
      <span className="mx-auto grid h-16 w-16 place-items-center rounded-3xl bg-brand-600/12 text-brand-700 dark:text-brand-300">
        <SearchX size={30} aria-hidden="true" />
      </span>
      <p className="mt-5 font-mono text-sm font-semibold text-brand-700 dark:text-brand-300">HTTP 404</p>
      <h1 className="mt-2 text-3xl font-bold tracking-tight">Страница не найдена</h1>
      <p className="mx-auto mt-3 max-w-xl leading-7 text-neutral-600 dark:text-neutral-300">
        Ссылка могла устареть, либо в адресе есть ошибка.
      </p>
      <div className="mt-7 flex justify-center">
        <Link to={primaryHref} className="btn-primary">
          <ArrowLeft size={18} aria-hidden="true" />
          <span className="ml-2">{primaryLabel}</span>
        </Link>
      </div>
    </section>
  );
}

NotFoundPage.displayName = 'TaskForgeNotFoundPage';
