param(
    [ValidateSet("auto", "x64", "arm64")]
    [string]$Architecture = "auto",
    [switch]$SkipBuild,
    [switch]$DontStart
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot

if ($Architecture -eq "auto") {
    $Architecture = if ($env:PROCESSOR_ARCHITECTURE -eq "ARM64") { "arm64" } else { "x64" }
}

$runtime = "win-$Architecture"
$publishedExecutable = Join-Path $repoRoot "artifacts\$runtime\CodexUsage.exe"
if (-not $SkipBuild) {
    & (Join-Path $PSScriptRoot "build.ps1") -Architecture $Architecture
}

if (-not (Test-Path -LiteralPath $publishedExecutable)) {
    throw "Build output not found at $publishedExecutable. Run scripts\build.ps1 first."
}

$installDirectory = Join-Path $env:LOCALAPPDATA "Programs\CodexUsage"
$installedExecutable = Join-Path $installDirectory "CodexUsage.exe"
New-Item -ItemType Directory -Force -Path $installDirectory | Out-Null
Copy-Item -LiteralPath $publishedExecutable -Destination $installedExecutable -Force

$startMenuDirectory = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs"
$shortcutPath = Join-Path $startMenuDirectory "Codex Usage.lnk"
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $installedExecutable
$shortcut.WorkingDirectory = $installDirectory
$shortcut.Description = "Codex usage limits in the Windows system tray"
$shortcut.Save()

Write-Host "Installed Codex Usage to $installedExecutable"
Write-Host "A Start Menu shortcut was created at $shortcutPath"

if (-not $DontStart) {
    Start-Process -FilePath $installedExecutable -WindowStyle Hidden
}
