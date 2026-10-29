set windows-shell := ["pwsh", "-NoProfile", "-Command"]

default:
    @just --list

# Build the selected game version; defaults to 1.7.
build version="1.7":
    pwsh -NoProfile -File build/Build.ps1 -Version {{version}}

# CPU-only regression, with optional read-only sibling RimExodus composition checks.
test:
    pwsh -NoProfile -File build/Build.ps1 -Version 1.7 -Test
