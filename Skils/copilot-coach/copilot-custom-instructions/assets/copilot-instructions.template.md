# <Project name>

<One paragraph: what the system does, who uses it, the stack with versions
(e.g. ".NET 9 minimal API, Angular 20, PostgreSQL 16 on Cloud SQL, Cloud Run, Terraform") and
anything unusual an outsider would not guess.>

## Build, test, run

- Restore and build: `<command>`
- Unit tests: `<command>`
- Integration tests: `<command>` (prerequisite: `<Docker / emulator / env var>`)
- Frontend: `<command>`
- Lint / format check: `<command>`
- Run locally: `<command>`; configuration comes from `<appsettings.Development.json / .env / secret manager>`

## Repository layout

- `<folder>` — <what lives here, one line>
- `<folder>` — <...>
- `tests/` — mirrors `src/`; integration tests use <Testcontainers / emulator>

## Conventions

<Five to ten rules an outside contributor gets wrong. Each one: the rule, the reason, and a file
to copy from.>

- <Rule>. Why: <reason>. Example: `<path/to/file>`.
- <Rule>. Why: <reason>. Example: `<path/to/file>`.

## Definition of done

- Tests added or updated and passing locally with the commands above.
- <Migration added / API docs updated / changelog entry> when relevant.
- No new compiler or lint warnings.

## Avoid

- <Thing to avoid> because <reason>.
- <Thing to avoid> because <reason>.

## See also

- `.github/instructions/` — per-language rules (C#, Angular, Terraform).
- `.github/skills/` — procedures (persistence, testing, deployment).
- `.github/prompts/` — reusable tasks; type `/` in chat to list them.
