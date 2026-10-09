---
description: Angular conventions for the web application
applyTo: "src/Web/src/app/**/*.ts,src/Web/src/app/**/*.html"
---
# Angular rules for `src/Web`

- Components are standalone; there are no NgModules in this project. Copy
  `src/Web/src/app/features/orders/order-card.component.ts`.
- Use built-in control flow (`@if`, `@for`, `@switch`), never `*ngIf` / `*ngFor`.
- Inputs and outputs are signal-based: `input()`, `input.required()`, `output()`.
- Inject with `inject(Service)` in field initialisers, not constructor parameters.
- State in components is `signal()` / `computed()`; derived server state comes from
  `resource()` / `httpResource()` or a store, not from manual `subscribe` in `ngOnInit`.
- Forms are typed reactive forms (`FormGroup<...>`); see `customer-form.component.ts`.
- Tests: Jest + Angular Testing Library style (`render`, `screen`), one `describe` per component,
  no `TestBed` boilerplate beyond what `render` needs. See `order-card.component.spec.ts`.
- Styling: Tailwind utility classes; no new global SCSS.
- Run `npm run lint && npm test -- --watch=false` before declaring a change done.
