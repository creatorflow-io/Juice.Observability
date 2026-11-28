import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import {
  BUILTIN_MODULES, CORE_CONSOLE_MODULES, EvidenceSections, InstalledModules, ModuleSlots, OBSERVABILITY_MODULES, ServerModule, provideJuiceObservability, routeMatches,
} from '@juice-js/observability';
import { PING_LIBRARY, PING_SERVER, StubPage, settle } from './testing';

const TENANT = { items: [{ id: 't1', key: 'k', displayName: 'T' }] };

let http: HttpTestingController | undefined;

function setup(modules: readonly ServerModule[]) {
  TestBed.configureTestingModule({
    providers: [
      provideRouter([{ path: '**', component: StubPage }]), provideHttpClient(), provideHttpClientTesting(), provideJuiceObservability({ approvalCount: false }),
      { provide: OBSERVABILITY_MODULES, useValue: [...CORE_CONSOLE_MODULES, PING_LIBRARY] },
      { provide: InstalledModules, useValue: { modules: signal(modules), names: signal(new Set(modules.map(m => m.name))) } },
    ],
  });
  http = TestBed.inject(HttpTestingController);
  return http;
}

async function evidence(http: HttpTestingController, skipCore: boolean, sections: unknown[]) {
  const fixture = TestBed.createComponent(EvidenceSections);
  fixture.componentRef.setInput('target', 'alert');
  fixture.componentRef.setInput('subjectId', 'a1');
  fixture.componentRef.setInput('skipCore', skipCore);
  fixture.detectChanges();
  await settle(2);
  http.expectOne('/api/v1/tenants').flush(TENANT);
  await settle(2);
  http.expectOne('/api/v1/tenants/t1/alerts/a1/evidence-sections').flush({ items: sections });
  await settle(2);
  fixture.detectChanges();
  return fixture.nativeElement as HTMLElement;
}

const SECTIONS = [
  {
    key: 'core.changes', module: 'core', state: 'ok', title: 'Changes', note: null, truncated: false,
    items: [{ label: 'Deploy', value: 'v2', at: '2026-11-29T10:00:00Z', severity: 'warning', link: { route: 'services/abc', query: { tab: 'traces' } } }],
  },
  {
    key: 'ping.latency', module: 'ping', state: 'ok', title: '<b>Latency</b>', note: '<i>note</i>', truncated: true,
    items: [
      { label: '<img src=x onerror=alert(1)>', value: 'javascript:alert(1)', link: { route: 'ping/42' } },
      { label: 'slos link', link: { route: 'slos/api' } },
      { label: 'outside', link: { route: 'https://evil.example/x' } },
      { label: 'escape', link: { route: '../auth/x' } },
    ],
  },
  { key: 'ping.broken', module: 'ping', state: 'failed', title: 'Secret exception text', note: 'stack trace', truncated: false, items: [{ label: 'leak' }] },
];

describe('Evidence sections and slots (core 2.0 M3, E7 E9)', () => {
  afterEach(() => { http?.verify(); http = undefined; });

  it('evidence_sections_text_only_links_only_when_route_matches_failed_shows_generic', async () => {
    const dom = await evidence(setup([...BUILTIN_MODULES.filter(m => m.name === 'core'), PING_SERVER]), false, SECTIONS);
    // text only: markup in a title, note, label or value is shown as text, never parsed
    expect(dom.querySelector('b, i, img')).toBeNull();
    expect(dom.textContent).toContain('<b>Latency</b>');
    expect(dom.textContent).toContain('<img src=x onerror=alert(1)>');
    expect(dom.textContent).toContain('javascript:alert(1)');
    // a link only to a route of an installed module's manifest that the build defines
    const href = (text: string) => [...dom.querySelectorAll('a')].find(a => a.textContent?.includes(text))?.getAttribute('href');
    expect(href('Deploy')).toBe('/services/abc?tab=traces');
    expect(href('<img')).toBe('/ping/42');
    for (const label of ['slos link', 'outside', 'escape']) expect(href(label)).toBeUndefined();
    expect(dom.textContent).toContain('slos link');
    // a failed section shows one generic line, none of what came with it
    const failed = dom.querySelector('[data-testid="evidence-ping.broken"]')!;
    expect(failed.textContent?.trim()).toBe('This section is unavailable.');
    expect(dom.textContent).not.toContain('Secret exception text');
    expect(dom.textContent).not.toContain('stack trace');
    expect(dom.textContent).toContain('Only the first items are shown.');
  });

  it('alert_page_skips_core_sections', async () => {
    const dom = await evidence(setup(BUILTIN_MODULES), true, SECTIONS);
    expect(dom.querySelector('[data-testid="evidence-core.changes"]')).toBeNull();
    expect(dom.querySelector('[data-testid="evidence-ping.latency"]')).not.toBeNull();
  });

  it('shows nothing when the call fails (an older server)', async () => {
    const http = setup(BUILTIN_MODULES);
    const fixture = TestBed.createComponent(EvidenceSections);
    fixture.componentRef.setInput('target', 'alert');
    fixture.componentRef.setInput('subjectId', 'a1');
    fixture.detectChanges();
    await settle(2);
    http.expectOne('/api/v1/tenants').flush(TENANT);
    await settle(2);
    http.expectOne('/api/v1/tenants/t1/alerts/a1/evidence-sections').flush({}, { status: 404, statusText: 'Not Found' });
    await settle(2);
    fixture.detectChanges();
    expect((fixture.nativeElement as HTMLElement).textContent?.trim()).toBe('');
  });

  it('matches route patterns by segment, a parameter taking one segment', () => {
    expect(routeMatches('slos/api', 'slos/:key')).toBe(true);
    expect(routeMatches('slos', 'slos/:key')).toBe(false);
    expect(routeMatches('slos/a/b', 'slos/:key')).toBe(false);
    expect(routeMatches('alerts/x', 'slos/:key')).toBe(false);
  });

  it('test_module_menu_route_and_slots_appear (slots)', async () => {
    setup([...BUILTIN_MODULES, PING_SERVER]);
    const slots = async (kind: 'overview' | 'service' | 'alert', subjectId: string | null) => {
      const fixture = TestBed.createComponent(ModuleSlots);
      fixture.componentRef.setInput('kind', kind);
      fixture.componentRef.setInput('subjectId', subjectId);
      fixture.detectChanges();
      await settle(3);
      fixture.detectChanges();
      return (fixture.nativeElement as HTMLElement).querySelector('[data-testid="ping-slot"]')?.textContent;
    };
    // the inputs the spec gives each kind: environment and range, and the service or alert id
    expect(await slots('overview', null)).toBe('-||1h');
    expect(await slots('service', 's1')).toBe('s1||1h');
    expect(await slots('alert', 'a1')).toBe('a1||1h');
  });

  it('slots of a module the server does not list stay out', async () => {
    setup(BUILTIN_MODULES);
    const fixture = TestBed.createComponent(ModuleSlots);
    fixture.componentRef.setInput('kind', 'overview');
    fixture.detectChanges();
    await settle(3);
    fixture.detectChanges();
    expect((fixture.nativeElement as HTMLElement).querySelector('[data-testid="ping-slot"]')).toBeNull();
  });
});
