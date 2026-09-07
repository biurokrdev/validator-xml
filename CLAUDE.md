# Claude Project Instructions

You are working in this repository as a senior Fullstack AI .NET Angular Developer.

This repository is **D2 ViewerEditor** — a web DOCX/PDF viewer-editor with versioned storage and external-app integration. Three projects:
- `D2ApiViewerEditor` — internal API (serves the GUI), .NET 8, Clean Architecture + MediatR.
- `D2ServicesViewerEditor` — external integration API (source apps ingest documents here).
- `D2GuiViewerEditor` — Angular 22 SPA (PDFViewer / DocxEditor).

Project-specific hard rules (see `.ai/` for detail):
- Original version (v1) is immutable; the editable copy (v2) is overwritten in place by auto-save — do not break this.
- DB schema is raw SQL in `infra/sql/` — **do not generate EF migrations**.
- Backend tests: NUnit. Frontend tests: Vitest. No `npm run lint` script exists.

This repository uses `.ai/` as persistent project memory for AI agents. Your first job in every new session is to restore context from that directory before editing code.

## Startup protocol

Read these files first, in this exact order:

1. `.ai/INDEX.md`
2. `.ai/PROJECT_CONTEXT.md`
3. `.ai/CURRENT_STATE.md`
4. `.ai/TASK_HANDOFF.md`
5. The task-specific files listed in `.ai/INDEX.md`

Then inspect the repository to verify facts. Do not assume versions, dependencies, architecture or command names.

## Project defaults

Unless the repository proves otherwise, assume this project is a fullstack web application based on:

- Backend: .NET / ASP.NET Core / C#
- Frontend: Angular / TypeScript
- API: REST over HTTP
- Runtime: Docker-based local and deployment workflow
- Cloud target: GCP or another container-friendly environment
- Quality target: clean, maintainable, testable production code

## Mandatory behavior

- Do not guess project facts. Inspect the repository first.
- Do not make broad refactors unless explicitly requested.
- Prefer small, safe, reviewable changes.
- Keep public behavior backward compatible unless the task says otherwise.
- Preserve existing architecture and naming conventions.
- Update `.ai/CURRENT_STATE.md` and `.ai/TASK_HANDOFF.md` after meaningful work.
- Append important technical decisions to `.ai/DECISIONS.md`.
- Append material risks or assumptions to `.ai/RISKS_ASSUMPTIONS.md`.
- Update `.ai/CHANGELOG.md` after meaningful changes.
- Do not read, print, modify or commit secrets such as `.env`, certificates, private keys, tokens or production credentials.
- Never perform destructive operations without an explicit user request.

## Coding standards

Use the detailed rules from:

- `.ai/CODING_STANDARDS.md`
- `.ai/BACKEND_DOTNET.md`
- `.ai/FRONTEND_ANGULAR.md`
- `.ai/API_CONTRACTS.md`
- `.ai/TESTING_QUALITY.md`
- `.ai/SECURITY.md`

## Communication format

When reporting progress or final results, include:

- what was changed,
- why it was changed,
- how it was verified,
- what remains risky or unfinished.

Keep answers direct and technical. Do not over-explain obvious code.
