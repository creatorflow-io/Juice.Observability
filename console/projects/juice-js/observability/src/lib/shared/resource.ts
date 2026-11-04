import { ResourceRef, Signal, computed, isSignal } from '@angular/core';
import { RxResourceOptions, rxResource } from '@angular/core/rxjs-interop';

/** A resource whose `value()` is `undefined` while its request failed, instead of throwing. */
export type ApiResource<T> = Omit<ResourceRef<T | undefined>, 'value'> & { readonly value: Signal<T | undefined> };

/**
 * An `rxResource` for an API read. Angular's `value()` throws while the resource is in error, so a failed request (a
 * trace not found, a 500, the API unreachable) would break the page's rendering, and with it every overlay such as a
 * select's options. Here `value()` reads `undefined` then, and `error()` says why.
 */
export function apiResource<T, P>(options: RxResourceOptions<T, P>): ApiResource<T> {
  const ref = rxResource<T, P>(options);
  const value = computed(() => (ref.hasValue() ? ref.value() : undefined));
  return new Proxy(ref, {
    get(target, prop) {
      if (prop === 'value') return value;
      const member = Reflect.get(target, prop, target);
      return typeof member === 'function' && !isSignal(member) ? member.bind(target) : member;
    },
  }) as unknown as ApiResource<T>;
}
