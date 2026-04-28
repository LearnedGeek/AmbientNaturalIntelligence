# 2026-04-28 — pull a read-only snapshot of the production ani-memory.db
# off the server, never touching the live DB with analysis queries.
#
# Pattern (per Mark's Apr 28 directive):
#   1. SSH to ani-server, run `sqlite3 ANI .backup SNAPSHOT` (online-safe).
#   2. scp the snapshot back to the local workspace under audit-snapshots/.
#   3. All audit work runs against the local snapshot, never against C:\dev\ani-data.
#
# Server: ani-server (alias in ~/.ssh/config → 192.168.1.100, cortexadmin)
# Server DB: C:\dev\ani-data\ani-memory.db
# Server sqlite3: C:\Users\cortexadmin\sqlite3.exe (per find-db-and-probe.ps1)
#
# Local destination: tools/audits/snapshots/ani-memory-<timestamp>.db
#
# Usage from repo root:
#   pwsh tools/audits/snapshot-and-pull.ps1
# Or from bash (Windows):
#   powershell -File tools/audits/snapshot-and-pull.ps1

$ErrorActionPreference = 'Stop'

$timestamp     = Get-Date -Format 'yyyyMMdd-HHmmss'
$serverHost    = 'ani-server'
$serverDb      = 'C:\dev\ani-data\ani-memory.db'
$serverSqlite  = 'C:\Users\cortexadmin\sqlite3.exe'
$serverTmpDir  = 'C:\Users\cortexadmin\ani-snapshots'
$serverSnap    = "$serverTmpDir\ani-memory-$timestamp.db"
# Forward-slash form for scp's remote spec — backslashes get eaten by the
# OpenSSH client's path parser. (cmd-side ops still use backslash form.)
$serverSnapFwd = $serverSnap -replace '\\', '/'

$localDir      = Join-Path $PSScriptRoot 'snapshots'
$localSnap     = Join-Path $localDir "ani-memory-$timestamp.db"

if (-not (Test-Path $localDir)) {
    New-Item -ItemType Directory -Path $localDir | Out-Null
}

Write-Host "[1/4] Ensuring server tmp dir exists: $serverTmpDir"
$mkdirCmd = "if not exist `"$serverTmpDir`" mkdir `"$serverTmpDir`""
ssh $serverHost "cmd /c $mkdirCmd"
if ($LASTEXITCODE -ne 0) { throw "ssh mkdir failed (exit $LASTEXITCODE)" }

Write-Host "[2/4] Snapshotting live DB on server (online-safe .backup)..."
# `.backup` is the SQLite-supported online backup API — runs concurrently with
# writers, produces a consistent snapshot, no locking of the live DB.
$backupCmd = "$serverSqlite `"$serverDb`" `".backup '$serverSnap'`""
ssh $serverHost "cmd /c $backupCmd"
if ($LASTEXITCODE -ne 0) { throw "server-side .backup failed (exit $LASTEXITCODE)" }

Write-Host "[3/4] Copying snapshot down to $localSnap"
# Use the forward-slash form for scp — OpenSSH's path parser eats backslashes
# in the remote spec. Single-quoted to keep PowerShell from re-interpreting.
scp "${serverHost}:${serverSnapFwd}" $localSnap
if ($LASTEXITCODE -ne 0) { throw "scp failed (exit $LASTEXITCODE)" }

Write-Host "[4/4] Cleaning up server-side snapshot"
$rmCmd = "del `"$serverSnap`""
ssh $serverHost "cmd /c $rmCmd"
# don't throw on cleanup failure — local copy already pulled
if ($LASTEXITCODE -ne 0) {
    Write-Warning "server-side cleanup failed (exit $LASTEXITCODE) — orphan at $serverSnap"
}

$sizeMB = [math]::Round((Get-Item $localSnap).Length / 1MB, 1)
Write-Host ""
Write-Host "DONE. Snapshot: $localSnap  ($sizeMB MB)"
Write-Host "All audit work runs against THIS file. Live DB at $serverDb is untouched."
