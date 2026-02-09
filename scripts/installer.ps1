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

if ([string]::IsNullOrWhiteSpace($InstallDir)) {
  $InstallDir = Join-Path $env:LOCALAPPDATA 'codex-d\bin'
}

$tag =
  if (-not [string]::IsNullOrWhiteSpace($Version)) { $Version }
  elseif ($Channel -ieq 'nightly') { 'nightly' }
  else { 'nightly' }

$assetName = 'codex-d-win-x64.zip'
$downloadUrl = "https://github.com/$Repo/releases/download/$tag/$assetName"

Write-Host "Installing CodexD from $downloadUrl"
Write-Host "Install dir: $InstallDir"

$tempRoot = Join-Path ([IO.Path]::GetTempPath()) ("codex-d-install-" + [Guid]::NewGuid().ToString('n'))
$zipPath = Join-Path $tempRoot $assetName
$extractDir = Join-Path $tempRoot 'extract'

New-Item -ItemType Directory -Path $extractDir -Force | Out-Null
New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null

try {
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

  Copy-Item -LiteralPath $exe -Destination (Join-Path $InstallDir 'codex-d.exe') -Force

  $currentUserPath = [Environment]::GetEnvironmentVariable('Path', 'User')
  if ($null -eq $currentUserPath) { $currentUserPath = '' }

  $pathParts = $currentUserPath -split ';' | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | ForEach-Object { $_.Trim() }
  $alreadyOnPath = $pathParts | Where-Object { $_ -ieq $InstallDir } | Select-Object -First 1
  if (-not $alreadyOnPath) {
    $newUserPath = ($pathParts + $InstallDir) -join ';'
    [Environment]::SetEnvironmentVariable('Path', $newUserPath, 'User')
    Write-Host "Added to PATH (User): $InstallDir"
  } else {
    Write-Host "Already on PATH (User): $InstallDir"
  }

  Write-Host ''
  Write-Host 'Installed. Open a new terminal and run:'
  Write-Host '  codex-d --help'
  Write-Host ''
  Write-Host 'In this session you can run:'
  Write-Host ("  & '" + (Join-Path $InstallDir 'codex-d.exe') + "' --help")
} finally {
  Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue | Out-Null
}
