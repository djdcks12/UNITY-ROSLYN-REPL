#requires -Version 5.1
<#
.SYNOPSIS
    Downloads Roslyn 4.8.0 binaries from NuGet and installs them to
    Editor/Plugins/Roslyn/ inside this package, with proper Plugin Importer
    .meta files (Editor-only, ValidateReferences=true).

.DESCRIPTION
    Run via the Editor menu (Tools / Roslyn REPL / Install Roslyn DLLs) or
    directly from PowerShell:
        pwsh -ExecutionPolicy Bypass -File ./install-roslyn.ps1
        powershell -ExecutionPolicy Bypass -File ./install-roslyn.ps1

    Idempotent: re-running overwrites existing DLLs and reuses stable GUIDs
    so Unity's GUID-based references survive reinstall.
#>

$ErrorActionPreference = 'Stop'
[System.Net.ServicePointManager]::SecurityProtocol = [System.Net.SecurityProtocolType]::Tls12

$PackageRoot = Split-Path -Parent $PSScriptRoot
$PluginsDir  = Join-Path $PackageRoot 'Editor/Plugins/Roslyn'
$TempRoot    = Join-Path ([System.IO.Path]::GetTempPath()) ('RoslynRepl-Install-' + [System.Guid]::NewGuid().ToString('N').Substring(0, 8))

# Stable GUIDs — keep these constant across reinstalls so Unity references don't break.
$Packages = @(
    [pscustomobject]@{ Id = 'Microsoft.CodeAnalysis.Common'; Version = '4.8.0'; Dll = 'Microsoft.CodeAnalysis.dll'; Guid = 'a1b2c3d40e1f4a2b8c3d4e5f60718293' }
    [pscustomobject]@{ Id = 'Microsoft.CodeAnalysis.CSharp'; Version = '4.8.0'; Dll = 'Microsoft.CodeAnalysis.CSharp.dll'; Guid = 'b2c3d4e51f2a4b3c9d4e5f6071829304' }
    [pscustomobject]@{ Id = 'System.Collections.Immutable'; Version = '7.0.0'; Dll = 'System.Collections.Immutable.dll'; Guid = 'c3d4e5f62a3b4c4d0e5f60718293a4b5' }
    [pscustomobject]@{ Id = 'System.Reflection.Metadata';   Version = '7.0.0'; Dll = 'System.Reflection.Metadata.dll';   Guid = 'd4e5f607384c5d6e1f60718293a4b5c6' }
)

function Write-Meta {
    param([string]$DllPath, [string]$Guid)
    # Build line-by-line to preserve trailing spaces required by Unity's YAML parser
    # for empty fields like "userData: ".
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
    # Prefer netstandard2.0
    $preferred = Join-Path $ExtractRoot "lib/netstandard2.0/$DllName"
    if (Test-Path $preferred) { return $preferred }
    # Fallback: any *.dll match anywhere in lib/
    $candidate = Get-ChildItem -Path (Join-Path $ExtractRoot 'lib') -Recurse -Filter $DllName -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -ne $candidate) { return $candidate.FullName }
    return $null
}

try {
    Write-Host "[Roslyn REPL] Installing to: $PluginsDir"
    New-Item -ItemType Directory -Force -Path $PluginsDir | Out-Null
    New-Item -ItemType Directory -Force -Path $TempRoot   | Out-Null

    foreach ($p in $Packages) {
        $url     = "https://www.nuget.org/api/v2/package/$($p.Id)/$($p.Version)"
        $nupkg   = Join-Path $TempRoot "$($p.Id).$($p.Version).nupkg"
        $zip     = "$nupkg.zip"
        $extract = Join-Path $TempRoot $p.Id

        Write-Host "  Downloading $($p.Id) $($p.Version)..."
        Invoke-WebRequest -Uri $url -OutFile $nupkg -UseBasicParsing

        Copy-Item $nupkg $zip -Force
        if (Test-Path $extract) { Remove-Item $extract -Recurse -Force }
        Expand-Archive -Path $zip -DestinationPath $extract -Force

        $src = Resolve-DllInPackage -ExtractRoot $extract -DllName $p.Dll
        if (-not $src) {
            throw "Could not locate $($p.Dll) inside $($p.Id) $($p.Version) package."
        }

        $dst = Join-Path $PluginsDir $p.Dll
        Copy-Item $src $dst -Force
        Write-Meta -DllPath $dst -Guid $p.Guid
        Write-Host "    Installed $($p.Dll) (guid=$($p.Guid))"
    }

    # Drop a third-party notices file
    $notice = @'
Roslyn REPL bundles the following third-party assemblies under the MIT License:

  - Microsoft.CodeAnalysis.dll          (Microsoft.CodeAnalysis.Common 4.8.0)
  - Microsoft.CodeAnalysis.CSharp.dll   (Microsoft.CodeAnalysis.CSharp 4.8.0)
  - System.Collections.Immutable.dll    (System.Collections.Immutable 7.0.0)
  - System.Reflection.Metadata.dll      (System.Reflection.Metadata 7.0.0)

Sources:
  https://www.nuget.org/packages/Microsoft.CodeAnalysis.Common/4.8.0
  https://www.nuget.org/packages/Microsoft.CodeAnalysis.CSharp/4.8.0
  https://www.nuget.org/packages/System.Collections.Immutable/7.0.0
  https://www.nuget.org/packages/System.Reflection.Metadata/7.0.0

License: https://licenses.nuget.org/MIT
'@
    [System.IO.File]::WriteAllText((Join-Path $PluginsDir 'THIRD_PARTY_NOTICES.md'), $notice, [System.Text.UTF8Encoding]::new($false))

    Write-Host "[Roslyn REPL] Done. Refresh AssetDatabase in Unity if it doesn't reimport automatically."
}
finally {
    if (Test-Path $TempRoot) { Remove-Item $TempRoot -Recurse -Force -ErrorAction SilentlyContinue }
}
