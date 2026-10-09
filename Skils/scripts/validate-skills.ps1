<#
.SYNOPSIS
  Sprawdza, czy każdy katalog skilla ma SKILL.md z poprawnym frontmatterem
  (name = nazwa katalogu, niepusty description) i czy ciało nie przekracza 500 linii.
#>
[CmdletBinding()]
param([string]$Root = (Split-Path -Parent $PSScriptRoot))

$errors = 0
$roles = Get-ChildItem -Path $Root -Directory |
  Where-Object { $_.Name -notin @('_template','scripts') -and -not $_.Name.StartsWith('.') }

foreach ($role in $roles) {
  foreach ($skill in Get-ChildItem -Path $role.FullName -Directory) {
    $file = Join-Path $skill.FullName 'SKILL.md'
    if (-not (Test-Path $file)) { Write-Host "BRAK SKILL.md: $($skill.FullName)"; $errors++; continue }
    $lines = @(Get-Content $file -Encoding UTF8)
    if ($lines[0] -ne '---') { Write-Host "Brak frontmattera: $file"; $errors++; continue }
    $end = [Array]::IndexOf($lines, '---', 1)
    if ($end -lt 1) { Write-Host "Niezamknięty frontmatter: $file"; $errors++; continue }
    $fm = $lines[1..($end-1)] -join "`n"
    if ($fm -notmatch "(?m)^name:\s*(\S+)") { Write-Host "Brak name: $file"; $errors++ }
    elseif ($Matches[1] -ne $skill.Name) { Write-Host "name '$($Matches[1])' != katalog '$($skill.Name)': $file"; $errors++ }
    if ($fm -notmatch "(?m)^description:\s*\S") { Write-Host "Brak description: $file"; $errors++ }
    $bodyLines = $lines.Count - $end - 1
    if ($bodyLines -gt 500) { Write-Host "Ciało ma $bodyLines linii (>500): $file"; $errors++ }
    Write-Host ("OK  {0,-40} {1,4} linii" -f "$($role.Name)/$($skill.Name)", $bodyLines)
  }
}
if ($errors -gt 0) { Write-Host "Błędy: $errors"; exit 1 } else { Write-Host "Wszystkie skille poprawne." }
