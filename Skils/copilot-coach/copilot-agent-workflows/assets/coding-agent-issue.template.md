---
name: Task for Copilot coding agent
about: A self-contained change that the coding agent can implement and verify
title: "[agent] <verb> <thing>"
labels: ["copilot-agent"]
---

## Goal

<One sentence, user-visible outcome. Example: "Customers can export invoices for a date range as CSV.">

## Scope

In scope:
- <bullet>

Out of scope (do not touch):
- <bullet, e.g. "src/Web (Angular app)", "migrations">

## Pointers

- Copy the structure of: `<path/to/exemplary/file>`
- Types involved: `<path>`, `<path>`
- Relevant docs: `<docs/...>` or `<URL>`
- Follow skill: `<dotnet-api-design / angular-modern-components / gcp-terraform-iac>`

## Acceptance criteria

<Observable behaviour plus the commands that prove it.>

- `GET /invoices/export?from=2026-01-01&to=2026-01-31` returns `text/csv` with columns `<...>`
- Empty range returns 200 with header row only
- Unknown customer returns 404 via `Results.Problem`
- `dotnet test tests/Api.Tests` passes; new tests cover the three cases above

## Definition of done

- [ ] Tests added and passing
- [ ] No changes outside the scope above
- [ ] Docs / API description updated (`docs/api.md`)
- [ ] Changelog entry under "Unreleased"
- [ ] PR description lists assumptions made

## Decisions already made

<Everything the agent might otherwise have to ask. Example: "CSV separator is `;` (Polish Excel
default). Dates formatted ISO-8601. Max 50k rows; return 400 above that.">

## Custom agent / reviewers

- Implement with: default agent
- Review with: `migration-reviewer` (if a migration appears), Copilot code review, then a human
