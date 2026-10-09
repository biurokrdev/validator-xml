---
name: angular-testing
description: >-
  Write and run tests for Angular 20+ apps: unit and component tests with Vitest + TestBed
  (fixture, setInput for signal inputs, provideHttpClientTesting / HttpTestingController,
  provideRouter and RouterTestingHarness, vi.fn mocks, vi.useFakeTimers, By.css and CDK component
  harnesses), testing services, stores and interceptors, deciding what not to test, Playwright e2e
  structure (page objects, fixtures, data-testid, page.route network mocking, trace on retry), CI
  commands and coverage. Use this skill whenever the user writes or fixes a .spec.ts file, says
  "test this component/service", "the test fails", "mock HttpClient", "TestBed", "vitest", "vi.fn",
  "fixture.detectChanges", "harness", "e2e", "playwright", "page object", "flaky test", "coverage",
  or adds a feature without tests, even if they do not mention testing at all.
---

# Testing Angular with Vitest and Playwright

This skill produces tests that fail for the right reasons: component specs that render the real
template and assert on the DOM, service specs that assert on HTTP traffic and public signals, and a
thin Playwright suite that proves user journeys against mocked or real APIs. Angular tests go bad in
two directions: they test implementation details (`expect(service.load).toHaveBeenCalled()`) and
break on every refactor, or they skip the hard parts (async, HTTP, routing) and pass while the app is
broken. The workflow keeps the suite in the middle.

## Workflow

1. **Check the runner and setup.** Angular CLI 20+ runs unit tests with Vitest through the
   `@angular/build:unit-test` builder (`"runner": "vitest"` in `angular.json`; default for new
   projects since v21). Look for a `test-setup.ts` or `runnerConfig` entry, whether the app is
   zoneless (then provide `provideZonelessChangeDetection()` in tests too) and how existing specs
   build their fixtures. Match them; two fixture styles in one repo means two sets of bugs.
2. **Decide the level.** Pure functions and validators: plain Vitest, no TestBed. Services, stores
   and interceptors: TestBed + `HttpTestingController` or mocked API. Components: TestBed fixture
   asserting on the DOM. User journeys across pages: Playwright. Pushing a journey into a component
   spec produces a 200-line test that mocks the world.
3. **Write the test from the user's point of view**: set inputs, click, type, assert what is
   rendered or what request left. Mock only the seams (API client, store), never the component
   under test.
4. **Handle async explicitly**: `await fixture.whenStable()` after inputs change or requests
   resolve, `vi.useFakeTimers()` for debounce, `flush()` on `HttpTestingController` for responses.
5. **Verify.** `ng test --watch=false` locally (fast, headless), `ng test --watch=false --coverage`
   for the report, `npx playwright test` for e2e. Run the new spec in isolation once
   (`ng test --include='**/order-card.spec.ts'`) to be sure it does not depend on test order.

## Component specs with TestBed

```ts
// features/orders/ui/order-card/order-card.spec.ts
import { describe, it, expect, vi } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { OrderCard } from './order-card';
import { Order } from '../../data/order.model';

const order: Order = {
  id: 'o-1', number: 'ORD-1', currency: 'EUR', createdAt: '2026-03-01T10:00:00Z',
  lines: [
    { id: 'l-1', name: 'Widget', quantity: 2, unitPrice: 10 },
    { id: 'l-2', name: 'Gadget', quantity: 1, unitPrice: 5 },
  ],
};

describe('OrderCard', () => {
  async function setup(input: Order = order, selected = false) {
    await TestBed.configureTestingModule({ imports: [OrderCard] }).compileComponents();
    const fixture = TestBed.createComponent(OrderCard);
    fixture.componentRef.setInput('order', input);
    fixture.componentRef.setInput('selected', selected);
    await fixture.whenStable();
    return fixture;
  }

  it('renders the number and the computed total', async () => {
    const fixture = await setup();
    const host: HTMLElement = fixture.nativeElement;
    expect(host.querySelector('h3')?.textContent).toBe('ORD-1');
    expect(host.querySelector('footer strong')?.textContent).toContain('€25.00');
  });

  it('shows the empty state when there are no lines', async () => {
    const fixture = await setup({ ...order, lines: [] });
    expect(fixture.debugElement.query(By.css('.order-card__empty'))).not.toBeNull();
    expect(fixture.debugElement.query(By.css('ul'))).toBeNull();
  });

  it('emits select with the order id and reflects selection on the host', async () => {
    const fixture = await setup();
    const onSelect = vi.fn();
    fixture.componentInstance.select.subscribe(onSelect);

    fixture.debugElement.query(By.css('button')).nativeElement.click();
    expect(onSelect).toHaveBeenCalledWith('o-1');

    fixture.componentRef.setInput('selected', true);
    await fixture.whenStable();
    expect(fixture.nativeElement.classList.contains('order-card--selected')).toBe(true);
  });
});
```

- `fixture.componentRef.setInput(name, value)` is the only correct way to set a signal input
  (`input()`); assigning to the property fails to compile, and it also marks the component for
  check so OnPush components re-render.
- `await fixture.whenStable()` runs change detection and waits for pending async work; it is the
  one call to use in zoneless and zone-based apps alike. `fixture.detectChanges()` is still fine
  for synchronous updates. `fixture.autoDetectChanges()` removes most calls in zoneless tests.
- Assert through `nativeElement` / `By.css` on text, attributes and roles, not on component fields.
  When a component fits the page, query by role or `data-testid` so the e2e and unit tests share
  selectors.
- Host-level smart components: replace the store with a stub whose signals you control
  (`{ provide: OrdersStore, useValue: { orders: signal([order]), loading: signal(false), error: signal(null) } }`),
  then assert the template branches.
- Routed components: `providers: [provideRouter([]), ...]` is enough for `routerLink`; to test
  param binding use `RouterTestingHarness`:
  `const harness = await RouterTestingHarness.create(); const cmp = await harness.navigateByUrl('/orders/42', OrderDetail);`
  with `provideRouter(routes, withComponentInputBinding())`.
- `@defer` blocks: `TestBed.configureTestingModule({ deferBlockBehavior: DeferBlockBehavior.Manual })`
  and `await (await fixture.getDeferBlocks())[0].render(DeferBlockState.Complete)` to assert the
  deferred content.
- CDK / Material: `const loader = TestbedHarnessEnvironment.loader(fixture); const select = await loader.getHarness(MatSelectHarness);`
  reads and drives components without knowing their DOM. Write your own harness
  (`class OrderCardHarness extends ComponentHarness { static hostSelector = 'app-order-card'; }`)
  when a component is used across many specs.

## Services, stores and interceptors

```ts
describe('OrdersApi', () => {
  let api: OrdersApi;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([errorInterceptor])),
        provideHttpClientTesting(),                     // after provideHttpClient
        { provide: API_URL, useValue: '/api' },
      ],
    });
    api = TestBed.inject(OrdersApi);
    http = TestBed.inject(HttpTestingController);
  });
  afterEach(() => http.verify());                        // fails on unexpected or unflushed requests

  it('maps ProblemDetails to ApiError with camel-cased field errors', async () => {
    const result = firstValueFrom(api.create({ customerEmail: 'x' } as CreateOrderCommand));
    const req = http.expectOne({ method: 'POST', url: '/api/orders' });
    req.flush(
      { title: 'Validation failed', status: 400, errors: { CustomerEmail: ['Invalid'] } },
      { status: 400, statusText: 'Bad Request', headers: { 'content-type': 'application/problem+json' } },
    );
    await expect(result).rejects.toMatchObject({ status: 400, fieldErrors: { customerEmail: ['Invalid'] } });
  });
});
```

- Test interceptors by registering them with `withInterceptors` and asserting on `req.request.headers`
  (`expect(req.request.headers.get('Authorization')).toBe('Bearer t')`) or on the mapped error.
- Stores: mock the API client with `vi.fn().mockReturnValue(of(page))`, call the public method,
  assert the public signals. For debounced pipelines use `vi.useFakeTimers()` before the call,
  `vi.advanceTimersByTime(300)` after, and `vi.useRealTimers()` in `afterEach`; with fake timers
  prefer `await vi.runAllTimersAsync()` over `whenStable()` to avoid deadlocks.
- Functions that use `inject()` (guards, validators built with `inject()` defaults) run under
  `TestBed.runInInjectionContext(() => authGuard(route, state))`.
- Vitest mocks: `vi.fn()`, `vi.spyOn(obj, 'method')`, `vi.mock('./module')` for ESM modules
  (hoisted; keep it at the top of the file). Type them with `vi.fn<typeof api.list>()` when the
  return type matters.

## What not to test

- Angular itself: that `@if` hides an element or that `[disabled]` sets the attribute.
- Private methods and internal signals; they change in every refactor. If something is hard to
  reach through the public surface, that is a design hint, not a reason for `(component as any)`.
- Exact markup snapshots of whole templates; a class rename fails ten specs with no information.
- Third-party components' behaviour (Material dialogs open); test that your code calls them with
  the right data, or cover it once in e2e.
- Mocked interactions as the assertion (`expect(api.list).toHaveBeenCalled()`) when the rendered
  result can be asserted instead. Verify calls only when the call *is* the behaviour (an analytics
  event, a delete request).

## Playwright e2e

```
e2e/
├── playwright.config.ts
├── fixtures/test.ts          # test.extend: page objects, authenticated storage state
├── pages/orders.page.ts      # page objects: locators + user actions, no assertions
└── tests/orders.spec.ts
```

```ts
// playwright.config.ts
export default defineConfig({
  testDir: './tests',
  fullyParallel: true,
  retries: process.env.CI ? 2 : 0,
  reporter: process.env.CI ? [['html', { open: 'never' }], ['github']] : 'list',
  use: { baseURL: 'http://localhost:4200', trace: 'on-first-retry', testIdAttribute: 'data-testid' },
  webServer: { command: 'npm start', url: 'http://localhost:4200', reuseExistingServer: !process.env.CI },
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'] } }],
});
```

```ts
// pages/orders.page.ts
export class OrdersPage {
  constructor(private readonly page: Page) {}
  readonly cards = () => this.page.getByTestId('order-card');
  async goto(status?: string) { await this.page.goto(status ? `/orders?status=${status}` : '/orders'); }
  async select(number: string) {
    await this.cards().filter({ hasText: number }).getByRole('button', { name: 'Select' }).click();
  }
}

// fixtures/test.ts
export const test = base.extend<{ ordersPage: OrdersPage }>({
  ordersPage: async ({ page }, use) => { await use(new OrdersPage(page)); },
});
export { expect } from '@playwright/test';

// tests/orders.spec.ts
test('lists orders and selects one', async ({ page, ordersPage }) => {
  await page.route('**/api/orders**', route =>
    route.fulfill({ json: [{ id: 'o-1', number: 'ORD-1', currency: 'EUR', createdAt: '2026-03-01', lines: [] }] }),
  );
  await ordersPage.goto('open');
  await expect(ordersPage.cards()).toHaveCount(1);
  await ordersPage.select('ORD-1');
  await expect(ordersPage.cards().first()).toHaveAttribute('aria-selected', 'true');
});

test('shows the backend error', async ({ page, ordersPage }) => {
  await page.route('**/api/orders**', route =>
    route.fulfill({ status: 500, contentType: 'application/problem+json', body: JSON.stringify({ title: 'Database unavailable' }) }),
  );
  await ordersPage.goto();
  await expect(page.getByRole('alert')).toContainText('Database unavailable');
});
```

- Prefer `getByRole` / `getByLabel` (they double as accessibility checks); fall back to
  `data-testid` for containers without a semantic role. Avoid CSS class selectors; they change with
  styling.
- Mock the API with `page.route` for fast, deterministic UI tests; keep a small separate project
  that hits the real backend (seeded test data, `storageState` for auth) for contract smoke tests.
- Web-first assertions (`await expect(locator).toBeVisible()`) retry; never `waitForTimeout`.
- Keep page objects assertion-free so a failing expectation points at the test, not the helper.
- `trace: 'on-first-retry'` plus `npx playwright show-report` explains CI failures without
  re-running; `npx playwright test --ui` for local debugging, `--project=chromium -g "selects"`
  to run one test.

## CI commands and coverage

```
ng test --watch=false                      # unit, headless, exit code for CI
ng test --watch=false --coverage           # writes coverage/ (v8 provider)
ng test --include='**/orders/**'           # a folder
npx playwright install --with-deps chromium && npx playwright test
```

- Put coverage thresholds in the Vitest config referenced by `runnerConfig` in `angular.json`
  (`coverage: { thresholds: { lines: 80, branches: 70 } }`) and exclude `*.routes.ts`,
  `main.ts`, models and generated clients so the number reflects logic.
- Fail CI on `console.error` during tests (Vitest `onConsoleLog`) to catch `NG0100`-style warnings
  and unhandled rejections early.
- Cache `~/.cache/ms-playwright` and `node_modules` in CI; upload `playwright-report/` and
  `test-results/` as artefacts on failure.

## Examples

**Example 1**
Input: "Write tests for the orders store that debounces the filter."
Output: A spec with `vi.useFakeTimers()`, `OrdersApi` mocked with `vi.fn().mockReturnValue(of(page))`,
`store.setFilter('open')`, `await vi.advanceTimersByTimeAsync(300)`, assertions on
`store.loading()` before and `store.orders()` after, plus a case where the API returns an
`ApiError` and `store.error()` is set; no assertions on `patchState` or private fields.

**Example 2**
Input: "The order-detail test fails with 'NG0950: input is required but no value is available'."
Output: The spec creates the component without setting the required input. Add
`fixture.componentRef.setInput('id', '42')` before the first `whenStable()`, or navigate with
`RouterTestingHarness` so `withComponentInputBinding` sets it.

**Example 3**
Input: "Add an e2e test for creating an order with validation errors."
Output: `tests/order-form.spec.ts` using an `OrderFormPage` object, `page.route` returning a 400
`application/problem+json` with `errors: { CustomerEmail: ['Already registered'] }`, and
`await expect(page.getByRole('alert')).toContainText('Already registered')` next to the e-mail
field; a second test with a 201 response asserting the redirect to `/orders/<id>`.
