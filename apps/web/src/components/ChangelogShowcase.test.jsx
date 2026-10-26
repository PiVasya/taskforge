import React, { act } from 'react';
import { createRoot } from 'react-dom/client';
import ChangelogShowcase from './ChangelogShowcase';

let container;
let root;

beforeEach(() => {
  global.IS_REACT_ACT_ENVIRONMENT = true;
  container = document.createElement('div');
  document.body.appendChild(container);
  root = createRoot(container);
});

afterEach(() => {
  act(() => root.unmount());
  container.remove();
  jest.restoreAllMocks();
});

const mob = {
  id: 'example', name: 'Скелет', index: '01 / 15',
  image: '/poster.png', animatedImage: '/idle.webp',
  fallbackImage: '/fallback.png', changes: [], loot: [],
};

const renderMob = (item) => {
  act(() => root.render(<ChangelogShowcase data={{
    sections: [{ type: 'mobs', title: 'Мобы', items: [item] }],
  }} />));
};

test('Animation is displayed immediately and is not replaced when the page becomes hidden', () => {
  renderMob(mob);
  const image = container.querySelector('.changelog-mob-image');
  expect(image.getAttribute('src')).toBe('/idle.webp');
  expect(image.getAttribute('width')).toBe('512');
  expect(image.getAttribute('height')).toBe('512');
  jest.spyOn(document, 'hidden', 'get').mockReturnValue(true);
  act(() => {
    document.dispatchEvent(new Event('visibilitychange'));
    window.dispatchEvent(new Event('pagehide'));
    window.dispatchEvent(new Event('pageshow'));
  });
  expect(image.getAttribute('src')).toBe('/idle.webp');
});

test('A failed animation is not silently replaced by a static image', () => {
  renderMob(mob);
  act(() => container.querySelector('.changelog-mob-image').dispatchEvent(new Event('error')));
  expect(container.querySelector('.changelog-mob-image')).toBeNull();
  expect(container.textContent).toContain('Скелет');
});

test('A local animated image works without a separate animation field and can recover after a source change', () => {
  renderMob({ ...mob, image: '/first.webp', animatedImage: undefined });
  expect(container.querySelector('img').getAttribute('src')).toBe('/first.webp');
  act(() => container.querySelector('img').dispatchEvent(new Event('error')));
  expect(container.querySelector('img')).toBeNull();
  renderMob({ ...mob, image: '/second.webp', animatedImage: undefined });
  expect(container.querySelector('img').getAttribute('src')).toBe('/second.webp');
});
