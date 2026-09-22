# S1: the one-pass latency matrix — 2 runtimes x 3 models x 2 fixtures, each repeated, reported as medians
# (prd-windows-parity.md rules 4-6). Run from anywhere:
#
#   pwsh windows/scripts/spike-s1.ps1
#   pwsh windows/scripts/spike-s1.ps1 -Exe C:\path\Spit.exe -OutputDir C:\temp\s1 -Repetitions 3
#
# It writes one --report JSON per run, then prints a markdown table to paste into docs/SPIKES.md. Every
# Vulkan row carries the device string whisper.cpp named, because a Vulkan run that silently fell through to
# the CPU library looks exactly like a slow GPU (rule 5).
param(
    [string]$Exe = '',
    [string]$OutputDir = '',
    [int]$Repetitions = 3,
    # Passed through as SPIT_DATA_DIR. Leave blank to read the real %LOCALAPPDATA%\Miraside\Spit.
    [string]$DataDir = ''
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
if (-not $Exe) { $Exe = Join-Path $root 'windows/publish/Spit.exe' }
if (-not $OutputDir) { $OutputDir = Join-Path $root 'windows/spike-s1' }
if ($Repetitions -lt 2) { throw "rule 4 discards the first repetition, so -Repetitions must be at least 2 (got $Repetitions)" }

$models = @(
    @{ File = 'ggml-small-q8_0.bin';            Short = 'small-q8'   },
    @{ File = 'ggml-large-v3-turbo-q5_0.bin';   Short = 'turbo-q5'   },
    @{ File = 'ggml-large-v3-turbo.bin';        Short = 'turbo-full' }
)
$fixtures = @(
    @{ Path = Join-Path $root 'mac/Fixtures/en.wav';           Short = 'en' },
    @{ Path = Join-Path $root 'mac/Fixtures/pt-synthetic.wav'; Short = 'pt' }
)
$runtimes = @('cpu', 'vulkan')

# --- Preflight ------------------------------------------------------------------------------------------
# Every missing file is named at once. Finding the third model absent after 24 runs is how an evening is lost.
$modelsDir = if ($DataDir) { Join-Path $DataDir 'models' }
             else { Join-Path $env:LOCALAPPDATA 'Miraside\Spit\models' }

$missing = @()
if (-not (Test-Path $Exe)) { $missing += "the app: $Exe (build it with: dotnet publish windows/Spit.App/Spit.App.csproj -c Release -r win-x64 --self-contained -o windows/publish)" }
foreach ($f in $fixtures) { if (-not (Test-Path $f.Path)) { $missing += "the fixture: $($f.Path)" } }
foreach ($m in $models) {
    $path = Join-Path $modelsDir $m.File
    if (-not (Test-Path $path)) { $missing += "the model: $path (download all three from Settings before starting)" }
}
if ($missing.Count -gt 0) {
    # One line each: PowerShell's exception view collapses embedded newlines, and the whole point of
    # naming every missing file at once is that the list stays readable.
    Write-Output 'S1 cannot start. Missing:'
    $missing | ForEach-Object { Write-Output "  - $_" }
    throw "S1 cannot start: $($missing.Count) thing(s) missing, listed above."
}

New-Item -ItemType Directory -Force $OutputDir | Out-Null
Write-Output "S1: $($runtimes.Count) runtimes x $($models.Count) models x $($fixtures.Count) fixtures x $Repetitions reps = $($runtimes.Count * $models.Count * $fixtures.Count * $Repetitions) runs"
Write-Output "Reports: $OutputDir`n"

# --- Run ------------------------------------------------------------------------------------------------
$rows = @()
foreach ($fixture in $fixtures) {
    foreach ($model in $models) {
        foreach ($runtime in $runtimes) {
            $reports = @()
            for ($rep = 1; $rep -le $Repetitions; $rep++) {
                $report = Join-Path $OutputDir "$($fixture.Short)-$($model.Short)-$runtime-$rep.json"
                $env:SPIT_WHISPER_RUNTIME = $runtime
                if ($DataDir) { $env:SPIT_DATA_DIR = $DataDir }
                # Start-Process, not &, so the exit code is unambiguous and the window never steals focus.
                $p = Start-Process -FilePath $Exe -PassThru -Wait -NoNewWindow -ArgumentList @(
                    '--smoke-test', $fixture.Path, '--report', $report, '--model', $model.File)
                if (-not (Test-Path $report)) {
                    Write-Warning "$($fixture.Short)/$($model.Short)/$runtime rep $rep wrote no report (exit $($p.ExitCode))"
                    continue
                }
                $r = Get-Content $report -Raw | ConvertFrom-Json
                $reports += $r
                Write-Output ("  {0,-2} {1,-10} {2,-6} rep {3}: {4,6} ms   exit {5}" -f $fixture.Short, $model.Short, $runtime, $rep, $r.transcribeMs, $p.ExitCode)
            }
            $rows += @{ Fixture = $fixture.Short; Model = $model.Short; Runtime = $runtime; Reports = $reports }
        }
    }
}
Remove-Item Env:\SPIT_WHISPER_RUNTIME -ErrorAction SilentlyContinue

# --- Collate --------------------------------------------------------------------------------------------
function Get-Prop($object, [string]$name) {
    if ($null -eq $object) { return $null }
    $property = $object.PSObject.Properties[$name]
    if ($null -eq $property) { return $null }
    return $property.Value
}

function Get-Median([int[]]$values) {
    if ($values.Count -eq 0) { return $null }
    $sorted = $values | Sort-Object
    $middle = [int][Math]::Floor($sorted.Count / 2)
    if ($sorted.Count % 2 -eq 1) { return $sorted[$middle] }
    return [int][Math]::Round(($sorted[$middle - 1] + $sorted[$middle]) / 2.0)
}

# whisper.cpp prints the adapter it chose; without that line a "vulkan" run proves nothing (rule 5).
# The line it prints is: "ggml_vulkan: 0 = NVIDIA GeForce RTX 4060 (NVIDIA) | uma: 0 | fp16: 1 | ..."
function Get-VulkanDevice($report) {
    $log = Get-Prop $report 'backendLog'
    if ($null -eq $log) { return $null }
    foreach ($line in $log) {
        if ($line -match 'ggml_vulkan:\s*\d+\s*=\s*([^|]+)') { return $Matches[1].Trim() }
    }
    # If whisper.cpp ever reworded that line, say so rather than reporting no GPU: the two are very
    # different problems and only one of them means the run was wasted.
    foreach ($line in $log) {
        if ($line -match 'ggml_vulkan' -and $line -match '=') { return "(unrecognised) $($line.Trim())" }
    }
    return $null
}

$table = @('| fixture | model | runtime asked | runtime loaded | audio ms | kept timings (ms) | median ms | median / audio | Vulkan device |',
           '|---|---|---|---|---|---|---|---|---|')
$void = @()
foreach ($row in $rows) {
    # Rule 4: the first repetition carries model load and first-inference warm-up in the OS file cache.
    $kept = @($row.Reports | Select-Object -Skip 1)
    $times = @($kept | ForEach-Object { [int](Get-Prop $_ 'transcribeMs') })
    $median = Get-Median $times
    $audioMs = if ($kept.Count -gt 0) { [int](Get-Prop $kept[0] 'audioMs') } else { 0 }
    $loaded = if ($kept.Count -gt 0) { Get-Prop $kept[0] 'runtime' } else { '(no run)' }
    $device = if ($kept.Count -gt 0) { Get-VulkanDevice $kept[0] } else { $null }
    $ratio = if ($median -and $audioMs -gt 0) { '{0:N2}x' -f ($median / $audioMs) } else { '-' }

    # Rule 5, and the cause matters: a fall-through is a wasted run, a missing device line is a run whose
    # log has to be read by hand before it can be trusted.
    $note = ''
    if ($row.Runtime -eq 'vulkan') {
        if ($loaded -notmatch 'Vulkan') {
            $note = ' **VOID**'
            $void += "$($row.Fixture)/$($row.Model): asked vulkan, loaded '$loaded' — Whisper.net fell through to the CPU library"
        }
        elseif (-not $device) {
            $note = ' **VOID**'
            $void += "$($row.Fixture)/$($row.Model): loaded Vulkan but printed no device line — read the raw backendLog before trusting this number"
        }
    }
    $table += "| $($row.Fixture) | $($row.Model) | $($row.Runtime) | $loaded | $audioMs | $($times -join ', ') | $median$note | $ratio | $(if ($device) { $device } else { '—' }) |"
}

Write-Output "`n$($table -join "`n")`n"

# --- The two things this spike is actually for ----------------------------------------------------------
if ($void.Count -gt 0) {
    Write-Warning "Rule 5: these Vulkan rows are void, not slow — Whisper.net fell through to the CPU library. Fix the runtime resolution and re-run before recording anything:"
    $void | ForEach-Object { Write-Warning "  $_" }
}

# Rule 6: the default changes to turbo iff its median Vulkan time on en.wav is <= 0.25 x audio duration.
$verdictRow = $rows | Where-Object { $_.Fixture -eq 'en' -and $_.Model -eq 'turbo-q5' -and $_.Runtime -eq 'vulkan' } | Select-Object -First 1
if ($verdictRow -and $verdictRow.Reports.Count -gt 1) {
    $keptVerdict = @($verdictRow.Reports | Select-Object -Skip 1)
    $medianVerdict = Get-Median @($keptVerdict | ForEach-Object { [int](Get-Prop $_ 'transcribeMs') })
    $audioVerdict = [int](Get-Prop $keptVerdict[0] 'audioMs')
    $threshold = [int]($audioVerdict * 0.25)
    $clears = $medianVerdict -le $threshold
    Write-Output "Rule 6: ggml-large-v3-turbo-q5_0 on en.wav over Vulkan had a median of ${medianVerdict} ms against a threshold of ${threshold} ms (0.25 x ${audioVerdict} ms of audio) — it $(if ($clears) { 'CLEARS' } else { 'MISSES' }) the bar, so the Windows default $(if ($clears) { 'changes to turbo' } else { 'stays Whisper small' })."
    Write-Output "Paste that sentence into docs/SPIKES.md verbatim (task 3.4), then do task $(if ($clears) { '3.5' } else { '3.6' })."
} else {
    Write-Warning "Rule 6 has no verdict: the en/turbo-q5/vulkan cell produced fewer than 2 reports."
}
