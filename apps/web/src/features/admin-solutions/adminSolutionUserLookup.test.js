import { searchAdminUsersSmart } from './adminSolutionUserLookup';
import { searchUsersOnce } from '../../api/admin';

jest.mock('../../api/admin', () => ({ searchUsersOnce: jest.fn() }));

describe('bounded server-backed account suggestions', () => {
  beforeEach(() => jest.resetAllMocks());

  test('does not fetch a directory for short or empty input', async () => {
    expect(await searchAdminUsersSmart('')).toEqual([]);
    expect(await searchAdminUsersSmart('а')).toEqual([]);
    expect(searchUsersOnce).not.toHaveBeenCalled();
  });

  test('exact matches stop extra queries and keep server UUIDs', async () => {
    searchUsersOnce.mockResolvedValue([{ id: 'user-1', firstName: 'Валерий' }]);
    const found = await searchAdminUsersSmart('Валерий');
    expect(found.map((u) => u.id)).toEqual(['user-1']);
    expect(searchUsersOnce).toHaveBeenCalledTimes(1);
  });

  test('wrong Russian keyboard recovers by one layout-corrected query', async () => {
    searchUsersOnce.mockImplementation(async (q) =>
      q === 'валерий' ? [{ id: 'user-1', firstName: 'Валерий' }] : []);
    const found = await searchAdminUsersSmart('Dfkthbq');
    expect(found.map((u) => u.id)).toEqual(['user-1']);
    expect(searchUsersOnce).toHaveBeenCalledTimes(2);
    expect(searchUsersOnce).toHaveBeenCalledWith('валерий', 30);
  });

  test('misspelled names use a bounded prefix query and are rechecked locally', async () => {
    searchUsersOnce.mockImplementation(async (q) =>
      q === 'вал' ? [
        { id: 'user-1', firstName: 'Валерий' },
        { id: 'user-2', firstName: 'Валентина' },
        { id: 'user-3', firstName: 'Пётр' },
      ] : []);
    const found = await searchAdminUsersSmart('Валреий');
    expect(found.map((u) => u.id)).toEqual(['user-1']);
    expect(searchUsersOnce.mock.calls.length).toBeLessThanOrEqual(4);
  });

  test('auxiliary request failure never loses confirmed primary matches', async () => {
    searchUsersOnce.mockImplementation(async (q) => {
      if (q === 'valeriy') return [{ id: 'user-1', firstName: 'Valery' }];
      throw new Error('network timeout');
    });
    const found = await searchAdminUsersSmart('valeriy');
    expect(found.map((u) => u.id)).toEqual(['user-1']);
  });
});
