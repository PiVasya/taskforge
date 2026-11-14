export function makeMatrixThemePalette(darkTheme, foreground, accent) {
  return {
    head: darkTheme ? foreground : accent,
    trail: accent,
  };
}
