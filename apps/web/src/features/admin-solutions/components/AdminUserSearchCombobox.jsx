import React, { useEffect, useId, useMemo, useRef, useState } from 'react';
import { Check, Search, X } from 'lucide-react';
import {
  liveUserSuggestions,
  normalizeUserSearch,
  searchedUserSuggestions,
  searchMatchLabel,
} from '../adminSolutionLiveModel';

function emphasizeMatch(text, normalizedQuery) {
  const value = String(text || '');
  const needle = String(normalizedQuery || '');
  if (!needle || !value || needle.length > value.length) return value;
  const haystack = normalizeUserSearch(value);
  const offset = haystack.indexOf(needle);
  if (offset < 0 || offset + needle.length > value.length) return value;
  return (
    <>
      {value.slice(0, offset)}
      <span className="font-bold text-[rgb(var(--accent))]">{value.slice(offset, offset + needle.length)}</span>
      {value.slice(offset + needle.length)}
    </>
  );
}

// Search is immediate and local in the realtime feed. On history tabs the
// parent debounces the existing admin-user API; selecting a person still uses
// their exact UUID for history/deletion/group operations.
export default function AdminUserSearchCombobox({
  query,
  onQueryChange,
  onSelectUser,
  onClear,
  selectedUser,
  live = false,
  liveItems = [],
  users = [],
  searchedQuery = '',
  searchLoading = false,
  onSubmitSearch,
  disabled = false,
}) {
  const [open, setOpen] = useState(false);
  const [activeIndex, setActiveIndex] = useState(-1);
  const rootRef = useRef(null);
  const inputId = useId();
  const listId = `${inputId}-suggestions`;
  const normalizedQuery = normalizeUserSearch(query);
  const canSuggest = live ? !!normalizedQuery : normalizedQuery.length >= 2;
  const fetchedCurrentQuery = normalizedQuery.length >= 2 && normalizeUserSearch(searchedQuery) === normalizedQuery;
  const suggestions = useMemo(() => {
    if (!canSuggest) return [];
    const local = live ? liveUserSuggestions(liveItems, query, 24) : [];
    const remote = fetchedCurrentQuery ? searchedUserSuggestions(users, query, 24) : [];
    const byId = new Map();
    for (const candidate of [...local, ...remote]) {
      const key = candidate.id.toLowerCase();
      const existing = byId.get(key);
      if (!existing || existing.match.score < candidate.match.score) byId.set(key, candidate);
    }
    return [...byId.values()]
      .sort((left, right) => right.match.score - left.match.score ||
        left.label.localeCompare(right.label, 'ru-RU'))
      .slice(0, live ? 20 : 12);
  }, [canSuggest, fetchedCurrentQuery, live, liveItems, query, users]);
  const expanded = open && canSuggest && !disabled && !selectedUser;

  useEffect(() => {
    if (!open) return undefined;
    const closeOnOutside = (event) => {
      if (!rootRef.current?.contains(event.target)) setOpen(false);
    };
    document.addEventListener('pointerdown', closeOnOutside);
    return () => document.removeEventListener('pointerdown', closeOnOutside);
  }, [open]);

  useEffect(() => { setActiveIndex(-1); }, [query, live, searchedQuery]);
  useEffect(() => { if (disabled) setOpen(false); }, [disabled]);

  const chooseUser = (candidate) => {
    onSelectUser(candidate.user, candidate.label);
    setOpen(false);
    setActiveIndex(-1);
  };

  const handleKeyDown = (event) => {
    if (event.altKey || event.ctrlKey || event.metaKey || event.isComposing) return;
    if (event.key === 'Escape') {
      if (open) {
        event.preventDefault();
        setOpen(false);
      }
      return;
    }
    if ((event.key === 'ArrowDown' || event.key === 'ArrowUp') && canSuggest && !selectedUser) {
      event.preventDefault();
      setOpen(true);
      if (!suggestions.length) return;
      const direction = event.key === 'ArrowDown' ? 1 : -1;
      setActiveIndex((current) => current < 0 ? (direction === 1 ? 0 : suggestions.length - 1)
        : (current + direction + suggestions.length) % suggestions.length);
      return;
    }
    if (event.key === 'Enter' && canSuggest && !selectedUser) {
      if (expanded && suggestions.length && activeIndex >= 0) {
        event.preventDefault();
        chooseUser(suggestions[activeIndex]);
      } else if (live) {
        // Enter must not silently turn a fuzzy filter into an exact user selection.
        event.preventDefault();
        setOpen(false);
      } else if (!fetchedCurrentQuery && normalizedQuery.length >= 2) {
        event.preventDefault();
        onSubmitSearch?.();
        setOpen(true);
      }
    }
  };

  return (
    <div ref={rootRef} className="relative min-w-0" data-testid="admin-user-search">
      <label htmlFor={inputId} className="mb-1 block text-xs uppercase tracking-wide text-neutral-500">
        Поиск пользователя
      </label>
      <div className="relative">
        <Search size={16} aria-hidden="true" className="pointer-events-none absolute left-3 top-1/2 -translate-y-1/2 text-neutral-500" />
        <input
          id={inputId}
          className="input w-full min-w-0 pl-9 pr-9"
          type="text"
          inputMode="search"
          enterKeyHint="search"
          autoComplete="off"
          autoCorrect="off"
          spellCheck={false}
          maxLength={100}
          placeholder={live ? 'Имя, логин, опечатки — поиск по эфиру' : 'Имя, фамилия, логин или email'}
          value={query}
          disabled={disabled}
          onFocus={() => setOpen(true)}
          onChange={(event) => {
            onQueryChange(event.target.value);
            setOpen(true);
          }}
          onKeyDown={handleKeyDown}
          onBlur={(event) => {
            if (!rootRef.current?.contains(event.relatedTarget)) setOpen(false);
          }}
          role="combobox"
          aria-autocomplete="list"
          aria-expanded={expanded}
          aria-controls={expanded ? listId : undefined}
          aria-activedescendant={expanded && activeIndex >= 0 && suggestions[activeIndex]
            ? `${listId}-${activeIndex}` : undefined}
        />
        {query ? (
          <button
            type="button"
            className="absolute right-2 top-1/2 inline-flex h-7 w-7 -translate-y-1/2 items-center justify-center rounded-md text-neutral-500 hover:bg-neutral-200 hover:text-neutral-900 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-1 focus-visible:outline-[rgb(var(--accent))] dark:hover:bg-neutral-700 dark:hover:text-white"
            onClick={() => { onClear(); setOpen(false); }}
            disabled={disabled}
            title="Очистить поиск и выбор пользователя"
            aria-label="Очистить поиск и выбор пользователя"
          >
            <X size={16} aria-hidden="true" />
          </button>
        ) : null}
      </div>
      {expanded ? (
        <div className="absolute left-0 right-0 z-[70] mt-1 max-h-72 overflow-y-auto rounded-xl border border-neutral-300 bg-[rgb(var(--card))] p-1 shadow-xl dark:border-neutral-700">
          <div id={listId} role="listbox" aria-label="Подходящие пользователи">
          {suggestions.map((candidate, index) => (
            <button
              key={candidate.id}
              type="button"
              id={`${listId}-${index}`}
              role="option"
              aria-selected={index === activeIndex}
              tabIndex={-1}
              onMouseDown={(event) => event.preventDefault()}
              onPointerDown={(event) => {
                // On touchscreens, the input may blur before a synthetic click.
                // Select the result before the dropdown can be unmounted.
                if (event.pointerType === 'touch' || event.pointerType === 'pen') {
                  event.preventDefault();
                  chooseUser(candidate);
                }
              }}
              onClick={() => chooseUser(candidate)}
              onMouseEnter={() => setActiveIndex(index)}
              className={`flex w-full min-w-0 items-center gap-2 rounded-lg px-2 py-2 text-left transition-colors ${index === activeIndex ? 'bg-neutral-200 dark:bg-neutral-700' : 'hover:bg-neutral-100 dark:hover:bg-neutral-800'}`}
            >
              <span aria-hidden="true" className="flex h-9 w-9 shrink-0 items-center justify-center rounded-lg bg-neutral-200 text-sm font-semibold text-neutral-700 dark:bg-neutral-700 dark:text-neutral-100">
                {candidate.label.trim().charAt(0).toLocaleUpperCase('ru-RU') || '?'}
              </span>
              <span className="min-w-0 flex-1">
                <span className="flex min-w-0 items-center gap-2">
                  <span className="min-w-0 flex-1 truncate text-sm font-semibold">
                    {emphasizeMatch(candidate.label, candidate.match.highlight)}
                  </span>
                  {searchMatchLabel(candidate.match) ? (
                    <span className="shrink-0 text-[10px] text-neutral-500 dark:text-neutral-400">
                      {searchMatchLabel(candidate.match)}
                    </span>
                  ) : null}
                </span>
                <span className="block truncate text-xs text-neutral-500 dark:text-neutral-400">
                  {emphasizeMatch([candidate.login ? `@${candidate.login}` : '', candidate.email].filter(Boolean).join(' · ') || `ID: ${candidate.id.slice(0, 12)}`, candidate.match.highlight)}
                </span>
              </span>
              {index === activeIndex ? <Check size={16} className="shrink-0" aria-hidden="true" /> : null}
            </button>
          ))}
          {!suggestions.length ? (
            <div className="px-3 py-3 text-sm text-neutral-600 dark:text-neutral-300" role="status">
              {!fetchedCurrentQuery && live
                ? 'Среди последних событий совпадений нет. Попробуй часть имени или другую раскладку.'
                : !fetchedCurrentQuery || searchLoading
                  ? 'Ищу пользователей…'
                  : 'Пользователи не найдены.'}
            </div>
          ) : null}
          </div>
          {live && normalizedQuery.length >= 2 && !fetchedCurrentQuery ? (
            <button
              type="button"
              className="w-full rounded-lg border-t border-neutral-200 px-3 py-2 text-left text-xs font-medium text-neutral-700 hover:bg-neutral-100 dark:border-neutral-700 dark:text-neutral-200 dark:hover:bg-neutral-800"
              onMouseDown={(event) => event.preventDefault()}
              onClick={() => onSubmitSearch?.()}
            >
              Искать во всех аккаунтах
            </button>
          ) : null}
          <div className="border-t border-neutral-200 px-3 py-2 text-xs text-neutral-500 dark:border-neutral-700 dark:text-neutral-400">
            {live ? 'Поиск учитывает опечатки и раскладку. Аккаунт выбирается отдельно.' : 'Совпадения ранжируются; аккаунт выбирается по точному ID.'}
          </div>
        </div>
      ) : null}
    </div>
  );
}
