param(
    [Parameter(Mandatory = $true)]
    [string]$Version
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

if ($Version -notmatch '^v?(?<major>\d+)\.(?<minor>\d+)\.(?<patch>\d+)$') {
    throw "Version '$Version' is invalid. Use a semantic version such as v1.2.3."
}

$major = [int]$Matches.major
$minor = [int]$Matches.minor
$patch = [int]$Matches.patch
if ($major -gt 255 -or $minor -gt 255 -or $patch -gt 65535) {
    throw "Version '$Version' cannot be represented as a Windows Installer product version."
}

$productVersion = "$major.$minor.$patch"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$outputRoot = Join-Path $repoRoot "output"
$releaseRoot = Join-Path $repoRoot "release"
$dracoBuildScript = Join-Path $repoRoot "src\draco_encoder_wrapper\build.ps1"
$revitProject = Join-Path $repoRoot "src\RevitGltfExporter\RevitGltfExporter\RevitGltfExporter.csproj"
$autoCadProject = Join-Path $repoRoot "src\AutoCadGltfExporter\AutoCadGltfExporter.csproj"
$revitInstaller = Join-Path $repoRoot "installers\Revit\RevitInstaller.wixproj"
$autoCadInstaller = Join-Path $repoRoot "installers\AutoCAD\AutoCadInstaller.wixproj"
$autoCadBundle = Join-Path $outputRoot "AutoCadGltfExporter.bundle"

function Resolve-MSBuild {
    $command = Get-Command "msbuild.exe" -ErrorAction SilentlyContinue
    if ($null -ne $command) {
        return $command.Source
    }

    $vswhereRoot = ${env:ProgramFiles(x86)}
    if (-not [string]::IsNullOrWhiteSpace($vswhereRoot)) {
        $vswhere = Join-Path $vswhereRoot "Microsoft Visual Studio\Installer\vswhere.exe"
        if (Test-Path -LiteralPath $vswhere) {
            $found = & $vswhere -latest -products "*" -requires Microsoft.Component.MSBuild -find "MSBuild\**\Bin\MSBuild.exe" | Select-Object -First 1
            if (-not [string]::IsNullOrWhiteSpace($found)) {
                return $found
            }
        }
    }

    throw "MSBuild.exe was not found. Install Visual Studio Build Tools with .NET desktop build tools."
}

function Resolve-DotNet {
    $command = Get-Command "dotnet.exe" -ErrorAction SilentlyContinue
    if ($null -ne $command) {
        return $command.Source
    }

    $candidate = Join-Path $env:ProgramFiles "dotnet\dotnet.exe"
    if (Test-Path -LiteralPath $candidate) {
        return $candidate
    }

    throw "dotnet.exe was not found. Install the .NET SDK."
}

function Invoke-Checked {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [Parameter(Mandatory = $true)][string]$FailureMessage
    )

    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw $FailureMessage
    }
}

function Assert-FileExists {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Required release file was not produced: $Path"
    }
}

New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
New-Item -ItemType Directory -Path $releaseRoot -Force | Out-Null
Get-ChildItem -LiteralPath $releaseRoot -Filter "*.msi" -File -ErrorAction SilentlyContinue | Remove-Item -Force
Remove-Item -LiteralPath (Join-Path $releaseRoot "SHA256SUMS.txt") -Force -ErrorAction SilentlyContinue

Write-Host "Building Draco encoder..."
Invoke-Checked -FilePath "powershell" -Arguments @(
    "-NoProfile",
    "-ExecutionPolicy", "Bypass",
    "-File", $dracoBuildScript,
    "-Config", "Release"
) -FailureMessage "Draco encoder build failed."

$msbuild = Resolve-MSBuild
$dotnet = Resolve-DotNet
$commonBuildArguments = @(
    "/restore",
    "/t:Rebuild",
    "/m",
    "/p:Configuration=Release",
    "/p:Platform=x64",
    "/p:DracoEncoderConfiguration=Release",
    "/p:UseAutodeskNuGetReferences=true"
)

Write-Host "Building Revit 2019 plug-in..."
Invoke-Checked -FilePath $msbuild -Arguments (@($revitProject) + $commonBuildArguments) -FailureMessage "Revit plug-in build failed."

Write-Host "Building AutoCAD 2020-2024 plug-in..."
Invoke-Checked -FilePath $msbuild -Arguments (@($autoCadProject) + $commonBuildArguments) -FailureMessage "AutoCAD plug-in build failed."

$requiredPluginFiles = @(
    (Join-Path $outputRoot "RevitGltfExporter.dll"),
    (Join-Path $outputRoot "GltfExporter.Shared.dll"),
    (Join-Path $outputRoot "Newtonsoft.Json.dll"),
    (Join-Path $outputRoot "draco_encoder.dll"),
    (Join-Path $autoCadBundle "PackageContents.xml"),
    (Join-Path $autoCadBundle "Contents\AutoCadGltfExporter.dll"),
    (Join-Path $autoCadBundle "Contents\GltfExporter.Shared.dll"),
    (Join-Path $autoCadBundle "Contents\Newtonsoft.Json.dll"),
    (Join-Path $autoCadBundle "Contents\draco_encoder.dll")
)
foreach ($file in $requiredPluginFiles) {
    Assert-FileExists $file
}

[xml]$bundleManifest = Get-Content -LiteralPath (Join-Path $autoCadBundle "PackageContents.xml")
$bundleManifest.ApplicationPackage.AppVersion = $productVersion
$xmlSettings = New-Object System.Xml.XmlWriterSettings
$xmlSettings.Encoding = New-Object System.Text.UTF8Encoding($false)
$xmlSettings.Indent = $true
$xmlSettings.NewLineChars = "`r`n"
$manifestWriter = [System.Xml.XmlWriter]::Create((Join-Path $autoCadBundle "PackageContents.xml"), $xmlSettings)
try {
    $bundleManifest.Save($manifestWriter)
}
finally {
    $manifestWriter.Close()
}

$installerProperties = @(
    "-c", "Release",
    "--nologo",
    "-p:ProductVersion=$productVersion",
    "-p:OutputPath=$releaseRoot"
)

Write-Host "Building Revit MSI..."
Invoke-Checked -FilePath $dotnet -Arguments (@(
    "build", $revitInstaller
) + $installerProperties + @(
    "-p:PayloadDir=$outputRoot"
)) -FailureMessage "Revit MSI build failed."

Write-Host "Building AutoCAD MSI..."
Invoke-Checked -FilePath $dotnet -Arguments (@(
    "build", $autoCadInstaller
) + $installerProperties + @(
    "-p:PayloadDir=$autoCadBundle"
)) -FailureMessage "AutoCAD MSI build failed."

$revitMsi = Join-Path $releaseRoot "RevitGltfExporter-$productVersion-x64.msi"
$autoCadMsi = Join-Path $releaseRoot "AutoCadGltfExporter-$productVersion-x64.msi"
Assert-FileExists $revitMsi
Assert-FileExists $autoCadMsi

$checksumLines = foreach ($msi in @($revitMsi, $autoCadMsi)) {
    $hash = Get-FileHash -LiteralPath $msi -Algorithm SHA256
    "$($hash.Hash.ToLowerInvariant())  $([System.IO.Path]::GetFileName($msi))"
}
$checksumLines | Set-Content -LiteralPath (Join-Path $releaseRoot "SHA256SUMS.txt") -Encoding ascii

Write-Host "Release installers are ready in '$releaseRoot'."
