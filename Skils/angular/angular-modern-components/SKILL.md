---
name: angular-modern-components
description: >-
  Write and structure Angular 20+ components the modern way: standalone components, signal
  inputs/outputs/model, computed, linkedSignal, effect (and when not to use it), resource and
  httpResource, the @if/@for/@switch/@defer control flow, OnPush by default, host metadata, inject(),
  lazy routes with loadComponent/loadChildren and route-level providers, feature folder layout
  (features/, core/, shared/), smart vs presentational split, SCSS conventions, accessibility basics
  and the migration schematics for older code. Use this skill whenever the user creates or edits an
  Angular component, directive, page, route or feature folder, asks "where should this file go",
  "generate a component", "convert to standalone/signals", "ngOnChanges vs computed", "*ngIf to @if",
  "OnPush", "zoneless", "lazy load this route", "two-way binding", or touches any .ts/.html/.scss
  file inside an Angular app, even if they only say "add a button that shows X".
---

# Modern Angular components and feature structure

This skill produces components that are small, OnPush-safe, zoneless-ready and easy to test: state
lives in signals, derived values in `computed`, templates use the built-in control flow and every
feature is lazy-loaded behind a route. Most "Angular is slow / flaky" complaints trace back to the
same handful of habits (`effect` used to derive state, logic in templates, `@for` without a stable
`track`, everything eagerly loaded in one folder). The workflow below is ordered so those habits are
caught before the first line of template is written.

## Workflow

1. **Read the project conventions first.** Check `package.json` for the Angular major version,
   `angular.json` for generator defaults (`schematics` section: style, file naming with or without
   `.component` suffix, `skipTests`), `app.config.ts` for zoneless / router features, and one
   existing feature folder. New code that mixes `@Input()` decorators with `input()` or `*ngIf` with
   `@if` in the same file makes reviews harder and blocks the automated migrations later.
2. **Decide where the code lives and what kind of component it is** (see *Folder structure* and
   *Smart vs presentational*). A routed page that talks to a store is a different artefact from a
   reusable card that only has inputs and outputs; deciding up front keeps the card free of
   `inject()` calls to feature services.
3. **Generate with the CLI**, do not hand-create files:
   `ng generate component features/orders/ui/order-card --dry-run` first, then without the flag. The
   generator applies the project's naming and style settings and creates the spec next to the file.
4. **Write the class with signal APIs and OnPush**, then the template with control flow. Keep
   derivations in `computed`, async data in `resource`/`httpResource` or a store (see
   `angular-data-and-forms`), and side effects in `effect` only when the outcome leaves Angular.
5. **Wire the route lazily** (`loadComponent` / `loadChildren`) and put feature-scoped services in
   route `providers` so they are created and destroyed with the feature.
6. **Verify.** `ng build` with strict templates (`strictTemplates: true` in `tsconfig.json`),
   `ng lint`, `ng test --watch=false` for the generated spec (see `angular-testing`), then open the
   page and tab through it with the keyboard. A component that cannot be operated without a mouse is
   not done.

## Folder structure

```
src/app/
├── app.ts  app.config.ts  app.routes.ts
├── core/                      # created once, app-wide; never imported by features' templates
│   ├── auth/                  # auth service, guards, token storage
│   ├── http/                  # functional interceptors, ApiError, API_URL token
│   └── layout/                # shell, nav, global error/toast
├── shared/                    # reusable, feature-agnostic: no HttpClient, no stores
│   ├── ui/                    # buttons, dialogs, field-error, empty-state ...
│   ├── directives/  pipes/  utils/
└── features/
    └── orders/
        ├── orders.routes.ts   # default-exported Routes, route-level providers
        ├── data/              # orders.api.ts, orders.store.ts, order.model.ts
        ├── pages/             # routed smart components (orders-page, order-detail)
        └── ui/                # presentational components (order-card, order-lines)
```

- Features import from `shared/` and `core/`, never from each other. Cross-feature needs go through
  a shared model or a `core/` service; otherwise lazy chunks start pulling each other in.
- One component per folder with `name.ts`, `name.html`, `name.scss`, `name.spec.ts`. The Angular 20
  style guide drops the `.component` suffix and the `Component` class suffix; follow whatever the
  project's `angular.json` generator settings produce and do not mix the two styles.
- `index.ts` barrels are optional; if used, keep them per feature and never re-export across
  features (barrels are the usual cause of circular imports and bloated chunks).

## Component anatomy

```ts
// features/orders/ui/order-card/order-card.ts
import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { CurrencyPipe, DatePipe } from '@angular/common';
import { Order } from '../../data/order.model';

@Component({
  selector: 'app-order-card',
  imports: [CurrencyPipe, DatePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    class: 'order-card',
    '[class.order-card--selected]': 'selected()',
    '[attr.aria-selected]': 'selected()',
  },
  templateUrl: './order-card.html',
  styleUrl: './order-card.scss',
})
export class OrderCard {
  readonly order = input.required<Order>();
  readonly selected = input(false);
  readonly select = output<string>();

  readonly total = computed(() =>
    this.order().lines.reduce((sum, line) => sum + line.quantity * line.unitPrice, 0),
  );
}
```

```html
<!-- order-card.html -->
<article>
  <header class="order-card__header">
    <h3>{{ order().number }}</h3>
    <time [attr.datetime]="order().createdAt">{{ order().createdAt | date: 'mediumDate' }}</time>
  </header>

  @if (order().lines.length > 0) {
    <ul class="order-card__lines">
      @for (line of order().lines; track line.id) {
        <li>{{ line.name }} × {{ line.quantity }}</li>
      }
    </ul>
  } @else {
    <p class="order-card__empty">No lines yet</p>
  }

  <footer class="order-card__footer">
    <strong>{{ total() | currency: order().currency }}</strong>
    <button type="button" [attr.aria-pressed]="selected()" (click)="select.emit(order().id)">
      Select
    </button>
  </footer>
</article>
```

- Components are standalone by default since v19; do not write `standalone: true`, and never add
  an `NgModule` for new code.
- `input()` / `input.required<T>()` replace `@Input()`. Use the `transform` option
  (`input(false, { transform: booleanAttribute })`) for attribute-style inputs. `model<T>()` gives a
  writable signal with two-way binding (`[(value)]="x"`); `output<T>()` replaces `@Output()` +
  `EventEmitter`, and `outputFromObservable` adapts an existing stream.
- `viewChild()` / `viewChildren()` / `contentChild()` are signal queries; `viewChild.required` throws
  early when the element is missing, which is better than an undefined `nativeElement` at runtime.
- `host: { ... }` metadata replaces `@HostBinding` / `@HostListener`. It is visible in one place and
  survives the signal migrations.
- `inject()` in field initialisers replaces constructor injection. It also works in functions that
  run in an injection context (guards, interceptors, `runInInjectionContext`), so helpers can be
  plain functions.
- `ChangeDetectionStrategy.OnPush` on every component. With signals it costs nothing and it is the
  behaviour a zoneless app has anyway (`provideZonelessChangeDetection()` in `app.config.ts`,
  stable since v21; prefixed `provideExperimentalZonelessChangeDetection` in v20).
- Lifecycle: prefer signals and `computed` to `ngOnChanges`; keep `ngOnInit` only for work that
  needs all inputs set (and even then a `computed`/`resource` keyed on the inputs is usually
  cleaner). `DestroyRef` + `takeUntilDestroyed()` replaces `ngOnDestroy` for cleanup.

## Choosing the signal primitive

| Need | Use |
|---|---|
| Value derived from other signals | `computed(() => ...)` — memoised, lazy, no glitches |
| Derived value the user may overwrite until the source changes (selected row resets when the list reloads) | `linkedSignal(() => this.rows()[0]?.id)` or the `{ source, computation }` form |
| Async data keyed by signals (route id, filter) | `resource({ params, loader })` for any promise; `httpResource(() => url)` for `HttpClient` requests; both expose `value()`, `isLoading()`, `error()`, `reload()` |
| Side effect outside the signal graph: localStorage, analytics, a chart library, focusing an element | `effect(() => ...)` |
| Work that needs the rendered DOM (measure, scroll) | `afterNextRender` / `afterRenderEffect` |
| Observable in, signal out | `toSignal(obs$, { initialValue })` from `@angular/core/rxjs-interop` |

`effect` deserves care. Writing to another signal inside an effect to "derive" state is the most
common misuse: it runs a tick late, produces intermediate renders and hides the dependency from
readers. If an effect's body ends with `someSignal.set(...)`, it should almost always be a
`computed` or `linkedSignal`. Legitimate effects read signals and push to something that is not a
signal. Use `untracked(() => ...)` inside an effect to read a signal without depending on it, and
return a cleanup via `onCleanup` when the effect starts timers or subscriptions. Effects created in a
component die with it; outside a component pass `{ injector }` or create them in a service.

`resource` / `httpResource` left developer preview recently; check the stability note for your
exact version before using them for writes (they are designed for reads; mutations stay on
`HttpClient` in a store or API client).

## Templates: control flow and deferred loading

- `@if / @else if / @else`, `@for ... track`, `@switch` and `@let` replace `*ngIf`, `*ngFor` and
  `ngSwitch`. Do not import `CommonModule`; import only the pipes you use.
- `@for` always needs `track`. Use a stable identity (`track item.id`); `track $index` is correct
  only for static lists. Tracking by object reference re-creates every DOM node on each reload.
  `@empty` renders the "no items" state without a separate `@if`.
- Keep templates declarative: no arithmetic, no `.filter()` / `.map()` and no method calls that do
  work. Call signals and `computed` freely; anything heavier belongs in a `computed`.
- `@defer (on viewport; prefetch on idle) { <app-heavy-chart /> } @placeholder { <div class="skeleton"></div> } @loading (minimum 200ms) { ... }`
  splits the chart and its dependencies into a separate chunk. Use it for below-the-fold widgets,
  rich editors, maps and anything that imports a large library. Deferred content must be a
  standalone component imported by the host; the compiler refuses to defer eagerly referenced ones.
- Event bindings call methods on the class (`(click)="select.emit(id)"` is the acceptable
  exception). Logic in `(click)="a = b; c()"` cannot be tested.

## Routing

```ts
// app.routes.ts
export const routes: Routes = [
  { path: '', loadComponent: () => import('./features/home/pages/home/home').then(m => m.Home) },
  { path: 'orders', loadChildren: () => import('./features/orders/orders.routes') },
  { path: '**', loadComponent: () => import('./core/layout/not-found/not-found').then(m => m.NotFound) },
];

// features/orders/orders.routes.ts
export default [
  {
    path: '',
    providers: [OrdersStore],            // created with the feature, disposed when it is left
    canActivate: [authGuard],            // CanActivateFn, uses inject() inside
    children: [
      { path: '', loadComponent: () => import('./pages/orders-page/orders-page').then(m => m.OrdersPage) },
      { path: ':id', loadComponent: () => import('./pages/order-detail/order-detail').then(m => m.OrderDetail) },
    ],
  },
] satisfies Routes;
```

- `provideRouter(routes, withComponentInputBinding())` in `app.config.ts` binds route params,
  query params and resolver data straight to `input()`s of the routed component
  (`readonly id = input.required<string>()`), which removes most `ActivatedRoute` plumbing.
- Default-exported route arrays work with `loadChildren` without `.then(...)`.
- Keep guards and resolvers as functions (`CanActivateFn`, `ResolveFn`); class-based ones are
  deprecated.
- `withViewTransitions()` and `withInMemoryScrolling({ scrollPositionRestoration: 'top' })` are
  cheap wins; add them when the app has more than a couple of pages.

## Smart vs presentational

- **Smart (pages/)**: routed, injects the store or API, owns the loading/error state, passes data
  down with inputs and reacts to outputs. One per route; thin template.
- **Presentational (ui/, shared/ui/)**: only `input()`, `model()`, `output()` and `computed`. No
  `inject()` of feature services, no HTTP, no router. These are the components that get reused and
  unit-tested with `setInput` alone (see `angular-testing`).
- A presentational component that needs a lot of inputs is a sign the page should pass one view
  model object (`input.required<OrderCardVm>()`) rather than eight scalars.

## SCSS conventions

- One `.scss` per component, scoped by emulated encapsulation. Style the host with `:host { display: block; }`
  (custom elements are `inline` by default, which breaks layout and sizing).
- Class names follow a light BEM (`order-card__header`, `order-card--selected`) so the generated
  attribute scoping is not the only thing preventing collisions.
- Global design tokens as CSS custom properties in `src/styles/_tokens.scss`, consumed with
  `var(--space-2)`; shared mixins in `src/styles/_mixins.scss` loaded via `@use` (Sass `@import` is
  removed in Dart Sass 3). Configure `stylePreprocessorOptions.includePaths` so components write
  `@use 'mixins'` instead of relative paths.
- `::ng-deep` is deprecated; style child components through their inputs, CSS custom properties or
  `:host-context`, and keep truly global overrides in `styles.scss`.
- Format with Prettier (`.prettierrc` with an override `{ "files": "*.html", "options": { "parser": "angular" } }`).

## Accessibility basics

- Use native elements before ARIA: `<button type="button">` not `<div (click)>`, `<a routerLink>`
  for navigation, `<label for>` or wrapping labels for every input, headings in order.
- Everything clickable is keyboard reachable and shows focus (`:focus-visible` styles, never
  `outline: none` without a replacement).
- Announce async results with `aria-live="polite"` regions or `LiveAnnouncer` from
  `@angular/cdk/a11y`; trap focus in dialogs with `cdkTrapFocus`; return focus to the trigger on
  close.
- Icons without text get `aria-label`; decorative ones `aria-hidden="true"`.
- Respect `prefers-reduced-motion` in SCSS animations and in `@angular/animations` usage.

## Migrating older code

Run the official schematics instead of editing by hand; they are safe to apply per folder with
`--path`:

- `ng generate @angular/core:standalone` (three modes: convert components, remove modules, bootstrap)
- `ng generate @angular/core:control-flow` (`*ngIf/*ngFor/ngSwitch` to `@if/@for/@switch`)
- `ng generate @angular/core:signal-inputs`, `@angular/core:outputs`, `@angular/core:signal-queries`
- `ng generate @angular/core:inject` (constructor parameters to `inject()`)
- `ng generate @angular/core:self-closing-tag`, `@angular/core:cleanup-unused-imports`
- `ng update @angular/core@<major> @angular/cli@<major>` one major at a time, reading the update
  guide at update.angular.dev between steps.

Commit each migration separately so the review diff is reviewable and the behaviour change (if any)
is bisectable.

## Examples

**Example 1**
Input: "Add an orders list page that filters by status from the query string."
Output: `features/orders/pages/orders-page/orders-page.ts` with
`readonly status = input<string>()` (bound by `withComponentInputBinding`),
`readonly orders = httpResource<Order[]>(() => ({ url: '/api/orders', params: { status: this.status() ?? '' } }), { defaultValue: [] })`,
a template with `@if (orders.isLoading())`, `@else if (orders.error())` with a retry button calling
`orders.reload()`, and `@for (order of orders.value(); track order.id) { <app-order-card ... /> } @empty { ... }`;
plus a lazy route entry in `orders.routes.ts`.

**Example 2**
Input: "This component keeps `selectedId` in sync with the list using `ngOnChanges`."
Output: Replace the input decorator and `ngOnChanges` with
`readonly rows = input.required<Row[]>()` and
`readonly selectedId = linkedSignal(() => this.rows()[0]?.id ?? null)`; the selection resets when
the list changes and can still be set by the user with `selectedId.set(id)`.

**Example 3**
Input: "The dashboard loads a 400 kB chart library on every page."
Output: Move the chart into its own standalone component, wrap its usage in
`@defer (on viewport; prefetch on idle)` with a skeleton `@placeholder`, and confirm with
`ng build --stats-json` + a bundle analyser that the library moved to a lazy chunk.
