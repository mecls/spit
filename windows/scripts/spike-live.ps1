# Session 3: live transcription over real clips (prd-windows-parity.md rules 14-16, §5.4). Record the clips first,
# through the same capture a dictation uses:
#
#   windows/publish/Spit.exe --record-clips C:\spit-clips      (Enter to start, Enter to stop; 10+ clips of 10-30 s)
#   pwsh windows/scripts/spike-live.ps1 -Clips C:\spit-clips
#   pwsh windows/scripts/spike-live.ps1 -Clips C:\spit-clips -OutputDir C:\temp\live-after   (task 5.6, after tuning)
#
# Every clip is replayed through the smoke test's live path — a StreamingSession fed in real time, the tail pass, the
# stitch — with --warm-pass, one report per clip. It prints rule 15's per-clip table and verdict, and writes
# live-texts.md holding the stream's own text, the tail's text and the one-pass text of every clip that missed:
# the reading task 5.3 asks for before any constant is touched (rule 16).
param(
    [Parameter(Mandatory)] [string]$Clips,
    [string]$Exe = '',
    [string]$OutputDir = '',
    # Blank: the app's default (ModelCatalog.DefaultFile), i.e. whatever S1 settled.
    [string]$Model = '',
    # Passed through as SPIT_DATA_DIR. Leave blank to read the real %LOCALAPPDATA%\Miraside\Spit.
    [string]$DataDir = ''
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
if (-not $Exe) { $Exe = Join-Path $root 'windows/publish/Spit.exe' }
if (-not $OutputDir) { $OutputDir = Join-Path $Clips 'reports' }

# Rule 15's bars, restated so the verdict is checkable without the spec open.
$MinSeconds = 10
$MaxSeconds = 30
$MinClips = 10
$IdenticalShare = 0.8     # "character-identical on at least 8 of 10"
$MaxTimeRatio = 1.3       # "release to final text <= 1.3 x the one-pass time, on all 10"

# --- Preflight ------------------------------------------------------------------------------------------
$missing = @()
if (-not (Test-Path $Exe)) { $missing += "the app: $Exe (build it with: dotnet publish windows/Spit.App/Spit.App.csproj -c Release -r win-x64 --self-contained -o windows/publish)" }
$wavs = @()
if (-not (Test-Path $Clips)) { $missing += "the clips folder: $Clips (record with: Spit.exe --record-clips $Clips)" }
else {
    $wavs = @(Get-ChildItem $Clips -Filter *.wav | Sort-Object Name)
    if ($wavs.Count -eq 0) { $missing += "any .wav in $Clips (record with: Spit.exe --record-clips $Clips)" }
}
if ($missing.Count -gt 0) {
    Write-Output 'Session 3 cannot start. Missing:'
    $missing | ForEach-Object { Write-Output "  - $_" }
    throw "Session 3 cannot start: $($missing.Count) thing(s) missing, listed above."
}

New-Item -ItemType Directory -Force $OutputDir | Out-Null
Remove-Item Env:\SPIT_WHISPER_RUNTIME -ErrorAction SilentlyContinue   # what friends get: auto, Vulkan first
if ($DataDir) { $env:SPIT_DATA_DIR = $DataDir }
Write-Output "Session 3: $($wavs.Count) clips, each replayed in real time (a 30 s clip takes over 30 s). Reports: $OutputDir`n"

# --- Run ------------------------------------------------------------------------------------------------
function Get-Prop($object, [string]$name) {
    if ($null -eq $object) { return $null }
    $property = $object.PSObject.Properties[$name]
    if ($null -eq $property) { return $null }
    return $property.Value
}

$rows = @()
foreach ($wav in $wavs) {
    $report = Join-Path $OutputDir ($wav.BaseName + '.json')
    $arguments = @('--smoke-test', $wav.FullName, '--report', $report, '--warm-pass')
    if ($Model) { $arguments += @('--model', $Model) }
    $p = Start-Process -FilePath $Exe -PassThru -Wait -NoNewWindow -ArgumentList $arguments
    if (-not (Test-Path $report)) {
        Write-Warning "$($wav.Name) wrote no report (exit $($p.ExitCode))"
        continue
    }
    $r = Get-Content $report -Raw | ConvertFrom-Json
    # The smoke test's exit code checks for en.wav's words, which a real clip does not contain. What matters here is
    # that both texts exist; anything else is a failed run, shown with its error.
    $text = [string](Get-Prop $r 'text')
    $live = [string](Get-Prop $r 'streamedText')
    if ([string]::IsNullOrWhiteSpace($text) -or [string]::IsNullOrWhiteSpace($live)) {
        Write-Warning "$($wav.Name): no usable texts ($(Get-Prop $r 'error'))"
        continue
    }
    $audioMs = [int](Get-Prop $r 'audioMs')
    $streamMs = [int](Get-Prop $r 'streamMs')
    $warmMs = [int](Get-Prop $r 'warmTranscribeMs')
    # Character-identical, as rule 15 says, after trimming the ends only (Stitch trims them too).
    $identical = $live.Trim() -ceq $text.Trim()
    # Not the rule — a hint for 5.3: identical once case and punctuation are ignored means the seam was right.
    $norm = { param($t) (($t.ToLowerInvariant() -replace '[^\p{L}\p{N}\s]', '') -split '\s+' | Where-Object { $_ }) -join ' ' }
    $sameWords = (& $norm $live) -eq (& $norm $text)
    $ratio = if ($warmMs -gt 0) { $streamMs / $warmMs } else { [double]::PositiveInfinity }
    $inRange = $audioMs -ge $MinSeconds * 1000 -and $audioMs -le $MaxSeconds * 1000
    $rows += [pscustomobject]@{
        Clip = $wav.BaseName; AudioMs = $audioMs; WarmMs = $warmMs; StreamMs = $streamMs; Ratio = $ratio
        Identical = $identical; SameWords = $sameWords; WholePass = [bool](Get-Prop $r 'wholePassFallback')
        Segments = [int](Get-Prop $r 'streamSegments'); Counted = $inRange; Report = $r
    }
    Write-Output ("  {0}: {1,5:N1} s  one-pass {2,6} ms  live {3,6} ms  x{4:N2}  identical {5}  whole-pass {6}" -f `
        $wav.BaseName, ($audioMs / 1000), $warmMs, $streamMs, $ratio, $identical, [bool](Get-Prop $r 'wholePassFallback'))
}
Remove-Item Env:\SPIT_DATA_DIR -ErrorAction SilentlyContinue

# --- Table ----------------------------------------------------------------------------------------------
$table = @('| clip | audio s | one-pass ms (warm) | live ms | live / one-pass | identical | same words | whole-pass fallback | segments | counted |',
           '|---|---|---|---|---|---|---|---|---|---|')
foreach ($row in $rows) {
    $table += ('| {0} | {1:N1} | {2} | {3} | {4:N2} | {5} | {6} | {7} | {8} | {9} |' -f $row.Clip, ($row.AudioMs / 1000), $row.WarmMs,
        $row.StreamMs, $row.Ratio, $(if ($row.Identical) { 'yes' } else { '**no**' }), $(if ($row.SameWords) { 'yes' } else { 'no' }),
        $(if ($row.WholePass) { '**yes**' } else { 'no' }), $row.Segments, $(if ($row.Counted) { 'yes' } else { "no (outside $MinSeconds-$MaxSeconds s)" }))
}
Write-Output "`n$($table -join "`n")`n"

# --- Texts for task 5.3 ---------------------------------------------------------------------------------
$details = @('# Live transcription: the clips that missed', '',
             'For each clip whose live text differs from the one-pass text or fell back to a whole pass: what `StreamTail.Combine` was given.', '')
foreach ($row in $rows | Where-Object { -not $_.Identical -or $_.WholePass }) {
    $r = $row.Report
    $details += "## $($row.Clip) ($('{0:N1}' -f ($row.AudioMs / 1000)) s, stream covered $(Get-Prop $r 'streamCoveredMs') ms, overlap had speech: $(Get-Prop $r 'overlapHadSpeech'), whole-pass fallback: $($row.WholePass))"
    $details += ''
    $details += "- **stream, before the tail:** $(Get-Prop $r 'streamRawText')"
    $details += "- **tail pass:** $(Get-Prop $r 'tailText')"
    $details += "- **live result:** $(Get-Prop $r 'streamedText')"
    $details += "- **one-pass:** $(Get-Prop $r 'text')"
    $details += ''
}
$detailsPath = Join-Path $OutputDir 'live-texts.md'
Set-Content -Path $detailsPath -Value $details -Encoding utf8
Write-Output "Streamed, tail and one-pass texts of every miss: $detailsPath"

# --- Rule 15 --------------------------------------------------------------------------------------------
$counted = @($rows | Where-Object Counted)
$fallbacks = @($rows | Where-Object WholePass).Count
Write-Output "Task 5.2 baseline: $fallbacks of $($rows.Count) clips fell back to a whole-recording pass."
if ($counted.Count -lt $MinClips) {
    Write-Warning "Rule 15 has no verdict: only $($counted.Count) clips of $MinSeconds-$MaxSeconds s (it needs $MinClips). Record more with --record-clips."
    return
}
$identicalCount = @($counted | Where-Object Identical).Count
$needed = [int][Math]::Ceiling($IdenticalShare * $counted.Count)
$slow = @($counted | Where-Object { $_.Ratio -gt $MaxTimeRatio })
$textOk = $identicalCount -ge $needed
$timeOk = $slow.Count -eq 0
Write-Output "Rule 15, text: $identicalCount of $($counted.Count) clips character-identical to the one-pass (needs $needed) — $(if ($textOk) { 'HOLDS' } else { 'FAILS' })."
Write-Output "Rule 15, time: $($counted.Count - $slow.Count) of $($counted.Count) clips at or under $MaxTimeRatio x the warm one-pass$(if ($slow.Count) { " (over: $(($slow | ForEach-Object { '{0} x{1:N2}' -f $_.Clip, $_.Ratio }) -join ', '))" }) — $(if ($timeOk) { 'HOLDS' } else { 'FAILS' })."
if ($textOk -and $timeOk) {
    Write-Output 'Both hold: record this in docs/SPIKES.md and raise whether LiveTranscription should stay off by default — Miguel''s call (task 5.7).'
} else {
    Write-Output 'LiveTranscription stays false (rule 14). Read live-texts.md before touching Stitch or StreamTail.OverlapMs (rule 16, task 5.3).'
}
