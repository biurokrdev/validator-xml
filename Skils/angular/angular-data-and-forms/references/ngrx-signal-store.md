# NgRx SignalStore quick reference

Read this when the hand-written signal store from `SKILL.md` is being copied into a third feature,
when the app needs entity collections (lists keyed by id with add/update/remove helpers), or when
the team wants Redux DevTools. SignalStore keeps the same mental model (private state, public
computed, methods that patch state) with less boilerplate and a consistent shape across features.

Install: `npm i @ngrx/signals @ngrx/operators` (match the major version to Angular).

## Minimal store

```ts
// features/orders/data/orders.store.ts
import { computed, inject } from '@angular/core';
import { patchState, signalStore, withComputed, withHooks, withMethods, withState } from '@ngrx/signals';
import { rxMethod } from '@ngrx/signals/rxjs-interop';
import { tapResponse } from '@ngrx/operators';
import { debounceTime, distinctUntilChanged, pipe, switchMap, tap } from 'rxjs';

type OrdersState = { orders: OrderDto[]; filter: string; loading: boolean; error: ApiError | null };
const initialState: OrdersState = { orders: [], filter: '', loading: false, error: null };

export const OrdersStore = signalStore(
  withState(initialState),
  withComputed(({ orders }) => ({
    count: computed(() => orders().length),
    open: computed(() => orders().filter(o => o.status === 'open')),
  })),
  withMethods((store, api = inject(OrdersApi)) => ({
    setFilter(filter: string) { patchState(store, { filter }); },
    load: rxMethod<string>(
      pipe(
        debounceTime(300),
        distinctUntilChanged(),
        tap(() => patchState(store, { loading: true, error: null })),
        switchMap(status =>
          api.list({ status }).pipe(
            tapResponse({
              next: page => patchState(store, { orders: page.items, loading: false }),
              error: (error: ApiError) => patchState(store, { error, loading: false }),
            }),
          ),
        ),
      ),
    ),
  })),
  withHooks({
    onInit(store) { store.load(store.filter); },   // passing a signal re-runs load when it changes
  }),
);
```

Provide it like any service: `providers: [OrdersStore]` in the feature routes (state resets when
the feature is left) or `{ providedIn: 'root' }` as the second argument of `signalStore` for global
state. Components read `store.orders()`, `store.loading()` and call `store.setFilter('open')`.

## Entities

```ts
import { addEntity, removeEntity, setAllEntities, updateEntity, withEntities } from '@ngrx/signals/entities';

export const OrdersStore = signalStore(
  withEntities<OrderDto>(),            // exposes entities(), entityMap(), ids()
  withMethods(store => ({
    setAll(orders: OrderDto[]) { patchState(store, setAllEntities(orders)); },
    upsert(order: OrderDto) { patchState(store, updateEntity({ id: order.id, changes: order })); },
    remove(id: string) { patchState(store, removeEntity(id)); },
  })),
);
```

Entities require an `id` property (or pass `{ selectId }`); use them whenever the UI edits rows in
place, because `updateEntity` keeps the rest of the array referentially stable and `@for ... track`
re-renders one row.

## Shared store features

Repeated concerns (request status, pagination, selection) become reusable `signalStoreFeature`s:

```ts
export function withRequestStatus() {
  return signalStoreFeature(
    withState<{ status: 'idle' | 'loading' | 'success' | 'error'; error: ApiError | null }>({ status: 'idle', error: null }),
    withComputed(({ status }) => ({ loading: computed(() => status() === 'loading') })),
  );
}
export const setLoading = () => ({ status: 'loading' as const, error: null });
export const setError = (error: ApiError) => ({ status: 'error' as const, error });
```

Then `signalStore(withEntities<OrderDto>(), withRequestStatus(), withMethods(...))`.

## Testing

Instantiate through `TestBed` with the API mocked:

```ts
TestBed.configureTestingModule({ providers: [OrdersStore, { provide: OrdersApi, useValue: { list: vi.fn().mockReturnValue(of(page)) } }] });
const store = TestBed.inject(OrdersStore);
store.load('open');
vi.advanceTimersByTime(300);          // with vi.useFakeTimers() for the debounce
expect(store.orders()).toEqual(page.items);
```

Assert on public signals, not on `patchState` calls (see `angular-testing`).

## When not to use it

- A single feature with one list and one filter: the plain service store in `SKILL.md` is enough.
- Server state that is purely read and keyed by signals: `httpResource` already does the job.
- Form state: keep it in Reactive Forms; copying every keystroke into a store doubles the work.
