param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [ValidateSet("x64")]
    [string]$Platform = "x64",

    [ValidateSet("Revit", "AutoCAD", "Both")]
    [string]$Target = "Both",

    [int]$RevitYear = 2019,

    [string]$RevitInstallPath = "",

    [string]$AutoCadInstallPath = ""
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$RepoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$OutputRoot = Join-Path $RepoRoot "output"
$RevitProject = Join-Path $RepoRoot "RevitGltfExporter\RevitGltfExporter\RevitGltfExporter.csproj"
$RevitSolution = Join-Path $RepoRoot "RevitGltfExporter\RevitGltfExporter.sln"
$RevitAddinTemplate = Join-Path $RepoRoot "RevitGltfExporter\RevitGltfExporter\Resources\RevitGltfExporter.addin"
$AutoCadProject = Join-Path $RepoRoot "AutoCadGltfExporter\AutoCadGltfExporter\AutoCadGltfExporter.csproj"
$AutoCadSolution = Join-Path $RepoRoot "AutoCadGltfExporter\AutoCadGltfExporter.sln"
$DracoBuildScript = Join-Path $RepoRoot "draco_encoder_wrapper\build.ps1"

function Resolve-MSBuild {
    $command = Get-Command "msbuild.exe" -ErrorAction SilentlyContinue
    if ($null -ne $command) {
        return $command.Source
    }

    $programFiles = @($env:ProgramFiles, ${env:ProgramFiles(x86)}) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
    $candidates = @()

    $vswhereRoot = ${env:ProgramFiles(x86)}
    if (-not [string]::IsNullOrWhiteSpace($vswhereRoot)) {
        $vswhere = Join-Path $vswhereRoot "Microsoft Visual Studio\Installer\vswhere.exe"
        if (Test-Path -LiteralPath $vswhere) {
            $found = & $vswhere -latest -products "*" -requires Microsoft.Component.MSBuild -find "MSBuild\**\Bin\MSBuild.exe" 2>$null | Select-Object -First 1
            if (-not [string]::IsNullOrWhiteSpace($found)) {
                return $found
            }
        }
    }

    foreach ($root in $programFiles) {
        $candidates += @(
            (Join-Path $root "Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\MSBuild.exe"),
            (Join-Path $root "Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe"),
            (Join-Path $root "Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe"),
            (Join-Path $root "Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe")
        )
    }

    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate) {
            return $candidate
        }
    }

    throw "MSBuild.exe was not found. Install Visual Studio Build Tools or run this script from a Developer PowerShell."
}

function Get-ProjectProperty {
    param(
        [Parameter(Mandatory = $true)][string]$ProjectPath,
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$Configuration,
        [Parameter(Mandatory = $true)][string]$Platform,
        [switch]$PreferConfigurationGroup
    )

    [xml]$projectXml = Get-Content -LiteralPath $ProjectPath

    if ($PreferConfigurationGroup) {
        $condition = " '" + '$' + "(Configuration)|" + '$' + "(Platform)' == '" + $Configuration + "|" + $Platform + "' "
        foreach ($group in $projectXml.Project.PropertyGroup) {
            if ($group.GetAttribute("Condition") -eq $condition) {
                $node = $group.SelectSingleNode("*[local-name()='$Name']")
                if ($null -ne $node -and -not [string]::IsNullOrWhiteSpace($node.InnerText)) {
                    return $node.InnerText
                }
            }
        }
    }

    foreach ($group in $projectXml.Project.PropertyGroup) {
        $node = $group.SelectSingleNode("*[local-name()='$Name']")
        if ($null -ne $node -and -not [string]::IsNullOrWhiteSpace($node.InnerText)) {
            return $node.InnerText
        }
    }

    throw "Property '$Name' was not found in '$ProjectPath'."
}

function Expand-MSBuildValue {
    param(
        [Parameter(Mandatory = $true)][string]$Value,
        [Parameter(Mandatory = $true)][hashtable]$Properties
    )

    $expanded = $Value
    foreach ($key in $Properties.Keys) {
        $expanded = $expanded.Replace('$(' + $key + ')', [string]$Properties[$key])
    }

    return $expanded
}

function Resolve-ProjectPath {
    param(
        [Parameter(Mandatory = $true)][string]$ProjectPath,
        [Parameter(Mandatory = $true)][string]$Path
    )

    if ([System.IO.Path]::IsPathRooted($Path)) {
        return [System.IO.Path]::GetFullPath($Path)
    }

    $projectDir = Split-Path -Parent $ProjectPath
    return [System.IO.Path]::GetFullPath((Join-Path $projectDir $Path))
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

function Build-Solution {
    param(
        [Parameter(Mandatory = $true)][string]$MSBuildPath,
        [Parameter(Mandatory = $true)][string]$SolutionPath,
        [hashtable]$AdditionalProperties = @{}
    )

    $arguments = @(
        $SolutionPath,
        "/m",
        "/p:Configuration=$Configuration",
        "/p:Platform=$Platform",
        "/p:DracoEncoderConfiguration=$Configuration"
    )

    foreach ($key in $AdditionalProperties.Keys) {
        $value = [string]$AdditionalProperties[$key]
        if (-not [string]::IsNullOrWhiteSpace($value)) {
            $arguments += "/p:$key=$value"
        }
    }

    Write-Host "Building $SolutionPath ($Configuration|$Platform)..."
    Invoke-Checked -FilePath $MSBuildPath -Arguments $arguments -FailureMessage "MSBuild failed for '$SolutionPath'."
}

function Assert-FileExists {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Required file was not found: $Path"
    }
}

function Assert-DirectoryExists {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
        throw "Required directory was not found: $Path"
    }
}

function Get-RevitOutputDir {
    $rawOutputPath = Get-ProjectProperty -ProjectPath $RevitProject -Name "OutputPath" -Configuration $Configuration -Platform $Platform -PreferConfigurationGroup
    $expanded = Expand-MSBuildValue -Value $rawOutputPath -Properties @{
        Configuration = $Configuration
        Platform = $Platform
    }

    return Resolve-ProjectPath -ProjectPath $RevitProject -Path $expanded
}

function Get-AutoCadBundleDir {
    $rawOutputPath = Get-ProjectProperty -ProjectPath $AutoCadProject -Name "OutputPath" -Configuration $Configuration -Platform $Platform -PreferConfigurationGroup
    $rawBundleDir = Get-ProjectProperty -ProjectPath $AutoCadProject -Name "BundleStagingDir" -Configuration $Configuration -Platform $Platform
    $expanded = Expand-MSBuildValue -Value $rawBundleDir -Properties @{
        Configuration = $Configuration
        Platform = $Platform
        OutputPath = $rawOutputPath
    }

    return Resolve-ProjectPath -ProjectPath $AutoCadProject -Path $expanded
}

function Install-RevitPlugin {
    $revitOutputDir = Get-RevitOutputDir
    $revitDll = Join-Path $revitOutputDir "RevitGltfExporter.dll"

    Assert-FileExists $revitDll
    Assert-FileExists (Join-Path $revitOutputDir "GltfExporter.Shared.dll")
    Assert-FileExists (Join-Path $revitOutputDir "Newtonsoft.Json.dll")
    Assert-FileExists (Join-Path $revitOutputDir "draco_encoder.dll")
    Assert-FileExists $RevitAddinTemplate

    $installDir = Join-Path $env:ProgramData "Autodesk\Revit\Addins\$RevitYear"
    $targetAddin = Join-Path $installDir "RevitGltfExporter.addin"

    Write-Host "Installing Revit add-in manifest to $targetAddin..."
    New-Item -ItemType Directory -Path $installDir -Force | Out-Null

    [xml]$addinXml = Get-Content -LiteralPath $RevitAddinTemplate
    $assemblyNode = $addinXml.SelectSingleNode("//Assembly")
    if ($null -eq $assemblyNode) {
        throw "Assembly element was not found in '$RevitAddinTemplate'."
    }

    $assemblyNode.InnerText = (Resolve-Path $revitDll).Path

    $settings = New-Object System.Xml.XmlWriterSettings
    $settings.Encoding = New-Object System.Text.UTF8Encoding($false)
    $settings.Indent = $true
    $settings.NewLineChars = "`r`n"
    $writer = [System.Xml.XmlWriter]::Create($targetAddin, $settings)
    try {
        $addinXml.Save($writer)
    }
    finally {
        $writer.Close()
    }

    Assert-FileExists $targetAddin

    [xml]$installedXml = Get-Content -LiteralPath $targetAddin
    $installedAssembly = $installedXml.SelectSingleNode("//Assembly")
    if ($null -eq $installedAssembly -or $installedAssembly.InnerText -ne (Resolve-Path $revitDll).Path) {
        throw "Installed Revit add-in manifest does not point to '$revitDll'."
    }
}

function Install-AutoCadPlugin {
    $bundleDir = Get-AutoCadBundleDir

    Assert-DirectoryExists $bundleDir
    Assert-FileExists (Join-Path $bundleDir "PackageContents.xml")
    Assert-FileExists (Join-Path $bundleDir "Contents\AutoCadGltfExporter.dll")
    Assert-FileExists (Join-Path $bundleDir "Contents\GltfExporter.Shared.dll")
    Assert-FileExists (Join-Path $bundleDir "Contents\Newtonsoft.Json.dll")
    Assert-FileExists (Join-Path $bundleDir "Contents\draco_encoder.dll")

    $applicationPluginsDir = Join-Path $env:ProgramData "Autodesk\ApplicationPlugins"
    $targetBundleDir = Join-Path $applicationPluginsDir "AutoCadGltfExporter.bundle"

    Write-Host "Installing AutoCAD bundle to $targetBundleDir..."
    New-Item -ItemType Directory -Path $applicationPluginsDir -Force | Out-Null

    if (Test-Path -LiteralPath $targetBundleDir) {
        Remove-Item -LiteralPath $targetBundleDir -Recurse -Force
    }

    Copy-Item -LiteralPath $bundleDir -Destination $applicationPluginsDir -Recurse -Force

    Assert-DirectoryExists $targetBundleDir
    Assert-FileExists (Join-Path $targetBundleDir "PackageContents.xml")
    Assert-FileExists (Join-Path $targetBundleDir "Contents\AutoCadGltfExporter.dll")
    Assert-FileExists (Join-Path $targetBundleDir "Contents\GltfExporter.Shared.dll")
    Assert-FileExists (Join-Path $targetBundleDir "Contents\Newtonsoft.Json.dll")
    Assert-FileExists (Join-Path $targetBundleDir "Contents\draco_encoder.dll")
}

try {
    New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null

    Write-Host "Building Draco encoder ($Configuration)..."
    Invoke-Checked -FilePath "powershell" -Arguments @("-ExecutionPolicy", "Bypass", "-File", $DracoBuildScript, "-Config", $Configuration) -FailureMessage "Draco encoder build failed."

    $msbuild = Resolve-MSBuild

    if ($Target -eq "Revit" -or $Target -eq "Both") {
        $properties = @{}
        if (-not [string]::IsNullOrWhiteSpace($RevitInstallPath)) {
            $properties["RevitInstallPath"] = $RevitInstallPath
        }

        Build-Solution -MSBuildPath $msbuild -SolutionPath $RevitSolution -AdditionalProperties $properties
        Install-RevitPlugin
    }

    if ($Target -eq "AutoCAD" -or $Target -eq "Both") {
        $properties = @{}
        if (-not [string]::IsNullOrWhiteSpace($AutoCadInstallPath)) {
            $properties["AutoCadInstallPath"] = $AutoCadInstallPath
        }

        Build-Solution -MSBuildPath $msbuild -SolutionPath $AutoCadSolution -AdditionalProperties $properties
        Install-AutoCadPlugin
    }

    Write-Host "Plugin installation completed."
}
catch [System.UnauthorizedAccessException] {
    Write-Error "Permission denied while installing to ProgramData. Run PowerShell as Administrator and execute the script again."
    exit 1
}
catch {
    $exception = $_.Exception
    while ($null -ne $exception) {
        if ($exception -is [System.UnauthorizedAccessException]) {
            Write-Error "Permission denied while installing to ProgramData. Run PowerShell as Administrator and execute the script again."
            exit 1
        }

        $exception = $exception.InnerException
    }

    Write-Error $_.Exception.Message
    exit 1
}
