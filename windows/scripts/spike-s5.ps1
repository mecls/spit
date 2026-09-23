# S5: installing 0.2.1 over 0.2.0 (prd-windows-parity.md §3.1 step 5). Snapshots what an upgrade must keep, before
# and after, and compares them:
#
#   1. Install windows/Releases/s5-0.2.0/Spit-Setup.exe. Open Spit, paste the token, keep Launch at login on, dictate once.
#   2. pwsh windows/scripts/spike-s5.ps1 -Snapshot before
#   3. Install windows/Releases/s5-0.2.1/Spit-Setup.exe over it.
#   4. pwsh windows/scripts/spike-s5.ps1 -Snapshot after      (prints the comparison and the verdict)
#
# The token is never read: only whether Credential Manager still holds its target. Nothing here writes anything
# but the two snapshot files.
param(
    [Parameter(Mandatory)] [ValidateSet('before', 'after')] [string]$Snapshot,
    [string]$OutputDir = ''
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
if (-not $OutputDir) { $OutputDir = Join-Path $root 'windows/spike-s5' }
New-Item -ItemType Directory -Force $OutputDir | Out-Null

# Where each thing lives: AppPaths.cs, TokenStore.DefaultTargetPrefix, LaunchAtLogin.RunKeyPath/DefaultValueName.
$install = Join-Path $env:LOCALAPPDATA 'Spit'
$data = Join-Path $env:LOCALAPPDATA 'Miraside\Spit'
$tokenPrefix = 'co.miraside.voice'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$runValue = 'Spit'

function Get-UninstallEntries {
    $keys = @('HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*',
              'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*',
              'HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*')
    @(foreach ($key in $keys) {
        Get-ItemProperty $key -ErrorAction SilentlyContinue |
            Where-Object { $_.PSObject.Properties['DisplayName'] -and $_.DisplayName -like '*Spit*' } |
            ForEach-Object {
                [pscustomobject]@{
                    Key = $_.PSChildName
                    DisplayName = $_.DisplayName
                    DisplayVersion = if ($_.PSObject.Properties['DisplayVersion']) { $_.DisplayVersion } else { '' }
                }
            }
    })
}

$exe = Join-Path $install 'current\Spit.exe'
$dataFiles = @(if (Test-Path $data) { Get-ChildItem $data -Recurse -File -ErrorAction SilentlyContinue })
$tokenTargets = @(cmdkey /list | Select-String -SimpleMatch $tokenPrefix | ForEach-Object { ($_.Line -replace '^\s*Target:\s*', '').Trim() })
$run = (Get-ItemProperty $runKey -Name $runValue -ErrorAction SilentlyContinue)
$runCommand = if ($run) { $run.$runValue } else { $null }

$state = [pscustomobject]@{
    Taken = (Get-Date).ToString('s')
    UninstallEntries = @(Get-UninstallEntries)
    AppVersion = if (Test-Path $exe) { (Get-Item $exe).VersionInfo.ProductVersion } else { $null }
    DataDirExists = Test-Path $data
    DataFileCount = $dataFiles.Count
    DataFiles = @($dataFiles | ForEach-Object { $_.FullName.Substring($data.Length + 1) } | Sort-Object)
    TokenTargets = $tokenTargets
    RunCommand = $runCommand
    RunTargetExists = if ($runCommand) { Test-Path (($runCommand -replace '^"([^"]+)".*$', '$1') -replace '^(\S+\.exe).*$', '$1') } else { $false }
    SpitProcesses = @(Get-Process Spit -ErrorAction SilentlyContinue).Count
}
$path = Join-Path $OutputDir "$Snapshot.json"
$state | ConvertTo-Json -Depth 5 | Set-Content -Path $path -Encoding utf8
Write-Output "S5 snapshot '$Snapshot' written to $path"
Write-Output ("  Installed Apps entries: {0}  app version: {1}  data files: {2}  token targets: {3}  Run entry: {4}" -f `
    $state.UninstallEntries.Count, $state.AppVersion, $state.DataFileCount, $state.TokenTargets.Count, $(if ($runCommand) { $runCommand } else { '(none)' }))

if ($Snapshot -eq 'before') {
    # A survival check on something that was never there proves nothing.
    if ($state.TokenTargets.Count -eq 0) { Write-Warning 'No stored token: paste one in Settings before upgrading, or "the token survived" means nothing.' }
    if (-not $runCommand) { Write-Warning 'No Run entry: turn Launch at login on before upgrading.' }
    if ($state.DataFileCount -eq 0) { Write-Warning 'The data folder is empty: dictate once before upgrading.' }
    return
}

# --- Compare --------------------------------------------------------------------------------------------
$beforePath = Join-Path $OutputDir 'before.json'
if (-not (Test-Path $beforePath)) { throw "No 'before' snapshot at $beforePath; take it before installing 0.2.1-test." }
$before = Get-Content $beforePath -Raw | ConvertFrom-Json

$lost = @($before.DataFiles | Where-Object { $state.DataFiles -notcontains $_ })
$checks = @(
    @{ Name = 'exactly one entry in Installed Apps'; Ok = $state.UninstallEntries.Count -eq 1; Seen = "$($state.UninstallEntries.Count) ($(($state.UninstallEntries | ForEach-Object { "$($_.DisplayName) $($_.DisplayVersion)" }) -join '; '))" },
    @{ Name = 'the app is the newer build'; Ok = "$($state.AppVersion)" -like '0.2.1*'; Seen = "before $($before.AppVersion), after $($state.AppVersion)" },
    @{ Name = 'the data directory survived'; Ok = $state.DataDirExists -and $lost.Count -eq 0; Seen = "$($before.DataFileCount) files before, $($state.DataFileCount) after$(if ($lost.Count) { "; gone: $($lost -join ', ')" })" },
    @{ Name = 'the stored token survived'; Ok = @($before.TokenTargets | Where-Object { $state.TokenTargets -notcontains $_ }).Count -eq 0 -and $state.TokenTargets.Count -gt 0; Seen = "$(@($before.TokenTargets).Count) target(s) before, $($state.TokenTargets.Count) after" },
    @{ Name = 'the Run entry survived and still points at a file'; Ok = [bool]$state.RunCommand -and $state.RunTargetExists; Seen = "before '$($before.RunCommand)', after '$($state.RunCommand)' (target exists: $($state.RunTargetExists))" }
)
$table = @('| check | result | what was seen |', '|---|---|---|')
foreach ($c in $checks) { $table += "| $($c.Name) | $(if ($c.Ok) { 'pass' } else { '**FAIL**' }) | $($c.Seen) |" }
Write-Output "`n$($table -join "`n")`n"
$table | Set-Content -Path (Join-Path $OutputDir 'table.md') -Encoding utf8
$failed = @($checks | Where-Object { -not $_.Ok })
if ($failed.Count -eq 0) {
    Write-Output 'S5 passes: paste the table into docs/SPIKES.md (task 2.15).'
} else {
    Write-Output "S5 fails ($($failed.Count) check(s)). Rule 2's branch applies: the /spit page tells users to uninstall first. Record the table in docs/SPIKES.md."
    exit 1
}
