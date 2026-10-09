---
name: dotnet-code-review
description: >-
  Review a .NET pull request or diff and report findings ordered by severity with file:line, a
  concrete failure scenario and a suggested fix. Covers correctness (async/await misuse, disposal,
  cancellation, swallowed exceptions), concurrency, EF Core pitfalls, security (injection, secrets,
  missing auth), performance (allocations, LINQ in hot paths, multiple enumeration, string
  concatenation), API contract changes, missing tests and unreviewed migrations. Use this skill
  whenever the user asks to "review", "look over", "check", "critique" or "find bugs in" C# code, a
  PR, a branch, a diff or a file, asks "is this safe/correct/thread-safe", "anything wrong with
  this", or pastes .NET code and asks for feedback, even without the word "review".
---

# Reviewing .NET code

This skill produces a review that an author can act on: each finding names the place, explains
what goes wrong at runtime and shows the fix. Reviews that list style preferences first and bury
the race condition in the middle get skimmed; reviews that say "this looks wrong" without a
scenario get argued with. The categories below are ordered by how often they ship real incidents
in .NET services, and the output format forces the reviewer to justify each finding.

## Workflow

1. **Read the intent first**: PR description, linked issue, commit messages. Then read the whole
   diff once without commenting. Findings made before understanding the goal are mostly wrong.
2. **Map the blast radius**: which layers change (domain, persistence, HTTP contract, config),
   whether a migration is included, whether public DTOs or serialised types change, whether any
   code runs on a hot path (per request, per message, in a loop).
3. **Run the checklist by category** (below, full version in `references/checklist.md`). For each
   candidate finding, write the failure scenario in one sentence; if you cannot, it is a nit or not
   a finding.
4. **Run what you can**: `dotnet build -warnaserror`, `dotnet test`, `dotnet format --verify-no-changes`,
   and for migrations `dotnet ef migrations script` to read the SQL. Tools find the mechanical
   issues so the review can focus on design.
5. **Write the report** in the output format below, ordered by severity, grouped by file inside a
   severity. Lead with the one finding that would block the merge if nothing else were fixed.
6. **Verify the review**: every Blocker/Major has a file:line, a scenario and a fix; nothing in
   Nits is secretly a Major; the summary states whether the PR is mergeable after the fixes.

## Severity and output format

| Severity | Meaning |
|---|---|
| Blocker | Data loss, security hole, crash or wrong result under realistic conditions; must fix before merge |
| Major | Bug or risk that will surface under load, retries, concurrency or a later change; fix before or immediately after merge |
| Minor | Correct today but fragile, misleading or inconsistent with the codebase |
| Nit | Style, naming, wording; optional |

```
## Summary
One paragraph: what the PR does, overall verdict (mergeable / mergeable after Blockers / needs rework), test coverage assessment.

## Findings

### Blocker
1. `src/Orders/OrderService.cs:84` — SaveChangesAsync is called without the request CancellationToken, inside a loop.
   Scenario: client cancels after 3 of 50 orders; the remaining 47 still write, and the client retries, producing duplicates.
   Fix: pass `ct` to every EF call and move the loop into a single `SaveChangesAsync` after `AddRange`.

### Major
2. `src/Api/Endpoints/OrderEndpoints.cs:31` — ...

### Minor
...

### Nit
...

## Missing tests
- No test covers the 409 path when the order number already exists.

## Migration review
- `20260301_AddOrderNumber.cs` drops column `ExternalRef` (data loss); confirm intended or replace with `RenameColumn`.
```

Keep findings to one issue each; if the same mistake repeats, report it once with all locations.
Quote the exact code only when it is load-bearing. Skip praise paragraphs; a one-line note of what
is done well is enough.

## Correctness: async, disposal, cancellation, exceptions

- `async void` outside event handlers: exceptions crash the process and the caller cannot await.
  Return `Task`.
- `.Result`, `.Wait()`, `.GetAwaiter().GetResult()` on async code in ASP.NET: thread-pool
  starvation under load and deadlocks where a synchronisation context exists. Make the call chain
  async.
- Missing `await` on a `Task` (fire-and-forget): exceptions vanish, work may be cut off when the
  request scope disposes. Await it, or hand it to a background service with its own scope.
- `ConfigureAwait(false)` is for libraries that may be called from a UI or legacy sync context;
  in ASP.NET Core application code it changes nothing. Do not demand it in app code; do flag its
  absence in a NuGet library's public surface.
- `IDisposable`/`IAsyncDisposable` created and not disposed (`HttpResponseMessage`, `Stream`,
  `DbConnection`, `CancellationTokenSource`, `IServiceScope`): leaks connections/handles until GC.
  `using`/`await using`, or register with the container.
- `CancellationToken` accepted but not passed on, or `CancellationToken.None` passed from request
  code without a comment: work continues after the client left and connections stay busy.
- `catch (Exception) { }`, `catch { return null; }`, or logging and continuing: the failure
  surfaces later as a null reference or a silent data gap. Catch only what you can handle; rethrow
  with `throw;` (not `throw ex;`, which resets the stack trace).
- Catching `OperationCanceledException` and reporting it as a 500 or an error log: cancellation is
  expected; map it to 499/204 and log at Debug.
- Exceptions used for control flow on hot paths (`int.Parse` in `try` instead of `TryParse`).
- Null handling: `!` on values that can legitimately be null (deserialised DTOs, `FirstOrDefault`).
  Nullable warnings-as-errors should be on; if the PR adds `#pragma warning disable CS8`, ask why.
- `DateTime.Now` instead of `DateTime.UtcNow`/`TimeProvider`; `DateTime` with `Kind.Unspecified`
  written to `timestamptz`.
- Floating point for money; `decimal` without a precision in the EF mapping.
- Equality on records with collection members (reference equality on `List<T>` surprises in tests
  and caches).

## Concurrency

- Shared mutable state in singletons (a `Dictionary`, `List`, a counter) mutated from request
  threads: corrupted collections, lost updates. Use `ConcurrentDictionary`, immutable snapshots, or
  a scoped service.
- `ConcurrentDictionary.GetOrAdd` with an expensive factory that must run once: the factory can run
  concurrently; wrap in `Lazy<T>`.
- Awaiting inside `lock` is a compile error, so code reaches for `Monitor.Enter` or `SemaphoreSlim`;
  a wait not released in `finally` deadlocks the next caller. Use `SemaphoreSlim.WaitAsync` with
  `try/finally`.
- `DbContext` used from parallel tasks (`Task.WhenAll` over queries on the same context): EF Core
  throws or corrupts tracking. Sequential awaits or one context per task via `IDbContextFactory`.
- Check-then-act on the database ("if not exists then insert"): races between two requests. Rely
  on a unique constraint and handle the violation (see `dotnet-ef-core-postgres`).
- `static` `HttpClient` created per class with no `IHttpClientFactory`: DNS changes ignored; or
  `new HttpClient()` per call: socket exhaustion.
- Background services: `ExecuteAsync` without catching per-iteration exceptions dies silently on
  the first error (host keeps running, work stops); missing `stoppingToken` in loops.

## EF Core pitfalls

- N+1: iterating a navigation inside a loop without `Include` or projection.
- Loading entities to count or check existence (`ToList().Count`, `.Any()` after `ToList()`).
- Tracking queries on read paths; `Include` chains where a `Select` projection would do.
- `SaveChanges` inside a loop; a transaction spanning an external HTTP call.
- Client evaluation hidden by an early `AsEnumerable()`/`ToList()`.
- `FromSqlRaw` with string interpolation: SQL injection. Use `FromSql`/`FromSqlInterpolated` or
  parameters.
- Migrations: drops/renames of columns with data, index creation on a large table without
  `CONCURRENTLY`, a migration edited after being applied elsewhere, model snapshot not updated,
  `Database.Migrate()` added to startup of a multi-instance service.
- Missing `AsNoTracking`, missing index for a new `Where`/`OrderBy`, missing `ct` on async calls.

## Security

- Authorization: new endpoint without a policy where the API uses opt-in auth; `AllowAnonymous`
  added without justification; object-level checks missing (user A reads order of user B by id).
- Injection: raw SQL, shell commands, LDAP, or file paths built from input; `Path.Combine` with a
  user-supplied segment (path traversal) without `Path.GetFullPath` + prefix check.
- Secrets in code, `appsettings.json`, test files or logs; connection strings with passwords in a
  committed file.
- Logging personal data or tokens; exceptions with `Detail` returned on 500.
- Deserialisation of untrusted input with `BinaryFormatter`, `TypeNameHandling.All`, or
  `XmlReader` with DTD processing enabled (XXE); `JsonSerializer` with unbounded depth on large
  payloads is fine, but a `MaxRequestBodySize` override is not.
- Outbound requests to URLs taken from input (SSRF) without an allow-list.
- Weak crypto (`MD5`/`SHA1` for passwords, `Random` for tokens): use `RandomNumberGenerator`,
  `PasswordHasher<T>` or Argon2/bcrypt libraries.
- CORS `AllowAnyOrigin` with credentials; JWT validation with `ValidateIssuer = false` or
  `ValidateLifetime = false`.
- Mass assignment: binding a domain entity or EF model directly as the request body.

## Performance

Flag these only on hot paths (per request, per message, loops over large sets); elsewhere they are
nits.

- LINQ chains allocating closures and iterators in tight loops; `Count()` where `Length`/`Count`
  exists; `Any()` on an `ICollection` is fine, `Count() > 0` is not.
- Multiple enumeration of an `IEnumerable<T>` (iterated twice, often once for `Any()` and once in
  `foreach`), especially when it wraps a database query: two queries, or an `ObjectDisposedException`.
  Materialise once (`ToList()`/`ToArray()`) or accept `IReadOnlyList<T>`.
- String concatenation in loops (`+=`); use `StringBuilder`, `string.Join` or interpolated
  handlers. Logging with interpolation instead of message templates (CA2254).
- `new JsonSerializerOptions()` per call, `new Regex()` per call (use `[GeneratedRegex]` or a static
  compiled instance), `new HttpClient()` per call.
- Boxing through `object` parameters, `params object[]` in hot logs, struct enumerators lost via
  interface casts.
- Large `byte[]` allocations per request where `ArrayPool<byte>`/`Span<T>` or streaming apply;
  reading a whole file or response into memory to forward it.
- Synchronous I/O (`File.ReadAllText`, `stream.Read`) on request threads.
- Unbounded caches (`MemoryCache` entries without size or expiration), unbounded `Channel`s.

## API contract, tests and migrations

- Contract: removed or renamed JSON property, changed type or nullability, new required request
  field, changed status code, changed enum string values, changed default page size. Each of these
  breaks a client; require a version bump or a compatibility shim (see `dotnet-api-design`).
- Serialisation attributes and casing consistent with the rest of the API; `DateTime` formats
  unchanged.
- Tests: new behaviour has a test that fails without the change; bug fixes include a regression
  test; integration tests cover the error paths, not only 200. Tests with `Thread.Sleep`, real time
  or network calls are flagged as flaky (see `dotnet-testing`).
- Migrations: read the generated `Up`/`Down` and the SQL; confirm naming, data-preserving renames,
  index strategy, and that the model snapshot changed accordingly.

## Examples

**Example 1**
Input: "Review this PR that adds order cancellation." (diff includes a handler, an endpoint, a
migration)
Output: Summary; Blocker at `OrderService.cs:84` (status check-then-update without concurrency
token, scenario: two cancel requests both succeed and both refunds are issued, fix: `xmin` row
version + 409 on `DbUpdateConcurrencyException`); Major at `OrderEndpoints.cs:31` (no
`RequireAuthorization`, scenario: anonymous caller cancels any order by id); Minor on
`CancellationToken` not forwarded; Missing tests: concurrent cancel, 401; Migration: adds index
without `CONCURRENTLY` on `orders`, acceptable if table is small, otherwise change.

**Example 2**
Input: "Is this thread-safe?" (a singleton with a `Dictionary<string, Token>` cache)
Output: Major finding with scenario (two requests add the same key, `Dictionary` throws or
corrupts internal state), fix with `ConcurrentDictionary<string, Lazy<Task<Token>>>` so the token
fetch runs once per key, plus a note on expiry.

## References

- `references/checklist.md` — read when doing a full review of a non-trivial PR; it is the
  complete per-category checklist with the exact questions to ask for each item.
