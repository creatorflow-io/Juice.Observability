import { LinkPipe, OBSERVABILITY_CAN_ADMIN } from '../core/options';
import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { MatButtonModule } from '@angular/material/button';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { apiResource } from '../shared/resource';
import { RouterLink } from '@angular/router';
import { distinctUntilChanged, forkJoin, map, skip } from 'rxjs';
import { ApiService } from '../core/api.service';
import { ContextService } from '../core/context.service';
import { Rule } from '../core/models';
import { AgoPipe } from '../shared/format';
import { ContextBar } from '../shared/ui';
import { problemText } from '../shared/telemetry';
import { RuleEditor, TEMPLATES } from './rule-editor';

/**
 * Rules and their current state (Console requirements §14); admins change them, everyone else views. They stay typed templates (D5): this page never
 * takes a query. A rule in the deployment's file, or the platform's own for an SLO, is shown but changed elsewhere. The form is {@link RuleEditor}.
 */
@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [LinkPipe, RouterLink, MatButtonModule, MatProgressBarModule, ContextBar, AgoPipe, RuleEditor],
  template: `
    <jc-context-bar>
      <h1>Rules</h1>
      <span class="jc-spacer"></span>
      @if (admin()) { <button mat-flat-button type="button" data-testid="rule-new" (click)="startNew()">New rule</button> }
    </jc-context-bar>
    @if (data.isLoading()) { <mat-progress-bar mode="indeterminate" /> }
    @if (data.error()) { <p class="jc-error">{{ error() }}</p> }
    @if (open(); as o) {
      @for (e of [o]; track e.id) {
        <jc-rule-editor [rule]="o.rule" [templates]="templates()" [packs]="data.value()?.packs ?? []" (saved)="saved()" (closed)="close()" />
      }
    }
    <table class="jc-table" data-testid="rules">
      <thead><tr><th>Rule</th><th>Template</th><th>Severity</th><th>Threshold</th><th>Recovery</th><th>Wait</th><th>Open alerts</th><th>Last evaluation</th><th></th></tr></thead>
      <tbody>
        @for (r of rows(); track r.key) {
          <tr [class.jc-muted]="!r.enabled">
            <td><strong>{{ r.name }}</strong><div class="jc-muted jc-small">{{ r.key }}@if (!r.enabled) { · disabled }@if (r.managedBy === 'config') { · from the rules file }@if (r.managedBy === 'platform') { · the platform's, for SLOs }</div>
              @if (r.quiet) { <div class="jc-error jc-small" [attr.data-testid]="'rule-quiet-' + r.key">Never observes: {{ r.quiet }}.</div> }</td>
            <td>{{ r.template }}</td>
            <td>{{ r.severity }}</td>
            <td>{{ r.definition['threshold'] ?? '—' }}</td>
            <td>{{ r.definition['recoveryThreshold'] ?? '—' }}</td>
            <td>{{ r.definition['forDuration'] ?? '—' }}</td>
            <td>
              @for (a of r.open; track a.id) {
                <div><a [routerLink]="['alerts', a.id] | jcLink" [queryParams]="ctx.keep()">{{ a.state }}</a> {{ a.subjectKey }} = {{ a.lastValue ?? 'no data' }}</div>
              } @empty { <span class="jc-muted">none</span> }
            </td>
            <td>{{ r.lastEvaluatedAt | ago }}</td>
            <td class="jc-row-actions">
              @if (r.managedBy === 'api' && admin()) { <button mat-button type="button" [attr.data-testid]="'rule-edit-' + r.key" (click)="edit(r)">Change</button> }
              @else { <button mat-button type="button" [attr.data-testid]="'rule-view-' + r.key" (click)="edit(r)">View</button> }
            </td>
          </tr>
        }
      </tbody>
    </table>`,
})
export class RulesPage {
  protected readonly ctx = inject(ContextService);
  private readonly api = inject(ApiService);
  private readonly canAdmin = inject(OBSERVABILITY_CAN_ADMIN);
  protected readonly admin = () => this.canAdmin();
  /** The form that is open, if any: a new rule or one of the list's. `id` is new for each opening, so the form starts from scratch every time. */
  protected readonly open = signal<{ id: number; rule: Rule | null } | null>(null);
  private opened = 0;
  protected readonly data = apiResource({
    stream: () => forkJoin({
      rules: this.api.rules().pipe(map(p => p.items)),
      firing: this.api.allAlerts({ state: 'firing' }).pipe(map(p => p.items)),
      pending: this.api.allAlerts({ state: 'pending' }).pipe(map(p => p.items)),
      packs: this.api.signalPacks(),
    }),
  });
  protected readonly error = computed(() => problemText(this.data.error()));
  /**
   * What a rule can be made from: SloBurn is described, but its rules are the platform's own, made for SLOs. A template of a signal pack that is
   * off is left out, except the one the rule being changed has, which it can still be saved with while disabled.
   */
  protected readonly templates = computed(() => {
    const off = new Set((this.data.value()?.packs ?? []).filter(p => !p.enabled).flatMap(p => p.ruleTemplates));
    const current = this.open()?.rule?.template;
    return Object.keys(TEMPLATES).filter(t => t !== 'SloBurn' && (!off.has(t) || t === current));
  });
  protected readonly rows = computed(() => {
    const d = this.data.value();
    if (!d) return [];
    const open = [...d.firing, ...d.pending];
    return d.rules.map(r => {
      const mine = open.filter(a => a.rule === r.key);
      const last = mine.map(a => a.lastEvaluatedAt).sort().at(-1) ?? null;
      return { ...r, open: mine, lastEvaluatedAt: last };
    });
  });

  constructor() {
    // A different tenant starts afresh: nothing drafted, previewed or refused for the last one stays on screen.
    this.api.tenant$.pipe(map(t => t.id), distinctUntilChanged(), skip(1), takeUntilDestroyed()).subscribe(() => { this.close(); this.data.reload(); });
  }

  protected startNew() { if (this.admin()) this.show(null); }
  protected edit(rule: Rule) { this.show(rule); }
  private show(rule: Rule | null) { this.open.set({ id: ++this.opened, rule }); }
  protected close() { this.open.set(null); }
  protected saved() { this.close(); this.data.reload(); }
}
