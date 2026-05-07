# 2026-05-07 — apply tier-review decisions to a target DB.
#
# Companion to Review-NullClassifications.ps1. This is the ONLY script in the
# tier-review pair that writes to the production / target DB. It reads the
# decisions side-table from the working copy (or any DB that holds the
# `tier_review_decisions` table) and applies the new_tier values to the
# target DB inside a single transaction, after backup and explicit
# confirmation.
#
# Hard rules:
#   - Backup the target DB before any write (refuses to run without backup).
#   - Single transaction; abort cleanly on failure.
#   - Skip rows where new_tier == original_tier (the 'i' keep-Interior path
#     records a decision but isn't a real change — no UPDATE needed).
#   - Confirmation prompt before writing. Pass -Yes to skip in scripted runs.
#
# Usage:
#   pwsh scripts/tier-review/Apply-TierReviewDecisions.ps1 \
#        -DbPath C:\dev\ani-data\ani-memory.db \
#        -DecisionsDb scripts/tier-review/review-working-copy.db
#
# Add -Yes to bypass the interactive confirmation (CI / scripted use).
# Add -DryRun to print what would change without writing.

[CmdletBinding()]
param(
    # Target DB to write into. Defaults to the live path; user must confirm.
    [Parameter(Mandatory)] [string] $DbPath,

    # Working copy (or any DB) that holds the `tier_review_decisions` table.
    [Parameter(Mandatory)] [string] $DecisionsDb,

    # Skip the interactive confirmation prompt.
    [switch] $Yes,

    # Print summary of pending changes; don't write.
    [switch] $DryRun,

    # Path to sqlite3.exe.
    [string] $Sqlite3 = ''
)

$ErrorActionPreference = 'Stop'

if (-not $Sqlite3) {
    $cmd = Get-Command sqlite3 -ErrorAction SilentlyContinue
    if (-not $cmd) { throw "sqlite3 not found on PATH; pass -Sqlite3 <path>" }
    $Sqlite3 = $cmd.Source
}

if (-not (Test-Path $DbPath))      { throw "Target DB not found: $DbPath" }
if (-not (Test-Path $DecisionsDb)) { throw "Decisions DB not found: $DecisionsDb" }

function Invoke-Sqlite {
    # NOTE: avoid `-Db` here; collides with the `-Debug` common-parameter alias.
    # NOTE: use `$sqliteArgs`, not `$args` — `$args` is a PowerShell automatic variable.
    param([string] $Database, [string] $Sql, [string] $Mode = 'list', [string] $Separator = "`t")
    $sqliteArgs = @($Database, "-cmd", ".mode $Mode", "-cmd", ".separator `"$Separator`"")
    return $Sql | & $Sqlite3 @sqliteArgs
}

function Invoke-SqliteScalar {
    param([string] $Database, [string] $Sql)
    $out = Invoke-Sqlite -Database $Database -Sql $Sql -Mode 'list'
    if ($null -eq $out) { return $null }
    return ($out | Select-Object -First 1)
}

# --- read decisions -----------------------------------------------------------
$decSql = @"
SELECT id, original_tier, new_tier
FROM tier_review_decisions
WHERE new_tier <> original_tier;
"@
$rows = @(Invoke-Sqlite -Database $DecisionsDb -Sql $decSql -Mode 'list' -Separator "`t" | Where-Object { $_ })

$totalDecisions = [int](Invoke-SqliteScalar -Database $DecisionsDb -Sql 'SELECT COUNT(*) FROM tier_review_decisions;')
$realChanges    = $rows.Count

# --- summary by target tier ---------------------------------------------------
$summarySql = @"
SELECT new_tier, COUNT(*)
FROM tier_review_decisions
WHERE new_tier <> original_tier
GROUP BY new_tier
ORDER BY new_tier;
"@
$summary = @(Invoke-Sqlite -Database $DecisionsDb -Sql $summarySql -Mode 'list' -Separator "`t" | Where-Object { $_ })

Write-Host ""
Write-Host "==============================================================="
Write-Host "Apply Tier-Review Decisions"
Write-Host "  target DB     : $DbPath"
Write-Host "  decisions DB  : $DecisionsDb"
Write-Host "  total reviews : $totalDecisions"
Write-Host "  real changes  : $realChanges (no-op 'keep' decisions skipped)"
foreach ($line in $summary) {
    if ([string]::IsNullOrWhiteSpace($line)) { continue }
    $p = $line -split "`t", 2
    Write-Host ("  -> {0,-10} {1}" -f $p[0], $p[1])
}
Write-Host "==============================================================="

if ($realChanges -eq 0) {
    Write-Host "Nothing to apply (no changed-tier decisions). Exiting."
    return
}

if ($DryRun) {
    Write-Host ""
    Write-Host "[dry-run] No writes performed."
    return
}

# --- confirmation -------------------------------------------------------------
if (-not $Yes) {
    Write-Host ""
    Write-Host "This will WRITE to $DbPath."
    $resp = Read-Host "Proceed? type 'apply' to confirm"
    if ($resp -ne 'apply') {
        Write-Host "Aborted by user."
        return
    }
}

# --- backup -------------------------------------------------------------------
$ts        = Get-Date -Format 'yyyyMMdd-HHmmss'
$backup    = "$DbPath.tier-review-backup-$ts.db"
Write-Host "[backup] Copying $DbPath -> $backup"
Copy-Item -LiteralPath $DbPath -Destination $backup
$backupSize = (Get-Item $backup).Length
if ($backupSize -le 0) { throw "Backup is zero bytes — aborting before any write." }

# --- attach decisions DB and apply atomically --------------------------------
# Use ATTACH DATABASE so the UPDATE can correlate against the decisions row
# set without copying it into the target. Wrap in BEGIN IMMEDIATE / COMMIT
# for atomicity. If anything fails, ROLLBACK leaves the target DB unchanged
# (and we still have the backup we just made).
$decAbs   = (Resolve-Path $DecisionsDb).Path -replace "'", "''"
$applySql = @"
ATTACH DATABASE '$decAbs' AS rev;

BEGIN IMMEDIATE;

UPDATE memories
SET tier = (
    SELECT d.new_tier
    FROM rev.tier_review_decisions d
    WHERE d.id = memories.id
)
WHERE id IN (
    SELECT d.id
    FROM rev.tier_review_decisions d
    WHERE d.new_tier <> d.original_tier
);

COMMIT;

DETACH DATABASE rev;
"@

Write-Host "[apply] Running atomic UPDATE..."
# Capture output so a non-zero exit can surface sqlite3's stdout in the error.
$applyOut = Invoke-Sqlite -Database $DbPath -Sql $applySql -Mode 'list'
if ($LASTEXITCODE -ne 0) {
    if ($applyOut) { Write-Host ($applyOut -join [Environment]::NewLine) }
    throw "sqlite3 apply failed (exit $LASTEXITCODE). Backup at $backup."
}

# --- post-apply verification --------------------------------------------------
# Sample the target DB and confirm a handful of decisions actually landed.
$verifySql = @"
ATTACH DATABASE '$decAbs' AS rev;
SELECT COUNT(*)
FROM memories m
JOIN rev.tier_review_decisions d ON d.id = m.id
WHERE d.new_tier <> d.original_tier
  AND m.tier = d.new_tier;
DETACH DATABASE rev;
"@
$verified = [int](Invoke-SqliteScalar -Database $DbPath -Sql $verifySql)

Write-Host ""
Write-Host "==============================================================="
Write-Host "Apply complete."
Write-Host "  rows updated and verified : $verified / $realChanges"
Write-Host "  backup                    : $backup"
if ($verified -ne $realChanges) {
    Write-Warning "Verified count != expected. Inspect target DB and consider restoring from backup."
} else {
    Write-Host "All decisions applied and verified."
}
Write-Host "==============================================================="
