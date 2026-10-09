---
description: Add a minimal-API endpoint with validation, handler and integration tests
mode: agent
tools: ['codebase', 'editFiles', 'runCommands', 'runTests']
argument-hint: "Resource name and the HTTP verbs needed"
---
Add a REST endpoint for ${input:resource:Resource name, singular, e.g. Invoice}.

Before editing, read `src/Api/Endpoints/Orders.cs`, `src/Api/Validation/OrdersValidator.cs` and
`tests/Api.Tests/OrdersTests.cs`; new code copies their structure.

Steps:
1. Create `src/Api/Endpoints/${input:resource}Endpoints.cs` with `MapGet` (list, by id) and
   `MapPost`. Register it in `Program.cs` next to the other `Map*Endpoints()` calls.
2. Add request/response records in the same file; validate with FluentValidation.
3. Return `Results.Problem(...)` with the project's `ProblemDetails` factory for errors.
4. Add `tests/Api.Tests/${input:resource}Tests.cs` covering 200, 400 (validation) and 404.
5. Run `dotnet test tests/Api.Tests`. If a test fails, fix the code, not the test, and show me
   the failing output before changing anything else.

Constraints: do not add NuGet packages, do not change migrations, do not touch `src/Web`.
Finish with a short summary of files changed and anything you were unsure about.
