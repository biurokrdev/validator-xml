---
name: copilot-adoption-workshop
description: >-
  Design and run a GitHub Copilot adoption programme for a software team: a 4–6 week plan with
  kick-off, role-based hands-on labs (.NET, Angular, GCP), office hours, show-and-tell and retro;
  90-minute and half-day workshop agendas with timed exercises; a champion network; measuring
  adoption with the Copilot metrics API/dashboard, PR cycle time, surveys and qualitative signals;
  answering common objections; guardrails on what not to paste and review discipline; materials
  checklists and a retro template. Use this skill whenever someone asks to "roll out Copilot",
  "train the team", plan a workshop, lab, bootcamp, enablement session, lunch-and-learn, onboarding
  to AI tools, measure Copilot ROI or usage, write a survey, handle scepticism about AI coding
  tools, or prepare slides and exercises, even if they only say "people have licences but don't
  use them".
---

# Running a GitHub Copilot adoption programme

This skill produces an adoption plan that changes daily habits rather than ticking a training box.
Licences alone rarely move usage: developers try Copilot once on a hard problem, get a mediocre
answer, and go back to what they know. A programme works when it starts from the team's real
codebase, gives each role a task that Copilot visibly accelerates, keeps a feedback loop open for
weeks, and measures something the team cares about. Everything below is written for any team;
the .NET, Angular and GCP exercises are illustrations to swap for the team's own stack.

## Workflow

1. **Establish the baseline.** Pull current usage from the Copilot metrics dashboard or API
   (active users, acceptance rate, chat use), check which clients people use (VS Code, Visual
   Studio, Rider, CLI), and run the five-question survey in `assets/survey.template.md`. Without a
   baseline you cannot show change six weeks later.
2. **Set two or three goals with the lead**, phrased as behaviour and a number: "80% of
   developers use chat weekly", "every PR gets a Copilot review", "median time to first PR for a
   new joiner drops". Goals about "productivity" are unmeasurable and invite scepticism.
3. **Prepare the environment before the first session.** Repository instructions, two or three
   prompt files and the team skills installed (`copilot-custom-instructions`), a sample repo
   or a branch with ready exercises, licences verified, the organisation policy for public-code
   matching and content exclusion known. Nothing kills a workshop like twenty minutes of login
   problems.
4. **Design the programme** from the 4–6 week skeleton below, with labs per role. Pick exercises
   from the team's backlog where possible; a real ticket beats a toy.
5. **Run it** with a champion per sub-team, weekly office hours and a show-and-tell. Collect
   prompts that worked into prompt files during the programme, not after.
6. **Measure at week 3 and week 6** against the baseline, then run the retro using
   `assets/retro.template.md`.
7. **Verify** adoption is sticking: four weeks after the programme ends, usage has not dropped
   back, instructions files have had commits from people other than the coach, and at least one
   prompt file or skill was added by the team. If not, the next cycle targets the gap.

## Programme skeleton (4–6 weeks)

| Week | Activity | Output |
|---|---|---|
| 0 | Baseline survey, environment prep, champion selection | numbers, instructions in repo |
| 1 | Kick-off (90 min, everyone): mental model, modes, guardrails, live demo on own repo | cheat sheet distributed |
| 2 | Role labs (half-day each): .NET, Angular, GCP | each person completes one real task with Agent mode |
| 2–5 | Weekly office hour (45 min, drop-in) | questions answered, prompt files added |
| 3 | Show-and-tell #1 (30 min): three people demo one win and one failure | recorded, linked in wiki |
| 3 | Mid-point metrics check | adjust labs/focus |
| 4 | Advanced session (90 min): coding agent, custom agents, MCP (`copilot-agent-workflows`) | issue template, one custom agent |
| 5 | Show-and-tell #2, 1:1 coaching for low-usage volunteers (`copilot-prompting-coach`) | |
| 6 | Metrics review, retro, decide what becomes standard practice | retro notes, updated guardrails |

For a 4-week version merge weeks 4 and 5 and drop the second show-and-tell.

## Workshop agendas

**90-minute kick-off** (everyone; full timing in `references/workshop-agendas.md`):

1. 0–10 Why we are doing this, goals, what is measured, what is not (no individual surveillance).
2. 10–25 Mental model: context window, modes, where instructions live; demo on the team's repo.
3. 25–55 Exercise A (pairs): pick a small real ticket, do it in Agent mode with a written prompt
   (goal, context, constraints, acceptance). Share one surprise.
4. 55–70 Review discipline: failure-mode table, spot the bug in a prepared generated diff.
5. 70–85 Guardrails and licensing, Q&A, objections.
6. 85–90 Commitments: what each person will try this week; champions announced.

**Half-day role lab** (3.5 h, one role per lab): environment check → lab exercises 1–3 with
timed boxes and a debrief after each → "capture" slot to write a prompt file or an instruction
line from what worked → retro. Exercises per role:

- .NET: "add an endpoint with validation and integration tests using Agent mode"; "write a
  migration for a new column and have the `migration-reviewer` agent review it"; "explain and fix
  a failing test with `#terminalLastCommand`". Points at `dotnet-api-design`, `dotnet-testing`,
  `dotnet-ef-core-postgres`.
- Angular: "migrate a component to signal inputs and `@for`"; "generate a typed reactive form and
  its spec"; "use `#fetch` on the Angular docs to resolve a deprecated API". Points at
  `angular-modern-components`, `angular-data-and-forms`, `angular-testing`.
- GCP: "write Terraform for a bucket and review the plan"; "add a Cloud Run deploy workflow with
  Workload Identity Federation"; "explain an alerting policy and propose SLO thresholds". Points at
  `gcp-terraform-iac`, `gcp-cloud-run-deploy`, `gcp-observability`.

Every exercise has an acceptance check the participant runs (tests pass, `terraform plan` shows
only adds) so success is not a matter of opinion.

## Champion network

- One champion per sub-team or stack, volunteer, 2–3 hours per week during the programme and one
  hour after. They answer first-line questions, curate prompt files, and run the show-and-tell.
- Give champions something the others do not have yet: early access to new features, a channel
  with the coach, and the mandate to edit `.github/copilot-instructions.md`.
- Rotate after a quarter so the knowledge does not sit with one person.

## Measuring adoption

- **Copilot metrics API / usage dashboard** (organisation or enterprise): daily active users,
  engaged users per feature (completions, chat, agent, code review), acceptance counts and rates,
  languages and editors. Enable the metrics policy in organisation settings first; data is
  retained for a limited window, so export it weekly. Endpoint names and fields change; verify
  in current docs.
- **Delivery signals** from GitHub: PR cycle time (open → merge), review turnaround, PRs with a
  Copilot review, number of PRs opened by the coding agent and how many merged.
- **Survey** (week 0, 3, 6): confidence using Copilot (1–5), hours per week saved (self-estimate),
  top use case, top blocker, "would you recommend to a colleague" (0–10). Keep it anonymous and
  five questions long; see `assets/survey.template.md`.
- **Qualitative**: prompts shared in the channel, prompt files and instruction commits by
  non-coaches, show-and-tell attendance, stories of a task done differently.
- Report trends, never individual numbers. Acceptance rate alone is a vanity metric (it rises
  when people stop reading); pair it with review findings and cycle time.

## Common objections and answers

| Objection | Answer |
|---|---|
| "It writes bad code" | It writes code that matches the context it is given; show the same task with and without instructions and a proper prompt. |
| "It is faster to write it myself" | Often true for ten lines; the gains are in tests, boilerplate, unfamiliar APIs, migrations and reading code. Ask for one week of trying on those. |
| "It will leak our code" | Business/Enterprise plans do not train on your code; prompts are processed, not stored for training. Explain content exclusion and what *is* sent (`#fetch`, MCP). |
| "Licensing risk from public code" | The organisation's public-code filter setting plus code referencing; reviewers remain responsible, as for any copy-paste. |
| "Juniors will stop learning" | Pair juniors with a reviewer, require them to explain generated code in review, and use `/explain` as a learning tool. |
| "Management will measure us" | State the metrics policy up front: team trends, no individual ranking. |
| "It is not for infrastructure / our old codebase" | Run the GCP lab; show `#codebase` and `/explain` on legacy code where documentation is missing. |

## Guardrails

- Never paste secrets, customer data, production logs with PII or third-party proprietary code
  into chat; redact first. Configure content exclusions for known sensitive paths.
- Generated code is reviewed like any other code; the author must be able to explain every line.
  Request a Copilot review on PRs, then a human review.
- Licensing: know the organisation's public-code matching policy and treat flagged matches like
  any other third-party code.
- Agents and MCP: tool approvals stay on in Agent mode during the programme; only vetted MCP
  servers (`copilot-agent-workflows`).
- Write guardrails in one page, bilingual, and link it from `copilot-instructions.md`.

## Language

For Polish-speaking teams: slides and explanations in Polish, prompts, instruction files and code
in English. Models follow English instructions more precisely, the code vocabulary is English,
and English prompt files can be shared with other teams. Tell participants they can ask Copilot to
answer in Polish ("explain in Polish") without losing quality. Bilingual templates (`assets/`)
have Polish headings next to the English ones.

## Materials checklist

- Kick-off slides (Polish), cheat sheet (`copilot-prompting-coach/assets/copilot-cheat-sheet.md`)
- Exercise repository or branch per role with a `README` of tasks and acceptance checks
- `.github/copilot-instructions.md`, path instructions, 2–3 prompt files, team skills installed
- Guardrails one-pager and the organisation policy summary (public code, content exclusion)
- Survey form (week 0/3/6), retro template, metrics export script or dashboard link
- Channel for questions, calendar invites for office hours and show-and-tells

## Examples

**Example 1**
Input: "We have 12 developers with licences, usage is 30%. Plan something for next month."
Output: 4-week programme: baseline survey and metrics export this week; kick-off in week 1 with a
live demo on their repo; two half-day labs (.NET+GCP combined, Angular) in week 2; office hours
weekly; show-and-tell in week 3; metrics + retro in week 4; two champions named; success target
"weekly active chat users ≥ 9 of 12".

**Example 2**
Input: "Prepare a 90-minute session for the Angular team only."
Output: agenda from `references/workshop-agendas.md` with exercise A = "migrate `order-list` to
signals with `@for`, spec must pass", exercise B = "spot the bug in a generated form component",
materials list, and the `angular-modern-components` skill installed in the exercise branch.

## References

- `references/workshop-agendas.md` — read when you need the minute-by-minute agendas, the full
  exercise descriptions with acceptance checks, and facilitator notes.
- `references/measurement.md` — read when setting up metrics: API usage, dashboard fields, cycle
  time queries, and how to present trends without individual tracking.
- `assets/retro.template.md`, `assets/survey.template.md` — bilingual templates to copy.
