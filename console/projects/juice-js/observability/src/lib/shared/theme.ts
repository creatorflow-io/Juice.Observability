/**
 * The app's colours, as `observability.colors($theme)` wrote them in `--jc-*` custom properties, for what CSS cannot
 * colour itself: the charts (ECharts) and the dependency graph (Cytoscape) draw on their own.
 */
export function themeColor(name: string, element: Element = document.body): string {
  return getComputedStyle(element).getPropertyValue(`--jc-${name}`).trim();
}

/** The categorical colours (`$series`): services in a trace, lines in a chart. */
export function seriesColors(element: Element = document.body): string[] {
  const count = Number(themeColor('series-count', element)) || 0;
  return Array.from({ length: count }, (_, i) => themeColor(`series-${i + 1}`, element)).filter(Boolean);
}

/**
 * Calls back when the app switches theme: a class change on `<html>` or `<body>`, as juice-layout's dark-mode toggle
 * makes. Returns the function that stops listening.
 */
export function onThemeChange(callback: () => void): () => void {
  const observer = new MutationObserver(callback);
  for (const element of [document.documentElement, document.body]) {
    observer.observe(element, { attributes: true, attributeFilter: ['class'] });
  }
  return () => observer.disconnect();
}
