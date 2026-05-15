# claude-recall UserPromptSubmit hook (Windows PowerShell)
#
# Two injections per turn:
# 1. STATIC interventions from ./interventions.md (user-editable, load-bearing
#    behavioral cues — load every turn so the agent sees them BEFORE
#    drafting a response, not after).
# 2. DYNAMIC prior-session recall via `claude-recall search` (semantic
#    surface of relevant past sessions).
#
# Both get merged into a single `additionalContext` block and emitted as
# JSON for Claude Code to consume.
#
# Failure policy: on ANY error, emit empty JSON and exit 0. Never block
# user prompts.
#
# To edit interventions: change ./interventions.md — no hook restart needed.
# To disable interventions: blank the file or delete it.
# To disable recall:        remove the claude-recall call below.
#
# Feature proposal upstream: claude-recall could ship native intervention
# support so this layering pattern doesn't need to live in each project's
# hook. See https://github.com/LearnedGeek/claude-recall/issues/29
# (filed 2026-05-15). When that ships, this hook's intervention-merge
# logic can be deleted and the config flag flipped instead.

$ErrorActionPreference = 'SilentlyContinue'

# Force UTF-8 on stdout (Windows PowerShell defaults to UTF-16LE which
# Claude Code's JSON parser would silently reject).
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

try {
    $raw = [Console]::In.ReadToEnd()
    if (-not $raw) { Write-Output '{}'; exit 0 }

    $parsed = $raw | ConvertFrom-Json
    $prompt = $parsed.prompt
    if (-not $prompt) { Write-Output '{}'; exit 0 }

    # --- 1. Static interventions (always injected when file is present) ---
    $interventionsPath = Join-Path $PSScriptRoot 'interventions.md'
    $interventions    = $null
    if (Test-Path $interventionsPath) {
        $content = (Get-Content $interventionsPath -Raw)
        if ($content) { $interventions = $content.Trim() }
    }

    # --- 2. Dynamic prior-session recall via claude-recall ---
    # claude-recall --agent-context emits the wrapped Claude Code hook shape:
    #   { hookSpecificOutput: { hookEventName: "UserPromptSubmit", additionalContext: "..." } }
    # Read the inner .additionalContext for merging with interventions.
    $recallJson    = & claude-recall search $prompt --days 30 --limit 3 --threshold 0.3 --agent-context 2>$null
    $recallContext = $null
    if ($recallJson -and $recallJson -ne '{}') {
        try {
            $recallParsed  = $recallJson | ConvertFrom-Json
            $recallContext = $recallParsed.hookSpecificOutput.additionalContext
        } catch { }
    }

    # --- Merge: interventions first (load-bearing), then recall (informational) ---
    $parts = @()
    if ($interventions)  { $parts += $interventions }
    if ($recallContext)  { $parts += $recallContext }

    if ($parts.Count -eq 0) {
        Write-Output '{}'
    } else {
        $combined = $parts -join "`n`n---`n`n"
        # Claude Code's strict-validation pass requires the wrapped
        # hookSpecificOutput shape (see LearnedGeek/claude-recall#21 — flat
        # {additionalContext: ...} is silently dropped). Emit the wrapped
        # form so the injection actually lands in the agent's context.
        @{
            hookSpecificOutput = @{
                hookEventName     = 'UserPromptSubmit'
                additionalContext = $combined
            }
        } | ConvertTo-Json -Compress -Depth 4
    }
} catch {
    Write-Output '{}'
}

exit 0
