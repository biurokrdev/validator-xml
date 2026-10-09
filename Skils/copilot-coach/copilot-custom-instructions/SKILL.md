---
name: copilot-custom-instructions
description: >-
  Author and maintain GitHub Copilot customisation files for a repository, organisation or developer:
  repository-wide `.github/copilot-instructions.md`, path-scoped `.github/instructions/*.instructions.md`
  with `applyTo` globs, reusable prompt files `.github/prompts/*.prompt.md`, custom agents
  `.github/agents/*.agent.md`, Agent Skills in `.github/skills/` or `~/.copilot/skills/`, `AGENTS.md`,
  organisation and personal instructions, plus precedence rules and how to verify instructions are
  actually used. Use this skill whenever someone mentions copilot-instructions, "Copilot ignores our
  conventions", "make Copilot follow our style", instruction files, prompt files, `/` prompts, chat modes,
  custom agents, agent.md, AGENTS.md, Copilot setup for a new repo, or asks to "configure Copilot for the
  team", even if they only say "Copilot keeps generating the wrong thing".
---

# GitHub Copilot custom instructions, prompts, agents and skills

This skill produces a small, maintainable set of Copilot customisation files that measurably change
what Copilot generates: correct build and test commands, the project's real architecture, and the
handful of conventions that matter. Most teams either have nothing (Copilot guesses the framework
version and folder layout) or a 400-line style guide that the model skims and mostly ignores. The
workflow below derives short, verifiable instructions from the repository itself, then confirms they
are being picked up before anyone trusts them.

## Workflow

1. **Inventory the repository before writing a word.** Read `README.md`, `CONTRIBUTING.md`,
   build scripts (`*.csproj`, `Directory.Build.props`, `package.json`, `angular.json`, `Makefile`,
   CI workflows), lint and formatter configuration (`.editorconfig`, `eslint.config.*`, `.prettierrc`,
   `stylecop`, `analyzers`), test projects, and three or four representative files per layer.
   Instructions that contradict what the repo actually does are worse than none: the model follows
   the code it sees, and the conflict wastes context.
2. **Pick the mechanism per need** using the table below. Always-on facts go into
   `copilot-instructions.md`; language- or folder-specific rules go into path-scoped instruction
   files; recurring tasks become prompt files; procedural know-how becomes a skill; a persona with a
   restricted toolset becomes a custom agent. Putting everything into one file makes it long and
   makes every rule cost context on every request.
3. **Write `.github/copilot-instructions.md` first**, 40–120 lines: what the project is, how to
   build, test and run it, the layout, and the five to ten conventions that an outside contributor
   gets wrong. Use `assets/copilot-instructions.template.md` as the skeleton.
4. **Add path-scoped instructions** only where a rule is specific to one technology or folder
   (`**/*.cs`, `src/app/**/*.ts`, `infra/**/*.tf`). Keep each file under 60 lines and give it one
   example per rule.
5. **Add prompt files, skills and agents for recurring work** (new endpoint, new component,
   migration review). Start with two or three; add more when developers ask for them.
6. **Verify the files are picked up.** Open chat, ask a question that the instructions should
   influence, and expand the *References* / *Used N references* list in the response: the instruction
   files that were loaded are listed there. For a stronger test add a temporary "canary" line
   (e.g. "Begin every answer with the word READY") and remove it once seen.
7. **Review and iterate like code.** Commit the files, open a PR, ask two developers to use Copilot
   for a day with them, and prune anything that did not change behaviour. Revisit after each major
   framework upgrade because version-specific guidance ages fast.

## Mechanisms at a glance

| Need | File | Scope | Loaded when |
|---|---|---|---|
| Always-on project facts | `.github/copilot-instructions.md` | repo | every chat request, code review, coding agent |
| Rule for a file type / folder | `.github/instructions/<name>.instructions.md` | files matching `applyTo` | request touches a matching file |
| Reusable task | `.github/prompts/<name>.prompt.md` | on demand | user types `/<name>` in chat |
| Persona + tool restrictions | `.github/agents/<name>.agent.md` | on demand | user picks the agent in the mode picker / coding agent |
| Procedural knowledge | `.github/skills/<name>/SKILL.md`, `~/.copilot/skills/<name>/SKILL.md` | on demand | model decides from `description` |
| Vendor-neutral agent notes | `AGENTS.md` (repo root, nested folders) | repo | agent mode, coding agent, CLI |
| Org-wide policy | organisation settings (Copilot → Custom instructions) | all repos | Copilot Chat on GitHub.com, code review; IDE support: verify in current docs |
| Personal preferences | VS Code user instruction files, GitHub.com personal instructions | one person | every request by that person |

Supported surfaces differ: VS Code, Visual Studio, JetBrains, Copilot CLI, Copilot Chat on
GitHub.com, Copilot code review and the coding agent all read `copilot-instructions.md`; prompt
files and custom agents started in VS Code and spread to the other clients over time. When a client
matters, verify in current docs rather than assuming. Inline code completions do not read these
files (verify in current docs); they follow the open file and neighbouring tabs.

## Repository-wide instructions (`.github/copilot-instructions.md`)

What belongs there, in this order:

1. One paragraph: what the system does, the main stack with versions (".NET 9, Angular 20, Postgres
   16 on Cloud SQL, deployed to Cloud Run").
2. Build, test, lint and run commands, exactly as CI runs them. The coding agent and agent mode
   execute these; a wrong command costs a whole iteration.
3. Repository layout: top-level folders and what lives in each (three to eight bullets).
4. Conventions that an outsider gets wrong: naming, error handling, how configuration is read, where
   tests go, what "done" means (tests pass, migration added, changelog updated).
5. Things to avoid, with the reason ("do not use the EF InMemory provider in tests; it accepts
   queries Npgsql cannot translate").
6. Pointers: "for persistence follow `.github/skills/dotnet-ef-core-postgres`", "for components see
   `.github/instructions/angular.instructions.md`".

What does not belong there: full style guides (the linter enforces them), licence headers,
commit-message etiquette, anything the model can read from `.editorconfig`, and instructions that
change how Copilot responds in general ("be concise", "always use British English") which GitHub
documents as ineffective and which pollute every request.

## Path-scoped instructions (`.github/instructions/*.instructions.md`)

```markdown
---
description: C# conventions for application and test projects
applyTo: "**/*.cs"
---
- Use `IEntityTypeConfiguration<T>` for EF mappings; data annotations are not used in this repo.
- Return `Results.Problem(...)` for errors in minimal APIs; see `src/Api/Endpoints/Orders.cs`.
- Tests use xUnit + FluentAssertions; name them `Method_Scenario_Expectation`.
```

- `applyTo` is a glob or a comma-separated list of globs (`"src/**/*.ts,src/**/*.html"`); `"**"`
  applies everywhere and behaves like a second repository-wide file.
- `description` is optional and shown in the UI; `excludeAgent` (`code-review` or `coding-agent`)
  keeps a file out of one surface, verify in current docs for the exact values.
- Several files can match one request; their contents are concatenated. Keep them non-overlapping
  so two files do not argue.
- VS Code also reads instruction files from folders configured in `chat.instructionsFilesLocations`
  and from the user profile. Relying on repo files keeps the behaviour the same for everyone.

## Prompt files (`.github/prompts/*.prompt.md`)

```markdown
---
description: Add a minimal-API endpoint with validation, handler and integration test
mode: agent
tools: ['codebase', 'editFiles', 'runCommands']
---
Add an endpoint for ${input:resource:Resource name, e.g. Invoice}.
Follow `.github/instructions/csharp.instructions.md`. Steps: ...
Acceptance: `dotnet test` passes; new tests cover 200, 400 and 404.
```

- Invoke with `/<file-name>` in chat (`/add-endpoint`), optionally followed by extra text.
- `mode` is `ask`, `edit`, `agent` or the name of a custom agent. Newer VS Code versions document
  the key as `agent` and accept `mode` for compatibility; verify in current docs.
- `tools` restricts the tools available; `model` pins a model; `${input:name:placeholder}`,
  `${selection}`, `${file}` and `${workspaceFolder}` are substituted. Tool identifiers differ per
  client, copy them from the tool picker rather than from memory.
- A prompt file is a *task*; it should read like a good ticket (see `copilot-prompting-coach`).

## Custom agents (`.github/agents/*.agent.md`)

```markdown
---
name: migration-reviewer
description: Reviews EF Core migrations for data loss, locking and naming before merge
tools: ['codebase', 'search', 'usages']
---
You review migrations only; do not edit files. For each migration check ...
```

- `name` and `description` are required; `tools` limits what the agent may do (a reviewer with no
  edit tools cannot "fix" things you did not ask for); `model` is optional.
- Agents appear in the VS Code mode picker (they replaced the older `*.chatmode.md` files) and can
  be selected when assigning work to the Copilot coding agent. Organisation-level agents live in the
  organisation's `.github` repository; verify the folder name in current docs.
- Design agents as personas with a narrow job; `copilot-agent-workflows` covers recurring-workflow
  agents in depth.

## Agent Skills (`.github/skills/<name>/SKILL.md`)

A skill is a folder with `SKILL.md` (frontmatter `name`, `description`, then instructions) plus
optional `references/`, `scripts/` and `assets/`. Copilot loads only the description at first and
reads the body when the description matches the task, so skills scale better than instructions for
long procedures. Repository skills go in `.github/skills/`, personal ones in `~/.copilot/skills/`
(VS Code also reads `.claude/skills/`, verify in current docs). The team skills repository
(`dotnet-*`, `angular-*`, `gcp-*`) is written in this format; install it with the repo's
`install-skills.ps1` script. Write the `description` as both *what* and *when*, with trigger
phrases, because it is the only thing that makes the skill fire.

## AGENTS.md, organisation and personal instructions

- `AGENTS.md` at the repository root (and, in VS Code, nested in subfolders) is read by agent mode,
  the coding agent and Copilot CLI, and by other agent tools. Use it when the team also uses
  Claude Code or Cursor; otherwise keep one source of truth in `copilot-instructions.md` and have
  `AGENTS.md` link to it, so the two never drift.
- Organisation instructions are set by an owner in organisation settings and apply to every
  repository. Keep them to policy (security, licensing, review expectations), not to code style.
- Personal instructions: VS Code user-profile instruction files (command *Chat: New Instructions
  File* → user data folder) and settings such as
  `github.copilot.chat.commitMessageGeneration.instructions`,
  `github.copilot.chat.reviewSelection.instructions` and
  `github.copilot.chat.pullRequestDescriptionGeneration.instructions`. GitHub.com has a separate
  *Personal custom instructions* page. Personal files are for taste ("explain in Polish"), never for
  project rules, because nobody else sees them.

## Precedence

All applicable instructions are combined into the request; nothing is silently dropped. When they
conflict, GitHub documents the priority as personal > repository > organisation on GitHub.com; in
the IDE, path-scoped files matching the current files are added alongside the repository file, and a
prompt file's body comes last and reads as the task. The practical rule: avoid conflicts rather than
rely on precedence, and keep one owner per topic.

## Why short, verifiable, example-based instructions win

- Every instruction consumes context on every request, competing with the code the model needs to
  read. Fifty lines that are always relevant beat four hundred that rarely are.
- The model copies patterns far more reliably than it obeys adjectives. "Use `Results.Problem`, see
  `Orders.cs`" is followed; "handle errors consistently" is not.
- A rule you can check (`dotnet format --verify-no-changes` passes, file lives in `tests/`) can be
  verified by the agent itself and by a reviewer; a vague rule cannot.
- Instructions age. A file short enough to re-read in two minutes gets maintained.

## Deriving instructions from an existing repository

1. Run the build and tests yourself and write down the exact commands that worked.
2. From the lint config, list only the rules the linter *cannot* enforce (architecture, naming of
   concepts, dependency direction).
3. Open the last ten merged PRs and note what reviewers asked to change; those are the conventions
   worth stating.
4. Pick one exemplary file per layer and reference it by path instead of describing it.
5. Ask Copilot to draft (`Chat: Generate Workspace Instructions File` in VS Code, or ask the coding
   agent), then cut everything you cannot verify against the repo.

## Examples

**Example 1**
Input: "Copilot keeps writing Angular components with NgModules and `*ngIf`; make it stop."
Output: `.github/instructions/angular.instructions.md` with `applyTo: "src/app/**/*.ts,src/app/**/*.html"`,
five lines: standalone components only, `@if/@for` control flow, `input()`/`output()` signals,
`inject()` over constructor injection, a pointer to one exemplary component. Verified by asking
"create a product card component" and checking the file appears under References.

**Example 2**
Input: "Set up Copilot for our new .NET + Angular monorepo."
Output: `copilot-instructions.md` (stack, `dotnet build`/`dotnet test`/`npm test` commands, layout,
eight conventions), `csharp.instructions.md`, `angular.instructions.md`, `terraform.instructions.md`,
`prompts/add-endpoint.prompt.md`, `prompts/add-component.prompt.md`, an `AGENTS.md` that links to
the instructions, and a PR description listing the canary test result for each client used.

## References

- `references/file-templates.md` — read when you need the complete template for any file type
  (instructions, prompt, agent, skill, AGENTS.md, VS Code settings) with every frontmatter key.
- `assets/copilot-instructions.template.md` — copy as the starting point for a repository file.
- `assets/example.instructions.md`, `assets/example.prompt.md`, `assets/example.agent.md` —
  ready-to-adapt path-scoped instruction, prompt and agent files.
