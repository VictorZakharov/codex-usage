param(
    [ValidateSet("x64", "arm64")]
    [string]$Architecture = "x64"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot "src\CodexUsage.App\CodexUsage.App.csproj"
$runtime = "win-$Architecture"
$output = Join-Path $repoRoot "artifacts\$runtime"

dotnet publish $project `
    --configuration Release `
    --runtime $runtime `
    --self-contained true `
    --output $output `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:PublishTrimmed=false `
    -p:DebugType=None `
    -p:DebugSymbols=false

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

$executable = Join-Path $output "CodexUsage.exe"
if (-not (Test-Path -LiteralPath $executable)) {
    throw "Publish completed without producing $executable"
}

$hash = (Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash.ToLowerInvariant()
$checksumPath = "$executable.sha256"
[System.IO.File]::WriteAllText($checksumPath, "$hash *CodexUsage.exe`n")

Write-Host "Built $executable"
Write-Host "SHA-256 $hash"
