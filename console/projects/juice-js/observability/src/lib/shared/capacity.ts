import { ChangeDetectionStrategy, Component, computed, inject, input, linkedSignal, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { ApiService } from '../core/api.service';
import { ContextService } from '../core/context.service';
import { Capacity } from '../core/models';
import { apiResource } from './resource';
import { problemText } from './telemetry';

export interface CapacityInputs {
  users: number;
  /** Seconds between one user's actions. */
  thinkSeconds: number;
  /** Requests or messages one user action causes in this service. */
  perAction: number;
  /** The CPU use to stay under, from 0 to 1. */
  targetCpu: number;
  coresPerInstance: number;
}

export interface CapacityPlan {
  perSecond: number;
  instances: number;
  cores: number;
  /** Per instance; null without memory data. */
  memoryBytes: number | null;
  /** The load each instance would carry. */
  perInstance: number;
  /** That load over the highest measured: above 1 is beyond what was measured. */
  beyondMeasured: number | null;
  /** What set the number of instances: CPU, or a consumer that was already busy all the time. */
  limitedBy: 'cpu' | 'messages';
}

/**
 * The calculator: `null` when the inputs or the measurements can't give an answer, such as a target that idle CPU alone
 * already exceeds. Each instance can carry (target × cores − idle) ÷ CPU per unit, unless a consumer was seen busy all the
 * time: more CPU doesn't help one that handles a message at a time, so an instance carries only the rate it reached.
 */
export function planCapacity(m: Capacity, i: CapacityInputs): CapacityPlan | null {
  if (!m.cpuSecondsPerUnit || m.cpuSecondsPerUnit <= 0) return null;
  if (!(i.users > 0 && i.perAction > 0 && i.targetCpu > 0 && i.targetCpu <= 1 && i.coresPerInstance > 0 && i.thinkSeconds >= 0)) return null;
  const response = (m.responseMs ?? 0) / 1000;
  if (i.thinkSeconds + response <= 0) return null;

  const byCpu = (i.targetCpu * i.coresPerInstance - (m.idleCores ?? 0)) / m.cpuSecondsPerUnit;
  if (byCpu <= 0) return null;
  const limitedBy = consumerBusy(m) && m.peakMessagesPerSecond < byCpu ? 'messages' : 'cpu';
  const carried = limitedBy === 'messages' ? m.peakMessagesPerSecond : byCpu;
  const perSecond = i.users * i.perAction / (i.thinkSeconds + response);
  const instances = Math.max(1, Math.ceil(perSecond / carried));
  const perInstance = perSecond / instances;
  const memoryBytes = m.memoryModel === 'fitted'
    ? m.idleMemoryBytes! + m.memoryBytesPerInFlight! * perInstance * response
    : m.peakMemoryBytes;
  // A consumer held back by message handling never used its cores: count what it used, at the target, not the machine's.
  const coresEach = limitedBy === 'messages' && m.peakCores !== null
    ? Math.min(i.coresPerInstance, Math.ceil(m.peakCores / i.targetCpu))
    : i.coresPerInstance;
  return {
    perSecond, instances, cores: instances * coresEach, memoryBytes, perInstance,
    beyondMeasured: m.peakPerSecond > 0 ? perInstance / m.peakPerSecond : null,
    limitedBy,
  };
}

/** A consumer counts as busy all the time from this much message processing per second of time. */
const BUSY_CONSUMER = 0.9;
function consumerBusy(m: Capacity): boolean { return m.peakMessagesInFlight >= BUSY_CONSUMER && m.peakMessagesPerSecond > 0; }

/** A thread pool queue of a few items comes and goes on a healthy host; from this many, work was really waiting. */
const NOTABLE_QUEUE = 10;

/** Response time counts as rising with load from this ratio between the busiest and quietest samples. */
const RISING_RESPONSE = 1.5;

function bytes(value: number | null): string {
  if (value === null) return '—';
  return value >= 1024 ** 3 ? `${(value / 1024 ** 3).toFixed(1)} GB` : `${Math.round(value / 1024 ** 2)} MB`;
}
function rate(value: number): string { return value >= 10 ? value.toFixed(0) : value.toFixed(2); }

/**
 * A service's Capacity tab: what it was measured to need in the selected window, and a calculator from users to instances,
 * cores and memory. The estimate says where it comes from and where it stops holding: scaling isn't linear, and the
 * service's own databases and brokers are not in it.
 */
@Component({
  selector: 'jc-service-capacity',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, MatFormFieldModule, MatInputModule, MatProgressBarModule],
  template: `
    @if (capacity.isLoading()) { <mat-progress-bar mode="indeterminate" /> }
    @if (capacity.error()) { <p class="jc-error">{{ error() }}</p> }
    @if (mixedEnvironments()) {
      <p class="jc-notice">This service runs in more than one environment, and their hardware may differ. Choose an environment for an estimate that means something.</p>
    }
    @if (capacity.value(); as m) {
      @if (m.samples === 0) {
        <p class="jc-muted">No telemetry from this service in this window.</p>
      } @else {
        <section class="jc-card" aria-labelledby="jc-capacity-estimate">
          <h3 id="jc-capacity-estimate">Estimate</h3>
          <div class="jc-capacity-inputs">
            <mat-form-field subscriptSizing="dynamic"><mat-label>Concurrent users</mat-label>
              <input matInput type="number" min="1" name="users" [ngModel]="users()" (ngModelChange)="users.set($event)" /></mat-form-field>
            <mat-form-field subscriptSizing="dynamic"><mat-label>Seconds between actions</mat-label>
              <input matInput type="number" min="0" name="thinkSeconds" [ngModel]="thinkSeconds()" (ngModelChange)="thinkSeconds.set($event)" /></mat-form-field>
            <mat-form-field subscriptSizing="dynamic"><mat-label>Requests per action</mat-label>
              <input matInput type="number" min="0" name="perAction" [ngModel]="perAction()" (ngModelChange)="perAction.set($event)" /></mat-form-field>
            <mat-form-field subscriptSizing="dynamic"><mat-label>Target CPU use (%)</mat-label>
              <input matInput type="number" min="1" max="100" name="targetCpu" [ngModel]="targetCpu()" (ngModelChange)="targetCpu.set($event)" /></mat-form-field>
            <mat-form-field subscriptSizing="dynamic"><mat-label>Cores per instance</mat-label>
              <input matInput type="number" min="1" name="coresPerInstance" [ngModel]="coresPerInstance()" (ngModelChange)="coresPerInstance.set($event)" /></mat-form-field>
          </div>
          @if (plan(); as p) {
            <div class="jc-capacity-result" data-testid="capacity-plan">
              <div><strong>{{ p.instances }}</strong><span class="jc-muted">instance{{ p.instances === 1 ? '' : 's' }}</span></div>
              <div><strong>{{ p.cores }}</strong><span class="jc-muted">cores in total</span></div>
              <div><strong>{{ memory(p.memoryBytes) }}</strong><span class="jc-muted">memory per instance{{ m.memoryModel === 'peak' ? ' (highest seen)' : '' }}</span></div>
              <div><strong>{{ perSecond(p.perSecond) }}</strong><span class="jc-muted">requests and messages/s</span></div>
            </div>
            @if (p.beyondMeasured !== null && p.beyondMeasured > 1) {
              <p class="jc-notice" data-testid="capacity-beyond">Each instance would carry {{ perSecond(p.perInstance) }}/s, {{ p.beyondMeasured.toFixed(1) }}× the highest measured ({{ perSecond(m.peakPerSecond) }}/s). This is an extrapolation, and only a load test can confirm it.</p>
            }
          } @else if (m.cpuModel === 'none') {
            <p class="jc-notice">No CPU data, so no estimate. CPU needs two readings at least a minute apart: choose a longer window, or wait. If it stays empty, add <code>AddMeter("System.Runtime")</code> to the host's metrics (.NET 9+).</p>
          } @else {
            <p class="jc-notice">No estimate for these values. Check them: idle CPU alone may already exceed the target.</p>
          }
          <ul class="jc-capacity-notes">
            @if (responseRising(); as r) {
              <li class="jc-bad">Response time already rises with load: {{ r.low }} ms when quiet, {{ r.high }} ms when busy. Something may be near its limit, so the real capacity may be lower.</li>
            }
            @if (busyConsumer()) {
              <li class="jc-bad" data-testid="capacity-busy-consumer">Message processing was busy nearly all the time, at about {{ perSecond(m.peakMessagesPerSecond) }} messages/s per instance. If it handles one message at a time, that is its limit whatever CPU says, and its queue may be growing. The estimate uses that rate. See which step takes the time in a trace.</li>
            }
            @if (m.outbox?.fallingBehind) {
              <li class="jc-bad" data-testid="capacity-outbox-behind">Outbox delivery is falling behind: {{ count(m.outbox!.undelivered) }} messages created in this window weren't delivered. CPU and requests can look healthy while this grows, and the estimate doesn't include it. If another service delivers this outbox, this doesn't apply.</li>
            }
            @if (m.peakQueue !== null && m.peakQueue >= notableQueue) {
              <li class="jc-bad">The thread pool queue reached {{ m.peakQueue }}: work was waiting for a thread.</li>
            }
            <li>Assumes cost grows in a straight line with load. A database, lock or connection limit can be reached long before CPU runs out.</li>
            <li>Covers this service only, not its databases, brokers or the services it calls.</li>
          </ul>
        </section>

        <section class="jc-card" aria-labelledby="jc-capacity-measured">
          <h3 id="jc-capacity-measured">Measured in this window</h3>
          <table class="jc-table jc-capacity-measured">
            <tbody>
              <tr><th>CPU per request or message</th>
                <td>
                  @if (m.cpuSecondsPerUnit !== null) { {{ (m.cpuSecondsPerUnit * 1000).toFixed(1) }} ms }
                  @else { <span class="jc-muted">No CPU data yet</span> }
                  @if (m.cpuModel === 'average') {
                    <div class="jc-muted jc-small">Measured in the busiest samples and includes idle CPU, so it can read high. Load didn't vary enough, or CPU didn't follow it in a straight line, to separate the two.</div>
                  }
                </td></tr>
              <tr><th>Idle CPU</th><td>@if (m.idleCores !== null) { {{ m.idleCores.toFixed(2) }} cores } @else { <span class="jc-muted">Not separated</span> }</td></tr>
              <tr><th>Idle memory</th><td>@if (m.idleMemoryBytes !== null) { {{ memory(m.idleMemoryBytes) }} } @else { <span class="jc-muted">Not separated</span> }</td></tr>
              <tr><th>Memory growth under load</th><td>@if (m.memoryBytesPerInFlight !== null) { {{ memory(m.memoryBytesPerInFlight) }} per request in flight } @else { <span class="jc-muted">Not separated</span> }</td></tr>
              <tr><th>Highest load, one instance</th><td>{{ perSecond(m.peakPerSecond) }} requests and messages/s</td></tr>
              <tr><th>Highest use, one instance</th><td>
                {{ m.peakCores !== null ? m.peakCores.toFixed(2) + ' cores' : 'CPU unknown' }} · {{ m.peakMemoryBytes !== null ? memory(m.peakMemoryBytes) : 'memory unknown' }}</td></tr>
              <tr><th>Average response</th><td>{{ m.responseMs !== null ? m.responseMs.toFixed(0) + ' ms' : '—' }}</td></tr>
              <tr><th>Measured on</th><td>
                {{ m.instances }} instance{{ m.instances === 1 ? '' : 's' }}@if (m.coresPerInstance) {, {{ m.coresPerInstance }} cores each}@if (m.hosts.length) {, on {{ m.hosts.join(', ') }}}.
                <div class="jc-muted jc-small">Figures from other hardware carry over only roughly.</div></td></tr>
              <tr><th>Based on</th><td>
                {{ m.samples }} samples of {{ m.stepSeconds }} s.
                <div class="jc-muted jc-small">{{ sourceNote() }}</div>
                @if (m.truncated) { <div class="jc-bad jc-small">Too many samples in this window, so only the newest were used. Choose a shorter one.</div> }</td></tr>
            </tbody>
          </table>
        </section>

        @if (m.outbox; as o) {
          <section class="jc-card" aria-labelledby="jc-capacity-outbox" data-testid="capacity-outbox">
            <h3 id="jc-capacity-outbox">Outbox delivery</h3>
            <table class="jc-table jc-capacity-measured">
              <tbody>
                <tr><th>Messages created</th><td>{{ perSecond(o.createdPerSecond) }}/s</td></tr>
                <tr><th>Messages delivered</th><td>{{ perSecond(o.deliveredPerSecond) }}/s</td></tr>
                <tr><th>Created but not delivered</th><td [class.jc-bad]="o.fallingBehind">{{ count(o.undelivered) }} in this window
                  <div class="jc-muted jc-small">Includes messages of rolled-back transactions, which are never delivered.</div></td></tr>
                <tr><th>Time to delivery, p95</th><td [class.jc-bad]="o.fallingBehind">@if (o.recentDelaySeconds !== null) { {{ o.recentDelaySeconds.toFixed(1) }} s in the last 5 minutes } @else { <span class="jc-muted">Nothing delivered in the last 5 minutes</span> }</td></tr>
              </tbody>
            </table>
          </section>
        }
      }
    }`,
})
export class ServiceCapacity {
  private readonly ctx = inject(ContextService);
  private readonly api = inject(ApiService);
  readonly serviceId = input.required<string>();
  /** The environments the service runs in: figures across several mix their hardware. */
  readonly environments = input<string[]>([]);

  protected readonly capacity = apiResource({
    params: () => ({ id: this.serviceId(), env: this.ctx.environment(), range: this.ctx.range(), abs: this.ctx.absolute() }),
    stream: ({ params }) => this.api.capacity({ serviceId: params.id, environment: params.env, ...this.ctx.window() }),
  });
  protected readonly users = signal(1000);
  protected readonly thinkSeconds = signal(10);
  protected readonly perAction = signal(1);
  protected readonly targetCpu = signal(60);
  protected readonly coresPerInstance = linkedSignal(() => this.capacity.value()?.coresPerInstance ?? 2);

  protected readonly error = computed(() => problemText(this.capacity.error()));
  protected readonly mixedEnvironments = computed(() => !this.ctx.environment() && new Set(this.environments()).size > 1);
  protected readonly plan = computed(() => {
    const m = this.capacity.value();
    return m ? planCapacity(m, {
      users: this.users(), thinkSeconds: this.thinkSeconds(), perAction: this.perAction(), targetCpu: this.targetCpu() / 100, coresPerInstance: this.coresPerInstance(),
    }) : null;
  });
  protected readonly busyConsumer = computed(() => { const m = this.capacity.value(); return !!m && consumerBusy(m); });
  protected readonly responseRising = computed(() => {
    const m = this.capacity.value();
    return m?.responseLowLoadMs && m.responseHighLoadMs && m.responseHighLoadMs >= RISING_RESPONSE * m.responseLowLoadMs
      ? { low: Math.round(m.responseLowLoadMs), high: Math.round(m.responseHighLoadMs) } : null;
  });
  protected readonly sourceNote = computed(() => this.capacity.value()?.requestSource === 'metrics'
    ? 'Requests are counted from the http.server.request.duration metric, messages from spans.'
    : 'Requests and messages are counted from spans, which undercount if traces are sampled. That makes the cost per request read high. Exporting http.server.request.duration fixes the request count.');

  protected readonly notableQueue = NOTABLE_QUEUE;
  protected readonly memory = bytes;
  protected readonly perSecond = rate;
  protected readonly count = (value: number) => Math.round(value).toLocaleString('en-US');
}
