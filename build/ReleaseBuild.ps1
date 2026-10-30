# Shared release build/freshness check for REF and its two consumers.
function Get-ReleaseBuildBlocker {
    param([string]$ProjectRoot, [string]$Version)
    if ($Version -ne '1.7') { return $null }

    $propsPath = Join-Path $ProjectRoot 'build/RimWorldReferences.props'
    $props = New-Object System.Xml.XmlDocument
    $props.Load($propsPath)
    $settings = $props.Project.PropertyGroup | Where-Object { $_.RimWorldDir } | Select-Object -First 1
    $gameDir = if ($env:RimWorldDir) { $env:RimWorldDir }
               elseif ($env:RimWorld17Dir) { $env:RimWorld17Dir }
               else { $settings.RimWorld17Dir.InnerText }
    $harmony = if ($env:HarmonyPath) { $env:HarmonyPath } else { $settings.HarmonyPath.InnerText }
    $versionFile = Join-Path $gameDir 'Version.txt'
    if ((Test-Path -LiteralPath $versionFile) -and
        !([IO.File]::ReadAllText($versionFile).Trim().StartsWith('1.7'))) {
        return "expected RimWorld 1.7 in $gameDir, found $([IO.File]::ReadAllText($versionFile).Trim())"
    }
    $required = @('Assembly-CSharp.dll', 'UnityStubs.dll', 'Waylith.dll', 'Brutal.Vulkan.dll',
                  'Brutal.Vulkan.Abstractions.dll', 'Brutal.Core.Numerics.dll')
    $missing = @($required | Where-Object { !(Test-Path -LiteralPath (Join-Path $gameDir $_)) })
    if ($missing.Count -gt 0) { return "1.7 references missing in $gameDir`: $($missing -join ', ')" }
    if (!(Test-Path -LiteralPath $harmony)) { return "1.7 Harmony reference missing: $harmony" }
    return $null
}

function Test-ReleaseArtifactFresh {
    param([string]$ProjectRoot, [string]$Assembly, [string[]]$AdditionalInputDirs,
          [string[]]$AdditionalInputFiles)
    if (!(Test-Path -LiteralPath $Assembly -PathType Leaf)) {
        return [pscustomobject]@{ Fresh = $false; Reason = "assembly missing: $Assembly" }
    }
    $builtAt = (Get-Item -LiteralPath $Assembly).LastWriteTimeUtc
    $dirs = @(Join-Path $ProjectRoot 'Source') + @($AdditionalInputDirs | Where-Object { $_ })
    $files = @((Join-Path $ProjectRoot 'build/Build.ps1')) + `
             @($AdditionalInputFiles | Where-Object { $_ })
    foreach ($dir in $dirs) {
        if (!(Test-Path -LiteralPath $dir -PathType Container)) {
            return [pscustomobject]@{ Fresh = $false; Reason = "input directory missing: $dir" }
        }
        foreach ($file in (Get-ChildItem -LiteralPath $dir -Recurse -File)) {
            if ($file.FullName -match '[\\/](obj|bin)[\\/]') { continue }
            if ($file.LastWriteTimeUtc -gt $builtAt) {
                return [pscustomobject]@{ Fresh = $false; Reason = "newer input: $($file.FullName)" }
            }
        }
    }
    foreach ($file in $files) {
        if (!(Test-Path -LiteralPath $file -PathType Leaf)) {
            return [pscustomobject]@{ Fresh = $false; Reason = "input missing: $file" }
        }
        if ((Get-Item -LiteralPath $file).LastWriteTimeUtc -gt $builtAt) {
            return [pscustomobject]@{ Fresh = $false; Reason = "newer input: $file" }
        }
    }
    return [pscustomobject]@{ Fresh = $true; Reason = $null }
}

function Invoke-ReleaseVersionBuild {
    param([string]$ProjectRoot, [string]$Version, [string]$AssemblyName,
          [string]$BuildScript, [string]$Configuration, [string[]]$AdditionalInputDirs,
          [string[]]$AdditionalInputFiles)
    $assembly = Join-Path $ProjectRoot "$Version/Assemblies/$AssemblyName"
    $failure = Get-ReleaseBuildBlocker -ProjectRoot $ProjectRoot -Version $Version
    if (!$failure) {
        try {
            $buildParameters = @{ Version = $Version }
            if ($Configuration) { $buildParameters.Configuration = $Configuration }
            & $BuildScript @buildParameters | Out-Host
            if (!(Test-Path -LiteralPath $assembly -PathType Leaf)) { throw "Built assembly missing: $assembly" }
            return [pscustomobject]@{ Version = $Version; Usable = $true; Reused = $false }
        } catch {
            $failure = $_.Exception.Message
        }
    }
    $fresh = Test-ReleaseArtifactFresh -ProjectRoot $ProjectRoot -Assembly $assembly `
        -AdditionalInputDirs $AdditionalInputDirs -AdditionalInputFiles $AdditionalInputFiles
    if (!$fresh.Fresh) {
        Write-Warning "$Version build unavailable ($failure); cached $AssemblyName is stale ($($fresh.Reason))."
        return [pscustomobject]@{ Version = $Version; Usable = $false; Reused = $false }
    }
    Write-Warning "$Version build unavailable ($failure); reusing current $AssemblyName."
    return [pscustomobject]@{ Version = $Version; Usable = $true; Reused = $true }
}
