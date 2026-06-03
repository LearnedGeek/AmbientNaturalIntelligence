#!/usr/bin/env pwsh
<#
.SYNOPSIS
  Diff a bare-pipeline scenario sweep against a prod-pipeline scenario sweep,
  surfacing per-turn divergences in reply content, SafeAck behavior, gate
  firings, and memory activity.

.DESCRIPTION
  Sibling to tools/anichat-sweep.ps1 (bare) and tools/anichat-sweep-prod.ps1
  (prod). Reads transcript JSON files from each run, matches scenarios by
  name, and produces a markdown comparison report.

  The methodology question this tool answers: "what does the production
  runtime do to the bare-persona output?" — quantified per turn across
  every scenario.

.PARAMETER BareRun
  Path to a bare-pipeline results directory (tools/scenarios/results/<timestamp>/).

.PARAMETER ProdRun
  Path to a prod-pipeline results directory (tools/scenarios/results-prod/<timestamp>/).

.PARAMETER OutputDir
  Where to write the diff report. Default: tools/scenarios/results-diffs/<timestamp>/.

.EXAMPLE
  ./tools/anichat-diff.ps1 -BareRun tools/scenarios/results/20260601-210505 -ProdRun tools/scenarios/results-prod/20260602-181634
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$BareRun,

    [Parameter(Mandatory)]
    [string]$ProdRun,

    [string]$OutputDir
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $BareRun)) {
    Write-Error "Bare run dir not found: $BareRun"
    exit 1
}
if (-not (Test-Path $ProdRun)) {
    Write-Error "Prod run dir not found: $ProdRun"
    exit 1
}

if (-not $OutputDir) {
    $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $OutputDir = "tools/scenarios/results-diffs/$stamp"
}

New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null

# ── Discover scenarios ─────────────────────────────────────────────────
function Get-ScenariosInDir([string]$dir) {
    Get-ChildItem -Path $dir -Filter "*.transcript.json" -File | ForEach-Object {
        # Strip ".transcript.json" suffix
        $_.Name -replace '\.transcript\.json$', ''
    }
}

$bareScenarios = @(Get-ScenariosInDir $BareRun)
$prodScenarios = @(Get-ScenariosInDir $ProdRun)

$bothScenarios = @($bareScenarios | Where-Object { $prodScenarios -contains $_ })
$bareOnly = @($bareScenarios | Where-Object { $prodScenarios -notcontains $_ })
$prodOnly = @($prodScenarios | Where-Object { $bareScenarios -notcontains $_ })

Write-Host "Bare run:  $BareRun" -ForegroundColor DarkGray
Write-Host "Prod run:  $ProdRun" -ForegroundColor DarkGray
Write-Host "Scenarios in both: $($bothScenarios.Count); bare-only: $($bareOnly.Count); prod-only: $($prodOnly.Count)" -ForegroundColor DarkGray

# ── Helpers ────────────────────────────────────────────────────────────
function Read-Transcript([string]$dir, [string]$name) {
    $path = Join-Path $dir "$name.transcript.json"
    if (-not (Test-Path $path)) { return $null }
    return Get-Content $path -Raw -Encoding UTF8 | ConvertFrom-Json
}

function Get-TurnPairs($transcript) {
    # Transcript is a flat list of {role, content, ...}. Group into (user, assistant) pairs by adjacency.
    $pairs = [System.Collections.ArrayList]@()
    $current = $null
    foreach ($entry in $transcript) {
        if ($entry.role -eq 'user') {
            if ($current) { [void]$pairs.Add($current) }
            $current = [pscustomobject]@{ User = $entry; Assistant = $null }
        } elseif ($entry.role -eq 'assistant' -and $current) {
            $current.Assistant = $entry
        }
    }
    if ($current) { [void]$pairs.Add($current) }
    return $pairs
}

function Test-IsSafeAck($asstEntry) {
    if (-not $asstEntry) { return $true }
    $reply = $asstEntry.content
    if (-not $reply -or $reply.Trim().Length -eq 0) { return $true }
    # Heuristic: hedge phrases that production uses when the gate cascade
    # remediated all candidates and fell through to a stub. Treat as a
    # soft-SafeAck for divergence counting.
    $hedgePatterns = @(
        'give me a second to gather my thoughts',
        'sorry — give me a second',
        'let me think about that'
    )
    foreach ($p in $hedgePatterns) {
        if ($reply.ToLower().Contains($p.ToLower())) { return $true }
    }
    return $false
}

function Compare-Reply([string]$bare, [string]$prod) {
    if ($null -eq $bare -and $null -eq $prod) { return 'both-empty' }
    if ($null -eq $bare) { return 'bare-only-empty' }
    if ($null -eq $prod) { return 'prod-only-empty' }
    if ($bare -eq $prod) { return 'identical' }
    # Crude similarity check: length ratio + 8-char head match
    $bLen = $bare.Length; $pLen = $prod.Length
    if ($bLen -gt 0 -and $pLen -gt 0) {
        $ratio = [Math]::Min($bLen, $pLen) / [Math]::Max($bLen, $pLen)
        $headMatch = ($bare.Substring(0, [Math]::Min(20, $bLen)) -eq $prod.Substring(0, [Math]::Min(20, $pLen)))
        if ($ratio -gt 0.8 -and $headMatch) { return 'similar' }
    }
    return 'different'
}

# ── Per-scenario diff ──────────────────────────────────────────────────
$sweepSummary = @()

foreach ($name in $bothScenarios) {
    $bareT = Read-Transcript $BareRun $name
    $prodT = Read-Transcript $ProdRun $name

    if (-not $bareT -or -not $prodT) {
        Write-Host "Skipping $name — one side has empty transcript" -ForegroundColor Yellow
        continue
    }

    $barePairs = @(Get-TurnPairs $bareT)
    $prodPairs = @(Get-TurnPairs $prodT)
    $turnCount = [Math]::Max($barePairs.Count, $prodPairs.Count)

    $turnDiffs = [System.Collections.ArrayList]@()
    $bareSafeAcks = 0
    $prodSafeAcks = 0
    $prodRemediationsTotal = 0
    $prodMemoryInsertsTotal = 0
    $verdictCounts = @{ identical = 0; similar = 0; different = 0; 'bare-only-empty' = 0; 'prod-only-empty' = 0; 'both-empty' = 0 }

    # Phase E.1 per-handler tally (scenario-scoped). Keyed by handler name;
    # value is a hashtable of result counts (each Result string seen, e.g.
    # "Pass", "Remediate", "Reject"). Result strings emitted verbatim from
    # the Theme O.2 telemetry so the table reflects whatever the runtime
    # actually labels them — no enum guessing here.
    $scenarioHandlerTally = @{}

    for ($i = 0; $i -lt $turnCount; $i++) {
        $bp = if ($i -lt $barePairs.Count) { $barePairs[$i] } else { $null }
        $pp = if ($i -lt $prodPairs.Count) { $prodPairs[$i] } else { $null }

        $bareReply = if ($bp -and $bp.Assistant) { $bp.Assistant.content } else { $null }
        $prodReply = if ($pp -and $pp.Assistant) { $pp.Assistant.content } else { $null }
        $userMsg = if ($bp -and $bp.User) { $bp.User.content } elseif ($pp -and $pp.User) { $pp.User.content } else { '(missing)' }

        $bareSafeAck = if ($bp) { Test-IsSafeAck $bp.Assistant } else { $true }
        $prodSafeAck = if ($pp) { Test-IsSafeAck $pp.Assistant } else { $true }

        if ($bareSafeAck) { $bareSafeAcks++ }
        if ($prodSafeAck) { $prodSafeAcks++ }

        $verdict = Compare-Reply $bareReply $prodReply
        $verdictCounts[$verdict]++

        $prodRemediations = @()
        $prodMemoryInserts = 0
        $prodGateRuns = @()
        if ($pp -and $pp.Assistant) {
            if ($pp.Assistant.remediations) { $prodRemediations = @($pp.Assistant.remediations) }
            if ($pp.Assistant.memoryInsertCount) { $prodMemoryInserts = [int]$pp.Assistant.memoryInsertCount }
            if ($pp.Assistant.gateRuns) { $prodGateRuns = @($pp.Assistant.gateRuns) }
        }
        $prodRemediationsTotal += $prodRemediations.Count
        $prodMemoryInsertsTotal += $prodMemoryInserts

        # Roll handler-verdict counts into the scenario tally so we can emit
        # a per-invariant rollup at the sweep summary level. Counts each
        # handler invocation (not each pipeline-final result) because the
        # J.5h scope question is "which invariants over-fire on which reply
        # shapes" — that's an invocation-level signal, not a final-verdict
        # one. Short-circuited pipelines naturally produce fewer counts on
        # downstream handlers, which is itself the interesting signal.
        foreach ($run in $prodGateRuns) {
            if (-not $run.HandlerVerdicts) { continue }
            foreach ($v in $run.HandlerVerdicts) {
                $handler = if ($v.Handler) { $v.Handler } else { '(unknown)' }
                $resultKey = if ($v.Result) { $v.Result } else { '(unknown)' }
                if (-not $scenarioHandlerTally.ContainsKey($handler)) {
                    $scenarioHandlerTally[$handler] = @{}
                }
                if (-not $scenarioHandlerTally[$handler].ContainsKey($resultKey)) {
                    $scenarioHandlerTally[$handler][$resultKey] = 0
                }
                $scenarioHandlerTally[$handler][$resultKey]++
            }
        }

        [void]$turnDiffs.Add([pscustomobject]@{
            Index = $i + 1
            User = $userMsg
            BareReply = $bareReply
            ProdReply = $prodReply
            Verdict = $verdict
            BareSafeAck = $bareSafeAck
            ProdSafeAck = $prodSafeAck
            ProdRemediations = $prodRemediations
            ProdMemoryInserts = $prodMemoryInserts
            ProdGateRuns = $prodGateRuns
        })
    }

    # Markdown per scenario
    $mdPath = Join-Path $OutputDir "$name.diff.md"
    $md = "# $name — bare vs prod diff`n`n"
    $md += "**Bare run:** ``$BareRun```n`n"
    $md += "**Prod run:** ``$ProdRun```n`n"
    $md += "**Turn count:** $turnCount (bare=$($barePairs.Count), prod=$($prodPairs.Count))`n`n"
    $md += "**Verdict counts:** "
    $md += (($verdictCounts.GetEnumerator() | Where-Object { $_.Value -gt 0 } | ForEach-Object { "$($_.Key)=$($_.Value)" }) -join ', ')
    $md += "`n`n"
    $md += "**SafeAcks:** bare=$bareSafeAcks, prod=$prodSafeAcks`n`n"
    $md += "**Prod gate remediations (total):** $prodRemediationsTotal`n`n"
    $md += "**Prod memory inserts (total):** $prodMemoryInsertsTotal`n`n"
    $md += "---`n`n"

    foreach ($td in $turnDiffs) {
        $md += "### Turn $($td.Index) — verdict: ``$($td.Verdict)```n`n"
        $md += "**Mark:** $($td.User)`n`n"

        $md += "**Bare:**`n`n"
        if ($td.BareReply) {
            $md += "> $($td.BareReply -replace "`n", "`n> ")`n`n"
        } else {
            $md += "_(no reply)_`n`n"
        }
        if ($td.BareSafeAck) { $md += "_bare: SafeAck-shaped_`n`n" }

        $md += "**Prod:**`n`n"
        if ($td.ProdReply) {
            $md += "> $($td.ProdReply -replace "`n", "`n> ")`n`n"
        } else {
            $md += "_(no reply)_`n`n"
        }
        if ($td.ProdSafeAck) { $md += "_prod: SafeAck-shaped_`n`n" }

        if ($td.ProdRemediations.Count -gt 0) {
            $md += "**Prod gate firings this turn:**`n"
            foreach ($r in $td.ProdRemediations) {
                $md += "- ``$r```n"
            }
            $md += "`n"
        }
        if ($td.ProdMemoryInserts -gt 0) {
            $md += "**Prod memory inserts this turn:** $($td.ProdMemoryInserts)`n`n"
        }

        # Phase E.1 per-stage gate cascade. One block per producer pipeline
        # (inner-thought, reply-composition, etc.). Truncate Details to
        # keep markdown readable — the full text lives in the transcript
        # JSON if anyone needs it for forensics.
        if ($td.ProdGateRuns -and $td.ProdGateRuns.Count -gt 0) {
            foreach ($run in $td.ProdGateRuns) {
                $producerLabel = if ($run.Producer) { $run.Producer } else { '(unknown producer)' }
                $modeLabel = if ($run.Mode) { $run.Mode } else { 'Full' }
                $md += "**Gate cascade on prod reply — producer: ``$producerLabel`` (mode: $modeLabel):**`n"
                if (-not $run.HandlerVerdicts -or @($run.HandlerVerdicts).Count -eq 0) {
                    $md += "_(no handler verdicts captured — pipeline may have short-circuited before any handler ran)_`n"
                } else {
                    foreach ($v in $run.HandlerVerdicts) {
                        $handler = if ($v.Handler) { $v.Handler } else { '(unknown)' }
                        $result = if ($v.Result) { $v.Result } else { '(unknown)' }
                        $duration = if ($v.DurationMs) { "$($v.DurationMs)ms" } else { '' }
                        $details = if ($v.Details) { $v.Details } else { '' }
                        # Single-line for the table-style list; truncate
                        # details to ~120 chars and collapse newlines.
                        $detailsClean = ($details -replace "[`r`n]+", ' ').Trim()
                        if ($detailsClean.Length -gt 120) {
                            $detailsClean = $detailsClean.Substring(0, 117) + '...'
                        }
                        $detailsPart = if ($detailsClean) { ": $detailsClean" } else { '' }
                        $md += "- ``$handler`` — **$result** ($duration)$detailsPart`n"
                    }
                }
                $finalReason = ''
                if ($run.ShortCircuitHandler) {
                    $reasonClean = if ($run.ShortCircuitReason) { ($run.ShortCircuitReason -replace "[`r`n]+", ' ').Trim() } else { '' }
                    if ($reasonClean.Length -gt 160) { $reasonClean = $reasonClean.Substring(0, 157) + '...' }
                    $finalReason = " — short-circuited at ``$($run.ShortCircuitHandler)``"
                    if ($reasonClean) { $finalReason += " ($reasonClean)" }
                }
                $finalResult = if ($run.FinalResult) { $run.FinalResult } else { '(unknown)' }
                # NB: PowerShell treats _ as a valid identifier char, so a
                # bare $finalReason_ at end-of-string is parsed as one
                # variable and eats the closing italic underscore. Wrap the
                # expression in $() to terminate the variable name.
                $md += "_Pipeline outcome: **$finalResult**$($finalReason)_`n`n"
            }
        }
    }

    Set-Content -Path $mdPath -Value $md -Encoding UTF8

    $sweepSummary += [pscustomobject]@{
        Scenario = $name
        Turns = $turnCount
        Identical = $verdictCounts['identical']
        Similar = $verdictCounts['similar']
        Different = $verdictCounts['different']
        BareSafeAcks = $bareSafeAcks
        ProdSafeAcks = $prodSafeAcks
        ProdRemediations = $prodRemediationsTotal
        ProdMemoryInserts = $prodMemoryInsertsTotal
        HandlerTally = $scenarioHandlerTally
    }
}

# ── Sweep summary ────────────────────────────────────────────────────
$summaryPath = Join-Path $OutputDir "_sweep-summary.md"
$summaryMd = "# Diff sweep summary`n`n"
$summaryMd += "**Generated:** $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss zzz')`n`n"
$summaryMd += "**Bare run:** ``$BareRun```n`n"
$summaryMd += "**Prod run:** ``$ProdRun```n`n"
$summaryMd += "**Scenarios diffed:** $($bothScenarios.Count)  •  bare-only: $($bareOnly.Count)  •  prod-only: $($prodOnly.Count)`n`n"
$summaryMd += "---`n`n"
$summaryMd += "## Per-scenario rollup`n`n"
$summaryMd += "| Scenario | Turns | Identical | Similar | Different | Bare SafeAcks | Prod SafeAcks | Prod Remediations | Prod Memory Inserts |`n"
$summaryMd += "|---|---:|---:|---:|---:|---:|---:|---:|---:|`n"
foreach ($s in $sweepSummary) {
    $summaryMd += "| $($s.Scenario) | $($s.Turns) | $($s.Identical) | $($s.Similar) | $($s.Different) | $($s.BareSafeAcks) | $($s.ProdSafeAcks) | $($s.ProdRemediations) | $($s.ProdMemoryInserts) |`n"
}
$summaryMd += "`n"

if ($bareOnly.Count -gt 0) {
    $summaryMd += "## Bare-only scenarios (not in prod run)`n`n"
    foreach ($n in $bareOnly) { $summaryMd += "- ``$n```n" }
    $summaryMd += "`n"
}
if ($prodOnly.Count -gt 0) {
    $summaryMd += "## Prod-only scenarios (not in bare run)`n`n"
    foreach ($n in $prodOnly) { $summaryMd += "- ``$n```n" }
    $summaryMd += "`n"
}

# ── Per-invariant verdict matrix (sweep-wide) ────────────────────────
# Cross-scenario rollup of per-handler verdict counts. This is the
# J.5h scope-decision signal: which handlers fire on what fraction of
# invocations. A handler with a high Remediate/Reject rate over a large
# sample is the candidate for retirement or rewrite under Phase J.5h
# (`docs/spec/ANI-Theme-J-Guard-Consistency-Refactor-Plan.md`).
$globalTally = @{}
$allResultKeys = New-Object 'System.Collections.Generic.HashSet[string]'
foreach ($s in $sweepSummary) {
    if (-not $s.HandlerTally) { continue }
    foreach ($handler in $s.HandlerTally.Keys) {
        if (-not $globalTally.ContainsKey($handler)) {
            $globalTally[$handler] = @{}
        }
        foreach ($resultKey in $s.HandlerTally[$handler].Keys) {
            [void]$allResultKeys.Add($resultKey)
            if (-not $globalTally[$handler].ContainsKey($resultKey)) {
                $globalTally[$handler][$resultKey] = 0
            }
            $globalTally[$handler][$resultKey] += $s.HandlerTally[$handler][$resultKey]
        }
    }
}

if ($globalTally.Count -gt 0) {
    # Sort result-key columns with Pass first (if present) so non-pass
    # signal is visually grouped on the right where the eye lands.
    $sortedResults = @($allResultKeys) | Sort-Object @{
        Expression = { if ($_ -ieq 'Pass') { 0 } elseif ($_ -ieq 'Continue') { 1 } else { 2 } }
    }, @{Expression = { $_ }}

    $summaryMd += "## Per-invariant verdict matrix (Phase E.1)`n`n"
    $summaryMd += "Counts per handler invocation across every turn in every scenario. "
    $summaryMd += "**Non-pass rate** is the share of invocations that returned a non-``Pass`` result; "
    $summaryMd += "high values flag handlers over-firing on the trained-model's output distribution and are the candidates for J.5h retirement review.`n`n"

    $header = "| Handler | " + (($sortedResults | ForEach-Object { $_ }) -join ' | ') + " | Total | Non-pass rate |`n"
    $sep = "|---|" + (($sortedResults | ForEach-Object { '---:' }) -join '|') + "|---:|---:|`n"
    $summaryMd += $header
    $summaryMd += $sep

    # Sort handlers by non-pass-rate desc so the loudest ones come first.
    $handlerRows = foreach ($handler in $globalTally.Keys) {
        $counts = $globalTally[$handler]
        $total = 0
        foreach ($k in $counts.Keys) { $total += $counts[$k] }
        $passCount = 0
        if ($counts.ContainsKey('Pass')) { $passCount = $counts['Pass'] }
        $nonPassRate = if ($total -gt 0) { 1.0 - ($passCount / $total) } else { 0.0 }
        [pscustomobject]@{
            Handler = $handler
            Counts = $counts
            Total = $total
            NonPassRate = $nonPassRate
        }
    }
    $handlerRows = $handlerRows | Sort-Object -Property NonPassRate -Descending

    foreach ($row in $handlerRows) {
        $cells = foreach ($r in $sortedResults) {
            if ($row.Counts.ContainsKey($r)) { $row.Counts[$r] } else { 0 }
        }
        $rateDisplay = "{0:P0}" -f $row.NonPassRate
        $summaryMd += "| ``$($row.Handler)`` | " + ($cells -join ' | ') + " | $($row.Total) | $rateDisplay |`n"
    }
    $summaryMd += "`n"
} else {
    $summaryMd += "## Per-invariant verdict matrix (Phase E.1)`n`n"
    $summaryMd += "_No handler verdicts captured. Run a sweep produced by an Eval CLI build that includes Phase E.1 (commit d44fc54 or later) to populate this section._`n`n"
}

Set-Content -Path $summaryPath -Value $summaryMd -Encoding UTF8

# ── Console ──────────────────────────────────────────────────────────
Write-Host ""
Write-Host "=== sweep summary ===" -ForegroundColor Yellow
$sweepSummary | Format-Table -AutoSize

Write-Host ""
Write-Host "Per-scenario diffs: $OutputDir" -ForegroundColor DarkGray
Write-Host "Top-level summary: $summaryPath" -ForegroundColor DarkGray
