# Copilot customisation file templates

Read this when you need the full shape of a file, including optional frontmatter keys. Where a
key or a tool identifier changes between client versions it is marked "verify in current docs";
copy identifiers from the client's own UI (tool picker, command palette) when in doubt.

## 1. `.github/copilot-instructions.md` (repository-wide)

No frontmatter. Plain Markdown, 40–120 lines. Section order that works:

```markdown
# <Project name>

<One paragraph: purpose, stack with versions, hosting.>

## Build, test, run
- Build: `dotnet build MyApp.sln`
- Test: `dotnet test MyApp.sln` (integration tests need Docker for Testcontainers)
- Frontend: `cd src/Web && npm ci && npm test -- --watch=false`
- Lint: `dotnet format --verify-no-changes`, `npm run lint`

## Layout
- `src/Api` — ASP.NET Core minimal API, one file per resource in `Endpoints/`
- `src/Domain` — entities and domain services, no framework references
- `src/Infrastructure` — EF Core, external clients
- `src/Web` — Angular application
- `infra/` — Terraform for GCP
- `tests/` — mirrors `src/` (`Api.Tests`, `Domain.Tests`, ...)

## Conventions
- <rule with reason and a file path to copy from>
- ...

## Definition of done
- Tests added or updated and passing; migration added when the model changes; no new warnings.

## Avoid
- <thing> because <reason>

## See also
- `.github/instructions/*.instructions.md` for per-language rules
- `.github/skills/` for procedures (persistence, testing, deployment)
```

## 2. `.github/instructions/<name>.instructions.md` (path-scoped)

```markdown
---
description: Short label shown in the UI (optional)
applyTo: "**/*.cs,**/*.csproj"
# excludeAgent: code-review   # keep this file out of one surface; verify values in current docs
---
<Markdown body: 5–25 rules, each with an example or a file path.>
```

Glob notes: patterns are relative to the repository root; `**` crosses directories; a single
`"**"` applies to every request. Several matching files are concatenated.

## 3. `.github/prompts/<name>.prompt.md` (reusable task)

```markdown
---
name: add-endpoint              # optional; defaults to the file name
description: Add a minimal-API endpoint with validation, handler and integration tests
mode: agent                     # ask | edit | agent | <custom agent name>; newer VS Code: `agent:` (verify)
model: GPT-5                    # optional; pins a model if available
tools: ['codebase', 'editFiles', 'runCommands', 'runTests']   # optional; identifiers: verify in tool picker
argument-hint: "Resource name and HTTP verbs"                  # optional; shown after /name
---
Add a REST endpoint for ${input:resource:Resource name}.

Context: read `src/Api/Endpoints/Orders.cs` and copy its structure.
Steps:
1. Create `src/Api/Endpoints/${input:resource}.cs` with GET list, GET by id, POST.
2. Validate requests with FluentValidation as in `OrdersValidator`.
3. Add integration tests in `tests/Api.Tests/${input:resource}Tests.cs` covering 200, 400, 404.
4. Run `dotnet test`; stop and report if anything fails.

Do not touch migrations or the Angular app. Ask before adding a NuGet package.
```

Variables: `${selection}`, `${selectedText}`, `${file}`, `${fileBasename}`, `${workspaceFolder}`,
`${input:name}`, `${input:name:placeholder}`. Invoke with `/add-endpoint` (optionally followed by
free text, which is appended).

## 4. `.github/agents/<name>.agent.md` (custom agent)

```markdown
---
name: migration-reviewer
description: Reviews EF Core migrations for data loss, locking and naming; never edits files
tools: ['codebase', 'search', 'usages', 'problems']   # omit edit/run tools for a read-only reviewer
model: Claude Sonnet 4.5                             # optional
# target: vscode | github-copilot                    # optional, limits where the agent is offered; verify
---
You are a reviewer of Entity Framework Core migrations on PostgreSQL.

For every file under `**/Migrations/*.cs` changed in the current branch:
1. Flag `DropColumn`/`DropTable` and renames expressed as drop + add (data loss).
2. Flag `CREATE INDEX` without `CONCURRENTLY` on tables listed in `docs/large-tables.md`.
3. Check the migration name describes the change.
Report as a checklist with file:line references. Do not propose code changes unless asked.
```

Older VS Code versions used `.github/chatmodes/<name>.chatmode.md` with the same idea; migrate
them to `.agent.md`.

## 5. `.github/skills/<name>/SKILL.md` (Agent Skill)

```markdown
---
name: <name>                 # must equal the folder name; lowercase, hyphens
description: >-
  What the skill does and when to use it, with concrete trigger phrases.
---
# Title
<instructions: workflow, rules of thumb, examples; keep under ~500 lines, longer material in references/>
```

Locations: `.github/skills/` (repository), `~/.copilot/skills/` (personal, Copilot CLI and IDE).
VS Code also scans `.claude/skills/` and `~/.claude/skills/` (verify in current docs).

## 6. `AGENTS.md`

```markdown
# Agent notes
Read `.github/copilot-instructions.md` first; it is the source of truth.
Build/test commands and layout are described there. Additional notes for autonomous agents:
- Run `dotnet test` before declaring a task done.
- Do not modify files under `infra/` without being asked.
```

Nested `AGENTS.md` files in subfolders apply to work in that subtree (VS Code setting
`chat.useNestedAgentsMdFiles`; verify). Keep it a pointer so it never contradicts the main file.

## 7. VS Code personal settings (`settings.json`)

```jsonc
{
  "github.copilot.chat.codeGeneration.useInstructionFiles": true,
  "chat.instructionsFilesLocations": { ".github/instructions": true, "docs/copilot": true },
  "chat.promptFilesLocations": { ".github/prompts": true },
  "github.copilot.chat.commitMessageGeneration.instructions": [
    { "text": "Use Conventional Commits; subject max 72 chars; body explains why." }
  ],
  "github.copilot.chat.reviewSelection.instructions": [
    { "file": ".github/instructions/review.instructions.md" }
  ],
  "github.copilot.chat.pullRequestDescriptionGeneration.instructions": [
    { "text": "Sections: Summary, Changes, Testing, Risks. Link the issue." }
  ]
}
```

User-level instruction and prompt files: *Chat: New Instructions File* / *Chat: New Prompt File*
→ choose "User Data Folder". They apply in every workspace for that person only.

## 8. Verifying pickup, per client

| Client | Where to look |
|---|---|
| VS Code | Chat response → "Used N references" → expand; instruction files are listed |
| Visual Studio | Chat response → References link |
| JetBrains | Response references panel (verify in current docs) |
| GitHub.com Chat / coding agent | Response references; coding agent session log shows loaded instructions |
| Copilot code review | Review comments reflect the rules; use the canary technique |

Canary technique: add a harmless, unmistakable line ("Start every answer with READY.") to the
file under test, ask any question, confirm, remove the line.
