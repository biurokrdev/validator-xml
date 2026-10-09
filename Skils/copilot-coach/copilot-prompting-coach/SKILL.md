---
name: copilot-prompting-coach
description: >-
  Coach developers to get reliable results from GitHub Copilot in VS Code, Visual Studio, JetBrains
  and Copilot CLI: how the context window is filled (open files, selection, #file, #codebase,
  @workspace, #fetch, #githubRepo), choosing Ask vs Edit vs Agent mode, chat participants and slash
  commands, writing task prompts with goals, constraints and acceptance criteria, iterating instead
  of one-shotting, reviewing AI output for hallucinated APIs and bad tests, inline completion and
  next-edit tips, Copilot code review, model selection, security and licensing. Use this skill
  whenever someone says Copilot "gives bad results", "ignores my code", "hallucinates", asks "how
  should I prompt", wants a prompt rewritten or reviewed, prepares Copilot tips or a cheat sheet,
  runs a 1:1 coaching session, or asks which Copilot mode or model to use, even if they only paste
  a prompt and ask "why doesn't this work".
---

# Coaching developers to prompt GitHub Copilot well

This skill turns "Copilot is hit and miss" into a repeatable practice: give the model the right
context, pick the mode that matches the task, state the goal and the acceptance criteria, iterate
in small steps, and review the output as you would a junior colleague's PR. Most disappointing
results trace back to one of five levers (context, mode, prompt, iteration, review); the coaching
format below finds which lever is wrong and changes one thing at a time so the developer can feel
the difference.

## Workflow

1. **Observe before advising.** Watch the developer do a real task with Copilot for ten minutes
   (or read their chat history). Note what context was visible, which mode they used, how the
   prompt was phrased, and what they did with the output. Advice given without observation is
   generic and gets ignored.
2. **Diagnose the lever.** Match the symptom to a lever using the table in *Diagnosing a bad
   result*. Wrong file edited → context; chat answers but nothing changes → mode; output is
   plausible but off-target → prompt; one huge diff that is hard to review → iteration; bug shipped
   → review.
3. **Suggest one change** and let the developer redo the same task with it. One change makes the
   effect attributable; three changes at once teach nothing.
4. **Show, don't tell.** Rewrite their prompt in front of them using the task-prompt structure
   below and compare both outputs side by side.
5. **Capture what worked** into a shared place: a prompt file, an instruction line, or a line in
   the team cheat sheet (see `copilot-custom-instructions`). Individual tips evaporate; files
   persist.
6. **Verify** by asking the developer to repeat the technique unprompted on a different task a few
   days later. Adoption is confirmed when the behaviour survives without the coach present.

## Mental model: what the model can see

Copilot answers from the context window: your prompt, the custom instructions, and whatever
context the client attaches. Nothing else exists for it, including the rest of the repository
unless something puts it there.

| Source | How it gets in | Notes |
|---|---|---|
| Current file and selection | automatic | the selection is the strongest signal for `/explain`, `/fix`, inline edits |
| Open tabs / neighbouring files | automatic for completions | close unrelated tabs when completions drift |
| `#file:path`, drag-and-drop | explicit | the most reliable way to anchor a task on a file |
| `#selection`, `#sym:Name`, `#usages` | explicit | point at a symbol instead of describing it |
| `#codebase` / `@workspace` | semantic search over the repo | good for "where is X handled"; it returns fragments, not the whole repo |
| `#fetch:url` | web page content | official docs for the exact framework version |
| `#githubRepo owner/name` | another repository | reuse patterns from a sibling service |
| `#changes`, `#terminalLastCommand`, `#problems` | git diff, last command, diagnostics | feed errors instead of retyping them |
| Custom instructions, skills, `AGENTS.md` | automatic | see `copilot-custom-instructions` |

Rules of thumb: attach the two or three files that define the pattern to copy; name the framework
version when it matters ("Angular 20 with signals", ".NET 9 minimal APIs"); when the model invents
an API, `#fetch` the documentation page and ask again. The identifiers above are VS Code's; Visual
Studio and JetBrains use similar `#` and `@` references with slightly different names, verify in
the client's picker.

## Choosing a mode

| Mode | Use when | Avoid when |
|---|---|---|
| Ask | understanding code, designing, comparing options, generating a snippet to paste | you want files changed (you will paste by hand and lose context) |
| Edit | a change you can describe precisely across a known set of files | the change needs exploration or running commands |
| Agent | multi-file work with build/test feedback, "add an endpoint with tests", refactors with verification | the task is vague; the agent will explore and guess |
| Plan (where available) / "plan first" prompt | large or risky change; you want a reviewed plan before edits | a two-line fix |
| Custom agent | a recurring persona (reviewer, test writer) with limited tools | one-off tasks |

A good habit: Ask to clarify, Agent to execute, Ask again (or a custom reviewer agent) to review.

## Participants and slash commands

- `@workspace` (search the repo), `@terminal` (shell help), `@vscode` (editor settings),
  `@github` (issues, PRs, web search in GitHub.com chat). Participants matter mostly in Ask mode;
  in Agent mode the agent chooses tools itself.
- `/explain` on a selection, `/fix` on a diagnostic or selection, `/tests` to generate tests for
  the selection, `/doc` for doc comments, `/new` to scaffold, `/clear` to reset context. Custom
  prompt files appear as `/<name>` too.
- Slash commands are shortcuts for a well-known prompt; the selection still decides the quality.
  `/tests` on an entire file produces shallow tests; on one method with a comment stating the edge
  cases it produces useful ones.

## Writing a task prompt

Structure a non-trivial prompt like a ticket you would hand to a contractor who has never seen the
repo:

```
Goal: <one sentence, the user-visible outcome>
Context: #file:... #file:... (the pattern to copy, the type to change)
Constraints: framework version, libraries allowed/forbidden, files not to touch, style
Acceptance criteria: tests to pass, commands to run, behaviour to preserve
Examples: an input/output pair or a reference implementation
Ask me before: adding packages, changing public APIs, deleting files
Output: what to show at the end (summary of changes, open questions)
```

- Keep prompts in English even in a Polish-speaking team; models follow English instructions
  more precisely and the code vocabulary is English anyway. Explanations can be requested in
  Polish ("explain in Polish").
- State the negative space: "do not modify migrations" prevents the most common over-reach.
- "Plan first, then wait for my OK" costs one round-trip and saves many on large tasks.

## Iterate, do not one-shot

- Break a feature into steps the size of a commit: scaffold → logic → tests → wiring. Review each.
- Reply to a wrong answer with the specific error ("the test fails with `NullReferenceException`
  at line 42: #terminalLastCommand"), not "that's wrong, try again".
- When the conversation drifts, start a new chat with a summary; long chats accumulate stale
  context and the model starts to contradict itself.
- Keep the diff reviewable: if Agent mode touched twelve files, ask it to explain why each was
  needed before accepting.

## Reviewing AI output critically

| Failure mode | How it looks | Check |
|---|---|---|
| Hallucinated API | method or package that does not exist; config key nobody uses | compile, search the docs with `#fetch`, check the package on NuGet/npm |
| Outdated framework idiom | `NgModule`, `*ngIf`, `Startup.cs`, `WebClient` | compare with the repo's exemplary file; add the idiom to instructions |
| Tests that assert the bug | test written from the implementation, passes trivially, mocks everything | read the assertion; ask "what would make this test fail?" |
| Over-broad change | reformatting, renamed unrelated symbols, "improved" code you did not ask for | diff size; ask for a minimal change |
| Silent assumption | picked a default (time zone, encoding, pagination size) without saying | ask the model to list assumptions |
| Security slip | string-built SQL, disabled TLS validation, secrets in config | security review step; `dotnet-code-review` / `angular-code-review` skills |

Treat generated code as a PR from a fast, well-read junior: it compiles often, it is confident
always, and it has not seen production.

## Inline completions and next edit suggestions

- Write the intent first: a one-line comment or a precise method name (`ParseIsoWeek`,
  `mapInvoiceToDto`) makes the completion land; `DoStuff` gets garbage.
- Keep the related type open in a tab; completions are strongly influenced by neighbouring files.
- Accept word by word (`Ctrl+Right` in VS Code) when only the start is right.
- Next edit suggestions (`github.copilot.nextEditSuggestions.enabled`) propose the *following*
  edit after you change something (rename propagation, matching updates). Use Tab to jump; turn
  it off while pairing if it distracts.
- Turn completions off for files that contain secrets or generated code (content exclusion at
  org/repo level, or per-language settings).

## Copilot code review on pull requests

- Request a review from `Copilot` on the PR (or configure automatic review via a ruleset). It
  reads `copilot-instructions.md` and path-scoped instructions, so review quality follows
  instruction quality.
- Use it as the first pass before human review, not a replacement: it is good at local issues
  (null handling, missing await, obvious duplication) and weak on design and domain rules.
- Reply to a review comment with `@copilot` to ask for a fix in the same PR when the coding agent
  is enabled.

## Model selection

- Default to the fast included model for completions and simple edits; switch to a stronger
  (often slower, premium-request) model for Agent mode tasks spanning several files, debugging
  with long logs, architecture questions and anything you will not review line by line.
- The model list and request multipliers change often; verify in the client's picker and the
  organisation's policy. Tell the team which models count as premium so nobody is surprised by
  usage limits.
- If results are poor, improve context and prompt before changing model; a stronger model with no
  context still guesses.

## Security and licensing

- Do not paste secrets, customer data or production logs with PII into chat; redact first.
  Content exclusion rules at organisation or repository level keep sensitive paths out of context.
- Know the organisation's *suggestions matching public code* setting (block or allow with code
  referencing) and what it means for licence review.
- Copilot (Business/Enterprise) does not train on your prompts or code; still, follow the
  company's data policy for what may leave the machine (`#fetch` sends URLs, MCP servers send
  whatever they receive, see `copilot-agent-workflows`).
- Generated code is the committer's responsibility; the review discipline above is the control.

## Prompt makeovers

Weak → strong, one per stack (more in `references/prompt-makeovers.md`):

- .NET, weak: "add caching to this service". Strong: "In #file:ProductService.cs add read-through
  caching for `GetByIdAsync` using `IMemoryCache` (already registered in Program.cs), 5-minute
  absolute expiry, key `product:{id}`, invalidate in `UpdateAsync`. Add tests in
  #file:ProductServiceTests.cs for hit, miss and invalidation. Do not change the public interface."
- Angular, weak: "make this component use signals". Strong: "Convert #file:order-list.component.ts
  from `@Input()`/`BehaviorSubject` to `input()`, `signal()` and `computed()`, keep the template
  behaviour identical, replace `*ngFor` with `@for` with `track order.id`. Existing spec must still
  pass; run `npm test -- --watch=false --testPathPattern=order-list`."
- GCP, weak: "write terraform for a bucket". Strong: "Add `infra/modules/exports-bucket/main.tf`
  for a GCS bucket in `europe-central2`, uniform bucket-level access, versioning on, lifecycle rule
  deleting noncurrent versions after 30 days, CMEK from `var.kms_key`, following
  #file:infra/modules/uploads-bucket/main.tf. Then run `terraform plan` and show me the plan; do
  not apply."

## 1:1 coaching session format (30 minutes)

1. **Observe (10 min)**: the developer works on a real task; the coach takes notes and does not
   interrupt.
2. **Ask (10 min)**: "What did you expect here?", "What did Copilot know at that moment?", "How
   did you decide to trust that output?". Let them find the lever themselves.
3. **Suggest one change (5 min)**: the single highest-leverage change, demonstrated on the same
   task.
4. **Agree a follow-up (5 min)**: what they will try this week, and one thing to share at the
   next show-and-tell. Record it in the template in `assets/feedback-session.template.md`.

## Examples

**Example 1**
Input: "Copilot wrote a repository class but used `DbContext.Database.ExecuteSqlRaw` with string
concatenation."
Output: lever = review + instructions. Explain the SQL-injection risk, ask Copilot to rewrite
with LINQ or parameterised `FromSqlInterpolated`, and add one line to
`csharp.instructions.md`: "Never build SQL by concatenation; use LINQ or `FromSqlInterpolated`."

**Example 2**
Input: "Agent mode changed 30 files when I asked it to rename a DTO property."
Output: lever = iteration + prompt. Revert, use Edit mode with `#usages` on the property, and the
constraint "rename only; do not reformat or touch unrelated code". Show the diff size difference.

## References

- `references/prompt-makeovers.md` — read when preparing training material or when a developer
  wants more before/after examples for .NET, Angular, GCP/Terraform, tests and debugging.
- `assets/feedback-session.template.md` — copy for each 1:1 coaching session.
- `assets/copilot-cheat-sheet.md` — one-page summary to hand to the team (bilingual headings).
