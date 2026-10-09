---
name: angular-code-review
description: >-
  Review Angular 20+ pull requests and produce a severity-ordered findings list with file:line, a
  concrete failure scenario and a suggested fix for each: subscription leaks, effect misuse for
  derived state, missing OnPush, logic in templates, any, untyped forms, direct DOM access, folder
  structure drift, missing lazy loading, unhandled HTTP errors, accessibility, performance (track,
  @defer, bundle size), missing or implementation-coupled tests, hardcoded i18n strings. Use this
  skill whenever the user asks to review, check, audit or "look over" Angular code, a PR, a diff, a
  branch or a component/service file, says "code review", "LGTM?", "any issues with this", "before I
  merge", or pastes Angular code and asks what is wrong, even if they do not say "review".
---

# Angular code review

This skill turns a diff into a short, prioritised list of findings a developer can act on in one
pass. Each finding names the file and line, describes what goes wrong for a user or a maintainer
(not just "this is not idiomatic"), and proposes a fix small enough to apply in the same PR. Reviews
that list twenty style nits with no ordering are ignored; reviews that miss the one subscription
leak ship it. The checklist below is ordered by what has historically hurt Angular apps most.

## Workflow

1. **Establish the scope and context.** Read the PR description and the diff (`git diff main...HEAD`
   or the PR view); then open the surrounding files of anything touched: the feature's routes, the
   store, `app.config.ts`. A line that looks wrong in isolation is often correct given a provider
   two folders up, and vice versa.
2. **Run what the CI runs**, if the code is available: `ng build`, `ng lint`,
   `ng test --watch=false`. Compiler and lint output are findings for free and remove guesswork
   about strict template checks.
3. **Walk the checklist** (sections below) once per file, in order: correctness and leaks first,
   then structure, then tests, then style. Collect findings with file:line as you go.
4. **Assign severity** using the scale below and drop anything that is neither a risk nor a
   maintainability cost; a reviewer's credibility is spent on nits.
5. **Write the output** in the format at the end: ordered by severity, each with scenario and fix,
   then a short "looks good" note for things done well (that is how the pattern spreads), then open
   questions.
6. **Verify the review itself**: every finding has file:line, every blocker has a scenario a user
   would notice, every fix is concrete (code or API name, not "refactor this"). Re-read the diff
   once more looking only for anything the checklist did not cover.

## Severity scale

| Level | Meaning | Examples |
|---|---|---|
| Blocker | Wrong behaviour, data loss, security, leak that grows with usage | subscription leak in a long-lived component, token sent to third-party host, error swallowed so the UI shows stale data as success |
| Major | Works today but will break on the next change or under load; or violates an architectural rule of the project | `effect` deriving state, missing `OnPush` on a list item, feature importing another feature, eager route for a heavy page |
| Minor | Maintainability or consistency cost, no user-visible impact | logic in templates, `any` in a local helper, naming drift, missing `track` on a static list |
| Nit | Taste; mention once, do not block | import order, comment wording |

## Checklist

### Correctness and leaks (blocker / major)

- **Subscriptions**: every `.subscribe()` on a source that does not complete (store streams,
  `valueChanges`, `router.events`, `interval`, websockets) ends with `takeUntilDestroyed()` or is
  replaced by `toSignal` / `async` pipe. Scenario: navigating back and forth ten times leaves ten
  listeners updating a destroyed view and ten API calls per change.
- **`effect` writing signals**: an effect whose body sets another signal is derived state done
  late; replace with `computed` or `linkedSignal`. Scenario: the first render shows the stale value
  for one frame, and tests pass only with an extra `whenStable()`.
- **Unhandled HTTP errors**: a `subscribe(next)` without an error path, a `catchError` returning
  `EMPTY` with no state update, or a `resource` whose `error()` branch is never rendered. Scenario:
  the backend returns 500 and the user sees an empty list labelled "no orders".
- **Race conditions**: `mergeMap`/`concatMap` or nested subscriptions on user-driven requests where
  `switchMap` (or `resource`) is needed. Scenario: type "ab" quickly, results for "a" arrive last
  and win.
- **Auth and secrets**: tokens attached by an interceptor to every host; API keys in source;
  `bypassSecurityTrust*` on user-provided content.
- **Direct DOM access**: `document.querySelector`, `ElementRef.nativeElement.innerHTML = ...`,
  manual event listeners without removal. Prefer bindings, `viewChild()`, `Renderer2`,
  `afterRenderEffect`. Scenario: SSR/hydration crash or a listener that outlives the component.
- **Required inputs and nullability**: `input<T>()` used where the component cannot render without
  it (should be `input.required`), `!` non-null assertions on async data, `as` casts hiding a
  shape mismatch with the DTO.

### Structure and change detection (major / minor)

- **`ChangeDetectionStrategy.OnPush`** missing on any new component. With signals there is no
  downside and it is the zoneless contract.
- **Signal APIs**: new code using `@Input()`, `@Output()`, `@ViewChild`, `@HostBinding`,
  constructor injection, `ngOnChanges` for derivation, `*ngIf`/`*ngFor`, `CommonModule` imports.
  Point at the migration schematic rather than asking for hand edits
  (`angular-modern-components`).
- **Folder structure**: files outside `features/<x>/{data,pages,ui}`, `core/`, `shared/`; a
  feature importing from another feature; a `shared/` component injecting `HttpClient` or a store;
  a `providedIn: 'root'` service that holds feature state.
- **Lazy loading**: a new page added with `component:` instead of `loadComponent:`; a feature
  routes file imported eagerly in `app.routes.ts`; a heavy widget (chart, editor, map) rendered
  without `@defer`. Check with `ng build` output whether the main chunk grew.
- **Logic in templates**: arithmetic, `.filter()/.map()`, ternary chains, method calls that do
  work, multi-statement event handlers. Move to `computed` or a method.
- **Smart/presentational drift**: a `ui/` component calling a store or router; a page with 300
  lines of template that should be split.

### Data and forms (major / minor)

- **`any`** and implicit `any` (`strict` must be on); `Record<string, any>` for DTOs; untyped
  `http.get(url)` without a generic.
- **Untyped forms**: `FormGroup` without a type argument, `FormBuilder` instead of
  `NonNullableFormBuilder`, `form.value` (nullable, partial) used to build a request where
  `getRawValue()` is intended, server validation errors not mapped to controls
  (`angular-data-and-forms`).
- **Error mapping**: components parsing `HttpErrorResponse` themselves instead of relying on the
  `ApiError` from the interceptor; toast text built from `err.message` (shows "Http failure
  response for ...").
- **Store hygiene**: public writable signals on a store (`readonly items = signal([])` exposed
  as-is), state mutated in place (`this.items().push(x)`) which OnPush never sees.

### Accessibility (major when the element is a primary action)

- Clickable `div`/`span` instead of `button`/`a`; inputs without a label or `aria-label`; icon
  buttons without a name; custom dropdowns and dialogs without keyboard support and focus
  management; `outline: none` without a visible focus replacement; colour as the only status
  indicator; async updates without `aria-live`.

### Performance (major / minor)

- `@for` without a stable `track` key (object identity on reloaded data re-creates every row).
- Pipes with heavy work and no memoisation, or impure pipes.
- Large images without `NgOptimizedImage` (`ngSrc`), lists over a few hundred rows without
  virtualisation (`cdk-virtual-scroll-viewport`) or pagination.
- Third-party libraries imported in `shared/` or `core/` that end up in the initial bundle; moment
  / full lodash imports.
- `effect` or `afterRenderEffect` running on every change where a `computed` would be lazy.

### Tests (major when missing for new behaviour)

- New component, store or validator with no spec, or a spec that only asserts
  `toBeTruthy()`.
- Specs asserting implementation details (`toHaveBeenCalled` on internal methods, private
  signals, snapshot of the whole template) instead of rendered output; see `angular-testing` for
  the expected shape.
- Missing coverage for the error branch of any new request.
- e2e added for logic that a unit test covers in milliseconds, or unit tests that mock three
  services to test routing.

### i18n and content (minor, major if the project is localised)

- Hardcoded user-facing strings in templates without `i18n` attributes / `$localize` (or the
  project's translation pipe); dates, numbers and currencies formatted manually instead of with
  the Angular pipes and the app locale; string concatenation that cannot be translated as a unit.

## Output format

```
## Review: <PR title or branch>  — <n> findings (<b> blocker, <m> major, <x> minor)

### 1. [Blocker] Subscription leak in OrdersPage
src/app/features/orders/pages/orders-page/orders-page.ts:34
Scenario: `this.store.orders$.subscribe(...)` in `ngOnInit` is never torn down. Open the orders
page, navigate away and back five times: five subscriptions update destroyed views and the
network tab shows five identical requests per filter change.
Fix: read the store signal directly in the template (`store.orders()`), or if the stream must
stay, add `takeUntilDestroyed()` to the pipe (component field initialiser) or `toSignal(...)`.

### 2. [Major] `effect` derives `total` from `lines`
src/app/features/orders/ui/order-lines/order-lines.ts:21
Scenario: ...
Fix: `readonly total = computed(() => ...)`.

...

### Looks good
- `OrderCard` is presentational with `input.required` and OnPush; spec asserts on DOM.

### Questions
- Is `OrdersStore` meant to survive leaving the feature? It is `providedIn: 'root'`.
```

Rules for the write-up: one finding per issue (not per occurrence; list the lines), highest
severity first, no finding without file:line, scenario in terms of what a user or the next
developer experiences, fix with the exact API or a two-line snippet. Keep the whole review under
roughly 40 lines for a small PR; if there are more than ten findings, the PR needs a conversation,
not a longer list. Mirror the format of the sibling `dotnet-code-review` skill so full-stack PRs
read consistently.

## Examples

**Example 1**
Input: "Review this diff" (adds a `CustomerPicker` component with `@Input() customers: any[]`,
`*ngFor` without `trackBy`, a `(click)` on a `div`, and a `subscribe` to `valueChanges` in
`ngOnInit`.)
Output: Four findings: [Blocker] `valueChanges` subscription leak at `customer-picker.ts:28`
with the back-and-forth navigation scenario and `takeUntilDestroyed()` / `toSignal` fix;
[Major] missing `OnPush` and decorator inputs at `:12` with the `signal-inputs` schematic;
[Major] `div (click)` at `customer-picker.html:4`, keyboard users cannot open the picker, fix
`<button type="button">`; [Minor] `any[]` at `:14`, type `CustomerDto[]`. A "Looks good" note on
the new spec covering the empty state.

**Example 2**
Input: "LGTM? Just a small change to the orders route."
(Diff switches `loadComponent` to `component` for `OrderDetail` to "fix a flicker".)
Output: One [Major] finding at `orders.routes.ts:9`: the detail page and its PDF library now land
in the initial bundle (confirm with `ng build` stats); scenario: first paint of the login page
slows for every user to fix a flicker on one route. Fix: keep `loadComponent` and address the
flicker with `@defer` + `@placeholder`, `withPreloading(PreloadAllModules)`, or a route-level
`resolve`.
