# 2026-05-07 — post-hoc tier review tool for the NULL-source bucket.
#
# Context (per ANI-Tier-Interface-Contract-1-Pager.md §2):
#   3,934 records had NULL `source_name` at migration time. Per Mark's lock
#   (May 7, 2026), the migration script defaulted them all to tier='Interior'
#   so streams could parallelize. This tool is the asynchronous quality pass:
#   walk the bucket, reclassify any record that should actually be Facts or
#   Episodic. Most are expected to remain Interior; small reclassification
#   count expected.
#
# Hard rules:
#   - Never write to the live DB. We operate on a working copy.
#   - The filter is `source_name IS NULL` (the stable predicate), NOT
#     `tier = 'Interior'` — post-migration the tier is the *result* of the
#     defaulting, not the identifying attribute of this bucket.
#   - Resumable: decisions are persisted to a side table on the working copy;
#     quit any time, re-run to pick up where you left off.
#   - Apply step is a separate script (`Apply-TierReviewDecisions.ps1`) and
#     is the only path that writes to the live DB, with explicit confirmation.
#
# Usage:
#   pwsh scripts/tier-review/Review-NullClassifications.ps1
#   pwsh scripts/tier-review/Review-NullClassifications.ps1 -DbPath <path>
#   pwsh scripts/tier-review/Review-NullClassifications.ps1 -DbPath <path> -BatchSize 20
#   pwsh scripts/tier-review/Review-NullClassifications.ps1 -Reset   # blow away decisions table
#
# Keys at the prompt:
#   f  reclassify -> Facts
#   e  reclassify -> Episodic
#   i  keep Interior  (records the no-op decision so we don't re-show it)
#   s  skip / undecided  (no decision recorded; will resurface next session)
#   q  quit, save progress, resume later
#   ?  show help

[CmdletBinding()]
param(
    # Path to the *live* DB. We never write to this; we copy it to a working
    # file alongside it on first run and re-use that working copy thereafter.
    [string] $DbPath = 'C:\dev\ani-data\ani-memory.db',

    # Where the working copy lives. Default sits next to this script so a
    # quit-and-resume picks it up automatically.
    [string] $WorkingCopy = (Join-Path $PSScriptRoot 'review-working-copy.db'),

    [int]    $BatchSize  = 20,

    # If set, drop and re-create the decisions side-table on the working copy.
    # Useful when you want to start fresh; does NOT touch the live DB.
    [switch] $Reset,

    # Path to sqlite3.exe. Falls back to Get-Command if not provided.
    [string] $Sqlite3 = ''
)

$ErrorActionPreference = 'Stop'

# --- locate sqlite3 -----------------------------------------------------------
if (-not $Sqlite3) {
    $cmd = Get-Command sqlite3 -ErrorAction SilentlyContinue
    if (-not $cmd) { throw "sqlite3 not found on PATH; pass -Sqlite3 <path>" }
    $Sqlite3 = $cmd.Source
}

# --- prepare working copy -----------------------------------------------------
# Copy the live DB to a working copy ONCE. After that, re-runs reuse the same
# working copy so the decisions side-table persists. To start over, pass -Reset
# (which only resets the decisions table) or delete the working copy file
# (which forces a fresh copy from live).
if (-not (Test-Path $WorkingCopy)) {
    if (-not (Test-Path $DbPath)) {
        throw "Live DB not found at $DbPath. Pass -DbPath <path>."
    }
    Write-Host "[init] Copying $DbPath -> $WorkingCopy (one-time, working copy is read+side-table only)"
    Copy-Item -LiteralPath $DbPath -Destination $WorkingCopy
} else {
    Write-Host "[resume] Reusing working copy at $WorkingCopy"
}

# --- helpers ------------------------------------------------------------------
function Invoke-Sqlite {
    # NOTE: avoid `-Db` here; it collides with the common parameter alias for
    # `-Debug` and PowerShell rejects the function definition.
    param(
        [Parameter(Mandatory)] [string] $Database,
        [Parameter(Mandatory)] [string] $Sql,
        [string] $Mode = 'list',          # list, line, etc.
        [string] $Separator = "`t"
    )
    # Use stdin so we can pass arbitrary SQL safely (no shell-escape woes).
    $sqliteArgs = @($Database, "-cmd", ".mode $Mode", "-cmd", ".separator `"$Separator`"")
    return $Sql | & $Sqlite3 @sqliteArgs
}

function Invoke-SqliteScalar {
    param([string] $Database, [string] $Sql)
    $out = Invoke-Sqlite -Database $Database -Sql $Sql -Mode 'list'
    if ($null -eq $out) { return $null }
    return ($out | Select-Object -First 1)
}

# --- ensure decisions side-table on working copy ------------------------------
# This table lives ONLY on the working copy. The apply script later reads it
# from the working copy and writes the resulting tier updates into the target
# (live) DB after a confirmation prompt.
$createTableSql = @'
CREATE TABLE IF NOT EXISTS tier_review_decisions (
    id            TEXT PRIMARY KEY,
    original_tier TEXT NOT NULL,
    new_tier      TEXT NOT NULL,
    reviewed_at   TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_trd_reviewed_at ON tier_review_decisions(reviewed_at);
'@

if ($Reset) {
    Write-Host "[reset] Dropping tier_review_decisions on working copy (live DB untouched)"
    Invoke-Sqlite -Database $WorkingCopy -Sql 'DROP TABLE IF EXISTS tier_review_decisions;' | Out-Null
}
Invoke-Sqlite -Database $WorkingCopy -Sql $createTableSql | Out-Null

# --- bucket counts ------------------------------------------------------------
# Filter is source_name IS NULL — the stable predicate. The tier column may
# read 'Interior' (post-migration) or 'Standard' (pre-migration snapshot);
# we display whatever's there as the "current tier" line for the reviewer.
$totalSql = "SELECT COUNT(*) FROM memories WHERE source_name IS NULL;"
$total = [int](Invoke-SqliteScalar -Database $WorkingCopy -Sql $totalSql)

$reviewedSql = "SELECT COUNT(*) FROM tier_review_decisions;"
$reviewed = [int](Invoke-SqliteScalar -Database $WorkingCopy -Sql $reviewedSql)

if ($total -eq 0) {
    Write-Host "No records match `source_name IS NULL`. Nothing to review."
    return
}

Write-Host ""
Write-Host "==============================================================="
Write-Host "Tier Review — NULL-source bucket"
Write-Host "  working copy : $WorkingCopy"
Write-Host "  total        : $total"
Write-Host "  reviewed     : $reviewed"
Write-Host "  remaining    : $($total - $reviewed)"
Write-Host "  batch size   : $BatchSize"
Write-Host "==============================================================="
Write-Host "Keys: [f]acts  [e]pisodic  [i]nterior-keep  [s]kip  [q]uit  [?]help"
Write-Host ""

# --- session timing for ETA ---------------------------------------------------
$sessionStart = Get-Date
$sessionDecisions = 0

function Show-Help {
    Write-Host ""
    Write-Host "  f  reclassify this record as Facts"
    Write-Host "  e  reclassify this record as Episodic"
    Write-Host "  i  keep Interior (records the decision so it won't re-appear)"
    Write-Host "  s  skip — no decision; will reappear next session"
    Write-Host "  q  quit — saves progress; rerun to resume"
    Write-Host "  ?  this help"
    Write-Host ""
}

function Save-Decision {
    param([string] $Id, [string] $OriginalTier, [string] $NewTier)
    # Use parameter binding via a here-doc + sqlite3 .param? sqlite3 CLI
    # doesn't support bind params on stdin easily. Ids and tier values come
    # from the DB itself (not from the user) and tier is a fixed enum, so
    # direct interpolation with single-quote escaping is safe here.
    $idEsc = $Id -replace "'", "''"
    $otEsc = $OriginalTier -replace "'", "''"
    $ntEsc = $NewTier -replace "'", "''"
    $now   = (Get-Date).ToString('o')
    $sql   = @"
INSERT INTO tier_review_decisions(id, original_tier, new_tier, reviewed_at)
VALUES ('$idEsc', '$otEsc', '$ntEsc', '$now')
ON CONFLICT(id) DO UPDATE SET
    new_tier    = excluded.new_tier,
    reviewed_at = excluded.reviewed_at;
"@
    Invoke-Sqlite -Database $WorkingCopy -Sql $sql | Out-Null
}

# --- main loop ----------------------------------------------------------------
$keepGoing = $true
while ($keepGoing) {
    # Pull next batch: NULL-source records that have NOT been reviewed yet.
    # Order by created_at so review walks the history in a sensible direction.
    $batchSql = @"
SELECT m.id, m.tier, m.importance, m.created_at, REPLACE(REPLACE(SUBSTR(m.content, 1, 200), char(10), ' '), char(13), ' ')
FROM memories m
LEFT JOIN tier_review_decisions d ON d.id = m.id
WHERE m.source_name IS NULL
  AND d.id IS NULL
ORDER BY m.created_at ASC
LIMIT $BatchSize;
"@
    $rows = Invoke-Sqlite -Database $WorkingCopy -Sql $batchSql -Mode 'list' -Separator "`t"

    if (-not $rows -or $rows.Count -eq 0) {
        Write-Host ""
        Write-Host "All NULL-source records have a decision recorded. Nothing left to review."
        break
    }

    $rowList = @($rows)
    foreach ($row in $rowList) {
        if ([string]::IsNullOrWhiteSpace($row)) { continue }
        $parts = $row -split "`t", 5
        if ($parts.Count -lt 5) { continue }
        $id        = $parts[0]
        $tier      = $parts[1]
        $imp       = $parts[2]
        $createdAt = $parts[3]
        $preview   = $parts[4]

        $reviewed = [int](Invoke-SqliteScalar -Database $WorkingCopy -Sql $reviewedSql)
        $remaining = $total - $reviewed
        $pctDone = if ($total -gt 0) { [math]::Round(($reviewed * 100.0) / $total, 1) } else { 0 }

        # ETA: only meaningful once we've recorded a decision this session.
        $etaText = ''
        if ($sessionDecisions -gt 0) {
            $elapsed = (Get-Date) - $sessionStart
            $secsPer = $elapsed.TotalSeconds / $sessionDecisions
            $etaSecs = [int]($secsPer * $remaining)
            $eta     = [TimeSpan]::FromSeconds($etaSecs)
            $etaText = " | ETA at current pace: $($eta.ToString('hh\:mm\:ss'))"
        }

        Write-Host ""
        Write-Host "---------------------------------------------------------------"
        Write-Host "Progress: $reviewed / $total  ($pctDone%)$etaText"
        Write-Host "  id         : $id"
        Write-Host "  tier (now) : $tier"
        Write-Host "  importance : $imp"
        Write-Host "  created_at : $createdAt"
        Write-Host "  preview    : $preview"
        Write-Host "---------------------------------------------------------------"

        $decided = $false
        while (-not $decided) {
            $key = Read-Host "[f/e/i/s/q/?]"
            if ($null -eq $key) { $key = '' }
            switch ($key.Trim().ToLower()) {
                'f' { Save-Decision -Id $id -OriginalTier $tier -NewTier 'Facts';    $sessionDecisions++; $decided = $true }
                'e' { Save-Decision -Id $id -OriginalTier $tier -NewTier 'Episodic'; $sessionDecisions++; $decided = $true }
                'i' { Save-Decision -Id $id -OriginalTier $tier -NewTier 'Interior'; $sessionDecisions++; $decided = $true }
                's' { Write-Host "  (skipped — will reappear next session)"; $decided = $true }
                'q' { $keepGoing = $false; $decided = $true }
                '?' { Show-Help }
                ''  { Write-Host "  (no input — type a key or ? for help)" }
                default { Write-Host "  unknown key '$key' — type ? for help" }
            }
            if (-not $keepGoing) { break }
        }
        if (-not $keepGoing) { break }
    }
}

# --- session summary ----------------------------------------------------------
$reviewed = [int](Invoke-SqliteScalar -Database $WorkingCopy -Sql $reviewedSql)
Write-Host ""
Write-Host "==============================================================="
Write-Host "Session complete."
Write-Host "  decisions this session : $sessionDecisions"
Write-Host "  total reviewed         : $reviewed / $total"
Write-Host "  working copy           : $WorkingCopy"
Write-Host ""
if ($reviewed -lt $total) {
    Write-Host "Resume by re-running this script. The working copy carries your progress."
} else {
    Write-Host "All NULL-source records are reviewed."
    Write-Host "Apply with:"
    Write-Host "  pwsh scripts/tier-review/Apply-TierReviewDecisions.ps1 -DbPath <live-db> -DecisionsDb $WorkingCopy"
}
Write-Host "==============================================================="
