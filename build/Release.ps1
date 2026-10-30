param([ValidateSet('1.6','1.7')][string]$Version='1.7')
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

# Use the SDK already shared by the sibling projects when the system SDK is older.
if (!$env:DOTNET_EXE -and $Version -eq '1.7') {
    $localSdk = Join-Path $parent 'RimExodus_Delta-V/.local-tools/dotnet/dotnet.exe'
    if (Test-Path -LiteralPath $localSdk) { $env:DOTNET_EXE = [IO.Path]::GetFullPath($localSdk) }
}
& "$PSScriptRoot/Build.ps1" -Version $Version

$assembly = Join-Path $root "$Version/Assemblies/RimExodus.Maps.dll"
if (!(Test-Path -LiteralPath $assembly)) { throw "Built assembly missing: $assembly" }

$about = New-Object System.Xml.XmlDocument
$about.PreserveWhitespace = $true
$about.Load((Join-Path $root 'About/About.xml'))
if ($about.ModMetaData.packageId -ne 'RimExodus.Framework.dev') {
    throw "Unexpected development packageId: $($about.ModMetaData.packageId)"
}
$about.ModMetaData.packageId = 'RimExodus.Framework'
$versions = $about.ModMetaData.supportedVersions
$versions.RemoveAll()
$versionNode = $about.CreateElement('li')
$versionNode.InnerText = $Version
[void]$versions.AppendChild($versionNode)

if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Recurse -Force }
New-Item -ItemType Directory -Path (Join-Path $target 'About') -Force | Out-Null
$assemblyDir = Join-Path $target "$Version/Assemblies"
New-Item -ItemType Directory -Path $assemblyDir -Force | Out-Null
$about.Save((Join-Path $target 'About/About.xml'))

foreach ($file in @('Preview.png', 'PublishedFileId.txt')) {
    $source = Join-Path $root "About/$file"
    if (Test-Path -LiteralPath $source) { Copy-Item -LiteralPath $source -Destination (Join-Path $target 'About') }
}
Copy-Item -LiteralPath $assembly -Destination $assemblyDir
Copy-Item -LiteralPath (Join-Path $root 'LoadFolders.xml') -Destination $target
Copy-Item -LiteralPath (Join-Path $root 'LICENSE') -Destination $target
Write-Output "Packaged RimExodus Framework $Version at $target."
