# Issue #47 J.5h-data Step 3: re-summarize the 28 quarantined records.
#
# Background: the 28 closed_conversation_records with validity =
# 'invalid_fabrication' were produced by the pre-J.5h summarizer prompt
# (no attribution rule). The post-J.5h pipeline produces honest gists
# (one-sided framing + invariant backstop). To enable broken-vs-fixed
# paper-figure comparison, this script regenerates the gists using the
# post-J.5h prompt and writes them to the new gist_v2 column. The
# original (fabricated) gist is preserved on the same row.
#
# Pipeline: read invalid_fabrication record -> read its source thread's
# conversation_messages -> build the post-J.5h prompt (mirrors
# ClosedConversationSummarizer.BuildGistPrompt as of commit 87b54d2) ->
# POST to Ollama localhost:11434/api/chat -> UPDATE gist_v2.
#
# Idempotent: re-running matches no rows (script targets gist_v2 IS NULL).
# Reversible: UPDATE closed_conversation_records SET gist_v2 = NULL WHERE
# validity = 'invalid_fabrication'.
#
# Prerequisites: gist_v2 column exists (commit 52a7c7f or later deployed).

param(
    [string] $DbPath       = 'C:/dev/ani-data/ani-memory.db',
    [string] $SqliteExe    = 'C:/Users/cortexadmin/sqlite3.exe',
    [string] $OllamaUrl    = 'http://localhost:11434/api/chat',
    [string] $Model        = 'ani-v7-conversation',
    [double] $Temperature  = 0.3
)

$ErrorActionPreference = 'Stop'

# Mirror of ClosedConversationSummarizer.BuildGistPrompt as of commit
# 87b54d2 (post-J.5h). Drift note: production prompt uses en-dash for
# "1-2"; this script uses ASCII hyphen because the script file is
# transferred to a Windows host where PowerShell reads non-BOM UTF-8 as
# Windows-1252, garbling multi-byte characters. The semantic intent
# (one-to-two-sentence summary) is preserved. Difference is purely
# orthographic; the attribution rule itself is byte-identical to the
# C# AttributionRule string.
$systemPrompt = @'
You are a paraphrase-only summariser. Produce a 1-2 sentence summary of the conversation between Mark (the contact) and Ani.

HARD CONSTRAINTS:
- DO NOT quote any contact (Mark) turn verbatim. Do not lift phrases of 7 or more consecutive words from any of his messages.
- ATTRIBUTION RULE - claims must be grounded in actual turns. Do NOT narrate Mark words, feelings, actions, presence, or internal state unless Mark actually has turns in the input. If Mark has zero turns, the exchange is one-sided (Ani reaching out without a response yet): summarize only what Ani sent and explicitly note that Mark has not responded. Do not invent Mark reactions to fulfill a between-them framing. The same rule applies to Ani: do not narrate Ani-side content that is not grounded in Ani actual turns.
- DO NOT use direct speech (she said, he asked). Use paraphrase.
- Focus on what shifted EMOTIONALLY between them, not what was literally said.
- 1 to 2 sentences. No more.
- Refer to the participants as Mark and Ani by name.
- Plain prose. No bullet points, no labels, no JSON, no quotation marks.
'@

function Invoke-SqliteJson {
    param([string] $Sql)
    $raw = & $SqliteExe -cmd '.mode json' $DbPath $Sql
    if (-not $raw) { return @() }
    $joined = if ($raw -is [array]) { $raw -join "`n" } else { [string]$raw }
    if ([string]::IsNullOrWhiteSpace($joined)) { return @() }
    $parsed = $joined | ConvertFrom-Json
    # Force array shape even when the JSON contains only one row.
    if ($null -eq $parsed) { return @() }
    if ($parsed -isnot [System.Collections.IEnumerable] -or $parsed -is [string]) {
        return ,@($parsed)
    }
    return @($parsed)
}

function Get-Targets {
    $sql = @"
SELECT id, thread_id FROM closed_conversation_records
WHERE validity = 'invalid_fabrication' AND gist_v2 IS NULL
ORDER BY closed_at ASC;
"@
    return Invoke-SqliteJson -Sql $sql
}

function Get-ThreadMessages {
    param([string] $ThreadId)
    $sql = @"
SELECT role, content FROM conversation_messages
WHERE thread_id = '$ThreadId'
ORDER BY sent_at ASC;
"@
    return Invoke-SqliteJson -Sql $sql
}

function Build-UserPrompt {
    param([object[]] $Messages)
    $sb = [System.Text.StringBuilder]::new()
    [void]$sb.AppendLine('Conversation:')
    foreach ($m in $Messages) {
        $who = if ($m.role -ieq 'mark') { 'Mark' } else { 'Ani' }
        [void]$sb.Append($who).Append(': ').AppendLine($m.content)
    }
    [void]$sb.AppendLine()
    [void]$sb.AppendLine('Write the gist now (1-2 sentences, paraphrased only):')
    return $sb.ToString()
}

function Invoke-OllamaChat {
    param(
        [string] $SystemPrompt,
        [string] $UserPrompt
    )
    # Force InvariantCulture for temperature serialization — PowerShell's
    # ConvertTo-Json honors the current culture's decimal separator, which
    # produces "0,3" on locales like de-DE and breaks Ollama's JSON parser.
    $tempStr = $Temperature.ToString([System.Globalization.CultureInfo]::InvariantCulture)
    $body = @{
        model    = $Model
        messages = @(
            @{ role = 'system'; content = $SystemPrompt },
            @{ role = 'user';   content = $UserPrompt }
        )
        stream   = $false
        options  = @{ temperature = [double]::Parse($tempStr, [System.Globalization.CultureInfo]::InvariantCulture) }
    } | ConvertTo-Json -Depth 10 -Compress

    # Belt-and-suspenders: regex-replace any comma decimal that slipped through.
    $body = [regex]::Replace($body, '"temperature":(\d+),(\d+)', '"temperature":$1.$2')

    $resp = Invoke-RestMethod -Uri $OllamaUrl -Method Post -Body $body -ContentType 'application/json' -TimeoutSec 120
    return $resp.message.content
}

function Format-Gist {
    param([string] $Raw)
    if ([string]::IsNullOrWhiteSpace($Raw)) { return 'Mark and Ani spoke briefly.' }
    $trimmed = $Raw.Trim()
    $collapsed = ($trimmed -split '\s+') -join ' '
    if ($collapsed.Length -gt 600) { $collapsed = $collapsed.Substring(0, 600) }
    return $collapsed
}

function Update-GistV2 {
    param([string] $RecordId, [string] $GistV2)
    # Write the UPDATE to a temp file and invoke via `.read` to sidestep
    # PowerShell/sqlite3 command-line argument quoting limits (the 27/28
    # success run died here on a record whose gist round-tripped fine but
    # produced a shell-level argument-truncation when passed inline).
    # SQL string-literal escape: double the single quotes.
    $escaped = $GistV2 -replace "'", "''"
    $sql = "UPDATE closed_conversation_records SET gist_v2 = '$escaped' WHERE id = '$RecordId';"
    $tmp = [System.IO.Path]::GetTempFileName()
    [System.IO.File]::WriteAllText($tmp, $sql, [System.Text.UTF8Encoding]::new($false))
    try {
        & $SqliteExe $DbPath ".read $tmp" | Out-Null
    }
    finally {
        Remove-Item $tmp -ErrorAction SilentlyContinue
    }
}

# Main
$targets = @(Get-Targets)
Write-Output "Targets (validity='invalid_fabrication' AND gist_v2 IS NULL): $($targets.Count)"
if ($targets.Count -eq 0) { Write-Output 'Nothing to do.'; return }

$i = 0
foreach ($t in $targets) {
    $i++
    $messages = @(Get-ThreadMessages -ThreadId $t.thread_id)
    if ($messages.Count -eq 0) {
        Write-Output "  [$i/$($targets.Count)] record=$($t.id) thread=$($t.thread_id) - no messages found, skipping"
        continue
    }
    $userPrompt = Build-UserPrompt -Messages $messages
    try {
        $raw = Invoke-OllamaChat -SystemPrompt $systemPrompt -UserPrompt $userPrompt
        $clean = Format-Gist -Raw $raw
        Update-GistV2 -RecordId $t.id -GistV2 $clean
        Write-Output ("[{0}/{1}] {2} -> {3}" -f $i, $targets.Count, $t.id.Substring(0, 8), ($clean.Substring(0, [Math]::Min(80, $clean.Length))))
    }
    catch {
        Write-Output "  [$i/$($targets.Count)] record=$($t.id) - Ollama call failed: $_"
    }
}

Write-Output ''
Write-Output '=== Verification ==='
$verifySql = @"
SELECT 'with_gist_v2'  AS label, COUNT(*) AS n FROM closed_conversation_records WHERE validity = 'invalid_fabrication' AND gist_v2 IS NOT NULL;
SELECT 'still_missing' AS label, COUNT(*) AS n FROM closed_conversation_records WHERE validity = 'invalid_fabrication' AND gist_v2 IS NULL;
"@
& $SqliteExe -separator '|' $DbPath $verifySql
