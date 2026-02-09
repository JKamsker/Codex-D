param(
  [string]$StateRoot = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not $IsWindows) {
  throw 'This uninstaller currently supports Windows only.'
}

if ([string]::IsNullOrWhiteSpace($StateRoot)) {
  $StateRoot = Join-Path $env:LOCALAPPDATA 'codex-d'
}

$configDir = Join-Path $StateRoot 'config'
$installStatePath = Join-Path $configDir 'install-state.json'
$appDir = Join-Path $StateRoot 'app'
$appExe = Join-Path $appDir 'codex-d.exe'

if (-not (Test-Path -LiteralPath $installStatePath)) {
  Write-Host "No install state found at: $installStatePath"
  Write-Host 'Nothing to uninstall.'
  exit 0
}

$state = Get-Content -LiteralPath $installStatePath -Raw | ConvertFrom-Json

foreach ($w in @($state.wrappers.cmd, $state.wrappers.ps1, $state.wrappers.sh)) {
  if ([string]::IsNullOrWhiteSpace($w)) { continue }
  if (Test-Path -LiteralPath $w) {
    Remove-Item -LiteralPath $w -Force -ErrorAction SilentlyContinue | Out-Null
    Write-Host "Removed wrapper: $w"
  }
}

if (Test-Path -LiteralPath $appExe) {
  Remove-Item -LiteralPath $appExe -Force -ErrorAction SilentlyContinue | Out-Null
  Write-Host "Removed app: $appExe"
}

Remove-Item -LiteralPath $installStatePath -Force -ErrorAction SilentlyContinue | Out-Null
Write-Host "Removed state: $installStatePath"

Write-Host 'Uninstall complete.'

