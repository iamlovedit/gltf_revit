param(
    [Parameter(Mandatory = $true)]
    [string]$Version,

    [ValidateSet(2019, 2020, 2021, 2022, 2023, 2024)]
    [int[]]$RevitVersions = @(2019, 2020, 2021, 2022, 2023, 2024),

    [string]$RevitInstallPath = "",

    [string[]]$RevitInstallPaths = @(),

    [switch]$UseLocalRevitReferences
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

function Get-RevitProductIdentity {
    param([Parameter(Mandatory = $true)][int]$Year)

    $identities = @{
        2019 = @{ UpgradeCode = "A73C6488-1EA0-49AE-9A5E-A0F12E04B68B"; ManifestGuid = "AD3D19A5-DDA7-4A5C-8B23-95C4C5A87170"; PluginGuid = "CB70E736-9D39-43C0-8EAA-C08484CFDD58" }
        2020 = @{ UpgradeCode = "B84E7599-2FB1-4B4D-9C2A-6B91D5F0A102"; ManifestGuid = "BE4E20A6-EA4B-4B6D-9C34-A6D5E7F81203"; PluginGuid = "DC81F847-AE50-44D1-9FBB-D19595D1EF69" }
        2021 = @{ UpgradeCode = "C95F86AA-30C2-4C5E-AD3B-7CA2E6F1B213"; ManifestGuid = "CF5F31B7-FB5C-4C7E-AD45-B7E6F8A92314"; PluginGuid = "ED92A958-BF61-45E2-AFCC-E2A6A6E2F07A" }
        2022 = @{ UpgradeCode = "DA6097BB-41D3-4D6F-BE4C-8DB3F702C324"; ManifestGuid = "D06042C8-0C6D-4D8F-BE56-C8F709BA3425"; PluginGuid = "FEA3BA69-C072-46F3-B0DD-F3B7B7F3018B" }
        2023 = @{ UpgradeCode = "EB71A8CC-52E4-4E70-CF5D-9EC40813D435"; ManifestGuid = "E17153D9-1D7E-4E90-CF67-D9081ACB4536"; PluginGuid = "A0B4CB7A-D183-4704-C1EE-A4C8C804129C" }
        2024 = @{ UpgradeCode = "FC82B9DD-63F5-4F81-D06E-AFD51924E546"; ManifestGuid = "F28264EA-2E8F-4FA1-D079-EA192BCD5647"; PluginGuid = "B1C5DC8B-E294-4815-D2FF-B5D9D91523AD" }
    }

    return $identities[$Year]
}

function Resolve-RevitInstallPath {
    param([Parameter(Mandatory = $true)][int]$Index, [Parameter(Mandatory = $true)][int]$Year)
    if ($RevitInstallPaths.Count -gt 0) {
        if ($RevitInstallPaths.Count -ne $RevitVersions.Count) {
            throw "RevitInstallPaths must contain one path per RevitVersions entry."
        }
        return $RevitInstallPaths[$Index]
    }
    if (-not [string]::IsNullOrWhiteSpace($RevitInstallPath)) {
        if ($RevitVersions.Count -ne 1) {
            throw "Use RevitInstallPaths with one path per version when building multiple Revit versions."
        }
        return $RevitInstallPath
    }
    return "C:\Program Files\Autodesk\Revit $Year"
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
    "/p:DracoEncoderConfiguration=Release"
)
$revitMsiPaths = @()
for ($index = 0; $index -lt $RevitVersions.Count; $index++) {
    $revitVersion = $RevitVersions[$index]
    $revitOutput = Join-Path $outputRoot ("Revit{0}" -f $revitVersion)
    $identity = Get-RevitProductIdentity -Year $revitVersion
    $revitBuildArguments = @($commonBuildArguments + "/p:RevitVersion=$revitVersion")
    if ($UseLocalRevitReferences) {
        $revitBuildArguments += "/p:UseLocalRevitReferences=true"
        $revitBuildArguments += "/p:RevitInstallPath=$(Resolve-RevitInstallPath -Index $index -Year $revitVersion)"
    }

    Write-Host "Building Revit $revitVersion plug-in..."
    Invoke-Checked -FilePath $msbuild -Arguments (@($revitProject) + $revitBuildArguments) -FailureMessage "Revit $revitVersion plug-in build failed."

    $revitAddin = Join-Path $revitOutput "RevitGltfExporter.addin"
    Assert-FileExists $revitAddin
    [xml]$revitAddinXml = Get-Content -LiteralPath $revitAddin
    $revitAssemblyNode = $revitAddinXml.SelectSingleNode("//Assembly")
    if ($null -eq $revitAssemblyNode) { throw "Revit $revitVersion add-in manifest does not contain an Assembly element." }
    $revitAssemblyNode.InnerText = "RevitGltfExporter\RevitGltfExporter.$revitVersion.dll"
    $revitAddinXml.Save($revitAddin)

    foreach ($file in @(
        (Join-Path $revitOutput ("RevitGltfExporter.{0}.dll" -f $revitVersion)),
        (Join-Path $revitOutput "GltfExporter.Shared.dll"),
        (Join-Path $revitOutput "Newtonsoft.Json.dll"),
        (Join-Path $revitOutput "draco_encoder.dll")
    )) { Assert-FileExists $file }

    $installerProperties = @(
        "-c", "Release", "--nologo",
        "-p:ProductVersion=$productVersion",
        "-p:OutputPath=$releaseRoot",
        "-p:RevitVersion=$revitVersion",
        "-p:RevitUpgradeCode=$($identity.UpgradeCode)",
        "-p:RevitManifestComponentGuid=$($identity.ManifestGuid)",
        "-p:RevitPluginComponentGuid=$($identity.PluginGuid)"
    )
    Write-Host "Building Revit $revitVersion MSI..."
    Invoke-Checked -FilePath $dotnet -Arguments (@("build", $revitInstaller) + $installerProperties + @("-p:PayloadDir=$revitOutput")) -FailureMessage "Revit $revitVersion MSI build failed."
    $revitMsiPaths += Join-Path $releaseRoot ("RevitGltfExporter-{0}-{1}-x64.msi" -f $revitVersion, $productVersion)
}

Write-Host "Building AutoCAD 2020-2024 plug-in..."
$autoCadBuildArguments = @($commonBuildArguments + "/p:UseAutodeskNuGetReferences=true")
Invoke-Checked -FilePath $msbuild -Arguments (@($autoCadProject) + $autoCadBuildArguments) -FailureMessage "AutoCAD plug-in build failed."

$requiredPluginFiles = @(
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

Write-Host "Building AutoCAD MSI..."
Invoke-Checked -FilePath $dotnet -Arguments (@(
    "build", $autoCadInstaller
) + @(
    "-c", "Release", "--nologo",
    "-p:ProductVersion=$productVersion",
    "-p:OutputPath=$releaseRoot",
    "-p:PayloadDir=$autoCadBundle"
)) -FailureMessage "AutoCAD MSI build failed."

$autoCadMsi = Join-Path $releaseRoot "AutoCadGltfExporter-$productVersion-x64.msi"
foreach ($revitMsi in $revitMsiPaths) { Assert-FileExists $revitMsi }
Assert-FileExists $autoCadMsi

$checksumLines = foreach ($msi in @($revitMsiPaths + $autoCadMsi)) {
    $hash = Get-FileHash -LiteralPath $msi -Algorithm SHA256
    "$($hash.Hash.ToLowerInvariant())  $([System.IO.Path]::GetFileName($msi))"
}
$checksumLines | Set-Content -LiteralPath (Join-Path $releaseRoot "SHA256SUMS.txt") -Encoding ascii

Write-Host "Release installers are ready in '$releaseRoot'."
