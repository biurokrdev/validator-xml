# Workshop agendas and exercises

Read this when running a session. Times are in minutes from the start. Every exercise has an
acceptance check that the participant runs; the facilitator never declares success for them.

## Facilitator preparation (day before)

- Exercise branch pushed with `README-LAB.md` listing tasks; `.github/copilot-instructions.md`
  and prompt files present; team skills installed in `.github/skills/`.
- Each participant confirmed: Copilot signed in, chat opens, Agent mode available, repo cloned,
  build and tests pass once.
- One prepared "generated diff with two bugs" per stack for the review exercise (e.g. a test
  that mocks the unit under test; an API that does not exist in the pinned framework version).
- Projector shows the facilitator's VS Code with font size 18+; a second screen for the timer.

## 90-minute kick-off (everyone)

| Time | Block | Notes |
|---|---|---|
| 0–10 | Why, goals, metrics policy | state explicitly: trends only, no individual ranking |
| 10–25 | Mental model and demo | show the context window table; demo `#file`, `#codebase`, Ask vs Agent on the real repo; expand *References* to show instructions loaded |
| 25–55 | Exercise A in pairs | one small real ticket per pair; written prompt (goal/context/constraints/acceptance); Agent mode; run tests |
| 55–60 | Debrief A | one surprise per pair, facilitator writes them on the board |
| 60–70 | Review discipline | failure-mode table; 5-minute "find the two bugs" on the prepared diff |
| 70–80 | Guardrails, licensing, objections | one-pager; invite objections openly and answer from the table in SKILL.md |
| 80–90 | Commitments and champions | each person names one thing to try; champions announced; office hour schedule shown |

## Half-day lab (3.5 h, one role)

| Time | Block |
|---|---|
| 0–15 | Environment check, lab README walkthrough, pairing |
| 15–60 | Exercise 1 (45 min) |
| 60–70 | Debrief 1: what context did you give, what did you reject |
| 70–80 | Break |
| 80–130 | Exercise 2 (50 min) |
| 130–140 | Debrief 2 |
| 140–180 | Exercise 3 (40 min) |
| 180–195 | Capture: each pair writes one prompt file or instruction line and opens a PR |
| 195–210 | Mini-retro (keep / change / try) |

## Exercises: .NET

**1. Endpoint with tests (Agent mode)**
Task: add `GET /invoices/{id}` and `POST /invoices` following the existing `Orders` endpoints.
Prompt scaffold: use `/add-endpoint` if present, otherwise write goal/context/constraints/acceptance.
Acceptance: `dotnet test tests/Api.Tests` green; new tests cover 200, 400, 404; no changes outside
`src/Api` and `tests/Api.Tests`.
Debrief question: did the agent touch files you did not expect? Why?

**2. Migration + review agent**
Task: add `DueDate` (nullable `timestamptz`) to `Invoice`, generate a migration, then run the
`migration-reviewer` custom agent (or a reviewer prompt) on it.
Acceptance: migration contains only `AddColumn`; reviewer output lists no blockers; `dotnet ef
migrations script` runs.
Debrief: what did the reviewer catch that you would have missed? Skill: `dotnet-ef-core-postgres`.

**3. Fix a failing test with context**
Task: a prepared failing test (rounding bug). Use `#terminalLastCommand` and Ask mode to find
whether the test or the code is wrong, then Edit mode for the minimal fix.
Acceptance: only one method changed; test passes; explanation written in two sentences.

## Exercises: Angular

**1. Migrate a component to signals**
Task: convert `order-list.component` from `@Input()` and `BehaviorSubject` to `input()`,
`signal()`, `computed()`, `@for` with `track`.
Acceptance: existing spec passes unchanged; `npm run lint` clean; no `*ngFor` left.
Skill: `angular-modern-components`.

**2. Typed form with spec**
Task: create `registration-form` (typed reactive form, three fields, cross-field validator)
following `customer-form`; generate a spec with `/tests` after selecting the component class.
Acceptance: spec covers invalid email, mismatch, success; the test would fail if the validator
were removed (verify by commenting it out). Skill: `angular-data-and-forms`, `angular-testing`.

**3. Resolve a deprecated API with `#fetch`**
Task: a prepared component uses a deprecated API; ask Copilot with `#fetch` on the Angular docs
page for the current version to migrate it.
Acceptance: no deprecation warnings in the build; the answer cites the docs page.

## Exercises: GCP

**1. Terraform bucket and plan review**
Task: add an `exports-bucket` module (region, uniform access, versioning, lifecycle, CMEK) copying
`uploads-bucket`; wire it in `envs/dev`.
Acceptance: `terraform fmt -check` clean; `terraform plan` shows only additions; the participant
explains each resource in the plan. Skill: `gcp-terraform-iac`.

**2. Cloud Run deploy workflow**
Task: a GitHub Actions workflow building and deploying `src/Api` with Workload Identity
Federation, copying cache patterns from `build.yml`.
Acceptance: workflow lints (`actionlint` if available); no JSON key secrets; `--min-instances`
and `--cpu` set per `docs/runtime.md`. Skill: `gcp-cloud-run-deploy`.

**3. Explain and improve an alert policy**
Task: paste the YAML of an existing alerting policy with `#file`; ask for an explanation and SLO
thresholds with rationale.
Acceptance: proposed thresholds are justified from the service's traffic numbers in `docs/`;
the participant can defend one of them. Skill: `gcp-observability`.

## Advanced session (90 min, week 4)

| Time | Block |
|---|---|
| 0–15 | Coding agent demo: assign an issue written with the template, watch the session, review the PR |
| 15–45 | Exercise: each pair writes an issue for the coding agent from the backlog using `copilot-agent-workflows/assets/coding-agent-issue.template.md`; assign it |
| 45–60 | Custom agents: create or adapt `migration-reviewer` / `test-writer` |
| 60–75 | MCP: review `.vscode/mcp.json`; discuss what each server sends out |
| 75–90 | Review the agent PRs that came back; debrief on issue quality vs result quality |

## Show-and-tell format (30 min)

Three presenters, 7 minutes each: one win, one failure, one prompt they now reuse. 9 minutes of
questions. Record it and link the recording and the prompts in the wiki. The facilitator adds
the reusable prompts to `.github/prompts/` the same day.

## Office hour format (45 min, weekly)

Drop-in. The coach starts with a 5-minute tip (one feature), then takes questions on real code.
Keep a running list of questions; recurring ones become instruction lines or cheat-sheet items.
