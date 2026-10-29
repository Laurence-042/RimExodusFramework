param([ValidateSet('1.6','1.7')][string]$Version='1.7', [switch]$Test)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$sdk = if ($env:DOTNET_EXE) { $env:DOTNET_EXE } else { 'dotnet' }
Push-Location $root
try {
    if ($Test) {
        & $sdk run --project scripts/MapCoreRegression -p:RimWorldVersion=1.7 -p:NuGetAudit=false
    } else {
        & $sdk build Source/RimExodus.Maps.csproj -c Release "-p:RimWorldVersion=$Version" -p:NuGetAudit=false
    }
    if ($LASTEXITCODE -ne 0) { throw "REF build/test failed ($LASTEXITCODE)." }
} finally { Pop-Location }
