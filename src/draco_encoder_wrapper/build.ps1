param(
    [ValidateSet("Debug", "Release")]
    [string]$Config = "Release",
    [string]$DracoSourceDir
)

$ErrorActionPreference = "Stop"
$resolvedDracoSourceDir = $null
if ($DracoSourceDir) {
    $dracoSourcePath = $DracoSourceDir
    if (-not [System.IO.Path]::IsPathRooted($dracoSourcePath)) {
        $dracoSourcePath = Join-Path (Get-Location) $dracoSourcePath
    }
    $resolvedDracoSourceDir = Resolve-Path -LiteralPath $dracoSourcePath
    if (-not (Test-Path -LiteralPath (Join-Path $resolvedDracoSourceDir "CMakeLists.txt"))) {
        throw "DracoSourceDir is not a Draco source tree: $resolvedDracoSourceDir"
    }
}

Set-Location -Path $PSScriptRoot

if (-not (Test-Path "build")) {
    New-Item -ItemType Directory -Path "build" | Out-Null
}

Write-Host "Configuring..."
$cmakeArguments = @("-S", ".", "-B", "build", "-A", "x64")
if ($resolvedDracoSourceDir) {
    $cmakeArguments += "-DDRACO_SOURCE_DIR=$resolvedDracoSourceDir"
} else {
    # Clear a cached local override so an ordinary build always uses the pinned archive.
    $cmakeArguments += "-DDRACO_SOURCE_DIR="
}

cmake @cmakeArguments
if ($LASTEXITCODE -ne 0) { throw "cmake configure failed" }

Write-Host "Building ($Config)..."
cmake --build build --config $Config
if ($LASTEXITCODE -ne 0) { throw "cmake build failed" }

$dll = Join-Path $PSScriptRoot "build\bin\$Config\draco_encoder.dll"
$outDir = Resolve-Path (Join-Path $PSScriptRoot "..\..\output")
if (-not (Test-Path $dll)) {
    # Fall back to default MSVC per-config dir if RUNTIME_OUTPUT_DIRECTORY was ignored.
    $dll = Join-Path $PSScriptRoot "build\$Config\draco_encoder.dll"
}
if (-not (Test-Path $dll)) {
    throw "draco_encoder.dll not found after build."
}

Copy-Item $dll $outDir -Force
Write-Host "Copied $dll -> $outDir"
