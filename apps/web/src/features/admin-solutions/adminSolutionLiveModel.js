// Live-feed search uses only identities attached to already received events.
// The same bounded ranking rules can order server-provided candidates.
export function normalizeUserSearch(value) {
  return String(value ?? '').normalize('NFKD').toLocaleLowerCase('ru-RU')
    .replace(/и\u0306/g, 'й').replace(/[\u0300-\u036f]/g, '').replace(/ё/g, 'е')
    .replace(/\s+/g, ' ').trim().slice(0, 160);
}

const MIXED_TO_RU = { a: 'а', e: 'е', o: 'о', p: 'р', c: 'с', x: 'х', y: 'у', k: 'к', m: 'м', h: 'н', b: 'в', t: 'т' };
const MIXED_TO_EN = Object.fromEntries(Object.entries(MIXED_TO_RU).map(([en, ru]) => [ru, en]));
const EN_KEYS = "qwertyuiop[]asdfghjkl;'zxcvbnm,.";
const RU_KEYS = 'йцукенгшщзхъфывапролджэячсмитьбю';
const EN_TO_RU = Object.fromEntries([...EN_KEYS].map((char, i) => [char, RU_KEYS[i]]));
const RU_TO_EN = Object.fromEntries([...RU_KEYS].map((char, i) => [char, EN_KEYS[i]]));
const CYRILLIC_LATIN = {
  а: 'a', б: 'b', в: 'v', г: 'g', д: 'd', е: 'e', ж: 'zh', з: 'z', и: 'i',
  й: 'y', к: 'k', л: 'l', м: 'm', н: 'n', о: 'o', п: 'p', р: 'r', с: 's',
  т: 't', у: 'u', ф: 'f', х: 'kh', ц: 'ts', ч: 'ch', ш: 'sh', щ: 'shch',
  ъ: '', ы: 'y', ь: '', э: 'e', ю: 'yu', я: 'ya',
};
const LATIN_DIGRAPHS = [
  ['shch', 'щ'], ['sch', 'щ'], ['shh', 'щ'], ['zh', 'ж'], ['kh', 'х'],
  ['ch', 'ч'], ['sh', 'ш'], ['ts', 'ц'], ['yu', 'ю'], ['ju', 'ю'],
  ['ya', 'я'], ['ja', 'я'], ['yo', 'е'], ['jo', 'е'],
  ['ye', 'е'], ['je', 'е'], ['iy', 'ий'],
];
const LATIN_CYRILLIC = {
  a: 'а', b: 'б', c: 'к', d: 'д', e: 'е', f: 'ф', g: 'г', h: 'х',
  i: 'и', j: 'й', k: 'к', l: 'л', m: 'м', n: 'н', o: 'о', p: 'п',
  q: 'к', r: 'р', s: 'с', t: 'т', u: 'у', v: 'в', w: 'в', x: 'кс',
  y: 'й', z: 'з',
};

function changeKeyboardLayout(text) {
  if (/[а-я]/u.test(text) && !/[a-z]/u.test(text)) {
    return [...text].map((char) => RU_TO_EN[char] ?? char).join('');
  }
  if (/[a-z]/u.test(text) && !/[а-я]/u.test(text)) {
    return [...text].map((char) => EN_TO_RU[char] ?? char).join('');
  }
  return '';
}

function romanize(text) {
  if (/[а-я]/u.test(text) && !/[a-z]/u.test(text)) {
    return [...text].map((char) => CYRILLIC_LATIN[char] ?? char).join('');
  }
  if (!/[a-z]/u.test(text) || /[а-я]/u.test(text)) return '';
  let result = '';
  for (let i = 0; i < text.length;) {
    const pair = LATIN_DIGRAPHS.find(([part]) => text.startsWith(part, i));
    if (pair) {
      result += pair[1];
      i += pair[0].length;
    } else {
      result += LATIN_CYRILLIC[text[i]] ?? text[i];
      i++;
    }
  }
  return result;
}

function searchVariants(query) {
  const value = normalizeUserSearch(query);
  if (!value) return [];
  const variants = [{ text: value, via: 'direct', penalty: 0 }];
  // Never reinterpret a mixed-script account identifier or an email address.
  if (value.length >= 3 && !/[\d@:/\\._-]/u.test(value) && /[а-я]/u.test(value) && /[a-z]/u.test(value)) {
    const latinCount = (value.match(/[a-z]/gu) || []).length;
    const cyrillicCount = (value.match(/[а-я]/gu) || []).length;
    const corrected = latinCount < cyrillicCount
      ? value.replace(/[a-z]/gu, (char) => MIXED_TO_RU[char] || char)
      : value.replace(/[а-я]/gu, (char) => MIXED_TO_EN[char] || char);
    if (corrected !== value) variants.push({ text: corrected, via: 'mixed', penalty: 40 });
  } else if (value.length >= 3 && !/[\d@:/\\._-]/u.test(value)) {
    const switched = normalizeUserSearch(changeKeyboardLayout(value));
    const romanized = normalizeUserSearch(romanize(value));
    if (switched && switched !== value) variants.push({ text: switched, via: 'layout', penalty: 230 });
    if (romanized && romanized !== value && romanized !== switched) {
      variants.push({ text: romanized, via: 'translit', penalty: 240 });
    }
  }
  return variants;
}

// The server still performs ordinary substring search. Fallback queries are
// bounded: correcting a keyboard layout, romanization, or a likely typo may
// retrieve a small candidate set, but never triggers a full-directory scan.
export function userSearchServerQueries(query) {
  const value = normalizeUserSearch(query);
  if (value.length < 2) return [];
  const variants = searchVariants(value)
    .filter(({ via }) => via !== 'direct')
    .map(({ text }) => text);
  const tokens = value.split(/[^\p{L}\p{N}]+/u).filter(Boolean);
  const prefix = tokens.length === 1 && tokens[0].length >= 5 ? tokens[0].slice(0, 3) : '';
  return [...new Set([value, ...variants, prefix].filter(Boolean))].slice(0, 4);
}

function tokensOf(text) {
  return text.split(/[^\p{L}\p{N}]+/u).filter(Boolean).slice(0, 30);
}

// Optimal-string-alignment Damerau-Levenshtein: normal substitutions and
// insert/delete errors, plus adjacent swapped letters. Stop beyond the bound.
export function boundedUserEditDistance(left, right, bound = 2) {
  const a = String(left);
  const b = String(right);
  const max = Math.min(3, Math.max(0, Math.floor(Number(bound) || 0)));
  if (Math.abs(a.length - b.length) > max) return max + 1;
  if (a === b) return 0;
  let beforePrevious = null;
  let previous = Array.from({ length: b.length + 1 }, (_, index) => index);
  for (let i = 1; i <= a.length; i++) {
    const current = [i];
    let minimum = current[0];
    for (let j = 1; j <= b.length; j++) {
      let cost = Math.min(
        current[j - 1] + 1,
        previous[j] + 1,
        previous[j - 1] + (a[i - 1] === b[j - 1] ? 0 : 1),
      );
      if (i > 1 && j > 1 && a[i - 1] === b[j - 2] && a[i - 2] === b[j - 1]) {
        cost = Math.min(cost, beforePrevious[j - 2] + 1);
      }
      current[j] = cost;
      minimum = Math.min(minimum, cost);
    }
    if (minimum > max) return max + 1;
    beforePrevious = previous;
    previous = current;
  }
  return previous[b.length];
}

function fieldsFor(user, fallbackLabel = '') {
  const full = [user?.firstName, user?.middleName, user?.lastName].filter(Boolean).join(' ');
  const fullReversed = [user?.lastName, user?.firstName, user?.middleName].filter(Boolean).join(' ');
  const fields = [
    [full, 'name'], [fullReversed, 'name'],
    [user?.firstName, 'name'], [user?.middleName, 'name'], [user?.lastName, 'name'],
    [user?.displayName, 'name'], [user?.fullName, 'name'],
    [user?.name, 'name'], [fallbackLabel, 'name'],
    [user?.login, 'login'], [user?.userName, 'login'], [user?.username, 'login'],
    [user?.email, 'email'], [user?.id, 'id'],
  ];
  const seen = new Set();
  return fields.flatMap(([raw, type]) => {
    const text = normalizeUserSearch(raw);
    const key = `${type}:${text}`;
    if (!text || seen.has(key)) return [];
    seen.add(key);
    return [{ text, type, words: tokensOf(text) }];
  });
}

function bestTermScore(term, fields) {
  let best = null;
  const add = (score, kind) => {
    if (!best || score > best.score) best = { score, kind };
  };
  for (const field of fields) {
    const bonus = field.type === 'login' ? 20 : field.type === 'email' ? -25 : 0;
    if (field.type === 'id') {
      if (term.length >= 8 && field.text.startsWith(term)) add(term === field.text ? 1020 : 790, 'exact');
      continue;
    }
    if (field.text === term) add(1010 + bonus, 'exact');
    if (field.text.startsWith(term)) {
      const firstWord = field.words[0] || field.text;
      add(875 + bonus - Math.min(35, firstWord.length - term.length), 'start');
    }
    if (term.length >= 3 && field.text.includes(term)) add(725 + bonus, 'part');
    for (const word of field.words) {
      if (word === term) {
        add(955 + bonus, 'exact');
        continue;
      }
      if (word.startsWith(term)) {
        add(850 + bonus - Math.min(35, word.length - term.length), 'start');
        continue;
      }
      if (term.length >= 3 && word.includes(term)) {
        add(695 + bonus, 'part');
        continue;
      }
      if (term.length < 4 || field.type === 'email') continue;
      const allowed = term.length >= 13 ? 3 : term.length >= 7 ? 2 : 1;
      const lengths = new Set([word.length, term.length - 1, term.length, term.length + 1, term.length + 2]);
      for (const length of lengths) {
        if (length < 3 || length > word.length || Math.abs(length - term.length) > allowed) continue;
        const distance = boundedUserEditDistance(term, word.slice(0, length), allowed);
        if (distance > 0 && distance <= allowed) {
          add(620 + bonus - distance * 105 - Math.max(0, word.length - length) * 2, 'typo');
        }
      }
    }
  }
  return best;
}

export function rankUserMatch(user, query, fallbackLabel = '') {
  const normalizedQuery = normalizeUserSearch(query);
  const identity = normalizeUserSearch(user?.id);
  if (normalizedQuery.length >= 8 && identity.startsWith(normalizedQuery)) {
    return { score: normalizedQuery === identity ? 1150 : 950, kind: 'exact', highlight: '' };
  }
  const fields = fieldsFor(user, fallbackLabel);
  if (!fields.length) return null;
  let best = null;
  for (const variant of searchVariants(query)) {
    const terms = tokensOf(variant.text);
    if (!terms.length || terms.length > 6) continue;
    const scores = terms.map((term) => bestTermScore(term, fields));
    if (scores.some((score) => score === null)) continue;
    const whole = fields.some((field) => field.type !== 'id' && field.text === variant.text);
    const phrase = fields.some((field) => field.type !== 'id' && field.text.includes(variant.text));
    const score = Math.round(scores.reduce((sum, result) => sum + result.score, 0) / terms.length)
      + (whole ? 85 : phrase ? 24 : 0) - variant.penalty;
    const kind = scores.some((value) => value.kind === 'typo') ? 'typo'
      : scores.some((value) => value.kind === 'part') ? 'part'
        : scores.some((value) => value.kind === 'start') ? 'start' : 'exact';
    const result = {
      score,
      kind: variant.via !== 'direct' ? variant.via : kind,
      // Only the direct query can be highlighted character-for-character.
      highlight: variant.via === 'direct' ? variant.text : '',
    };
    if (!best || result.score > best.score) best = result;
  }
  return best;
}

export function userDisplayName(user, fallback = '') {
  const fullName = [user?.firstName, user?.lastName].filter(Boolean).join(' ').trim();
  return fullName || user?.displayName || user?.fullName || user?.name || user?.login || user?.userName || user?.email || fallback || '';
}

export function userMatchesSearch(user, query, fallbackLabel = '') {
  return !normalizeUserSearch(query) || !!rankUserMatch(user, query, fallbackLabel);
}

function matchingSuggestion(user, id, fallbackLabel, query) {
  const match = rankUserMatch({ ...(user || {}), id }, query, fallbackLabel);
  if (!match) return null;
  return {
    id, user: { ...(user || {}), id },
    label: userDisplayName(user, fallbackLabel || id),
    login: user?.login || user?.userName || user?.username || '',
    email: user?.email || '',
    match,
  };
}

function sortSuggestions(values, limit) {
  return values
    .sort((left, right) => right.match.score - left.match.score ||
      left.label.localeCompare(right.label, 'ru-RU') || left.id.localeCompare(right.id))
    .slice(0, Math.max(0, Number(limit) || 0));
}

export function liveUserSuggestions(items, query, limit = 8) {
  if (!normalizeUserSearch(query)) return [];
  const byId = new Map();
  for (const item of items || []) {
    const id = String(item?.userId || '').trim();
    if (!id) continue;
    const candidate = matchingSuggestion(item.user, id, item.userLabel, query);
    if (!candidate) continue;
    const key = id.toLowerCase();
    const current = byId.get(key);
    // Prefer the richest user metadata but preserve the strongest match.
    if (!current || current.match.score < candidate.match.score ||
      (current.match.score === candidate.match.score && candidate.label.length > current.label.length)) {
      byId.set(key, candidate);
    }
  }
  return sortSuggestions([...byId.values()], limit);
}

export function searchedUserSuggestions(users, query, limit = 8) {
  if (!normalizeUserSearch(query)) return [];
  const byId = new Map();
  for (const user of users || []) {
    const id = String(user?.id || '').trim();
    if (!id) continue;
    const candidate = matchingSuggestion(user, id, '', query);
    if (candidate && !byId.has(id.toLowerCase())) byId.set(id.toLowerCase(), candidate);
  }
  return sortSuggestions([...byId.values()], limit);
}

export function searchMatchLabel(match) {
  if (match?.kind === 'layout') return 'Исправлена раскладка';
  if (match?.kind === 'translit') return 'Транслитерация';
  if (match?.kind === 'mixed') return 'Исправлена раскладка';
  if (match?.kind === 'typo') return 'Возможная опечатка';
  return '';
}
export function filterLiveItems(items, { userId = '', groupId = '', groupUserIds = [], filterDays = 0, searchText = '', now = Date.now() } = {}) {
  const groupSet = new Set((groupUserIds || []).map((id) => String(id)));
  const days = Number(filterDays || 0);
  const since = days > 0 ? Number(now) - days * 24 * 60 * 60 * 1000 : null;

  const matchingIds = !userId && normalizeUserSearch(searchText)
    ? new Set(liveUserSuggestions(items, searchText, (items || []).length).map((candidate) => candidate.id.toLowerCase()))
    : null;

  return (items || []).filter((item) => {
    if (userId && String(item?.userId || '').toLowerCase() !== String(userId).toLowerCase()) return false;
    if (matchingIds && !matchingIds.has(String(item?.userId || '').toLowerCase())) return false;
    if (groupId && !groupSet.has(String(item?.userId || ''))) return false;
    if (since && new Date(item?.occurredAtUtc || 0).getTime() < since) return false;
    return true;
  });
}

export function filterLiveItemsByTab(items, tab) {
  const value = String(tab || 'live').toLowerCase();
  if (value === 'groups') return [];
  if (value === 'code' || value === 'sql') return (items || []).filter((item) => String(item?.kind || '').toLowerCase() === value);
  if (value === 'tests') return (items || []).filter((item) => String(item?.kind || '').toLowerCase() === 'test');
  if (value === 'images') return (items || []).filter((item) => String(item?.kind || '').toLowerCase() === 'image');
  if (value === 'math') return (items || []).filter((item) => String(item?.kind || '').toLowerCase() === 'math');
  return items || [];
}

export function solutionLiveStateLabel(state) {
  if (state === 'live') return 'В эфире';
  if (state === 'reconnecting') return 'Переподключение';
  return 'Подключение';
}

export function solutionKindLabel(kind) {
  const value = String(kind || '').toLowerCase();
  if (value === 'sql') return 'SQL';
  if (value === 'test') return 'Тест';
  if (value === 'math') return 'Математика';
  if (value === 'image') return 'Картинка';
  return 'Код';
}

export function solutionStatusLabel(status) {
  const value = String(status || '').trim().toLowerCase();
  if (!value) return null;
  if (['accepted', 'passed', 'success'].includes(value)) return 'Принято';
  if (['rejected', 'wronganswer', 'wrong_answer', 'failed'].includes(value)) return 'Не принято';
  if (['preparing', 'queued', 'running', 'pending', 'judging'].includes(value)) return 'Проверяется';
  if (value === 'compileerror' || value === 'compile_error') return 'Ошибка компиляции';
  if (value === 'runtimeerror' || value === 'runtime_error') return 'Ошибка выполнения';
  if (value === 'timelimitexceeded' || value === 'time_limit_exceeded') return 'Лимит времени';
  if (value === 'outputlimitexceeded' || value === 'output_limit_exceeded') return 'Лимит вывода';
  if (value === 'judgeunavailable' || value === 'judge_unavailable') return 'Проверка недоступна';
  if (value === 'policyfailed' || value === 'policy_failed') return 'Отклонено';
  return status;
}

export function isPendingLiveStatus(status) {
  return ['preparing', 'queued', 'running', 'pending', 'judging'].includes(String(status || '').trim().toLowerCase());
}

export function reconcileLiveDetail(item, detail) {
  if (!item || !detail || !isPendingLiveStatus(item.status)) return item;
  const status = detail.status || detail.verdict || null;
  if (!status) return item;
  return {
    ...item,
    detail,
    status,
    score: detail.score ?? detail.scorePercent ?? detail.similarityPercent ?? item.score ?? null,
  };
}

// An SSE event can arrive after its HTTP detail request, or without the detail at all.
// An immutable code submission cannot move back from a definitive verdict.
export function mergeLiveEvent(existing, incoming) {
  if (!existing || isPendingLiveStatus(existing.status)) return incoming;
  const nextStatus = String(incoming.status || '').trim().toLowerCase();
  const oldStatus = String(existing.status || '').trim().toLowerCase();
  const oldIsCode = String(existing.kind || incoming.kind || '').toLowerCase() === 'code';
  const weak = (value) => value === 'judgeunavailable' || value === 'judge_unavailable';
  const preserveTerminal = !nextStatus || isPendingLiveStatus(nextStatus) ||
    (oldIsCode && !weak(oldStatus) && oldStatus !== nextStatus);
  if (!preserveTerminal) return incoming;
  return { ...incoming, status: existing.status, score: existing.score, detail: existing.detail ?? incoming.detail };
}
