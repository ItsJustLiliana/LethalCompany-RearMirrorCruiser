param(
    [string]$OutputZip = "artifacts/RearMirrorCruiser.zip"
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

$requiredRootFiles = @(
    "README.md",
    "manifest.json",
    "icon.png"
)

foreach ($file in $requiredRootFiles) {
    if (-not (Test-Path -LiteralPath $file)) {
        throw "Required file missing: $file"
    }
}

$dllCandidates = @(
    "bin/Release/netstandard2.1/RearMirrorCruiser.dll",
    "bin/Debug/netstandard2.1/RearMirrorCruiser.dll",
    "RearMirrorCruiser.dll"
)

$dllPath = $null
foreach ($candidate in $dllCandidates) {
    if (Test-Path -LiteralPath $candidate) {
        $dllPath = $candidate
        break
    }
}

if (-not $dllPath) {
    throw "RearMirrorCruiser.dll not found in expected locations. Build the project in CI or provide a prebuilt DLL."
}

$outputFullPath = Join-Path $repoRoot $OutputZip
$outputDirectory = Split-Path -Parent $outputFullPath
if (-not (Test-Path -LiteralPath $outputDirectory)) {
    New-Item -ItemType Directory -Path $outputDirectory | Out-Null
}

$stagingDir = Join-Path $env:TEMP "rmc-thunderstore-package"
if (Test-Path -LiteralPath $stagingDir) {
    Remove-Item -LiteralPath $stagingDir -Recurse -Force
}
New-Item -ItemType Directory -Path $stagingDir | Out-Null

Copy-Item -LiteralPath $dllPath -Destination (Join-Path $stagingDir "RearMirrorCruiser.dll")
Copy-Item -LiteralPath "README.md" -Destination (Join-Path $stagingDir "README.md")
Copy-Item -LiteralPath "manifest.json" -Destination (Join-Path $stagingDir "manifest.json")
Copy-Item -LiteralPath "icon.png" -Destination (Join-Path $stagingDir "icon.png")

if (Test-Path -LiteralPath $outputFullPath) {
    Remove-Item -LiteralPath $outputFullPath -Force
}

Compress-Archive -Path (Join-Path $stagingDir "*") -DestinationPath $outputFullPath -Force
Write-Host "Created package: $OutputZip"
