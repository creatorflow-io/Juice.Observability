import { NgComponentOutlet } from '@angular/common';
import { ChangeDetectionStrategy, Component, Type, computed, effect, inject, input, signal } from '@angular/core';
import { ContextService } from '../core/context.service';
import { ConsoleSlot, SlotKind, slotsOf } from './console-module';
import { InstalledModules, OBSERVABILITY_MODULES } from './installed-modules';

interface Loaded { key: string; component: Type<unknown>; inputs: Record<string, unknown> }

/**
 * The slots of one kind on a page, from the modules installed on the server and compiled into this build: Overview panels, service sections or alert
 * sections. Each is a component its module's library loads lazily; a failing one is left out, and never takes the page with it.
 */
@Component({
  selector: 'jc-module-slots',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [NgComponentOutlet],
  template: `
    @for (slot of loaded(); track slot.key) {
      <div class="jc-module-slot" [attr.data-slot]="slot.key">
        <ng-container *ngComponentOutlet="slot.component; inputs: slot.inputs" />
      </div>
    }`,
})
export class ModuleSlots {
  private readonly installed = inject(InstalledModules);
  private readonly libraries = inject(OBSERVABILITY_MODULES, { optional: true }) ?? [];
  private readonly ctx = inject(ContextService);

  readonly kind = input.required<SlotKind>();
  /** The service or alert the page shows (`serviceId` or `alertId` of the slot's inputs); none on the Overview. */
  readonly subjectId = input<string | null>(null);

  /** Which slots show: the module is installed, in `order` then `key`. The core is always installed. */
  protected readonly wanted = computed(() => {
    const names = this.installed.names();
    const slots: { module: string; slot: ConsoleSlot }[] = [];
    for (const library of this.libraries) {
      if (library.name !== 'core' && !names.has(library.name)) continue;
      for (const slot of slotsOf(library, this.kind())) slots.push({ module: library.name, slot });
    }
    return slots.sort((a, b) => a.slot.order - b.slot.order || a.slot.key.localeCompare(b.slot.key));
  });

  private readonly inputs = computed<Record<string, unknown>>(() => {
    const common = { environment: this.ctx.environment(), range: this.ctx.range() };
    const id = this.subjectId();
    return this.kind() === 'service' ? { serviceId: id, ...common } : this.kind() === 'alert' ? { alertId: id, ...common } : common;
  });

  private readonly requested = new Set<string>();
  private readonly components = signal(new Map<string, Type<unknown>>());
  protected readonly loaded = computed<Loaded[]>(() => this.wanted()
    .map(w => ({ key: `${w.module}.${w.slot.key}`, component: this.components().get(`${w.module}.${w.slot.key}`), inputs: this.inputs() }))
    .filter((l): l is Loaded => !!l.component));

  constructor() {
    effect(() => {
      for (const { module, slot } of this.wanted()) {
        const key = `${module}.${slot.key}`;
        if (this.requested.has(key)) continue;
        this.requested.add(key);
        slot.component().then(
          component => this.components.update(map => new Map(map).set(key, component)),
          () => console.warn(`[observability] slot '${key}' failed to load and is left out`));
      }
    });
  }
}
