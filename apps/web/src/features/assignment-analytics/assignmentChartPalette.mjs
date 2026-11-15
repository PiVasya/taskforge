// SVG/HTML colors use the live CSS tokens, so switching palettes never needs a chart remount.
export const chartColors = Object.freeze({
  passed: 'rgb(var(--accent-700))',
  failed: 'rgb(var(--accent2))',
  pending: 'rgba(var(--text-muted) / 0.65)',
  axes: 'rgb(var(--text-muted))',
  grid: 'rgba(var(--border) / 0.38)',
  status: 'rgb(var(--accent3))',
});

export function resultFill(name) {
  if (name === 'Успешные') return chartColors.passed;
  if (name === 'Неуспешные') return chartColors.failed;
  return chartColors.pending;
}
