import { acceptedSources, dailySeries, languageLabel, successPercent } from './publicStatisticsModel';

describe('public user statistics', () => {
  const utcNow = Date.UTC(2026, 9, 24, 14, 30);

  test('fills missing UTC days with zeros and preserves pending results', () => {
    const rows = dailySeries([
      { day: '2026-10-22', accepted: 3, failed: 2, pending: 1 },
      { day: '2026-10-24', accepted: 1 },
    ], 7, utcNow);
    expect(rows).toHaveLength(7);
    expect(rows.at(-3)).toMatchObject({ day: '2026-10-22', accepted: 3, failed: 2, pending: 1 });
    expect(rows.at(-2)).toMatchObject({ day: '2026-10-23', accepted: 0, failed: 0, pending: 0 });
    expect(rows.at(-1)).toMatchObject({ day: '2026-10-24', accepted: 1 });
  });

  test('success rate excludes pending and returns zero when empty', () => {
    expect(successPercent(7, 3)).toBe(70);
    expect(successPercent(0, 0)).toBe(0);
    expect(successPercent(9, 0)).toBe(100);
  });

  test('names languages and accepted sources without exposing identifiers', () => {
    expect(languageLabel('cpp')).toBe('C++');
    expect(languageLabel('sql')).toBe('SQL');
    expect(acceptedSources([{ kind: 'task', acceptedAttempts: 12 }, { kind: 'image', acceptedAttempts: 2 }]))
      .toEqual([{ label: 'Тесты и математика', value: 12 }, { label: 'Изображения', value: 2 }]);
  });
});
