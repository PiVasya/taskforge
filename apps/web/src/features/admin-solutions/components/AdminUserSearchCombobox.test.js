import React, { act } from 'react';
import { createRoot } from 'react-dom/client';
import AdminUserSearchCombobox from './AdminUserSearchCombobox';

const liveItems = [
  { userId: 'u-valera', userLabel: 'Валерий Иванов', user: { firstName: 'Валерий', lastName: 'Иванов', email: 'valera@example.test' } },
  { userId: 'u-valya', userLabel: 'Валентина', user: { firstName: 'Валентина', login: 'valya' } },
  { userId: 'u-igor', userLabel: 'Игорь', user: { firstName: 'Игорь' } },
];

describe('admin user search combobox', () => {
  let container;
  let root;

  beforeEach(() => {
    container = document.createElement('div');
    document.body.appendChild(container);
    root = createRoot(container);
  });

  afterEach(() => {
    act(() => root.unmount());
    container.remove();
  });

  const renderSearch = (props = {}) => {
    const onSelectUser = props.onSelectUser || jest.fn();
    const onClear = props.onClear || jest.fn();
    act(() => {
      root.render(
        <AdminUserSearchCombobox
          query="Вале"
          onQueryChange={jest.fn()}
          onSelectUser={onSelectUser}
          onClear={onClear}
          live
          liveItems={liveItems}
          {...props}
        />,
      );
    });
    return { onSelectUser, onClear };
  };

  test('typing a partial name shows only matching participants under the input', () => {
    renderSearch();
    const input = container.querySelector('input[role="combobox"]');
    act(() => input.focus());
    expect(input.getAttribute('aria-expanded')).toBe('true');
    const options = [...container.querySelectorAll('[role="option"]')];
    expect(options).toHaveLength(2);
    expect(options[0].textContent).toContain('Валерий Иванов');
    expect(options[1].textContent).toContain('Валентина');
    expect(container.textContent).not.toContain('Игорь');
    expect(container.textContent).not.toContain('Найти');
  });

  test('keyboard ArrowDown / Enter chooses an exact user ID', () => {
    const onSelectUser = jest.fn();
    renderSearch({ onSelectUser });
    const input = container.querySelector('input[role="combobox"]');
    act(() => input.focus());
    act(() => input.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowDown', bubbles: true })));
    act(() => input.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowDown', bubbles: true })));
    act(() => input.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true })));
    expect(onSelectUser).toHaveBeenCalledWith(expect.objectContaining({ id: 'u-valya' }), 'Валентина');
    expect(input.getAttribute('aria-expanded')).toBe('false');
  });

  test('stale server results never display in response to a newer query', () => {
    renderSearch({ live: false, searchedQuery: 'другой', users: [{ id: 'u-valera', firstName: 'Валерий' }] });
    act(() => container.querySelector('input').focus());
    expect(container.querySelectorAll('[role="option"]')).toHaveLength(0);
    expect(container.textContent).toContain('Ищу пользователей');
    renderSearch({ live: false, searchedQuery: 'Вале', users: [{ id: 'u-valera', firstName: 'Валерий' }] });
    expect(container.querySelectorAll('[role="option"]')).toHaveLength(1);
  });

  test('live Enter does not automatically pick the first user or search the directory', () => {
    const onSelectUser = jest.fn();
    const onSubmitSearch = jest.fn();
    renderSearch({ onSelectUser, onSubmitSearch });
    const input = container.querySelector('input');
    act(() => input.focus());
    act(() => input.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true })));
    expect(onSelectUser).not.toHaveBeenCalled();
    expect(onSubmitSearch).not.toHaveBeenCalled();
    expect(input.getAttribute('aria-expanded')).toBe('false');
  });

  test('the full directory can be requested explicitly without changing realtime filtering', () => {
    const onSubmitSearch = jest.fn();
    renderSearch({ onSubmitSearch });
    act(() => container.querySelector('input').focus());
    const button = [...container.querySelectorAll('button')]
      .find(item => item.textContent.includes('Искать во всех аккаунтах'));
    expect(button).toBeTruthy();
    act(() => button.click());
    expect(onSubmitSearch).toHaveBeenCalledTimes(1);
    renderSearch({ users: [{ id: 'u-remote', firstName: 'Валентина', lastName: 'Петрова' }], searchedQuery: 'Вале' });
    expect(container.querySelectorAll('[role="option"]')).toHaveLength(3);
  });

  test('selection does not open a dropdown, and clearing is an explicit action', () => {
    const onClear = jest.fn();
    renderSearch({ selectedUser: { id: 'u-valera' }, onClear });
    const input = container.querySelector('input');
    act(() => input.focus());
    expect(container.querySelectorAll('[role="option"]')).toHaveLength(0);
    const button = container.querySelector('button[aria-label="Очистить поиск и выбор пользователя"]');
    act(() => button.click());
    expect(onClear).toHaveBeenCalledTimes(1);
  });

  test('Escape closes suggestions without choosing a user', () => {
    const onSelectUser = jest.fn();
    renderSearch({ onSelectUser });
    const input = container.querySelector('input');
    act(() => input.focus());
    act(() => input.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true })));
    expect(input.getAttribute('aria-expanded')).toBe('false');
    expect(onSelectUser).not.toHaveBeenCalled();
  });

  test('typos are visible as ranked suggestions with a reason, without selecting an account', () => {
    const onSelectUser = jest.fn();
    renderSearch({ query: 'Валреий', onSelectUser });
    act(() => container.querySelector('input').focus());
    expect(container.querySelectorAll('[role="option"]')).toHaveLength(1);
    expect(container.textContent).toContain('Валерий Иванов');
    expect(container.textContent).toContain('Возможная опечатка');
    expect(onSelectUser).not.toHaveBeenCalled();
  });

  test('Latin keys in a Russian name produce a keyboard-layout hint', () => {
    renderSearch({ query: 'Dfkthbq' });
    act(() => container.querySelector('input').focus());
    expect(container.textContent).toContain('Валерий Иванов');
    expect(container.textContent).toContain('Исправлена раскладка');
    expect(container.textContent).not.toContain('Игорь');
  });

  test('history Enter never auto-selects an unhighlighted approximate user', () => {
    const onSelectUser = jest.fn();
    const onSubmitSearch = jest.fn();
    renderSearch({
      live: false,
      searchedQuery: 'Валреий',
      query: 'Валреий',
      users: [{ id: 'u-valera', firstName: 'Валерий', lastName: 'Иванов' }],
      onSelectUser, onSubmitSearch,
    });
    const input = container.querySelector('input');
    act(() => input.focus());
    act(() => input.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true })));
    expect(onSelectUser).not.toHaveBeenCalled();
    expect(onSubmitSearch).not.toHaveBeenCalled();
    act(() => input.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowDown', bubbles: true })));
    act(() => input.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true })));
    expect(onSelectUser).toHaveBeenCalledWith(expect.objectContaining({ id: 'u-valera' }), 'Валерий Иванов');
  });

  test('direct matching name is highlighted, without HTML injection or added width outside dropdown', () => {
    renderSearch({ query: 'Вале' });
    act(() => container.querySelector('input').focus());
    const bold = container.querySelector('[role="option"] span.font-bold');
    expect(bold?.textContent).toBe('Вале');
    expect(container.querySelector('[data-testid="admin-user-search"] > div[role="listbox"]')).toBeNull();
  });

});
