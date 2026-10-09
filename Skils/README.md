# Skills — repozytorium skilli dla zespołu

Zbiór **Agent Skills** (format `SKILL.md`) dla czterech ról:

| Rola | Katalog | Skille |
|---|---|---|
| .NET developer | [dotnet/](dotnet/) | `dotnet-project-structure`, `dotnet-api-design`, `dotnet-ef-core-postgres`, `dotnet-testing`, `dotnet-code-review` |
| Angular developer | [angular/](angular/) | `angular-modern-components`, `angular-data-and-forms`, `angular-testing`, `angular-code-review` |
| GCP engineer | [gcp/](gcp/) | `gcp-cloud-run-deploy`, `gcp-terraform-iac`, `gcp-data-services`, `gcp-observability` |
| AI coach (GitHub Copilot) | [copilot-coach/](copilot-coach/) | `copilot-custom-instructions`, `copilot-prompting-coach`, `copilot-adoption-workshop`, `copilot-agent-workflows` |

Skille są napisane po angielsku (modele działają na nich najlepiej), dokumentacja repozytorium po polsku.
Każdy skill jest samowystarczalny: można skopiować pojedynczy katalog bez reszty repozytorium.

## Format

Każdy skill to katalog z plikiem `SKILL.md`:

```
nazwa-skilla/
├── SKILL.md          # wymagane: frontmatter (name, description) + instrukcje
├── references/       # opcjonalne: dłuższe materiały ładowane na żądanie
├── scripts/          # opcjonalne: skrypty wykonywane deterministycznie
└── assets/           # opcjonalne: szablony, pliki wyjściowe
```

Frontmatter:

```yaml
---
name: nazwa-skilla          # lowercase, myślniki, zgodne z nazwą katalogu
description: >-             # CO robi i KIEDY go użyć (to jedyny mechanizm wyzwalania)
  ...
---
```

Zasady pisania są w [_template/SKILL.md](_template/SKILL.md). W skrócie:

- `description` zawiera zarówno co skill robi, jak i konkretne frazy / sytuacje, w których ma się uruchomić. Modele mają tendencję do nieużywania skilli, więc opis powinien być "nachalny".
- Ciało `SKILL.md` poniżej ~500 linii. Dłuższe treści idą do `references/` z jasnym wskazaniem, kiedy je czytać.
- Tryb rozkazujący, wyjaśnianie *dlaczego* zamiast wielkich liter MUST/NEVER.
- Skill ma być ogólny: działa dla wielu projektów, nie tylko tego repozytorium.

## Instalacja

### GitHub Copilot (VS Code, Visual Studio, JetBrains, Copilot CLI)

Copilot ładuje skille z katalogu `.github/skills/<nazwa>/SKILL.md` w repozytorium
oraz z `~/.copilot/skills/` (skille osobiste). Skopiuj wybrane katalogi:

```powershell
# wszystkie skille dla roli .NET do bieżącego repozytorium
.\scripts\install-skills.ps1 -Role dotnet -Target repo

# skille osobiste (wszystkie role) dla Copilot CLI
.\scripts\install-skills.ps1 -Role all -Target copilot-user
```

Copilot sam zdecyduje, kiedy użyć skilla na podstawie `description`. W czacie można też
wymusić skill, pisząc np. `użyj skilla dotnet-testing`.

Dodatkowo skill `copilot-custom-instructions` opisuje pozostałe mechanizmy Copilota:
`.github/copilot-instructions.md`, `*.instructions.md` z `applyTo`, `*.prompt.md`, `*.agent.md`.

### Claude Code

Claude Code ładuje skille z `.claude/skills/<nazwa>/SKILL.md` (projekt) i `~/.claude/skills/` (użytkownik).

```powershell
.\scripts\install-skills.ps1 -Role angular -Target claude-repo
.\scripts\install-skills.ps1 -Role all -Target claude-user
```

Wywołanie ręczne: `/nazwa-skilla` w czacie.

### Ręcznie

Skopiuj katalog skilla do odpowiedniego miejsca. Nazwa katalogu musi być równa polu `name`.

## Jak dodać nowy skill

1. Skopiuj `_template/` do `<rola>/<nazwa-skilla>/` i uzupełnij `SKILL.md`.
2. Sprawdź `description`: czy wymienia frazy, po których developer rozpozna, że to ten skill?
3. Przetestuj na 2–3 realnych promptach (z skillem i bez) i porównaj wyniki.
4. Dodaj wpis w tabeli powyżej.

Walidacja struktury całego repozytorium:

```powershell
.\scripts\validate-skills.ps1
```
