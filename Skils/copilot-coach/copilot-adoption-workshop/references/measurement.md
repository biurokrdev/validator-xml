# Measuring Copilot adoption

Read this when setting up the baseline and the week-3 / week-6 measurements. Report trends for
the team, never per-person tables; say so in the kick-off, because fear of measurement is one of
the main reasons people do not try the tool.

## Copilot metrics API and dashboard

- Enable the policy "Copilot metrics API access" (organisation settings → Copilot → Policies);
  without it the API returns nothing.
- Usage dashboard: organisation settings → Copilot → Usage (naming changes between releases;
  verify in current docs). Shows active and engaged users, feature usage (completions, chat,
  agent mode, code review, coding agent), acceptance, languages and editors.
- API: `GET /orgs/{org}/copilot/metrics` (also `/enterprises/{enterprise}/copilot/metrics` and
  per-team endpoints). Requires a token with `manage_billing:copilot`, `read:org` or
  `read:enterprise` scope depending on the endpoint. Returns daily rows for a limited window
  (28 days at the time of writing), so export weekly. Fields of interest:
  `total_active_users`, `total_engaged_users`, `copilot_ide_code_completions` (per editor,
  language: `total_code_acceptances`, `total_code_suggestions`, `total_code_lines_accepted`),
  `copilot_ide_chat` (`total_chats`, `total_chat_insertion_events`),
  `copilot_dotcom_chat`, `copilot_dotcom_pull_requests`. Field names evolve; verify in current
  docs before building a dashboard on them.
- Example export (GitHub CLI):

```powershell
gh api "/orgs/$Org/copilot/metrics" --paginate > "copilot-metrics-$(Get-Date -Format yyyy-MM-dd).json"
```

- Seat data: `GET /orgs/{org}/copilot/billing/seats` shows `last_activity_at` and
  `last_activity_editor` per seat. Use it only in aggregate (how many seats inactive > 30 days),
  never to confront individuals.

## Delivery signals

- PR cycle time (created → merged) and time to first review, per repository, monthly median.
  GitHub Insights, a GraphQL query over `pullRequests`, or the team's existing DORA tooling.
- Share of PRs with a Copilot review: search `reviewed-by:copilot-pull-request-reviewer[bot]`
  (verify the bot login in current docs).
- Coding agent: PRs by `copilot-swe-agent[bot]` / branch prefix `copilot/` (verify), merge rate,
  and how many review rounds they needed.
- New joiner time-to-first-merged-PR, if hiring happens during the window.

Treat these as context, not proof: cycle time moves for many reasons. Pair every number with a
story from the show-and-tell.

## Survey (week 0, 3, 6)

Five questions, anonymous, two minutes. Keep wording identical across rounds.

1. How confident are you using Copilot for daily work? (1–5)
2. In a typical week, how many hours does Copilot save you? (0 / <1 / 1–3 / 3–6 / >6)
3. What do you use it for most? (completions / chat questions / edits / agent mode / tests / docs / PR review / other)
4. What stops you from using it more? (free text)
5. Would you recommend it to a colleague on this team? (0–10)

Report: distribution per question, change since baseline, three quotes from Q4.

## Qualitative signals

- Prompts shared in the channel per week.
- Commits to `.github/copilot-instructions.md`, `.github/prompts/`, `.github/agents/`,
  `.github/skills/` by people other than the coach.
- Show-and-tell attendance and number of volunteers.
- Questions in office hours shifting from "how do I" to "how should we".

## Presenting results

- One page: three goals from week 0, the number then and now, one chart per goal, three quotes,
  and the decisions taken in the retro.
- Call out what did not work. A programme that reports only wins loses credibility with the
  sceptics it most needs.
- Set the next cycle's goals from the biggest gap, not from the biggest win.
