param(
    [ValidateSet("x64", "arm64")]
    [string]$Architecture = "x64",

    [switch]$FrameworkDependent
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot "src\CodexUsage.App\CodexUsage.App.csproj"
$runtime = "win-$Architecture"
$outputFolder = if ($FrameworkDependent) { "$runtime-framework-dependent" } else { $runtime }
$output = Join-Path $repoRoot "artifacts\$outputFolder"
$deploymentOption = if ($FrameworkDependent) { "--no-self-contained" } else { "--self-contained" }

$publishArguments = @(
    "publish"
    $project
    "--configuration"
    "Release"
    "--runtime"
    $runtime
    $deploymentOption
    "--output"
    $output
    "-p:IntermediateOutputPath=obj\$outputFolder\"
    "-p:PublishSingleFile=true"
    "-p:PublishTrimmed=false"
    "-p:DebugType=None"
    "-p:DebugSymbols=false"
)

if (-not $FrameworkDependent) {
    $publishArguments += "-p:IncludeNativeLibrariesForSelfExtract=true"
    $publishArguments += "-p:EnableCompressionInSingleFile=true"
}

dotnet @publishArguments

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
if ($FrameworkDependent) {
    Write-Host "Runtime required: .NET 10 Desktop Runtime ($Architecture)"
}
else {
    Write-Host "Runtime required: none (self-contained)"
}
Write-Host "SHA-256 $hash"
