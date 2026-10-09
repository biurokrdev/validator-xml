---
name: angular-data-and-forms
description: >-
  Fetch, cache and mutate data from a REST backend (ASP.NET Core or any JSON API) in Angular 20+ and
  build typed forms: HttpClient with functional interceptors (bearer token, ProblemDetails error
  mapping, correlation id), one typed API client per feature, loading/error state with signals
  (AsyncState), toSignal/toObservable, when RxJS still wins (debounced search, switchMap
  cancellation), a signal-based store service or NgRx SignalStore, typed Reactive Forms with
  NonNullableFormBuilder, custom and async validators, FormArray, a reusable field-error component,
  submit/disable handling, server-side validation errors and file upload with progress. Use this
  skill whenever the user calls an API from Angular, writes a service with HttpClient, says
  "interceptor", "401", "ProblemDetails", "loading spinner", "store", "state management", "NgRx",
  "signalStore", "FormGroup", "FormBuilder", "validator", "form array", "upload", "debounce search",
  "subscribe", or asks how to show backend errors in the UI, even if they only say "save this form".
---

# Data access, state and forms in Angular

This skill produces data code that is typed end to end (DTO → model → view), renders every request's
three states (loading, success, error), cancels stale requests and keeps RxJS at the edges where it
is still the best tool. Forms come out typed, with messages users can act on and backend validation
errors landing on the right field. The common failures here are silent (a stream never unsubscribed,
an error swallowed in an interceptor, an untyped form that compiles while the contract changed), so
the workflow is ordered to prevent them.

## Workflow

1. **Read the backend contract and the existing `core/http/` setup.** Note DTO shapes, the error
   format (`application/problem+json`) and how auth is passed; check `app.config.ts` for
   `provideHttpClient(...)` and existing interceptors (a duplicated auth interceptor sends two headers).
2. **Define the types first**: a `*.model.ts` per feature with request/response DTOs, generated
   from OpenAPI (`openapi-typescript`) when the API is large. Keep dates as ISO `string` in DTOs and
   convert at the mapper.
3. **Write the API client** (one `providedIn: 'root'` service per feature returning
   `Observable<T>`), then the **store or resource** that owns state, then the component. Components
   never call `HttpClient` directly; that keeps mocking to one seam in tests.
4. **Model loading and error state explicitly** with `AsyncState<T>` or `resource` and render all
   three branches. A list that shows its empty state while loading is reported as "data disappeared".
5. **For forms, derive the form type from the DTO**, build it with `NonNullableFormBuilder`, add
   validators, render errors with the shared field-error component, map server errors back on submit.
6. **Verify.** Unit test the API client with `HttpTestingController` and the store with a mocked
   client (`angular-testing`); in the browser trigger a 400, a 401 and a 500 and watch each render.

## HttpClient setup and functional interceptors

```ts
// app.config.ts
provideHttpClient(withFetch(), withInterceptors([correlationIdInterceptor, authInterceptor, errorInterceptor]))
```

```ts
// core/http/auth.interceptor.ts
export const authInterceptor: HttpInterceptorFn = (req, next) => {
  const token = inject(AuthService).accessToken();
  const apiUrl = inject(API_URL);
  if (!token || !req.url.startsWith(apiUrl)) return next(req);
  return next(req.clone({ setHeaders: { Authorization: `Bearer ${token}` } }));
};
// core/http/correlation-id.interceptor.ts
export const correlationIdInterceptor: HttpInterceptorFn = (req, next) =>
  next(req.clone({ setHeaders: { 'X-Correlation-Id': crypto.randomUUID() } }));
// core/http/error.interceptor.ts
export const errorInterceptor: HttpInterceptorFn = (req, next) =>
  next(req).pipe(
    catchError((err: unknown) =>
      throwError(() => (err instanceof HttpErrorResponse ? toApiError(err) : err)),
    ),
  );
```

- Interceptors are plain functions run in order; `inject()` works inside them. Scope the auth header
  to your own API (`API_URL` is an `InjectionToken<string>` from the environment file) so tokens
  never leak to third-party hosts.
- Handle 401 in one place (refresh and retry once in the auth interceptor, then redirect to login);
  the error interceptor only maps. `withFetch()` is required for SSR streaming and fine in browsers.

## Mapping ProblemDetails to user messages

```ts
// core/http/api-error.ts
export interface ProblemDetails {
  type?: string; title?: string; status?: number; detail?: string; instance?: string; traceId?: string;
  errors?: Record<string, string[]>;          // ASP.NET ValidationProblemDetails
}

export class ApiError extends Error {
  constructor(readonly status: number, readonly problem: ProblemDetails | null, readonly fieldErrors: Record<string, string[]> = {}) {
    super(problem?.detail ?? problem?.title ?? `Request failed (${status})`);
  }
  get isValidation() { return this.status === 400 && Object.keys(this.fieldErrors).length > 0; }
}

export function toApiError(err: HttpErrorResponse): ApiError {
  const isProblem = err.headers.get('content-type')?.includes('application/problem+json');
  const problem = isProblem && err.error && typeof err.error === 'object' ? (err.error as ProblemDetails) : null;
  const fieldErrors = Object.fromEntries(
    Object.entries(problem?.errors ?? {}).map(([key, msgs]) => [toCamel(key), msgs]),
  );
  return new ApiError(err.status, problem, fieldErrors);
}

const toCamel = (s: string) => s.charAt(0).toLowerCase() + s.slice(1);
```

ASP.NET keys validation errors in PascalCase (`CustomerEmail`); camel-casing lets `fieldErrors` map
straight onto controls. `title`/`detail` feed the toast; `traceId` is logged, not shown.

## Typed API client per feature

```ts
// features/orders/data/orders.api.ts
@Injectable({ providedIn: 'root' })
export class OrdersApi {
  private readonly http = inject(HttpClient);
  private readonly url = `${inject(API_URL)}/orders`;

  list(query: { status?: string; page?: number }): Observable<Page<OrderDto>> {
    return this.http.get<Page<OrderDto>>(this.url, { params: toHttpParams(query) }); // drops undefined keys
  }
  create(cmd: CreateOrderCommand) { return this.http.post<OrderDto>(this.url, cmd); }
}
```

## Loading and error state with signals

```ts
// shared/utils/async-state.ts
export type AsyncState<T> =
  | { status: 'idle' }
  | { status: 'loading' }
  | { status: 'success'; data: T }
  | { status: 'error'; error: ApiError };
```

- **Read-only data keyed by signals** (route param, filter): use `httpResource` / `resource` in the
  page; they expose `value()`, `isLoading()`, `error()`, `reload()` and cancel the previous request
  when the key changes (`angular-modern-components`).
- **State shared across components or mutated by the user**: a store (below) holding an
  `AsyncState<T>` signal.
- `toSignal(obs$, { initialValue })` and `toObservable(sig)` bridge the two worlds; both need an
  injection context (field initialisers) and unsubscribe on destroy.
- **Keep RxJS where time is involved**: debounce (`debounceTime`, `distinctUntilChanged`), stale
  request cancellation (`switchMap`), `retry({ count: 2, delay: 500 })`, polling (`timer(0, 10_000)`),
  websockets. Convert to a signal at the end; do not rebuild these with `effect` + `setTimeout`.
- Manual subscriptions end with `takeUntilDestroyed()`. A single `HttpClient` call completes on its
  own; a `switchMap` chain over a long-lived source does not.

## Signal-based store

```ts
// features/orders/data/orders.store.ts  (provided in orders.routes.ts, not root)
@Injectable()
export class OrdersStore {
  private readonly api = inject(OrdersApi);
  private readonly state = signal<AsyncState<OrderDto[]>>({ status: 'idle' });
  private readonly filter = signal('');

  readonly loading = computed(() => this.state().status === 'loading');
  readonly error = computed(() => { const s = this.state(); return s.status === 'error' ? s.error : null; });
  readonly orders = computed(() => { const s = this.state(); return s.status === 'success' ? s.data : []; });

  constructor() {
    toObservable(this.filter)
      .pipe(
        debounceTime(300),
        distinctUntilChanged(),
        switchMap(status =>
          this.api.list({ status }).pipe(
            map(page => ({ status: 'success', data: page.items }) as const),
            catchError((error: ApiError) => of({ status: 'error', error } as const)),
            startWith({ status: 'loading' } as const),
          ),
        ),
        takeUntilDestroyed(),
      )
      .subscribe(next => this.state.set(next));
  }

  setFilter(value: string) { this.filter.set(value); }
}
```

- Private `signal`, public `computed`: consumers cannot write state behind the store's back, and
  the template reads plain signals. Mutations are `async` methods that `await firstValueFrom(...)`
  and patch the state (optimistically if the UI benefits, restoring the previous value on error).
- Provide feature stores at the route (`providers: [OrdersStore]`) so state resets when the user
  leaves the feature; `providedIn: 'root'` is for truly global state (current user, settings).
- When three or more features repeat this shape, or entity collections and devtools are needed,
  move to **NgRx SignalStore** (`signalStore`, `withState`, `withComputed`, `withMethods`,
  `rxMethod`, `withEntities`); read `references/ngrx-signal-store.md` when adopting it.

## Typed Reactive Forms

```ts
interface OrderLineForm { productId: FormControl<string>; quantity: FormControl<number>; }

@Component({ /* ... */ imports: [ReactiveFormsModule, FieldError] })
export class OrderForm {
  private readonly fb = inject(NonNullableFormBuilder);
  private readonly api = inject(OrdersApi);

  readonly form = this.fb.group({
    customerEmail: ['', [Validators.required, Validators.email], [uniqueEmail()]],
    deliveryDate: ['', [Validators.required, notInPast()]],
    notes: [''],
    lines: this.fb.array<FormGroup<OrderLineForm>>([]),
  });
  readonly submitting = signal(false);
  readonly saved = output<OrderDto>();

  addLine() {
    this.form.controls.lines.push(
      this.fb.group({ productId: ['', Validators.required], quantity: [1, [Validators.min(1)]] }),
    );
  }
  removeLine(index: number) { this.form.controls.lines.removeAt(index); }

  async submit() {
    this.form.markAllAsTouched();
    if (this.form.invalid || this.submitting()) return;
    this.submitting.set(true);
    try {
      this.saved.emit(await firstValueFrom(this.api.create(this.form.getRawValue())));
    } catch (error) {
      if (error instanceof ApiError && error.isValidation) applyServerErrors(this.form, error.fieldErrors);
      else throw error;                               // global ErrorHandler shows the toast
    } finally {
      this.submitting.set(false);
    }
  }
}
```

- `NonNullableFormBuilder` makes `reset()` return to initial values instead of `null`, and
  `getRawValue()` matches the DTO without `| null` noise. Declare the form in a field initialiser so
  the type is inferred (`form.controls.lines` is a `FormArray<FormGroup<OrderLineForm>>`).
- `FormArray` templates: `@for (line of form.controls.lines.controls; track line; let i = $index)`
  with `[formGroup]="line"` inside `formArrayName="lines"`.
- `updateOn: 'blur'` on fields with async validators avoids a request per keystroke.
- Signal Forms (`@angular/forms/signals`, experimental since v21) are coming; do not mix them with
  reactive forms in one feature until they are stable.

### Validators

```ts
export function notInPast(): ValidatorFn {
  return (control: AbstractControl<string>) => {
    if (!control.value) return null;                     // required handles emptiness
    return new Date(control.value) < new Date(new Date().toDateString()) ? { notInPast: true } : null;
  };
}

export function uniqueEmail(api = inject(CustomersApi)): AsyncValidatorFn {   // call in an injection context
  return control =>
    timer(300).pipe(
      switchMap(() => api.emailExists(control.value)),
      map(exists => (exists ? { emailTaken: true } : null)),
      catchError(() => of(null)),                          // network failure must not block the form
      first(),
    );
}

export function applyServerErrors(form: FormGroup, errors: Record<string, string[]>) {
  for (const [path, messages] of Object.entries(errors)) {
    const control = form.get(path.replace(/\[(\d+)\]/g, '.$1'));   // Lines[0].Quantity -> lines.0.quantity
    control?.setErrors({ server: messages.join(' ') });
    control?.markAsTouched();
  }
}
```

Validators return `null` when valid, otherwise an object keyed by error name; keep each single-purpose
so messages map one to one. Cross-field rules go on the group (`{ validators: [dateRange('from', 'to')] }`).

### Error display component

```ts
@Component({
  selector: 'app-field-error',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `@if (message(); as msg) { <p class="field-error" role="alert">{{ msg }}</p> }`,
})
export class FieldError {
  readonly control = input.required<AbstractControl>();
  readonly label = input('This field');

  private readonly state = toSignal(
    toObservable(this.control).pipe(
      switchMap(c => c.events.pipe(startWith(null), map(() => ({ errors: c.errors, show: c.touched && c.invalid })))),
    ),
    { initialValue: { errors: null, show: false } },
  );

  readonly message = computed(() => {
    const { errors, show } = this.state();
    if (!show || !errors) return null;
    if (errors['required']) return `${this.label()} is required`;
    if (errors['email']) return 'Enter a valid e-mail address';
    if (errors['server']) return String(errors['server']);
    return 'Invalid value';                                 // add one line per custom validator
  });
}
```

Usage: `<app-field-error [control]="form.controls.customerEmail" label="E-mail" />` under the input, linked
with `aria-describedby`. `control.events` (v18+) fires on value, status, touched and pristine changes.

## Submitting, disabling and file upload

- Disable the submit button with `[disabled]="submitting()"` only; disabling it on `form.invalid`
  hides *why* the form cannot be sent. `markAllAsTouched()` on submit surfaces every message.
  Reset after success with `form.reset()`; for edit forms `form.reset(dto)` repopulates.
- File upload: send `FormData`, let the browser set the multipart boundary (no manual
  `Content-Type`), and report progress through a signal:

```ts
readonly progress = signal<number | null>(null);

upload(file: File) {
  const body = new FormData();
  body.append('file', file, file.name);
  return this.http.post<UploadResult>(`${this.url}/import`, body, { reportProgress: true, observe: 'events' }).pipe(
    tap(e => { if (e.type === HttpEventType.UploadProgress && e.total) this.progress.set(Math.round((100 * e.loaded) / e.total)); }),
    filter((e): e is HttpResponse<UploadResult> => e.type === HttpEventType.Response),
    map(e => e.body!),
    finalize(() => this.progress.set(null)),
  );
}
```

Check size and extension client-side for fast feedback; the backend's ProblemDetails stays the source of truth.

## Examples

**Example 1**
Input: "Show a toast with the backend error when saving fails."
Output: `errorInterceptor` converts `HttpErrorResponse` to `ApiError`; a custom `ErrorHandler` in
`core/` (with `provideBrowserGlobalErrorListeners()`) shows `error.message` in a toast and logs
`traceId`. The component handles only `ApiError.isValidation` to map field errors; the rest rethrows.

**Example 2**
Input: "Add a search box that filters orders as the user types."
Output: `filter = signal('')` in the store, `toObservable(filter).pipe(debounceTime(300), distinctUntilChanged(), switchMap(api.list))`
with `startWith({ status: 'loading' })`, a `computed` `orders`, and a template `@if (store.loading())`
branch; no `effect`, no manual `subscribe` in the component.

## References

- `references/ngrx-signal-store.md` — read when the store pattern above is used by three or more
  features or when devtools, entity collections or shared store features are required.
