# Agent set-up examples

Read this when you need complete configuration files rather than the excerpts in SKILL.md.
Client details change between releases; where a path or key is marked "verify", check the
client's current documentation before relying on it.

## 1. MCP configuration per client

| Client | Where | Top-level key | Notes |
|---|---|---|---|
| VS Code (workspace) | `.vscode/mcp.json` | `servers`, `inputs` | shareable via repo; `inputs` prompt for secrets |
| VS Code (user) | `settings.json` → `mcp` or user `mcp.json` | `servers` | personal servers |
| Visual Studio | `.mcp.json` at solution root (verify) | `servers` | |
| JetBrains | Copilot plugin settings → MCP (verify) | `servers` | |
| Copilot CLI | `~/.copilot/mcp-config.json` (verify) | `mcpServers` | `/mcp` command manages it |
| Coding agent | repo Settings → Copilot → Coding agent → MCP configuration | `mcpServers` | secrets as `COPILOT_MCP_*` in the `copilot` environment; GitHub server built in |

Server types: `stdio` (local process, `command` + `args` + `env`), `http` (remote, `url`,
`headers`), `sse` (legacy streaming; prefer `http`).

### VS Code `.vscode/mcp.json` (full)

```jsonc
{
  "inputs": [
    { "id": "pg-url", "type": "promptString", "description": "Dev Postgres connection URL (read-only user)", "password": true },
    { "id": "jira-token", "type": "promptString", "description": "Jira API token", "password": true }
  ],
  "servers": {
    "github": {
      "type": "http",
      "url": "https://api.githubcopilot.com/mcp/"
    },
    "playwright": {
      "type": "stdio",
      "command": "npx",
      "args": ["@playwright/mcp@0.0.40"]
    },
    "filesystem": {
      "type": "stdio",
      "command": "npx",
      "args": ["-y", "@modelcontextprotocol/server-filesystem", "${workspaceFolder}/docs", "${workspaceFolder}/fixtures"]
    },
    "postgres": {
      "type": "stdio",
      "command": "npx",
      "args": ["-y", "@modelcontextprotocol/server-postgres", "${input:pg-url}"]
    },
    "jira": {
      "type": "http",
      "url": "https://mcp.example-jira-gateway.internal/",
      "headers": { "Authorization": "Bearer ${input:jira-token}" }
    }
  }
}
```

Pin versions in shared configs. Scope the filesystem server to the folders it needs. The GitHub
remote server authenticates with OAuth on first use; no token in the file.

### Coding agent MCP configuration (repository settings)

```json
{
  "mcpServers": {
    "postgres-schema": {
      "type": "local",
      "command": "npx",
      "args": ["-y", "@modelcontextprotocol/server-postgres", "$COPILOT_MCP_PG_URL"],
      "env": { "COPILOT_MCP_PG_URL": "COPILOT_MCP_PG_URL" },
      "tools": ["query"]
    }
  }
}
```

Keys (`type: local|http`, `tools` allow list, env mapping to `COPILOT_MCP_*` secrets) — verify in
current docs; the shape above reflects the documented format at the time of writing.

## 2. `copilot-setup-steps.yml` for .NET + Angular + Terraform

```yaml
name: Copilot Setup Steps

on:
  workflow_dispatch:
  push:
    paths: [".github/workflows/copilot-setup-steps.yml"]
  pull_request:
    paths: [".github/workflows/copilot-setup-steps.yml"]

jobs:
  copilot-setup-steps:            # mandatory job id; other jobs are ignored
    runs-on: ubuntu-latest        # larger runners allowed; verify
    timeout-minutes: 15
    permissions:
      contents: read              # minimum; add packages: read for private feeds
    steps:
      - uses: actions/checkout@v4

      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: "9.0.x"
      - run: dotnet restore MyApp.sln
      - run: dotnet tool restore            # dotnet-ef, formatters

      - uses: actions/setup-node@v4
        with:
          node-version: 22
          cache: npm
          cache-dependency-path: src/Web/package-lock.json
      - run: npm ci --prefix src/Web

      - uses: hashicorp/setup-terraform@v3
        with:
          terraform_version: "1.9.x"
      - run: terraform -chdir=infra/envs/dev init -backend=false

      # Services the tests need (Testcontainers needs Docker, available on ubuntu runners)
      - run: docker pull postgres:16-alpine
```

Running the workflow manually (`workflow_dispatch`) validates it. Keep it fast: the agent waits
for these steps on every session. Private feeds: add the host to the firewall allow list in the
coding agent settings and the credential as an environment secret.

## 3. Custom agents in full

### `test-writer.agent.md`

```markdown
---
name: test-writer
description: Writes unit and integration tests for a given class or component, verifying they fail without the implementation
tools: ['codebase', 'search', 'usages', 'editFiles', 'runTests']
---
You write tests only. Follow the `dotnet-testing` skill for .NET and `angular-testing` for
Angular; copy structure from the nearest existing spec.

Process:
1. Read the unit under test and list behaviours and edge cases; show the list and wait for OK.
2. Write tests one behaviour at a time; name them `Method_Scenario_Expectation`.
3. Run only the new tests. If all pass immediately, temporarily break the implementation
   (comment out one branch) and confirm at least one test fails; then restore it. Report which
   test caught it.
4. Stop after the first full run and summarise coverage gaps you did not address.

Do not modify production code except the temporary break in step 3, and restore it before
finishing. Do not mock the unit under test.
```

### `docs-updater.agent.md`

```markdown
---
name: docs-updater
description: Updates docs/ and API comments after a feature merges; never touches code logic
tools: ['codebase', 'search', 'changes', 'editFiles']
---
Update documentation for the changes in #changes (or the PR given). Edit only files under
`docs/**`, `README.md`, and doc comments (`///` in C#, JSDoc in TypeScript). Keep the existing
tone and headings; add a changelog entry under "Unreleased". List every file changed at the end.
```

### `terraform-planner.agent.md`

```markdown
---
name: terraform-planner
description: Writes and plans Terraform for GCP; never applies
tools: ['codebase', 'search', 'editFiles', 'runCommands']
---
Follow the `gcp-terraform-iac` skill. The only commands you run are `terraform fmt`,
`terraform validate` and `terraform plan` (with the env's var file). Never run `apply`,
`destroy` or `import`. After `plan`, summarise adds/changes/destroys and explain any destroy.
```

## 4. Issue-to-PR checklist for the coding agent

Before assigning:
- [ ] Issue follows the template (scope, pointers, acceptance commands, done definition)
- [ ] `copilot-setup-steps.yml` ran green in the last 30 days
- [ ] Instructions mention the exact test command
- [ ] No decision left open ("decide if we need pagination")

When the PR arrives:
- [ ] CI ran (approve the workflow run if required) and is green
- [ ] Diff touches only the files in scope; ask `@copilot` to explain any other
- [ ] Every test would fail if the feature were removed
- [ ] No new dependencies without justification in the PR description
- [ ] Screenshots or logs attached for UI / behavioural changes
- [ ] Human review by someone other than the task author

After merge:
- [ ] Anything you had to say twice is now in instructions, a skill or the template
