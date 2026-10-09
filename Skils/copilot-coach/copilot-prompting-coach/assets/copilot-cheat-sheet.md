# GitHub Copilot — one-page cheat sheet / ściąga

Prompts in English; explanations may be requested in Polish ("explain in Polish").

## Give context / Daj kontekst
- `#file:path` — anchor on a file (drag-and-drop works too)
- `#selection`, `#sym:Name`, `#usages` — point at code instead of describing it
- `#codebase` / `@workspace` — search the repo for where something is handled
- `#fetch:https://...` — pull official docs for the exact framework version
- `#githubRepo owner/name` — borrow a pattern from another repository
- `#terminalLastCommand`, `#problems`, `#changes` — feed errors and diffs directly

## Pick the mode / Wybierz tryb
- **Ask**: understand, design, compare options
- **Edit**: precise change in known files
- **Agent**: multi-file work that needs build/test feedback
- Big change? "Plan first, wait for my OK."

## Write the prompt / Napisz prompt
```
Goal: ...
Context: #file:... #file:...
Constraints: versions, libraries, files not to touch
Acceptance: tests/commands that must pass
Ask me before: adding packages, changing public APIs
```

## Iterate / Iteruj
- Commit-sized steps: scaffold → logic → tests → wiring
- Reply with the exact error, not "wrong, try again"
- New chat when the conversation drifts

## Review / Sprawdź
- Does the API exist? Compile; check docs.
- Is the idiom current (signals, minimal APIs) or legacy?
- Would this test fail if the code were wrong?
- Did it change more than asked?
- Any secrets, raw SQL, disabled security?

## Shortcuts / Skróty
- `/explain` `/fix` `/tests` `/doc` `/new` `/clear` and team prompts `/add-endpoint` ...
- Completions: write the comment or method name first; accept word by word with Ctrl+Right
- Request a review from `Copilot` on every PR before asking a human

## Do not / Nie
- Paste secrets, customer data or unredacted production logs
- Ship generated code you have not read
- Trust a stronger model to fix a missing-context problem
