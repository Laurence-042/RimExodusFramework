set windows-shell := ["powershell.exe", "-NoProfile", "-Command"]

default:
    @just --list

# Build the selected game version; defaults to 1.7.
build version="1.7":
    powershell.exe -NoProfile -File build/Build.ps1 -Version {{version}}

# Build when available, then package current 1.6 and 1.7 assemblies outside the repository.
release:
    powershell.exe -NoProfile -File build/Release.ps1

# CPU-only regression, with optional read-only sibling RimExodus composition checks.
test:
    powershell.exe -NoProfile -File build/Build.ps1 -Version 1.7 -Test

# Version-independent scheduling and generation coordination; requires SDK 8 or later.
test-services:
    dotnet run --project scripts/ServiceRegression/ServiceRegression.csproj -p:NuGetAudit=false
