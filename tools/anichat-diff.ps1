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
        if ($pp -and $pp.Assistant) {
            if ($pp.Assistant.remediations) { $prodRemediations = @($pp.Assistant.remediations) }
            if ($pp.Assistant.memoryInsertCount) { $prodMemoryInserts = [int]$pp.Assistant.memoryInsertCount }
        }
        $prodRemediationsTotal += $prodRemediations.Count
        $prodMemoryInsertsTotal += $prodMemoryInserts

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

Set-Content -Path $summaryPath -Value $summaryMd -Encoding UTF8

# ── Console ──────────────────────────────────────────────────────────
Write-Host ""
Write-Host "=== sweep summary ===" -ForegroundColor Yellow
$sweepSummary | Format-Table -AutoSize

Write-Host ""
Write-Host "Per-scenario diffs: $OutputDir" -ForegroundColor DarkGray
Write-Host "Top-level summary: $summaryPath" -ForegroundColor DarkGray
