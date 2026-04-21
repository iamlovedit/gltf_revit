param(
    [string]$Config = "Release"
)

$ErrorActionPreference = "Stop"
Set-Location -Path $PSScriptRoot

if (-not (Test-Path "build")) {
    New-Item -ItemType Directory -Path "build" | Out-Null
}

Write-Host "Configuring..."
cmake -S . -B build -A x64
if ($LASTEXITCODE -ne 0) { throw "cmake configure failed" }

Write-Host "Building ($Config)..."
cmake --build build --config $Config
if ($LASTEXITCODE -ne 0) { throw "cmake build failed" }

$dll = Join-Path $PSScriptRoot "build\bin\$Config\draco_encoder.dll"
$outDir = Resolve-Path (Join-Path $PSScriptRoot "..\output")
if (-not (Test-Path $dll)) {
    # Fall back to default MSVC per-config dir if RUNTIME_OUTPUT_DIRECTORY was ignored.
    $dll = Join-Path $PSScriptRoot "build\$Config\draco_encoder.dll"
}
if (-not (Test-Path $dll)) {
    throw "draco_encoder.dll not found after build."
}

Copy-Item $dll $outDir -Force
Write-Host "Copied $dll -> $outDir"
