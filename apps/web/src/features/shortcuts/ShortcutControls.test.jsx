import React, { act, createRef } from 'react';
import { createRoot } from 'react-dom/client';
import { MemoryRouter } from 'react-router-dom';
import { Button, Field, Input } from '../../components/ui';
import { ShortcutButton, ShortcutLink, ShortcutAnchor } from './ShortcutControls';

let container, root;
beforeEach(() => {
  container = document.createElement('div'); document.body.append(container); root = createRoot(container);
});
afterEach(() => { act(() => root.unmount()); container.remove(); });
function render(children) { act(() => root.render(children)); }

test('a shortcut button preserves its original content, handler and accessible name', () => {
  let calls = 0;
  render(<Button shortcut="primary" onClick={() => { calls++; }}><span className="original">Submit solution</span></Button>);
  const button = container.querySelector('button');
  expect(button.firstElementChild.className).toBe('original');
  expect(button.firstElementChild.textContent).toBe('Submit solution');
  expect(button.querySelector('.tf-shortcut-hint').getAttribute('aria-hidden')).toBe('true');
  expect(button.getAttribute('aria-keyshortcuts')).toBe('Alt+Enter');
  act(() => button.click()); expect(calls).toBe(1);
});
test('the wrapper forwards the ref and disabled state to the actual native button', () => {
  const ref = createRef(); let calls = 0;
  render(<ShortcutButton shortcut="primary" ref={ref} disabled onClick={() => { calls++; }}>Send</ShortcutButton>);
  expect(ref.current).toBe(container.querySelector('button'));
  expect(ref.current.disabled).toBe(true);
  act(() => ref.current.click()); expect(calls).toBe(0);
});
test('unassigned destructive actions receive no accidental shortcut', () => {
  render(<Button variant="outline">Delete account</Button>);
  expect(container.querySelector('[data-tf-shortcut]')).toBe(null);
  expect(container.querySelector('.tf-shortcut-hint')).toBe(null);
});
test('each navigation link retains its own action, not the menu close callback identity', () => {
  render(<MemoryRouter future={{ v7_startTransition: true, v7_relativeSplatPath: true }}><ShortcutLink to="/courses" onClick={() => {}}>Courses</ShortcutLink><ShortcutLink to="/my/solutions" onClick={() => {}}>Solutions</ShortcutLink></MemoryRouter>);
  const [courses, solutions] = container.querySelectorAll('a');
  expect(courses.dataset.tfShortcut).toBe('nav.courses');
  expect(solutions.dataset.tfShortcut).toBe('nav.solutions');
  expect(courses.getAttribute('aria-keyshortcuts')).toBe('Alt+C Ctrl+Alt+C');
  expect(solutions.getAttribute('aria-keyshortcuts')).toBe('Alt+R Ctrl+Alt+R');
});
test('a navigation button keeps its explicit URL and priority without changing click behavior', () => {
  let calls = 0;
  render(<ShortcutButton shortcut="results" shortcutHref="/assignment/a/results" shortcutPriority={2} onClick={() => { calls++; }}>Results</ShortcutButton>);
  const button = container.querySelector('button');
  expect(button.dataset.tfShortcutHref).toBe('/assignment/a/results');
  expect(button.dataset.tfShortcutPriority).toBe('2');
  act(() => button.click()); expect(calls).toBe(1);
});
test('a native result link retains target and rel rather than becoming a button', () => {
  render(<ShortcutAnchor shortcut="results" href="/assignment/a/results" target="_blank" rel="noreferrer">Results</ShortcutAnchor>);
  const a = container.querySelector('a');
  expect(a.getAttribute('href')).toBe('/assignment/a/results');
  expect(a.getAttribute('target')).toBe('_blank');
  expect(a.getAttribute('rel')).toBe('noreferrer');
});
test('search field labels keep their input association alongside the hint', () => {
  render(<Field shortcut="search" label="Search"><Input defaultValue="query" /></Field>);
  const label = container.querySelector('label');
  expect(label.control).toBe(container.querySelector('input'));
  expect(label.dataset.tfShortcut).toBe('search');
  expect(container.querySelector('input').value).toBe('query');
});
