# .NET code review checklist (full)

Read this for a complete review of a non-trivial PR. Work through the categories in order; the
early ones ship incidents more often. For every item that applies, write the failure scenario
before writing the finding; if no realistic scenario exists, drop it or make it a Nit.

Severity guide: Blocker = data loss, security, crash, wrong result under realistic conditions.
Major = fails under load, retries, concurrency or the next change. Minor = fragile or misleading.
Nit = style.

## 1. Intent and scope

- [ ] Does the diff do what the description says, and nothing unrelated (drive-by refactors mixed
      with a bug fix make rollbacks hard)?
- [ ] Are public types, DTOs, configuration keys, database schema or message contracts changed?
      Each is a compatibility question, not just a code question.
- [ ] Is anything on a hot path: per request, per message, inside a loop over user data?

## 2. Async and threading

- [ ] `async void` anywhere except event handlers.
- [ ] `.Result`, `.Wait()`, `.GetAwaiter().GetResult()`, `Task.Run(...).Result` in request code.
- [ ] Tasks created and not awaited or stored (fire-and-forget); `_ = SomethingAsync()` without a
      comment and error handling.
- [ ] `Task.Run` used to "make it async" in ASP.NET (wastes a thread, no benefit).
- [ ] `async` method with no `await` returning `Task.FromResult`: fine; `async` with a synchronous
      blocking call inside: not fine.
- [ ] `ValueTask` awaited twice or stored.
- [ ] `ConfigureAwait(false)` consistently present in library code (NuGet packages, shared SDKs);
      not required in ASP.NET Core app code.
- [ ] `lock` on `this`, on a string, or on a public object; `SemaphoreSlim` released outside
      `finally`.
- [ ] Shared mutable state in singletons, static fields, or captured in lambdas registered once.
- [ ] `Parallel.ForEachAsync`/`Task.WhenAll` over operations that share a `DbContext` or other
      non-thread-safe object.
- [ ] Timers/`PeriodicTimer`/`BackgroundService` loops: exceptions caught per iteration, token
      observed, backoff on failure.

## 3. Disposal and resources

- [ ] Every `IDisposable`/`IAsyncDisposable` created locally is disposed (`using`, `await using`),
      including `HttpResponseMessage`, `Stream`, `IServiceScope`, `CancellationTokenSource`,
      `NpgsqlConnection`, `DbContext` from a factory.
- [ ] Objects owned by the DI container are not disposed manually.
- [ ] `HttpClient` from `IHttpClientFactory` or a typed client, never `new HttpClient()` per call or
      a static instance without `PooledConnectionLifetime`.
- [ ] Streams returned from methods document who disposes them; `MemoryStream` not copied
      needlessly (`ToArray()` on a large stream).
- [ ] Linked `CancellationTokenSource` (`CreateLinkedTokenSource`) disposed.

## 4. Cancellation

- [ ] Handlers, services and repositories accept `CancellationToken` and pass it through to EF,
      `HttpClient`, `Stream` and `Channel` calls.
- [ ] `CancellationToken.None` or `default` passed in request code only with a comment explaining
      the point of no return (after commit, outbox publish).
- [ ] `OperationCanceledException` not logged as an error or mapped to 500.
- [ ] Timeouts: outbound calls have a timeout (resilience handler or `CancelAfter`), long queries
      have a `CommandTimeout`.

## 5. Exceptions and error handling

- [ ] No empty `catch`; no `catch (Exception)` that logs and continues unless the loop must
      continue by design (and then the error is counted/alerted).
- [ ] `throw;` rather than `throw ex;`.
- [ ] Exceptions carry context (ids) and wrap the inner exception.
- [ ] Domain exceptions map to status codes in one place (`IExceptionHandler`), not per endpoint.
- [ ] No exceptions for expected flow on hot paths (`TryParse`, `TryGetValue`).
- [ ] `ArgumentNullException.ThrowIfNull` / `ArgumentOutOfRangeException.ThrowIf*` at public
      boundaries rather than deep inside.
- [ ] Retries do not wrap non-idempotent operations without an idempotency key.

## 6. Nullability and types

- [ ] No new `#pragma warning disable CS86xx` / `!` without justification.
- [ ] `FirstOrDefault`/`SingleOrDefault` results checked for null; `Single` used only where the
      invariant is guaranteed (unique index) and the exception is acceptable.
- [ ] `required` / `init` on DTO members; no mutable public setters on domain entities without
      reason.
- [ ] `decimal` for money with `HasPrecision`; `DateTimeOffset`/UTC `DateTime`; `TimeProvider`
      rather than `DateTime.UtcNow` in logic that is tested.
- [ ] Enum values serialised as strings if they are part of the API (`JsonStringEnumConverter`);
      new enum members placed at the end if persisted as int.
- [ ] Records with collection members: equality and `GetHashCode` semantics understood.

## 7. EF Core and data access

- [ ] Queries project (`Select`) on read paths; `AsNoTracking` where no update follows.
- [ ] No N+1: no navigation access in loops without `Include`/projection; collection includes use
      `AsSplitQuery` when more than one.
- [ ] No `ToList()`/`AsEnumerable()` before filtering, ordering or paging.
- [ ] Paging has an `OrderBy` on a deterministic key.
- [ ] Indexes exist for new `Where`/`Join`/`OrderBy` columns on large tables.
- [ ] `SaveChangesAsync` once per unit of work, not per item; bulk operations use
      `ExecuteUpdate`/`ExecuteDelete` where appropriate.
- [ ] Transactions do not span external calls; execution strategy used with retries.
- [ ] Concurrency token (`xmin`) on entities updated from several places; `DbUpdateConcurrencyException`
      handled.
- [ ] Raw SQL parameterised (`FromSql`, `ExecuteSql`), never interpolated into `FromSqlRaw`.
- [ ] `DbContext` lifetime scoped; not captured by singletons or background services without a
      scope.
- [ ] Migration reviewed: `Up`/`Down` both correct, data-preserving rename vs drop+add, no edit of
      an applied migration, snapshot updated, `CONCURRENTLY` for indexes on big tables, no
      `Migrate()` at startup for multi-instance deployments.

## 8. Security

- [ ] Every new endpoint has an authorization policy or an explicit, justified `AllowAnonymous`.
- [ ] Object-level authorization: the caller may access this specific resource, not just the
      endpoint.
- [ ] Input used in SQL, file paths, process arguments, URLs, LDAP, regex (ReDoS: untrusted pattern
      or catastrophic backtracking) is validated or parameterised.
- [ ] No secrets in source, config files, test fixtures, Dockerfiles or logs; secrets read from
      environment/secret manager.
- [ ] Logs do not contain tokens, passwords, full card numbers, personal data beyond what the
      policy allows.
- [ ] Error responses hide stack traces and internal messages on 5xx.
- [ ] Deserialisation: no `BinaryFormatter`, no `TypeNameHandling` other than `None`, `XmlReaderSettings.DtdProcessing = Prohibit`.
- [ ] Outbound URLs from input validated against an allow-list (SSRF).
- [ ] Crypto: `RandomNumberGenerator` for tokens, modern password hashing, TLS not disabled
      (`ServerCertificateCustomValidationCallback` returning true).
- [ ] Upload handling: size limits, content-type checks, files stored outside the web root.
- [ ] Request body/size limits unchanged or justified; rate limiting present for public endpoints.
- [ ] CORS origins explicit; JWT validation parameters not weakened.
- [ ] Request DTOs separate from entities (no mass assignment).

## 9. Performance (hot paths only)

- [ ] Multiple enumeration of `IEnumerable<T>`; `IQueryable` enumerated twice.
- [ ] LINQ in tight loops; `Count()` on collections; `OrderBy().First()` instead of `MinBy`.
- [ ] String building in loops; `string.Format`/interpolation in log calls instead of templates.
- [ ] Per-call allocation of `JsonSerializerOptions`, `Regex`, `HttpClient`, `StringBuilder` where a
      cached instance fits.
- [ ] Synchronous I/O (`File.ReadAllText`, `.Read(...)`) on request threads.
- [ ] Large arrays or `MemoryStream` where streaming or `ArrayPool` applies; `ToArray()` chains.
- [ ] Boxing via `object`/`params object[]`, `Enum.ToString()` in loops, `string` comparisons
      without `StringComparison`.
- [ ] Caches have size limits and expiration; channels and queues are bounded.
- [ ] `Task.WhenAll` over an unbounded list of I/O operations (needs a `SemaphoreSlim` or
      `Parallel.ForEachAsync` with `MaxDegreeOfParallelism`).

## 10. API contract

- [ ] No property removed, renamed, retyped or made required in a published DTO without a version
      bump.
- [ ] Status codes unchanged for existing scenarios; new error cases use ProblemDetails.
- [ ] JSON casing, date formats, enum serialisation consistent with the rest of the API.
- [ ] OpenAPI document updated (or generated) and diffed.
- [ ] Deprecations announced (`Sunset` header, docs) rather than silent removal.

## 11. Configuration and operations

- [ ] New settings bound through an options class with validation and `ValidateOnStart`.
- [ ] Defaults safe for production; development-only behaviour gated by environment.
- [ ] Health checks updated for new dependencies; new dependencies have timeouts.
- [ ] Logging at appropriate levels (no `Information` per item in loops); structured properties
      named consistently (`OrderId` everywhere).
- [ ] Feature flags/kill switches for risky changes.

## 12. Tests

- [ ] New behaviour has a test that fails without the change.
- [ ] Bug fix includes a regression test reproducing the bug.
- [ ] Integration tests cover the error paths (400, 401/403, 404, 409), not only the happy path.
- [ ] No `Thread.Sleep`, real clock, real network or shared mutable fixtures across parallel
      tests; `FakeTimeProvider` used for time.
- [ ] Tests assert behaviour, not implementation (`Received()` calls only where the call is the
      contract).
- [ ] Test names describe the rule; a reader can derive the specification from the test list.

## 13. Readability and consistency (Minor / Nit)

- [ ] Follows the codebase's existing patterns (endpoint style, folder layout, naming).
- [ ] Methods short enough to name; no boolean parameters that change behaviour (`DoWork(true)`).
- [ ] Comments explain why, not what; no commented-out code; no TODOs without an issue reference.
- [ ] `var` usage, expression bodies, pattern matching consistent with `.editorconfig`.
- [ ] Public members documented when the project ships a package.
