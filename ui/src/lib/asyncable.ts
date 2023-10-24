import type { Writable, Unsubscriber, Subscriber } from 'svelte/store';
import { derived, writable, get } from 'svelte/store';

type AnyFunction = (...args: any[]) => any;

function asyncable(
  getter: AnyFunction,
  setter: AnyFunction = () => { },
  stores: any[] = []
): {
  subscribe: (run: Subscriber<any>) => () => void;
  update: (reducer: AnyFunction) => Promise<void>;
  set: (newValue: any) => Promise<void>;
  get: () => Promise<any>;
} {
  let resolve: (value: any) => void;
  const initial = new Promise<any>((res) => (resolve = res));

  const derived$ = derived(stores, (values) => values);

  const store$: Writable<Promise<any>> = writable(initial, (set) => {
    return derived$.subscribe(async (values = []) => {
      let value = getter(...values);
      if (value === undefined) return;
      value = Promise.resolve(value);
      set(value);
      resolve(value);
    });
  });

  async function set(newValue: any, oldValue: any) {
    if (newValue === oldValue) return;
    store$.set(Promise.resolve(newValue));
    try {
      await setter(newValue, oldValue);
    } catch (err) {
      store$.set(Promise.resolve(oldValue));
      throw err;
    }
  }

  return {
    subscribe: store$.subscribe,
    async update(reducer: AnyFunction) {
      if (!setter) return;
      let oldValue: any;
      let newValue: any;
      try {
        oldValue = await get(store$);
        newValue = await reducer(shallowCopy(oldValue));
      } finally {
        await set(newValue, oldValue);
      }
    },
    async set(newValue: any) {
      if (!setter) return;
      let oldValue: any;
      try {
        oldValue = await get(store$);
        newValue = await newValue;
      } finally {
        await set(newValue, oldValue);
      }
    },
    get() {
      return get(store$);
    },
  };
}

function syncable(stores: any[], initialValue: any) {
  return derived(
    stores,
    ($values: any[], set: (value: any) => void): void | Unsubscriber => {
      (Array.isArray(stores) ? Promise.allSettled : Promise.resolve)
        .call(Promise, $values)
        .then(set)
    },
    initialValue
  );
}

function shallowCopy(value: any): any {
  if (typeof value !== 'object' || value === null) return value;
  return Array.isArray(value) ? [...value] : { ...value };
}

export { asyncable, syncable };
