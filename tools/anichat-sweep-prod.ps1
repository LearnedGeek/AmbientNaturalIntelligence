#!/usr/bin/env pwsh
<#
.SYNOPSIS
  Run a JSON scenarios file through the PRODUCTION ANI Runtime pipeline
  via the AniRuntime.Eval CLI driver, against an isolated snapshot of
  ani-server's memory DB. Captures replies + gate telemetry per turn.

.DESCRIPTION
  Sibling to tools/anichat-sweep.ps1 (which hits Ollama directly with
  bare-pipeline persona). This wrapper drives the full production pipeline
  end-to-end: perception -> context -> inner thought -> desire -> reply
  composition -> J.5a invariants -> frontier verifier -> dispatch.

  Each scenario gets its own copy of the base snapshot so memory state
  persists across turns within a scenario but doesn't leak between
  scenarios. Production memory is NEVER touched — only the local snapshot
  copies see writes.

  Sweep flow:
    1. Snapshot ani-server's ani-memory.db once (or use --base-snapshot
       if you already have one)
    2. For each scenario: copy snapshot -> per-scenario eval DB
    3. For each turn in scenario: invoke AniRuntime.Eval against the
       per-scenario DB, capture the JSON output
    4. Write transcripts as JSON + markdown per scenario, plus a sweep
       summary

.PARAMETER ScenariosFile
  Path to JSON scenarios file (same shape as tools/scenarios/baseline.json).
  Default: tools/scenarios/baseline.json.

.PARAMETER BaseSnapshot
  Path to a pre-existing ani-memory.db snapshot. If omitted, this script
  takes a fresh snapshot from ani-server via SSH.

.PARAMETER OutputDir
  Where to write transcripts. Default: tools/scenarios/results-prod/<timestamp>/.

.PARAMETER OnlyScenario
  Run a single scenario by name.

.PARAMETER EvalExe
  Path to the AniRuntime.Eval binary. Default: tools/AniRuntime.Eval/bin/Release/net8.0/publish/AniRuntime.Eval.exe.
  If missing, the script publishes it.

.PARAMETER OllamaUrl
  Forwarded to the Eval driver. Default: http://ani-server:11434.

.EXAMPLE
  ./tools/anichat-sweep-prod.ps1
  ./tools/anichat-sweep-prod.ps1 -ScenariosFile tools/scenarios/baseline.json
  ./tools/anichat-sweep-prod.ps1 -BaseSnapshot E:/tmp/eval-snapshots/eval-snapshot-20260602-170928.db
  ./tools/anichat-sweep-prod.ps1 -OnlyScenario baseline-day
#>

[CmdletBinding()]
param(
    [string]$ScenariosFile = "tools/scenarios/baseline.json",
    [string]$BaseSnapshot,
    [string]$OutputDir,
    [string]$OnlyScenario,
    [string]$EvalExe = "tools/AniRuntime.Eval/bin/Release/net8.0/publish/AniRuntime.Eval.exe",
    [string]$OllamaUrl = "http://ani-server:11434",
    [int]$TurnTimeoutSeconds = 300
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $ScenariosFile)) {
    Write-Error "Scenarios file not found: $ScenariosFile"
    exit 1
}

if (-not (Test-Path $EvalExe)) {
    Write-Host "Eval binary not found at $EvalExe — publishing..." -ForegroundColor Yellow
    dotnet publish tools/AniRuntime.Eval/AniRuntime.Eval.csproj --nologo --verbosity quiet -c Release | Out-Null
    if (-not (Test-Path $EvalExe)) {
        Write-Error "Publish failed; eval binary still not at $EvalExe"
        exit 1
    }
}

if (-not $OutputDir) {
    $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $OutputDir = Join-Path (Split-Path $ScenariosFile -Parent) "results-prod/$stamp"
}

New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null

# ── Base snapshot ──────────────────────────────────────────────────────
if (-not $BaseSnapshot) {
    $snapshotStamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $remoteSnap = "C:/dev/ani-data/backups/eval-snapshot-$snapshotStamp.db"
    $BaseSnapshot = "/e/tmp/eval-snapshots/eval-snapshot-$snapshotStamp.db"

    Write-Host "Snapshotting ani-server -> $BaseSnapshot" -ForegroundColor Cyan
    New-Item -ItemType Directory -Path (Split-Path $BaseSnapshot -Parent) -Force | Out-Null

    ssh ani-server "C:\Tools\sqlite\sqlite3.exe C:/dev/ani-data/ani-memory.db `".backup $remoteSnap`""
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Remote snapshot failed."
        exit 1
    }

    scp "ani-server:$remoteSnap" $BaseSnapshot
    if ($LASTEXITCODE -ne 0) {
        Write-Error "scp failed."
        exit 1
    }
    Write-Host "Snapshot ready: $BaseSnapshot" -ForegroundColor Green
} elseif (-not (Test-Path $BaseSnapshot)) {
    Write-Error "Base snapshot does not exist: $BaseSnapshot"
    exit 1
} else {
    Write-Host "Using existing snapshot: $BaseSnapshot" -ForegroundColor DarkGray
}

# ── Load scenarios ─────────────────────────────────────────────────────
$rawJson = Get-Content $ScenariosFile -Raw -Encoding UTF8
$scenarios = @($rawJson | ConvertFrom-Json)
if (-not $scenarios -or $scenarios.Count -eq 0) {
    Write-Error "No scenarios found in $ScenariosFile"
    exit 1
}

if ($OnlyScenario) {
    $scenarios = @($scenarios | Where-Object { $_.name -eq $OnlyScenario })
    if ($scenarios.Count -eq 0) {
        Write-Error "Scenario '$OnlyScenario' not found"
        exit 1
    }
}

$summary = @()

foreach ($scenario in $scenarios) {
    Write-Host ""
    Write-Host "=== $($scenario.name) ===" -ForegroundColor Cyan
    if ($scenario.description) {
        Write-Host $scenario.description -ForegroundColor DarkGray
    }

    # Per-scenario DB copy — isolation between scenarios; memory state
    # persists across turns WITHIN this scenario.
    $scenarioDb = Join-Path $OutputDir "$($scenario.name).db"
    Copy-Item $BaseSnapshot $scenarioDb -Force

    $transcript = [System.Collections.ArrayList]@()
    $aborted = $false
    $turnsCompleted = 0
    $totalCapturedReplies = 0
    $totalSafeAcks = 0

    foreach ($userTurn in $scenario.turns) {
        Write-Host ""
        Write-Host "user: $userTurn" -ForegroundColor White

        # Invoke Eval CLI. Output is JSON on stdout; logs go to stderr.
        # We capture stdout only via redirection.
        $stdoutPath = [System.IO.Path]::GetTempFileName()
        $stderrPath = [System.IO.Path]::GetTempFileName()
        try {
            $proc = Start-Process -FilePath $EvalExe `
                -ArgumentList @(
                    "--db-path", $scenarioDb,
                    "--message", $userTurn,
                    "--ollama-url", $OllamaUrl
                ) `
                -NoNewWindow -Wait -PassThru `
                -RedirectStandardOutput $stdoutPath `
                -RedirectStandardError $stderrPath

            if ($proc.ExitCode -ne 0) {
                Write-Host "  (eval exit code $($proc.ExitCode))" -ForegroundColor Red
            }

            $rawOut = Get-Content $stdoutPath -Raw -Encoding UTF8
            $rawErr = Get-Content $stderrPath -Raw -Encoding UTF8

            $evalJson = $null
            try {
                $evalJson = $rawOut | ConvertFrom-Json
            } catch {
                Write-Host "  (failed to parse eval JSON output)" -ForegroundColor Red
                $aborted = $true
                break
            }

            $reply = $null
            if ($evalJson.CapturedReplies -and $evalJson.CapturedReplies.Count -gt 0) {
                $reply = $evalJson.CapturedReplies[0].Message
                $totalCapturedReplies += $evalJson.CapturedReplies.Count
                Write-Host "ani: $reply" -ForegroundColor Cyan
            } else {
                Write-Host "ani: (no reply dispatched — possible SafeAck)" -ForegroundColor Yellow
                $totalSafeAcks++
            }

            # Pull any J.5a remediation lines from stderr (info-level logs)
            $remediations = @()
            if ($rawErr) {
                $matches = [regex]::Matches($rawErr, "J\.5[ah] gate Remediate[^\n]+", "IgnoreCase")
                foreach ($m in $matches) {
                    $remediations += $m.Value.Trim()
                }
            }

            [void]$transcript.Add([pscustomobject]@{
                role = "user"
                content = $userTurn
            })
            [void]$transcript.Add([pscustomobject]@{
                role = "assistant"
                content = $reply
                cycleCompleted = [bool]$evalJson.CycleCompleted
                evalError = $evalJson.Error
                remediations = $remediations
                syntheticSid = $evalJson.SyntheticSid
            })
            $turnsCompleted++
        }
        finally {
            Remove-Item $stdoutPath -ErrorAction SilentlyContinue
            Remove-Item $stderrPath -ErrorAction SilentlyContinue
        }
    }

    # ── Save transcripts ────────────────────────────────────────────────
    $transcriptJson = Join-Path $OutputDir "$($scenario.name).transcript.json"
    ConvertTo-Json -InputObject @($transcript) -Depth 10 | Set-Content -Path $transcriptJson -Encoding UTF8

    $transcriptMd = Join-Path $OutputDir "$($scenario.name).transcript.md"
    $md = "# $($scenario.name) (prod-pipeline)`n`n"
    if ($scenario.description) { $md += "_$($scenario.description)_`n`n" }
    if ($aborted) { $md += "> **Aborted after $turnsCompleted turn(s).**`n`n" }
    $md += "---`n`n"
    for ($i = 0; $i -lt $transcript.Count; $i += 2) {
        $u = $transcript[$i]
        $a = if ($i + 1 -lt $transcript.Count) { $transcript[$i + 1] } else { $null }
        $md += "**Mark:** $($u.content)`n`n"
        if ($a) {
            if ($a.content) {
                $md += "**Ani:** $($a.content)`n`n"
            } else {
                $md += "_(no reply dispatched — possible SafeAck)_`n`n"
            }
            if ($a.remediations -and $a.remediations.Count -gt 0) {
                $md += "**Remediations this turn:**`n"
                foreach ($r in $a.remediations) {
                    $md += "- ``$r```n"
                }
                $md += "`n"
            }
            if ($a.evalError) {
                $md += "**Eval error:** ``$($a.evalError)```n`n"
            }
        }
    }
    Set-Content -Path $transcriptMd -Value $md -Encoding UTF8

    $summary += [pscustomobject]@{
        Name = $scenario.name
        TurnsTotal = $scenario.turns.Count
        TurnsRan = $turnsCompleted
        Aborted = $aborted
        CapturedReplies = $totalCapturedReplies
        SafeAcks = $totalSafeAcks
    }
}

# ── Sweep summary ─────────────────────────────────────────────────────
Write-Host ""
Write-Host "=== prod-sweep summary ===" -ForegroundColor Yellow
$summary | Format-Table -AutoSize

$summaryFile = Join-Path $OutputDir "_summary.json"
ConvertTo-Json -InputObject @{
    BaseSnapshot = $BaseSnapshot
    ScenariosFile = $ScenariosFile
    OllamaUrl = $OllamaUrl
    StartedAt = (Get-Date).ToString("o")
    Scenarios = $summary
} -Depth 5 | Set-Content -Path $summaryFile -Encoding UTF8

Write-Host ""
Write-Host "Transcripts: $OutputDir" -ForegroundColor DarkGray
Write-Host "Per-scenario DBs preserved at $OutputDir/*.db for inspection (gitignored)." -ForegroundColor DarkGray
