# Publishes Spit.App self-contained for win-x64 and packs it with Velopack into windows/Releases/Spit-Setup.exe
# (prd-spit-mac-windows.md rules 8, 44). Run from anywhere: pwsh windows/scripts/pack.ps1
#
# S5 needs two installers that differ only in version, which is what -PackVersion and -OutputDir are for
# (prd-windows-parity.md rule 2, S5). Both default to today's behaviour, so CI and the release workflow are
# unaffected:
#
#   pwsh windows/scripts/pack.ps1 -PackVersion 0.2.0-test -OutputDir windows/Releases/s5-0.2.0
#   pwsh windows/scripts/pack.ps1 -PackVersion 0.2.1-test -OutputDir windows/Releases/s5-0.2.1
#
# Keep -OutputDir under windows/Releases/: that path is in windows/.gitignore, and a sibling directory is
# not — two 180 MB installers would otherwise show up as untracked files.
#
# -PackVersion never writes to the VERSION file: that file is checked against mac/project.yml by
# scripts/check-version.sh, and a `0.2.1-test` committed there would fail CI on both clients.
param(
    [string]$Configuration = 'Release',
    [string]$PackVersion = '',
    [string]$OutputDir = ''
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$version = if ($PackVersion) { $PackVersion } else { (Get-Content (Join-Path $root 'VERSION') -Raw).Trim() }
$publish = Join-Path $root 'windows/publish'
$out = if ($OutputDir) { [System.IO.Path]::GetFullPath($OutputDir, (Get-Location).Path) } else { Join-Path $root 'windows/Releases' }

Remove-Item -Recurse -Force $publish, $out -ErrorAction SilentlyContinue

# InformationalVersion is what `Coordinator.ClientVersion` reads and what goes on the wire, so an S5 build
# identifies itself. AssemblyVersion is left alone: Directory.Build.props appends '.0', and '0.2.0-test.0'
# is not a valid assembly version.
dotnet publish (Join-Path $root 'windows/Spit.App/Spit.App.csproj') -c $Configuration -r win-x64 --self-contained -o $publish -p:InformationalVersion=$version -p:EnableWindowsTargeting=true
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }
if (-not (Test-Path (Join-Path $publish 'Spit.exe'))) { throw 'publish produced no Spit.exe' }

# Pinned to the Velopack library version in Spit.App.csproj; `update` installs it when missing.
dotnet tool update --global vpk --version 1.2.0
if ($LASTEXITCODE -ne 0) { throw "installing vpk failed ($LASTEXITCODE)" }

vpk pack `
    --packId Spit `
    --packVersion $version `
    --packTitle Spit `
    --packAuthors Miraside `
    --packDir $publish `
    --mainExe Spit.exe `
    --icon (Join-Path $root 'windows/Spit.App/Assets/Spit.ico') `
    --outputDir $out
if ($LASTEXITCODE -ne 0) { throw "vpk pack failed ($LASTEXITCODE)" }

# Release assets carry no version and one fixed name, so latest/download URLs keep working (rule 8).
# Velopack may include the channel in the file name (Spit-win-Setup.exe); normalise it.
$setup = Get-ChildItem -Path $out -Filter '*Setup.exe' | Select-Object -First 1
if ($null -eq $setup) { throw 'vpk produced no Setup.exe' }
$target = Join-Path $out 'Spit-Setup.exe'
if ($setup.FullName -ne $target) { Move-Item -Force $setup.FullName $target }

Write-Output $target
