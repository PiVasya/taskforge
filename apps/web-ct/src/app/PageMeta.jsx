import { useLayoutEffect, useMemo } from 'react';
import { matchPath, useLocation } from 'react-router-dom';

const SITE_NAME = 'TaskForge.by CT';
const CANONICAL_ORIGIN = 'https://ct.taskforge.by';
const DEFAULT_DESCRIPTION = 'TaskForge.by CT — курсы, конспекты и задания для подготовки к ЦЭ и ЦТ.';

const rules = [
  { path: '/', title: 'главная', description: DEFAULT_DESCRIPTION },
  { path: '/login', title: 'вход', description: 'Вход в учебный раздел TaskForge.by CT.' },
  { path: '/register', title: 'регистрация', description: 'Создание аккаунта для работы с курсами TaskForge.by CT.' },
  { path: '/privacy', title: 'политика конфиденциальности', description: 'Политика конфиденциальности TaskForge.by CT.', indexable: true },
  { path: '/courses', title: 'курсы' },
  { path: '/courses/:courseSlug', title: 'курс' },
  { path: '/courses/:courseSlug/conspects/:slug', title: 'конспект' },
  { path: '/courses/:courseSlug/tasks', title: 'задания' },
  { path: '/conspects/:slug', title: 'конспект' },
  { path: '/editor/*', title: 'редактор' },
];

function resolveMeta(pathname) {
  const rule = rules.find((item) => matchPath({ path: item.path, end: true }, pathname || '/'));
  return {
    title: rule ? `${SITE_NAME} — ${rule.title}` : `${SITE_NAME} — страница`,
    description: rule?.description || DEFAULT_DESCRIPTION,
    indexable: Boolean(rule?.indexable),
  };
}

function setMeta(selector, attributes, content) {
  let tag = document.head.querySelector(selector);
  if (!tag) {
    tag = document.createElement('meta');
    Object.entries(attributes).forEach(([name, value]) => tag.setAttribute(name, value));
    document.head.appendChild(tag);
  }
  tag.setAttribute('content', content);
}

function setCanonical(href) {
  let tag = document.head.querySelector('link[rel="canonical"]');
  if (!tag) {
    tag = document.createElement('link');
    tag.setAttribute('rel', 'canonical');
    document.head.appendChild(tag);
  }
  tag.setAttribute('href', href);
}

export default function PageMeta() {
  const location = useLocation();
  const meta = useMemo(() => resolveMeta(location.pathname), [location.pathname]);

  useLayoutEffect(() => {
    const pathname = location.pathname || '/';
    const canonical = `${CANONICAL_ORIGIN}${pathname}`;
    const html = document.documentElement;

    html.lang = 'ru';
    html.dataset.taskforgeReady = 'loading';
    html.dataset.taskforgeRoute = pathname;
    document.title = meta.title;

    setMeta('meta[name="description"]', { name: 'description' }, meta.description);
    setMeta('meta[name="robots"]', { name: 'robots' }, meta.indexable ? 'index,follow' : 'noindex,nofollow');
    setMeta('meta[property="og:site_name"]', { property: 'og:site_name' }, SITE_NAME);
    setMeta('meta[property="og:title"]', { property: 'og:title' }, meta.title);
    setMeta('meta[property="og:description"]', { property: 'og:description' }, meta.description);
    setMeta('meta[property="og:url"]', { property: 'og:url' }, canonical);
    setCanonical(canonical);

    // PageContent marks the document ready only after the lazy route has
    // resolved inside Suspense, so Chromium never treats the fallback as final.
  }, [location.pathname, meta.description, meta.indexable, meta.title]);

  return null;
}
