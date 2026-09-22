import { canConfirmCourseDeletion, normalizedCourseDeleteTitle } from './courseDeleteConfirmation';

describe('course deletion confirmation', () => {
  test('requires the exact course title and an explicit acknowledgement', () => {
    expect(canConfirmCourseDeletion({ expectedTitle: 'Основы Python', typedTitle: 'Основы Python', acknowledged: true })).toBe(true);
    expect(canConfirmCourseDeletion({ expectedTitle: 'Основы Python', typedTitle: 'основы Python', acknowledged: true })).toBe(false);
    expect(canConfirmCourseDeletion({ expectedTitle: 'Основы Python', typedTitle: 'Основы Python', acknowledged: false })).toBe(false);
  });

  test('allows harmless surrounding whitespace but blocks deletion while busy', () => {
    expect(normalizedCourseDeleteTitle('  Курс  ')).toBe('Курс');
    expect(canConfirmCourseDeletion({ expectedTitle: 'Курс', typedTitle: '  Курс  ', acknowledged: true })).toBe(true);
    expect(canConfirmCourseDeletion({ expectedTitle: 'Курс', typedTitle: 'Курс', acknowledged: true, busy: true })).toBe(false);
  });

  test('never enables confirmation without a real course title', () => {
    expect(canConfirmCourseDeletion({ expectedTitle: '', typedTitle: '', acknowledged: true })).toBe(false);
  });
});
