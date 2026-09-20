import {
  asAdminHistoryPage,
  exactUserHistoryPage,
  removeAdminHistoryItem,
  paginateAdminHistoryPage,
} from './adminSolutionHistoryModel';

describe('admin solution history model', () => {
  const alice = '11111111-1111-1111-1111-111111111111';
  const bob = '22222222-2222-2222-2222-222222222222';

  test('keeps total count for a page that belongs entirely to the selected user', () => {
    const page = exactUserHistoryPage({
      items: [{ id: 's1', userId: alice }, { id: 's2', userId: alice }],
      total: 137,
      resultUserId: alice,
    }, alice);

    expect(page.items).toHaveLength(2);
    expect(page.total).toBe(137);
  });

  test('fails closed when server response declares another user', () => {
    const page = exactUserHistoryPage({
      items: [{ id: 's1', userId: bob }],
      total: 25,
      resultUserId: bob,
    }, alice);

    expect(page.items).toEqual([]);
    expect(page.total).toBe(0);
  });

  test('fails closed when even one row belongs to another or has no user id', () => {
    const mixed = exactUserHistoryPage({
      items: [{ id: 's1', userId: alice }, { id: 's2', userId: bob }],
      total: 2,
      resultUserId: alice,
    }, alice);
    const unattributed = exactUserHistoryPage({
      items: [{ id: 's1' }],
      total: 1,
      resultUserId: alice,
    }, alice);

    expect(mixed.items).toEqual([]);
    expect(mixed.total).toBe(0);
    expect(unattributed.items).toEqual([]);
    expect(unattributed.total).toBe(0);
  });

  test('paginates a complete admin history locally without losing the total', () => {
    const items = Array.from({ length: 137 }, (_, index) => ({ id: `s${index + 1}`, userId: alice }));
    const exact = exactUserHistoryPage({ items, total: 137, resultUserId: alice }, alice);

    const first = paginateAdminHistoryPage(exact, 1, 50);
    const third = paginateAdminHistoryPage(exact, 3, 50);

    expect(first.items).toHaveLength(50);
    expect(first.items[0].id).toBe('s1');
    expect(first.total).toBe(137);
    expect(third.items).toHaveLength(37);
    expect(third.items[0].id).toBe('s101');
    expect(third.items[36].id).toBe('s137');
    expect(third.total).toBe(137);
  });

  test('removing a row updates the page total without making it negative', () => {
    expect(removeAdminHistoryItem({ items: [{ id: 's1' }, { id: 's2' }], total: 10 }, (item) => item.id !== 's1')).toEqual({
      items: [{ id: 's2' }],
      total: 9,
      resultUserId: '',
    });
    expect(asAdminHistoryPage(null)).toEqual({ items: [], total: 0, resultUserId: '' });
  });
});
