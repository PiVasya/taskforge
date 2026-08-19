import React from 'react';
import {
  Braces,
  Database,
  Eye,
  KeyRound,
  Layers3,
  Network,
  ServerCog,
  ShieldCheck,
} from 'lucide-react';

const sections = [
  {
    icon: Layers3,
    title: 'React SPA и постоянная оболочка',
    text: 'Основной интерфейс работает как React 18 SPA через BrowserRouter. Шапка, боковая панель, фон, уведомления и глобальные провайдеры остаются смонтированными, а при переходах меняется содержимое маршрута.',
  },
  {
    icon: KeyRound,
    title: 'Авторизация',
    text: 'Access-токен хранится в памяти вкладки. Восстановление сессии выполняется через HttpOnly-cookie, недоступную JavaScript. Сервер повторно проверяет подпись, срок действия, тип токена, состояние аккаунта и требуемые роли.',
  },
  {
    icon: Network,
    title: 'API и микросервисы',
    text: 'Браузер обращается к same-origin маршрутам /api и /hubs. Публичный gateway передаёт запросы специализированным сервисам, а внутренние API требуют отдельный сервисный ключ и не полагаются на скрытие URL.',
  },
  {
    icon: Database,
    title: 'Данные и клиентский кэш',
    text: 'Серверные данные кэшируются с привязкой к текущей пользовательской сессии. При смене аккаунта клиентский кэш создаётся заново, поэтому данные разных пользователей не должны смешиваться в одной вкладке.',
  },
  {
    icon: ServerCog,
    title: 'Выполнение пользовательского кода',
    text: 'Решения не исполняются внутри frontend. Перед запуском исходник проходит отдельный анализатор, а runner принимает только аттестацию точного проверенного текста и запускает код в ограниченной среде.',
  },
  {
    icon: Eye,
    title: 'Что видно в DevTools',
    text: 'Frontend-код, названия компонентов, сетевые запросы и публичные sourcemaps могут быть изучены пользователем. Они не считаются секретной границей: права доступа, проверка ответов и административные операции контролируются сервером.',
  },
];

export default function TechnologyPage() {
  return (
    <div className="mx-auto max-w-6xl space-y-6 pb-10">
      <header className="card p-6 sm:p-8">
        <div className="flex items-center gap-3 text-sm font-semibold text-[rgb(var(--accent))]">
          <ShieldCheck size={20} aria-hidden="true" />
          <span>Технический обзор</span>
        </div>
        <h1 className="mt-4 text-3xl font-semibold tracking-tight sm:text-5xl">Как устроен TaskForge</h1>
        <p className="mt-4 max-w-4xl text-sm leading-7 text-[rgb(var(--text-muted))] sm:text-base sm:leading-8">
          Краткое описание текущих архитектурных и защитных границ платформы. Страница не заменяет аудит развёрнутой инфраструктуры, но показывает, какие проверки не доверены браузеру.
        </p>
      </header>

      <section className="grid gap-4 md:grid-cols-2" aria-label="Архитектура TaskForge">
        {sections.map(({ icon: Icon, title, text }) => (
          <article key={title} className="card p-5 sm:p-6">
            <span className="grid h-11 w-11 place-items-center rounded-2xl border border-[rgb(var(--border))] bg-[rgb(var(--muted))] text-[rgb(var(--accent))]">
              <Icon size={21} aria-hidden="true" />
            </span>
            <h2 className="mt-4 text-lg font-semibold sm:text-xl">{title}</h2>
            <p className="mt-3 text-sm leading-7 text-[rgb(var(--text-muted))]">{text}</p>
          </article>
        ))}
      </section>

      <section className="card p-5 sm:p-6">
        <div className="flex items-start gap-3">
          <Braces size={22} className="mt-0.5 shrink-0 text-[rgb(var(--accent))]" aria-hidden="true" />
          <div>
            <h2 className="text-lg font-semibold sm:text-xl">Граница доверия</h2>
            <p className="mt-3 text-sm leading-7 text-[rgb(var(--text-muted))]">
              Любые ограничения, существующие только в интерфейсе, считаются удобством, а не защитой. Клиент может быть изменён через DevTools, поэтому backend самостоятельно проверяет пользователя, роль, принадлежность ресурса, допустимость перехода состояния и входные данные каждого защищённого действия.
            </p>
          </div>
        </div>
      </section>
    </div>
  );
}
