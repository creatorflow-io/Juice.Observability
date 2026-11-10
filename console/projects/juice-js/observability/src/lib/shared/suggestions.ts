import { ChangeDetectionStrategy, Component, inject, input, output, signal } from '@angular/core';
import { DatePipe, DecimalPipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { RouterLink } from '@angular/router';
import { ApiService } from '../core/api.service';
import { ContextService } from '../core/context.service';
import { CorrelationDecision, Suggestion, Suggestions } from '../core/models';
import { LinkPipe } from '../core/options';
import { problemText } from './telemetry';

/**
 * Alerts suggested together (Phase 9, R9), on the alert and incident pages: each with its score and every piece of evidence and its weight,
 * labelled as correlation. An operator accepts (merge into one incident) or dismisses with a reason; nothing merges on its own.
 */
@Component({
  selector: 'jc-suggestions',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [LinkPipe, RouterLink, DatePipe, DecimalPipe, FormsModule, MatButtonModule, MatFormFieldModule, MatInputModule],
  template: `
    @if (suggestions(); as s) {
      <p class="jc-notice" data-testid="suggestions-note">{{ s.note }}</p>
      <table class="jc-table"><tbody>
        @for (x of s.items; track x.alert.id + x.other.id) {
          <tr data-testid="suggestion">
            <td>
              <a [routerLink]="['alerts', x.other.id] | jcLink" [queryParams]="ctx.keep()"><strong>{{ x.other.rule }}</strong></a>
              <div class="jc-small">{{ x.other.subjectKey }} · {{ x.other.severity }} · fired {{ x.other.firedAt | date: 'HH:mm:ss' }}</div>
              @if (withAlert()) { <div class="jc-muted jc-small">with {{ x.alert.rule }} on {{ x.alert.subjectKey }}</div> }
            </td>
            <td class="jc-nowrap"><strong data-testid="suggestion-score">{{ x.score | number: '1.2-2' }}</strong></td>
            <td class="jc-small"><ul class="jc-reasons">@for (e of x.evidence; track e.code) { <li>{{ e.text }} <span class="jc-muted">(+{{ e.weight | number: '1.2-2' }})</span></li> }</ul></td>
            <td class="jc-row-actions">
              @if (dismissing() === key(x)) {
                <form class="jc-filter-form" (ngSubmit)="dismiss(x)">
                  <mat-form-field subscriptSizing="dynamic"><mat-label>Why not related</mat-label><input matInput name="reason" data-testid="suggestion-reason" [(ngModel)]="reason" maxlength="500" required></mat-form-field>
                  <button mat-stroked-button type="submit" data-testid="suggestion-dismiss-confirm" [disabled]="sending() || !reason.trim()">Dismiss</button>
                  <button mat-button type="button" (click)="dismissing.set('')">Cancel</button>
                </form>
              } @else {
                <button mat-flat-button type="button" data-testid="suggestion-accept" [disabled]="sending()" (click)="accept(x)">{{ acceptLabel(x) }}</button>
                <button mat-button type="button" data-testid="suggestion-dismiss" [disabled]="sending()" (click)="dismissing.set(key(x)); reason = ''">Dismiss</button>
              }
            </td>
          </tr>
        } @empty { <tr><td class="jc-muted">No alerts fired with this one closely enough, or with enough in common, to suggest (a score of {{ s.threshold | number: '1.1-1' }} or more).</td></tr> }
      </tbody></table>
      @if (error()) { <p class="jc-error" data-testid="suggestion-error">{{ error() }}</p> }
    }`,
})
export class SuggestionsCard {
  protected readonly ctx = inject(ContextService);
  private readonly api = inject(ApiService);
  readonly suggestions = input<Suggestions | undefined>();
  /** On an incident page, the alert in the incident each suggestion goes with. */
  readonly withAlert = input(false);
  /** The incident the page shows, so accepting into it says "this incident". */
  readonly incident = input<string | null>(null);
  /** A decision made, or null when one was refused as stale (decided meanwhile, no longer suggested): the page reads the list again. */
  readonly decided = output<CorrelationDecision | null>();
  protected readonly sending = signal(false);
  protected readonly error = signal('');
  protected readonly dismissing = signal('');
  protected reason = '';

  protected key(x: Suggestion) { return `${x.alert.id}:${x.other.id}`; }

  protected acceptLabel(x: Suggestion) {
    if (!x.incidentId) return 'Merge into a new incident';
    return x.incidentId === this.incident() ? 'Add to this incident' : 'Add to its incident';
  }

  protected accept(x: Suggestion) { this.run(this.api.acceptSuggestion(x.alert.id, x.other.id)); }

  protected dismiss(x: Suggestion) { this.run(this.api.dismissSuggestion(x.alert.id, x.other.id, this.reason.trim())); }

  private run(request: ReturnType<ApiService['acceptSuggestion']>) {
    this.sending.set(true);
    this.error.set('');
    request.subscribe({
      next: d => { this.sending.set(false); this.dismissing.set(''); this.decided.emit(d); },
      error: e => {
        const status = (e as { status?: number }).status;
        this.sending.set(false);
        this.error.set(status === 403 ? 'Deciding needs the operator or admin role.' : problemText(e));
        // The row was stale: someone decided it, or it stopped being suggested. Show the list as it is now, keeping the reason why.
        if (status === 409) { this.dismissing.set(''); this.decided.emit(null); }
      },
    });
  }
}
