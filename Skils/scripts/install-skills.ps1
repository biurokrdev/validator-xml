<#
.SYNOPSIS
  Kopiuje skille z tego repozytorium do miejsca, z którego czyta je GitHub Copilot lub Claude Code.

.PARAMETER Role
  dotnet | angular | gcp | copilot-coach | all

.PARAMETER Target
  repo         -> <RepoRoot>\.github\skills      (Copilot, skille repozytorium)
  copilot-user -> ~\.copilot\skills              (Copilot CLI, skille osobiste)
  claude-repo  -> <RepoRoot>\.claude\skills      (Claude Code, projekt)
  claude-user  -> ~\.claude\skills               (Claude Code, użytkownik)
  custom       -> ścieżka podana w -Path

.EXAMPLE
  .\install-skills.ps1 -Role dotnet -Target repo -RepoRoot C:\src\MojProjekt
#>
[CmdletBinding()]
param(
  [ValidateSet('dotnet','angular','gcp','copilot-coach','all')]
  [string]$Role = 'all',
  [ValidateSet('repo','copilot-user','claude-repo','claude-user','custom')]
  [string]$Target = 'repo',
  [string]$RepoRoot = (Get-Location).Path,
  [string]$Path,
  [switch]$Force
)

$ErrorActionPreference = 'Stop'
$skillsRoot = Split-Path -Parent $PSScriptRoot

$destination = switch ($Target) {
  'repo'         { Join-Path $RepoRoot '.github\skills' }
  'copilot-user' { Join-Path $HOME '.copilot\skills' }
  'claude-repo'  { Join-Path $RepoRoot '.claude\skills' }
  'claude-user'  { Join-Path $HOME '.claude\skills' }
  'custom'       { if (-not $Path) { throw 'Podaj -Path dla -Target custom' }; $Path }
}

$roles = if ($Role -eq 'all') { @('dotnet','angular','gcp','copilot-coach') } else { @($Role) }

New-Item -ItemType Directory -Force -Path $destination | Out-Null
$copied = 0
foreach ($r in $roles) {
  $roleDir = Join-Path $skillsRoot $r
  if (-not (Test-Path $roleDir)) { Write-Warning "Brak katalogu roli: $roleDir"; continue }
  Get-ChildItem -Path $roleDir -Directory | ForEach-Object {
    $skillFile = Join-Path $_.FullName 'SKILL.md'
    if (-not (Test-Path $skillFile)) { return }
    $dest = Join-Path $destination $_.Name
    if ((Test-Path $dest) -and -not $Force) {
      Write-Host "  pomijam (istnieje, użyj -Force): $($_.Name)"
      return
    }
    if (Test-Path $dest) { Remove-Item -Recurse -Force $dest }
    Copy-Item -Recurse -Path $_.FullName -Destination $dest
    Write-Host "  skopiowano: $($_.Name)"
    $script:copied++
  }
}
Write-Host "Gotowe: $copied skilli w $destination"
