#requires -Version 5.1
<#
.SYNOPSIS
    Downloads Lib.Harmony from NuGet and installs it to
    Editor/Plugins/Harmony/ inside this package, with the same
    Plugin Importer .meta shape we use for the bundled Roslyn DLLs
    (Editor-only, ValidateReferences=true).

.DESCRIPTION
    Run via the Editor menu (Tools / Roslyn REPL / Install Harmony) or
    directly from PowerShell:
        pwsh -ExecutionPolicy Bypass -File ./install-harmony.ps1
        powershell -ExecutionPolicy Bypass -File ./install-harmony.ps1

    Harmony powers the Runtime Method Patch feature — without this DLL
    Patch / Revert can't redirect a method's calls. The Lib.Harmony
    package on NuGet is MIT-licensed and ships a single
    netstandard2.0 / 0Harmony.dll. The "0" prefix in the filename is
    deliberate: it makes the assembly load early so its detour
    machinery is in place before any user code touches it.

    Idempotent: re-running overwrites the existing DLL and reuses the
    stable GUID below so Unity's GUID-based references survive
    reinstall.
#>

$ErrorActionPreference = 'Stop'
[System.Net.ServicePointManager]::SecurityProtocol = [System.Net.SecurityProtocolType]::Tls12

$PackageRoot = Split-Path -Parent $PSScriptRoot
$PluginsDir  = Join-Path $PackageRoot 'Editor/Plugins/Harmony'
$TempRoot    = Join-Path ([System.IO.Path]::GetTempPath()) ('RoslynRepl-Harmony-' + [System.Guid]::NewGuid().ToString('N').Substring(0, 8))

# Stable GUID — keep constant across reinstalls so existing
# references inside Unity don't break.
$Package = [pscustomobject]@{
    Id      = 'Lib.Harmony'
    Version = '2.3.3'
    Dll     = '0Harmony.dll'
    Guid    = 'e5f6071829304a5b6c7d8e9f0a1b2c3d'
}

function Write-Meta {
    param([string]$DllPath, [string]$Guid)
    # Build line-by-line to preserve trailing spaces required by Unity's
    # YAML parser for empty fields like "userData: ".
    $sb = New-Object System.Text.StringBuilder
    [void]$sb.AppendLine("fileFormatVersion: 2")
    [void]$sb.AppendLine("guid: $Guid")
    [void]$sb.AppendLine("PluginImporter:")
    [void]$sb.AppendLine("  externalObjects: {}")
    [void]$sb.AppendLine("  serializedVersion: 2")
    [void]$sb.AppendLine("  iconMap: {}")
    [void]$sb.AppendLine("  executionOrder: {}")
    [void]$sb.AppendLine("  defineConstraints: []")
    [void]$sb.AppendLine("  isPreloaded: 0")
    [void]$sb.AppendLine("  isOverridable: 0")
    [void]$sb.AppendLine("  isExplicitlyReferenced: 0")
    [void]$sb.AppendLine("  validateReferences: 1")
    [void]$sb.AppendLine("  platformData:")
    [void]$sb.AppendLine("  - first:")
    [void]$sb.AppendLine("      Any: ")
    [void]$sb.AppendLine("    second:")
    [void]$sb.AppendLine("      enabled: 0")
    [void]$sb.AppendLine("      settings: {}")
    [void]$sb.AppendLine("  - first:")
    [void]$sb.AppendLine("      Editor: Editor")
    [void]$sb.AppendLine("    second:")
    [void]$sb.AppendLine("      enabled: 1")
    [void]$sb.AppendLine("      settings:")
    [void]$sb.AppendLine("        DefaultValueInitialized: true")
    [void]$sb.AppendLine("  userData: ")
    [void]$sb.AppendLine("  assetBundleName: ")
    [void]$sb.AppendLine("  assetBundleVariant: ")
    [System.IO.File]::WriteAllText("$DllPath.meta", $sb.ToString(), [System.Text.UTF8Encoding]::new($false))
}

function Resolve-DllInPackage {
    param([string]$ExtractRoot, [string]$DllName)
    # Lib.Harmony ships netstandard2.0 / net48 / net5.0 / net6.0; pick
    # netstandard2.0 to stay maximally compatible with older Unity
    # editor runtimes.
    $preferred = Join-Path $ExtractRoot "lib/netstandard2.0/$DllName"
    if (Test-Path $preferred) { return $preferred }
    $candidate = Get-ChildItem -Path (Join-Path $ExtractRoot 'lib') -Recurse -Filter $DllName -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -ne $candidate) { return $candidate.FullName }
    return $null
}

try {
    Write-Host "[Roslyn REPL] Installing Harmony to: $PluginsDir"
    New-Item -ItemType Directory -Force -Path $PluginsDir | Out-Null
    New-Item -ItemType Directory -Force -Path $TempRoot   | Out-Null

    $url     = "https://www.nuget.org/api/v2/package/$($Package.Id)/$($Package.Version)"
    $nupkg   = Join-Path $TempRoot "$($Package.Id).$($Package.Version).nupkg"
    $zip     = "$nupkg.zip"
    $extract = Join-Path $TempRoot $Package.Id

    Write-Host "  Downloading $($Package.Id) $($Package.Version)..."
    Invoke-WebRequest -Uri $url -OutFile $nupkg -UseBasicParsing

    Copy-Item $nupkg $zip -Force
    if (Test-Path $extract) { Remove-Item $extract -Recurse -Force }
    Expand-Archive -Path $zip -DestinationPath $extract -Force

    $src = Resolve-DllInPackage -ExtractRoot $extract -DllName $Package.Dll
    if (-not $src) {
        throw "Could not locate $($Package.Dll) inside $($Package.Id) $($Package.Version) package."
    }

    $dst = Join-Path $PluginsDir $Package.Dll
    Copy-Item $src $dst -Force
    Write-Meta -DllPath $dst -Guid $Package.Guid
    Write-Host "    Installed $($Package.Dll) (guid=$($Package.Guid))"

    $notice = @'
Roslyn REPL bundles the following third-party assembly under the MIT License:

  - 0Harmony.dll  (Lib.Harmony 2.3.3)

Source:
  https://www.nuget.org/packages/Lib.Harmony/2.3.3

License: https://licenses.nuget.org/MIT
'@
    [System.IO.File]::WriteAllText((Join-Path $PluginsDir 'THIRD_PARTY_NOTICES.md'), $notice, [System.Text.UTF8Encoding]::new($false))

    Write-Host "[Roslyn REPL] Done. Refresh AssetDatabase in Unity if it doesn't reimport automatically."
}
finally {
    if (Test-Path $TempRoot) { Remove-Item $TempRoot -Recurse -Force -ErrorAction SilentlyContinue }
}
