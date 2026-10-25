import { acceptedSources, dailySeries, languageLabel, resultTypes, successPercent } from './publicStatisticsModel';

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

  test('result types include all assignment kinds with equal verdict categories', () => {
    const rows = resultTypes([
      { kind: 'math', attempts: 5, accepted: 3, failed: 2, pending: 0 },
      { kind: 'code', attempts: 8, accepted: 4, failed: 3, pending: 1 },
      { kind: 'test', attempts: 7, accepted: 6, failed: 1, pending: 0 },
      { kind: 'image', attempts: 2, accepted: 1, failed: 1, pending: 0 },
      { kind: 'sql', attempts: 4, accepted: 2, failed: 2, pending: 0 },
    ]);
    expect(rows.map((r) => r.label)).toEqual([
      'Программирование', 'SQL', 'Изображения', 'Тесты', 'Математика',
    ]);
    expect(rows.reduce((sum, r) => sum + r.attempts, 0)).toBe(26);
    expect(rows.find((r) => r.kind === 'code')).toMatchObject({ accepted: 4, failed: 3, pending: 1 });
  });

  test('names languages and accepted sources without exposing identifiers', () => {
    expect(languageLabel('cpp')).toBe('C++');
    expect(languageLabel('sql')).toBe('SQL');
    expect(acceptedSources([{ kind: 'task', acceptedAttempts: 12 }, { kind: 'image', acceptedAttempts: 2 }]))
      .toEqual([{ label: 'Тесты и математика', value: 12 }, { label: 'Изображения', value: 2 }]);
  });
});
