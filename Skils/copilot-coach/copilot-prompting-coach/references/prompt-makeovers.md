# Prompt makeovers: weak → strong

Read this when building training material or when a developer needs more before/after examples.
Each makeover names the lever that was missing (context, mode, prompt, iteration, review). The
`#file:` references are VS Code syntax; adapt for other clients.

## .NET

**Add an endpoint**
Weak: "Create an endpoint for customers."
Lever: prompt, context.
Strong (Agent mode):
```
Goal: add GET /customers/{id} and POST /customers to the minimal API.
Context: #file:src/Api/Endpoints/OrdersEndpoints.cs (copy structure), #file:src/Domain/Customer.cs
Constraints: .NET 9, FluentValidation for the POST body, Results.Problem for errors, no new packages.
Acceptance: tests in tests/Api.Tests/CustomersTests.cs for 200, 400, 404; `dotnet test` green.
Ask me before changing Program.cs beyond registering the endpoint group.
```

**Fix a failing test**
Weak: "Fix this test."
Lever: context, iteration.
Strong (Ask, then Edit): "#terminalLastCommand shows `InvoiceTotalTests.Rounds_half_up` failing with
expected 10.01 got 10.00. Explain which rounding `InvoiceCalculator.Total` uses (#file) and whether
the test or the code is wrong before changing anything."

**EF Core performance**
Weak: "This query is slow, optimise it."
Lever: context, acceptance.
Strong: "#file:OrderQueries.cs `GetOrdersWithItems` loads full entities then maps. Rewrite as a
projection to `OrderListItemDto` with `AsNoTracking`, keyset pagination on `Id`, and show the SQL
from `ToQueryString()` before and after. Keep the method signature. Follow the
`dotnet-ef-core-postgres` skill."

**Background job**
Weak: "Add a job that cleans old files."
Lever: constraints.
Strong: "Add a `BackgroundService` `ExportCleanupService` that deletes files in the `exports`
GCS bucket older than 30 days, runs daily at 02:00 UTC using `PeriodicTimer`, logs count deleted,
and is registered only when `Jobs:ExportCleanup:Enabled` is true. Use the existing
`IStorageClient` abstraction (#file:IStorageClient.cs); do not call the Google SDK directly. Unit
test the selection logic with a fake clock."

## Angular

**Migrate to signals**
Weak: "Use signals here."
Lever: acceptance, constraints.
Strong: "Convert #file:product-list.component.ts and its template to signal inputs (`input()`),
`computed()` for `filteredProducts`, and `@for` with `track product.id`. Keep the public selector
and inputs' names; the existing spec must pass unchanged. Explain each removed RxJS operator."

**Form with validation**
Weak: "Make a registration form."
Lever: context, examples.
Strong: "Create a standalone `RegistrationFormComponent` with a typed reactive form: email
(required, email), password (min 12), confirm (must match). Follow
#file:customer-form.component.ts for structure and error display. Submit calls
`AuthService.register` (#file:auth.service.ts) and shows the server error in the existing
`<app-alert>`. Add a spec covering invalid email, mismatch and successful submit."

**Debug change detection**
Weak: "The list does not update."
Lever: context.
Strong: "In #file:cart.component.ts the list does not re-render after `addItem`. The component is
`OnPush` and `items` is a plain array mutated with `push`. Propose the minimal fix using a signal
or a new array reference, explain why OnPush skipped the update, and do not change the service."

## GCP / infrastructure

**Terraform bucket**
Weak: "Terraform for a bucket."
Lever: constraints, acceptance.
Strong: "Add a module `infra/modules/exports-bucket` (GCS, `europe-central2`, uniform bucket-level
access, versioning, 30-day noncurrent lifecycle, CMEK from `var.kms_key_id`, labels from
`var.labels`). Mirror #file:infra/modules/uploads-bucket/main.tf. Wire it in
`infra/envs/dev/main.tf`. Run `terraform fmt` and `terraform plan -var-file=dev.tfvars` and paste
the plan; do not apply. Follow the `gcp-terraform-iac` skill."

**Cloud Run deploy workflow**
Weak: "Deploy this to Cloud Run."
Lever: context, "ask me before".
Strong: "Create `.github/workflows/deploy-api.yml`: build the `src/Api` Docker image, push to
Artifact Registry `europe-central2-docker.pkg.dev/$PROJECT/apps/api`, deploy to Cloud Run service
`api-dev` with Workload Identity Federation (no JSON keys). Use the existing `build.yml` for
caching patterns. Ask me before adding any secrets or changing IAM."

**Explain a plan**
Weak: "Is this plan OK?"
Lever: prompt.
Strong: "#terminalLastCommand is a `terraform plan`. List every destroy or replace, explain the
cause for each (which attribute forces replacement), and tell me which are safe at 14:00 on a
weekday for the dev project."

## Tests

Weak: "/tests" on a 400-line file.
Lever: context, acceptance.
Strong: select the single method, then: "/tests for `CalculateDiscount`: cover zero quantity,
negative price (should throw `ArgumentOutOfRangeException`), tier boundaries 10 and 100, and the
rounding to 2 decimals. Use the existing `DiscountTestData` builder. No mocks."

Review question to ask of any generated test: "Which line in the implementation would I have to
break for this test to fail?" If the answer is "none", the test asserts the bug.

## Debugging with logs

Weak: paste 300 lines of logs, "what is wrong?"
Lever: context hygiene, security.
Strong: redact IDs and tokens, keep the first exception and its inner exception, then: "This is
the stack trace from `POST /invoices` in staging (#file:trace.txt). The code is
#file:InvoiceEndpoints.cs and #file:InvoiceService.cs. Identify the null source and propose the
smallest fix plus a regression test."

## Meta-prompts that help

- "Before writing code, list the files you will change and why. Wait for my OK."
- "List the assumptions you made."
- "Give me two options with trade-offs; do not implement yet."
- "Explain this in Polish, keep code and identifiers in English."
- "Show the diff only for the method you changed."
