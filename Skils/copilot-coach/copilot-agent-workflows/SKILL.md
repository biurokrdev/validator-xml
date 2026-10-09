---
name: copilot-agent-workflows
description: >-
  Use GitHub Copilot's agentic features well: Agent mode in VS Code, Visual Studio and JetBrains
  (planning, tool approvals, terminal use, when to stop and review), the Copilot coding agent on
  GitHub.com (assigning issues, writing issues an agent can execute, reviewing agent PRs,
  `copilot-setup-steps.yml`), Copilot CLI, MCP servers (`.vscode/mcp.json`, GitHub, Playwright,
  filesystem, security of third-party servers), custom agents for recurring workflows, and a
  decision guide for prompt file vs instructions vs skill vs custom agent. Use this skill whenever
  someone mentions Agent mode, "assign to Copilot", the coding agent, agent PRs, `@copilot` in a
  PR, copilot-setup-steps, Copilot CLI, MCP, mcp.json, tool approvals, "let Copilot do the whole
  ticket", automating a recurring review or test-writing task, or asks "should this be a prompt,
  an instruction, a skill or an agent", even if they just say "can Copilot do this on its own".
---

# Copilot agent workflows: Agent mode, coding agent, CLI, MCP and custom agents

This skill produces agent set-ups that finish tasks instead of generating plausible half-work:
tasks scoped so an agent can verify its own result, environments where the build and tests
actually run, tool access that is wide enough to be useful and narrow enough to be safe, and
review habits that catch what the agent got wrong. Agents amplify whatever the repository already
makes explicit (commands, conventions, acceptance checks) and flounder on whatever lives only in
people's heads; most of the work below is making the implicit explicit.

## Workflow

1. **Choose the mechanism** with the decision guide below. A one-off task is a prompt; an
   always-true rule is an instruction; a procedure is a skill; a persona with limited tools is a
   custom agent; a whole ticket done asynchronously is the coding agent.
2. **Make the repository agent-ready** before delegating anything: `copilot-instructions.md`
   with exact build/test commands, `copilot-setup-steps.yml` for the coding agent, skills
   installed (`copilot-custom-instructions`). An agent without a working `dotnet test` cannot tell
   success from failure.
3. **Write the task as an executable brief**: goal, pointers to files, constraints, acceptance
   criteria the agent can run, definition of done. Use the issue template in
   `assets/coding-agent-issue.template.md` even for IDE tasks.
4. **Run with checkpoints.** In the IDE ask for a plan first on anything over a few files; keep
   tool approvals on for terminal commands that mutate state; stop and review when the agent
   starts touching files outside the stated scope.
5. **Review the result as a PR**, not as a chat answer: read the diff, run the tests yourself,
   check for the failure modes in `copilot-prompting-coach`, and ask the agent to explain any
   file you did not expect.
6. **Capture the lesson.** Whatever you had to tell the agent twice goes into instructions, a
   prompt file or a skill, so the next run does not need it.
7. **Verify** the set-up by delegating a small, well-specified ticket end to end (issue → agent
   PR → CI green → merge) and timing how many review rounds it took. More than two rounds means
   the brief or the environment needs work, not the model.

## Decision guide

| Need | Mechanism | Why |
|---|---|---|
| One-off or occasional task with a stable recipe ("add endpoint", "release notes") | prompt file `/name` | invoked on demand, costs no context otherwise |
| Rule that must always hold (commands, conventions, forbidden things) | `copilot-instructions.md` / `*.instructions.md` | loaded on every request |
| Procedural knowledge longer than a page (how we do migrations, deploys) | Agent Skill | loaded only when the description matches |
| Persona with a restricted toolset (reviewer, test writer, docs writer) | custom agent `*.agent.md` | tool limits prevent over-reach; selectable in IDE and coding agent |
| Whole ticket done asynchronously with a PR | coding agent on GitHub.com | runs in Actions, produces a reviewable PR |
| Multi-file change with build/test feedback while you watch | Agent mode in the IDE | interactive approvals |
| Scripting and repo chores from the terminal | Copilot CLI | shell-native, scriptable with `-p` |
| External data or actions (issues, browser, database) | MCP server | tools the agent can call |

Combine them: a custom agent can reference skills and be launched by a prompt file; the coding
agent reads instructions, skills and custom agents from the same `.github/` folder.

## Agent mode in the IDE

- Agent mode plans, edits, runs commands and reads the output in a loop until it believes the
  task is done. It is only as good as its feedback: make sure tests and linters run quickly and
  are mentioned in instructions.
- Ask for a plan first ("list files you will change and why; wait for my OK") when the task
  spans more than three files or touches infrastructure. VS Code's built-in *Plan* agent does the
  same, verify availability in the client.
- Tool approvals: keep confirmation on for terminal commands and for tools that change external
  state; auto-approve read-only tools (search, read file). Per-command allow lists exist in VS Code
  settings (`chat.tools.terminal.autoApprove` or similar; verify current name). Never enable
  "approve everything" on a machine with production credentials.
- Terminal use: the agent runs commands in your shell with your permissions. Watch for
  `rm`/`git push`/`terraform apply` and for commands that hang (watch modes, interactive prompts);
  tell it to use non-interactive flags (`--watch=false`, `--no-input`).
- When to stop: the agent edits files outside scope, loops on the same failing test more than
  twice, starts "fixing" tests to pass, or proposes installing packages you did not ask for. Stop,
  revert what is wrong, narrow the brief, restart.
- Use `#changes` to review the diff before accepting; keep or undo per file.

## Copilot coding agent on GitHub.com

**Assigning work**: assign an issue to `Copilot` (Assignees → Copilot), open a task from the
Agents page, delegate from VS Code chat, or from Copilot CLI (verify in current docs). The agent
runs in a GitHub Actions environment, pushes to a `copilot/*` branch and opens a draft PR that
requests your review. Each session costs premium requests and Actions minutes.

**Writing an issue an agent can execute** (`assets/coding-agent-issue.template.md`):

- Scope: one change, one PR; "add X" not "improve the module".
- Pointers: files to read and copy from, files not to touch.
- Acceptance criteria as commands and observable behaviour (`dotnet test` green; endpoint returns
  404 for unknown id).
- Definition of done: tests, docs, migration, changelog, screenshots for UI.
- Assumptions resolved up front; an agent cannot ask the product owner.
- Mention the custom agent to use if one fits (`migration-reviewer`, `test-writer`).

**Reviewing agent PRs**: review as you would a contractor's PR. Check scope creep, read every
test, run the app for UI changes, confirm the CI actually ran (workflows on agent PRs may need
"Approve and run" the first time; a repository setting controls this, verify in current docs).
Request changes by commenting with `@copilot` followed by the change; the agent pushes to the same
PR. Branch protection that requires a reviewer other than the author applies: the person who
started the task counts as an author.

**`copilot-setup-steps.yml`** pre-installs what the agent needs so it does not waste its first
minutes discovering missing SDKs (full example in `assets/copilot-setup-steps.yml`):

```yaml
# .github/workflows/copilot-setup-steps.yml
name: Copilot Setup Steps
on: [workflow_dispatch]
jobs:
  copilot-setup-steps:          # the job name is mandatory
    runs-on: ubuntu-latest
    permissions: { contents: read }
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with: { dotnet-version: '9.0.x' }
      - uses: actions/setup-node@v4
        with: { node-version: 22, cache: npm, cache-dependency-path: src/Web/package-lock.json }
      - run: dotnet restore && npm ci --prefix src/Web
```

Only the `copilot-setup-steps` job is used; keep it under a few minutes. The agent runs behind a
firewall with a default allow list; add hosts (private NuGet/npm feeds) in repository settings →
Copilot → Coding agent, rather than disabling it. Environment variables and secrets for the agent
go into the `copilot` Actions environment.

## Copilot CLI

- Install (`npm install -g @github/copilot` or the platform package; verify) and run `copilot` in
  the repository. It is an interactive agent that reads `copilot-instructions.md`, `AGENTS.md`,
  path instructions and skills (`~/.copilot/skills`, `.github/skills`).
- Useful commands: `/model`, `/mcp`, `/login`, `/help`; non-interactive `copilot -p "..."` for
  scripts; `--allow-tool` / `--deny-tool` to pre-approve or block tools, `--allow-all-tools` only
  in disposable environments. Command names change; check `copilot /help`.
- Good uses: repository chores (rename across files, bump versions, generate release notes),
  explaining a failing CI log piped in, running the same prompt over several repositories.
- MCP servers for the CLI are configured in `~/.copilot/mcp-config.json` (verify path), separate
  from VS Code's `.vscode/mcp.json`; keep the list the same to avoid surprises.
- The older `gh copilot` extension (suggest/explain) is superseded by the standalone CLI.

## MCP servers

Model Context Protocol servers give the agent tools: query GitHub issues, drive a browser, read a
database schema, call an internal API. In VS Code configure `.vscode/mcp.json` (shared via the
repo) or user settings; Visual Studio reads `.mcp.json` at the solution root; the coding agent
takes its configuration from repository settings → Copilot → Coding agent → MCP configuration,
with secrets named `COPILOT_MCP_*`; verify each in current docs.

```jsonc
// .vscode/mcp.json
{
  "inputs": [
    { "id": "pg-url", "type": "promptString", "description": "Dev Postgres URL", "password": true }
  ],
  "servers": {
    "github":     { "type": "http",  "url": "https://api.githubcopilot.com/mcp/" },
    "playwright": { "type": "stdio", "command": "npx", "args": ["@playwright/mcp@latest"] },
    "postgres":   { "type": "stdio", "command": "npx", "args": ["-y", "@modelcontextprotocol/server-postgres", "${input:pg-url}"] }
  }
}
```

Useful servers: GitHub (issues, PRs, code search; built in for the coding agent), Playwright
(browser testing and UI verification), filesystem (scoped read/write outside the workspace),
Postgres (schema and read-only queries), Terraform/GCP documentation servers where available.

Security: a server runs with your credentials and everything the model sends it leaves your
machine. Install only servers from publishers you would install a library from; pin versions
instead of `@latest` for shared configs; prefer `http` servers with OAuth over copying tokens into
JSON; give database servers read-only credentials; review the tool list the server exposes (a
server can describe tools that mislead the model, "tool poisoning"); keep tool approvals on for
anything that writes. Treat `.vscode/mcp.json` as code: PR review, no secrets, use `inputs`.

## Custom agents for recurring workflows

Define personas in `.github/agents/<name>.agent.md` (`copilot-custom-instructions` has the
frontmatter). Patterns that pay off:

- **migration-reviewer**: read-only tools; checks data loss, locking, naming; outputs a checklist.
- **test-writer**: edit + run-tests tools, no terminal beyond the test command; instructed to write
  tests that fail without the implementation and to stop after the first failing run for review.
- **docs-updater**: edits only `docs/**` and XML/JSDoc comments; invoked after a feature merges.
- **release-notes**: read-only on git history and PRs via the GitHub MCP server; outputs Markdown.

Each agent names the skill it should follow (`dotnet-testing`, `angular-testing`,
`gcp-terraform-iac`) and the file it should copy from. Keep bodies short; put procedure in skills.

## Combining skills, instructions and prompts

- Instructions state *what is true here* (commands, layout, conventions).
- Skills state *how we do a kind of task* (persistence, testing, deployment) and load on demand.
- Prompt files state *do this task now* and can name a custom agent via `mode`/`agent`.
- Custom agents state *who is doing it and with which tools*.

A full workflow for "add a feature": issue written with the template → coding agent (reads
instructions + `dotnet-api-design` skill) → PR → `migration-reviewer` and Copilot code review →
human review → `docs-updater` prompt. Each piece is small; the composition does the work.

## Examples

**Example 1**
Input: "Can Copilot implement this ticket: 'Customers should be able to export invoices as CSV'?"
Output: rewrite the ticket with the template (endpoint `GET /invoices/export?from&to`, CSV columns
listed, file to copy `OrdersExport.cs`, tests for empty range and 10k rows, do not touch the
Angular app), check `copilot-setup-steps.yml` restores .NET 9, assign to Copilot, review the PR
with the checklist, add "export endpoints stream with `IAsyncEnumerable`" to instructions after
the first review round.

**Example 2**
Input: "Agent mode keeps running `terraform apply`."
Output: add to `terraform.instructions.md` "never run `terraform apply`; run `plan` and stop",
turn on terminal confirmation for `terraform` in the auto-approve settings, and create a
`terraform-planner` custom agent without run-command tools except `terraform fmt/validate/plan`.

## References

- `references/agent-setup-examples.md` — read when configuring MCP for a specific client,
  writing a complete `copilot-setup-steps.yml` for .NET + Node + Terraform, or defining the
  `test-writer` and `docs-updater` agents in full.
- `assets/coding-agent-issue.template.md` — issue template to copy into `.github/ISSUE_TEMPLATE/`.
- `assets/mcp.json`, `assets/copilot-setup-steps.yml` — ready-to-adapt configuration files.
