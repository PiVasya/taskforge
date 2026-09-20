export function asAdminHistoryPage(value) {
  if (!value || typeof value !== 'object') return { items: [], total: 0, resultUserId: '' };
  const items = Array.isArray(value.items) ? value.items : [];
  const totalValue = Number(value.total);
  return {
    items,
    total: Number.isFinite(totalValue) && totalValue >= 0 ? totalValue : items.length,
    resultUserId: String(value.resultUserId || ''),
  };
}

export function exactUserHistoryPage(value, expectedUserId) {
  const page = asAdminHistoryPage(value);
  const expected = String(expectedUserId || '').trim().toLowerCase();
  if (!expected) return { ...page, items: [] };

  if (page.resultUserId && page.resultUserId.trim().toLowerCase() !== expected) {
    return { items: [], total: 0, resultUserId: page.resultUserId };
  }

  const hasForeignOrUnattributedItem = page.items.some((item) => {
    const actual = String(item?.userId ?? item?.UserId ?? '').trim().toLowerCase();
    return !actual || actual !== expected;
  });

  // Fail closed. A mixed or unattributed page is more dangerous than an empty
  // admin result because it could make an operator attribute somebody else's
  // code to the selected student.
  if (hasForeignOrUnattributedItem) {
    return { items: [], total: 0, resultUserId: page.resultUserId || expectedUserId };
  }

  return page;
}

export function removeAdminHistoryItem(value, predicate) {
  const page = asAdminHistoryPage(value);
  const nextItems = page.items.filter(predicate);
  const removed = page.items.length - nextItems.length;
  return { ...page, items: nextItems, total: Math.max(0, page.total - removed) };
}

export function paginateAdminHistoryPage(value, page, pageSize) {
  const history = asAdminHistoryPage(value);
  const safeSize = Math.max(1, Number(pageSize) || 50);
  const totalPages = Math.max(1, Math.ceil(history.items.length / safeSize));
  const safePage = Math.min(Math.max(1, Number(page) || 1), totalPages);
  const start = (safePage - 1) * safeSize;
  return {
    ...history,
    items: history.items.slice(start, start + safeSize),
    total: history.items.length,
  };
}
