$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$parent = Split-Path $root -Parent
$target = [IO.Path]::GetFullPath((Join-Path $parent 'RimExodusFramework.release'))
if ((Split-Path $target -Parent) -ne $parent -or (Split-Path $target -Leaf) -ne 'RimExodusFramework.release') {
    throw "Unexpected release destination: $target"
}
if ((Test-Path -LiteralPath $target) -and ((Get-Item -LiteralPath $target).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
    throw 'Refusing to replace a linked release directory.'
}

. "$PSScriptRoot/ReleaseBuild.ps1"
$results = @()
foreach ($version in @('1.6', '1.7')) {
    # Use the SDK already shared by the sibling projects for a 1.7 build.
    if ($version -eq '1.7' -and !$env:DOTNET_EXE) {
        $localSdk = Join-Path $parent 'RimExodus_Delta-V/.local-tools/dotnet/dotnet.exe'
        if (Test-Path -LiteralPath $localSdk) { $env:DOTNET_EXE = [IO.Path]::GetFullPath($localSdk) }
    }
    $results += Invoke-ReleaseVersionBuild -ProjectRoot $root -Version $version `
        -AssemblyName 'RimExodus.Maps.dll' -BuildScript "$PSScriptRoot/Build.ps1"
}
if (@($results | Where-Object { !$_.Usable }).Count -gt 0) {
    throw 'Release aborted: both current game-version assemblies are required.'
}

$about = New-Object System.Xml.XmlDocument
$about.PreserveWhitespace = $true
$about.Load((Join-Path $root 'About/About.xml'))
if ($about.ModMetaData.packageId -ne 'RimExodus.Framework.dev') {
    throw "Unexpected development packageId: $($about.ModMetaData.packageId)"
}
$about.ModMetaData.name = 'RimExodus Framework'
$about.ModMetaData.packageId = 'RimExodus.Framework'
$before = $about.ModMetaData.loadBefore.SelectSingleNode("li[.='RimExodus.DeltaV.dev']")
if ($null -eq $before) { throw 'Development Delta-V load order entry missing.' }
$before.InnerText = 'RimExodus.DeltaV'
if (($about.ModMetaData.supportedVersions.li -join ',') -ne '1.6,1.7') {
    throw 'Release metadata must support both 1.6 and 1.7.'
}

if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Recurse -Force }
New-Item -ItemType Directory -Path (Join-Path $target 'About') -Force | Out-Null
$about.Save((Join-Path $target 'About/About.xml'))

foreach ($file in @('Preview.png', 'PublishedFileId.txt')) {
    $source = Join-Path $root "About/$file"
    if (Test-Path -LiteralPath $source) { Copy-Item -LiteralPath $source -Destination (Join-Path $target 'About') }
}
foreach ($version in @('1.6', '1.7')) {
    $assemblyDir = Join-Path $target "$version/Assemblies"
    New-Item -ItemType Directory -Path $assemblyDir -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $root "$version/Assemblies/RimExodus.Maps.dll") -Destination $assemblyDir
}
Copy-Item -LiteralPath (Join-Path $root 'LoadFolders.xml') -Destination $target
Copy-Item -LiteralPath (Join-Path $root 'LICENSE') -Destination $target

foreach ($gameDir in @(
    $(if ($env:RimWorld16Dir) { $env:RimWorld16Dir } else { 'D:\SteamLibrary\steamapps\common\RimWorld_1.6' }),
    $(if ($env:RimWorld17Dir) { $env:RimWorld17Dir } else { 'D:\SteamLibrary\steamapps\common\RimWorld' })
)) {
    $mods = Join-Path $gameDir 'Mods'
    if (!(Test-Path -LiteralPath $mods -PathType Container)) {
        Write-Warning "Game Mods directory not found; release link skipped: $mods"
        continue
    }
    $link = Join-Path $mods 'RimExodusFramework.release'
    $existing = Get-Item -LiteralPath $link -Force -ErrorAction SilentlyContinue
    if ($null -ne $existing) {
        if ($existing.LinkType -ne 'SymbolicLink' -or $existing.Target -ne $target) {
            throw "Release link path is occupied by another entry: $link"
        }
        continue
    }
    New-Item -ItemType SymbolicLink -Path $link -Target $target | Out-Null
    Write-Output "Linked RimExodus Framework release at $link."
}
Write-Output "Packaged RimExodus Framework 1.6 and 1.7 at $target."
