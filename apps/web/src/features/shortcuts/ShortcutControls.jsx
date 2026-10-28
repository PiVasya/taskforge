import React from 'react';
import { Link } from 'react-router-dom';
import { SHORTCUTS, shortcutForHref, shortcutKeys, shortcutLabel } from './shortcutCatalog';

function KeyText({ id, newTab }) {
  return shortcutKeys(id, newTab).map((key, index) => (
    <React.Fragment key={index}>{index > 0 ? <><span>+</span><wbr /></> : null}{key === 'Shift' ? '⇧' : key}</React.Fragment>
  ));
}

export function ShortcutHint({ shortcut, navigation = false }) {
  if (!SHORTCUTS[shortcut]) return null;
  return (
    <span className="tf-shortcut-hint" data-tf-shortcut-hint="" aria-hidden="true">
      <span className="tf-shortcut-normal"><KeyText id={shortcut} /></span>
      {navigation ? <span className="tf-shortcut-newtab"><KeyText id={shortcut} newTab /></span> : null}
    </span>
  );
}

export function shortcutAttributes(shortcut, href) {
  if (!SHORTCUTS[shortcut]) return {};
  return {
    'data-tf-shortcut': shortcut,
    'data-tf-shortcut-href': href || undefined,
    'data-tf-shortcut-nav': href ? 'true' : undefined,
    'aria-keyshortcuts': [shortcutLabel(shortcut), ...(href ? [shortcutLabel(shortcut, true)] : [])].join(' '),
  };
}

export const ShortcutButton = React.forwardRef(function ShortcutButton({
  shortcut, shortcutHref, shortcutPriority, children, className = '', ...props
}, ref) {
  if (!SHORTCUTS[shortcut]) return <button {...props} ref={ref} className={className}>{children}</button>;
  return <button {...props} {...shortcutAttributes(shortcut, shortcutHref)} data-tf-shortcut-priority={shortcutPriority} ref={ref} className={`${className} tf-hotkey-control`}>
    {children}<ShortcutHint shortcut={shortcut} navigation={Boolean(shortcutHref)} />
  </button>;
});

export const ShortcutLink = React.forwardRef(function ShortcutLink({
  shortcut, to, shortcutPriority, children, className = '', ...props
}, ref) {
  const id = shortcut || shortcutForHref(to);
  if (!SHORTCUTS[id]) return <Link {...props} to={to} ref={ref} className={className}>{children}</Link>;
  const href = typeof to === 'string' ? to : `${to.pathname || ''}${to.search || ''}${to.hash || ''}`;
  return <Link {...props} {...shortcutAttributes(id, href)} data-tf-shortcut-priority={shortcutPriority} to={to} ref={ref} className={`${className} tf-hotkey-control`}>
    {children}<ShortcutHint shortcut={id} navigation />
  </Link>;
});


export const ShortcutAnchor = React.forwardRef(function ShortcutAnchor({
  shortcut, href, shortcutPriority, children, className = '', ...props
}, ref) {
  const id = shortcut || shortcutForHref(href);
  if (!SHORTCUTS[id]) return <a {...props} href={href} ref={ref} className={className}>{children}</a>;
  return <a {...props} {...shortcutAttributes(id, href)} data-tf-shortcut-priority={shortcutPriority}
    href={href} ref={ref} className={`${className} tf-hotkey-control`}>
    {children}<ShortcutHint shortcut={id} navigation />
  </a>;
});
