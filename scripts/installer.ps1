param(
  [string]$Repo = 'JKamsker/Codex-D',
  [string]$Channel = 'nightly',
  [string]$Version = '',
  [string]$InstallDir = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not $IsWindows) {
  throw 'This installer currently supports Windows only.'
}

$stateRoot = Join-Path $env:LOCALAPPDATA 'codex-d'
$appDir = Join-Path $stateRoot 'app'
$configDir = Join-Path $stateRoot 'config'
$installStatePath = Join-Path $configDir 'install-state.json'
$appExe = Join-Path $appDir 'codex-d.exe'

function Get-AllPathEntries {
  $processPath = $env:Path
  $userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
  $machinePath = [Environment]::GetEnvironmentVariable('Path', 'Machine')
  @($processPath, $userPath, $machinePath) `
  | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } `
  | ForEach-Object { $_ -split ';' } `
  | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } `
  | ForEach-Object { $_.Trim().TrimEnd('\') } `
  | Select-Object -Unique
}

function Test-DirWritable([string]$dir) {
  try {
    if (-not (Test-Path -LiteralPath $dir)) { return $false }
    $probe = Join-Path $dir (".codex-d-write-probe-" + [Guid]::NewGuid().ToString('n') + ".tmp")
    New-Item -ItemType File -Path $probe -Force | Out-Null
    Remove-Item -LiteralPath $probe -Force -ErrorAction SilentlyContinue | Out-Null
    return $true
  } catch {
    return $false
  }
}

function Find-BinDir {
  $pathEntries = Get-AllPathEntries

  $userHome = $env:USERPROFILE
  $generalCandidates = @(
    (Join-Path $userHome '.local\bin'),
    (Join-Path $userHome '.cargo\bin'),
    (Join-Path $userHome 'bin')
  )

  $pkgCandidates = @(
    (Join-Path $userHome '.dotnet\tools'),
    (Join-Path $env:APPDATA 'npm'),
    (Join-Path $userHome 'scoop\shims')
  )

  foreach ($dir in $generalCandidates) {
    $dirNorm = $dir.TrimEnd('\')
    if (($pathEntries -contains $dirNorm) -and (Test-DirWritable $dirNorm)) { return $dirNorm }
  }

  foreach ($dir in $pkgCandidates) {
    $dirNorm = $dir.TrimEnd('\')
    if (($pathEntries -contains $dirNorm) -and (Test-DirWritable $dirNorm)) { return $dirNorm }
  }

  return (Join-Path $env:LOCALAPPDATA 'codex-d\bin')
}

function Add-ToUserPath([string]$dir) {
  $dirNorm = $dir.TrimEnd('\')
  $currentUserPath = [Environment]::GetEnvironmentVariable('Path', 'User')
  if ($null -eq $currentUserPath) { $currentUserPath = '' }

  $pathParts = $currentUserPath -split ';' | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | ForEach-Object { $_.Trim().TrimEnd('\') }
  $alreadyOnPath = $pathParts | Where-Object { $_ -ieq $dirNorm } | Select-Object -First 1
  if (-not $alreadyOnPath) {
    $newUserPath = ($pathParts + $dirNorm) -join ';'
    [Environment]::SetEnvironmentVariable('Path', $newUserPath, 'User')
    Write-Host "Added to PATH (User): $dirNorm"
  } else {
    Write-Host "Already on PATH (User): $dirNorm"
  }
}

function Remove-PreviousWrappers {
  if (-not (Test-Path -LiteralPath $installStatePath)) { return }
  try {
    $state = Get-Content -LiteralPath $installStatePath -Raw | ConvertFrom-Json
    if ($null -eq $state.wrappers) { return }
    foreach ($w in @($state.wrappers.cmd, $state.wrappers.ps1, $state.wrappers.sh)) {
      if ([string]::IsNullOrWhiteSpace($w)) { continue }
      if (Test-Path -LiteralPath $w) {
        Remove-Item -LiteralPath $w -Force -ErrorAction SilentlyContinue | Out-Null
      }
    }
  } catch {
    # Best-effort cleanup only.
  }
}

if ([string]::IsNullOrWhiteSpace($InstallDir)) {
  $InstallDir = Find-BinDir
}

$tag =
  if (-not [string]::IsNullOrWhiteSpace($Version)) { $Version }
  elseif ($Channel -ieq 'nightly') { 'nightly' }
  else { 'nightly' }

$assetName = 'codex-d-win-x64.zip'
$downloadUrl = "https://github.com/$Repo/releases/download/$tag/$assetName"

Write-Host "Installing CodexD from $downloadUrl"
Write-Host "App dir: $appDir"
Write-Host "Wrapper dir: $InstallDir"

$tempRoot = Join-Path ([IO.Path]::GetTempPath()) ("codex-d-install-" + [Guid]::NewGuid().ToString('n'))
$zipPath = Join-Path $tempRoot $assetName
$extractDir = Join-Path $tempRoot 'extract'

New-Item -ItemType Directory -Path $extractDir -Force | Out-Null
New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
New-Item -ItemType Directory -Path $appDir -Force | Out-Null
New-Item -ItemType Directory -Path $configDir -Force | Out-Null

try {
  Remove-PreviousWrappers

  Invoke-WebRequest -Uri $downloadUrl -OutFile $zipPath -UseBasicParsing
  Expand-Archive -LiteralPath $zipPath -DestinationPath $extractDir -Force

  $exe = Join-Path $extractDir 'codex-d.exe'
  if (-not (Test-Path -LiteralPath $exe)) {
    $foundExe = Get-ChildItem -Path $extractDir -Recurse -Filter '*.exe' -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -ne $foundExe) {
      $exe = $foundExe.FullName
    }
  }

  if (-not (Test-Path -LiteralPath $exe)) {
    throw "Expected codex-d.exe in archive but it was not found. Downloaded: $downloadUrl"
  }

  Copy-Item -LiteralPath $exe -Destination $appExe -Force

  $cmdWrapper = Join-Path $InstallDir 'codex-d.cmd'
  $ps1Wrapper = Join-Path $InstallDir 'codex-d.ps1'
  $shWrapper = Join-Path $InstallDir 'codex-d.sh'

  Set-Content -LiteralPath $cmdWrapper -Encoding Ascii -NoNewline -Value "@echo off`r`n`"%LOCALAPPDATA%\\codex-d\\app\\codex-d.exe`" %*`r`n"
  Set-Content -LiteralPath $ps1Wrapper -Encoding utf8NoBOM -Value @'
$ErrorActionPreference = 'Stop'
& "$env:LOCALAPPDATA\codex-d\app\codex-d.exe" @args
'@

  Set-Content -LiteralPath $shWrapper -Encoding utf8NoBOM -Value @'
#!/usr/bin/env bash
set -euo pipefail

if command -v cygpath >/dev/null 2>&1; then
  exe="\$(cygpath "\${LOCALAPPDATA:-}" 2>/dev/null)/codex-d/app/codex-d.exe"
else
  exe="\${LOCALAPPDATA:-}/codex-d/app/codex-d.exe"
fi

exec "\$exe" "\$@"
'@

  $fallbackDir = (Join-Path $env:LOCALAPPDATA 'codex-d\bin')
  $selectedIsFallback = ($InstallDir.TrimEnd('\') -ieq $fallbackDir.TrimEnd('\'))
  if ($selectedIsFallback) {
    Add-ToUserPath $InstallDir
  } elseif (-not [string]::IsNullOrWhiteSpace($PSBoundParameters['InstallDir'])) {
    Add-ToUserPath $InstallDir
  }

  $installState = [ordered]@{
    repo = $Repo
    channel = $Channel
    version = $Version
    tag = $tag
    installedAtUtc = ([DateTime]::UtcNow.ToString('o'))
    appExe = $appExe
    wrapperDir = $InstallDir
    wrappers = [ordered]@{
      cmd = $cmdWrapper
      ps1 = $ps1Wrapper
      sh = $shWrapper
    }
  } | ConvertTo-Json -Depth 5
  Set-Content -LiteralPath $installStatePath -Encoding UTF8 -Value $installState

  Write-Host ''
  Write-Host 'Installed. Open a new terminal and run:'
  Write-Host '  codex-d --help'
  Write-Host ''
  Write-Host 'In this session you can run:'
  Write-Host ("  & '" + $appExe + "' --help")
} finally {
  Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue | Out-Null
}
