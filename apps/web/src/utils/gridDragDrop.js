function getGridColumnCount(grid) {
  if (!grid || typeof window === 'undefined') return 1;
  const template = window.getComputedStyle(grid).gridTemplateColumns || '';
  if (!template || template === 'none') return 1;
  return template.split(/\s+/).filter(Boolean).length || 1;
}

function resolveAxisDropIntent(value, start, size, beforeEdge, afterEdge, allowInside) {
  const relative = size > 0 ? (value - start) / size : 0.5;

  if (allowInside && relative >= 0.28 && relative <= 0.72) {
    return { mode: 'inside', edge: '' };
  }

  const before = relative < 0.5;
  return before
    ? { mode: 'before', edge: beforeEdge }
    : { mode: 'after', edge: afterEdge };
}

export function resolveCardDropIntent(event, { allowInside = false } = {}) {
  const target = event?.currentTarget;
  if (!target?.getBoundingClientRect) return { mode: 'before', edge: 'top' };

  const rect = target.getBoundingClientRect();
  const grid = target.closest?.('.auto-fill-grid');
  const multiColumn = getGridColumnCount(grid) > 1;

  if (multiColumn) {
    return resolveAxisDropIntent(event.clientX, rect.left, rect.width, 'left', 'right', allowInside);
  }

  return resolveAxisDropIntent(event.clientY, rect.top, rect.height, 'top', 'bottom', allowInside);
}

export function resolveFlowNodeDropIntent(event, { allowInside = false } = {}) {
  const target = event?.currentTarget;
  if (!target?.getBoundingClientRect) return { mode: 'before', edge: 'left' };
  const rect = target.getBoundingClientRect();
  return resolveAxisDropIntent(event.clientX, rect.left, rect.width, 'left', 'right', allowInside);
}

function makeRows(entries) {
  const rows = [];
  for (const entry of entries) {
    const rect = entry.rect;
    const centerY = rect.top + rect.height / 2;
    const last = rows[rows.length - 1];
    if (!last) {
      rows.push({ items: [entry], top: rect.top, bottom: rect.bottom, centerY });
      continue;
    }

    const tolerance = Math.max(10, Math.min(rect.height, last.bottom - last.top) * 0.35);
    if (Math.abs(centerY - last.centerY) <= tolerance) {
      last.items.push(entry);
      last.top = Math.min(last.top, rect.top);
      last.bottom = Math.max(last.bottom, rect.bottom);
      last.centerY = last.items.reduce((sum, item) => sum + item.rect.top + item.rect.height / 2, 0) / last.items.length;
    } else {
      rows.push({ items: [entry], top: rect.top, bottom: rect.bottom, centerY });
    }
  }
  return rows;
}

function before(item, edge = 'left') {
  return { key: item.key, mode: 'before', edge };
}

function after(item, edge = 'right') {
  return { key: item.key, mode: 'after', edge };
}

export function resolveGridGapDropIntent(grid, clientX, clientY, selector, draggedKey, keyFromElement) {
  if (!grid?.querySelectorAll) return null;

  const entries = Array.from(grid.querySelectorAll(selector))
    .map((element) => ({
      element,
      key: keyFromElement(element),
      rect: element.getBoundingClientRect(),
    }))
    .filter((entry) => entry.key && entry.key !== draggedKey && entry.rect.width > 0 && entry.rect.height > 0);

  if (entries.length === 0) return null;

  const rows = makeRows(entries);
  const firstRow = rows[0];
  const lastRow = rows[rows.length - 1];

  if (clientY < firstRow.top) return before(firstRow.items[0], 'top');
  if (clientY > lastRow.bottom) return after(lastRow.items[lastRow.items.length - 1], 'bottom');

  for (let i = 0; i < rows.length - 1; i += 1) {
    const row = rows[i];
    const next = rows[i + 1];
    if (clientY > row.bottom && clientY < next.top) {
      const distanceToUpper = clientY - row.bottom;
      const distanceToLower = next.top - clientY;
      return distanceToUpper <= distanceToLower
        ? after(row.items[row.items.length - 1], 'bottom')
        : before(next.items[0], 'top');
    }
  }

  let row = rows.find((candidate) => clientY >= candidate.top && clientY <= candidate.bottom);
  if (!row) {
    row = rows.reduce((best, candidate) => (
      Math.abs(candidate.centerY - clientY) < Math.abs(best.centerY - clientY) ? candidate : best
    ), rows[0]);
  }

  const items = [...row.items].sort((a, b) => a.rect.left - b.rect.left);
  for (const item of items) {
    const centerX = item.rect.left + item.rect.width / 2;
    if (clientX < centerX) return before(item, 'left');
  }
  return after(items[items.length - 1], 'right');
}

export function isPointerInsideDndItem(event, selector) {
  const element = event?.target?.closest?.(selector);
  return Boolean(element && event.currentTarget?.contains?.(element));
}
