/**
 * The app's colours, as `observability.colors($theme)` wrote them in `--jc-*` custom properties, for what CSS cannot
 * colour itself: the charts (ECharts) and the dependency graph (Cytoscape) draw on their own.
 */
export function themeColor(name: string, element: Element = document.body): string {
  return resolveColor(rawProperty(name, element), element);
}

/** The categorical colours (`$series`): services in a trace, lines in a chart. */
export function seriesColors(element: Element = document.body): string[] {
  const count = Number(rawProperty('series-count', element)) || 0;
  return Array.from({ length: count }, (_, i) => themeColor(`series-${i + 1}`, element)).filter(Boolean);
}

/**
 * Calls back when the app switches theme: a class change on `<html>` or `<body>` (juice-layout's dark mode toggle), or
 * a palette change (`data-juice-palette`). Returns the function that stops listening.
 */
export function onThemeChange(callback: () => void): () => void {
  const observer = new MutationObserver(callback);
  for (const element of [document.documentElement, document.body]) {
    observer.observe(element, { attributes: true, attributeFilter: ['class', 'data-juice-palette'] });
  }
  return () => observer.disconnect();
}

function rawProperty(name: string, element: Element): string {
  return getComputedStyle(element).getPropertyValue(`--jc-${name}`).trim();
}

let canvas: CanvasRenderingContext2D | null | undefined;

/**
 * A colour the charts can draw with. Following juice-layout, a property holds CSS the browser resolves where it is
 * used (`light-dark(…)`, `color-mix(…)`), which ECharts and Cytoscape don't parse: resolve it on an element under
 * `element` (so its light or dark scheme applies), then paint it on a pixel and read it back as `#rrggbb` or `rgba(…)`
 * (the browser may write a computed colour as `color(srgb …)`, even through a canvas's fillStyle).
 */
export function resolveColor(value: string, element: Element = document.body): string {
  if (!value || /^(#[0-9a-f]{3,8}|rgba?\([^()]*\))$/i.test(value)) return value;
  const probe = document.createElement('span');
  probe.style.display = 'none';
  probe.style.color = value;
  element.appendChild(probe);
  const computed = getComputedStyle(probe).color;
  probe.remove();
  if (!computed || /^(#[0-9a-f]{3,8}|rgba?\([^()]*\))$/i.test(computed)) return computed || value;
  canvas ??= Object.assign(document.createElement('canvas'), { width: 1, height: 1 }).getContext('2d', { willReadFrequently: true });
  if (!canvas) return computed;
  canvas.clearRect(0, 0, 1, 1);
  canvas.fillStyle = computed;
  canvas.fillRect(0, 0, 1, 1);
  const [r, g, b, a] = canvas.getImageData(0, 0, 1, 1).data;
  return a === 255
    ? '#' + [r, g, b].map(c => c.toString(16).padStart(2, '0')).join('')
    : `rgba(${r}, ${g}, ${b}, ${+(a / 255).toFixed(3)})`;
}
