# Turns radar findings into GitHub Issues: one open Issue per file with actionable findings, no duplicates.
# Grouping by file keeps a fix complete: related breaks in one file are fixed and built together.
# Issues whose findings are gone are closed; closed Issues whose findings are back are reopened; an open
# Issue whose list of findings changed gets its body updated. Old one-per-finding Issues are closed as superseded.
# Default fix rules for Claude belong in the target repository's CLAUDE.md, which Claude reads on every run.
# Needs the gh CLI with GH_TOKEN (issues: write). -DryRun prints the plan without touching GitHub.
param(
    [Parameter(Mandatory = $true)][string]$Findings,
    [string]$Repo = $env:GITHUB_REPOSITORY,
    [string]$BuildCommand = 'dotnet build',
    [string]$RunUrl = '',
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
if (-not $Repo -and -not $DryRun) { throw 'Pass -Repo owner/name or set GITHUB_REPOSITORY.' }
$report = Get-Content -LiteralPath $Findings -Raw | ConvertFrom-Json
$label = 'radar'

function Kind([string]$category) {
    $kind = ($category -replace '_', ' ').ToLowerInvariant()
    $kind.Substring(0, 1).ToUpperInvariant() + $kind.Substring(1)
}
function Where-Text($f) {
    $lines = if ($f.lines -and @($f.lines).Count -gt 1) { "lines $(@($f.lines) -join ', ')" } elseif ($f.line -gt 0) { "line $($f.line)" } else { '' }
    $lines
}
function Title([string]$file, $items) {
    $leaf = if ($file) { Split-Path $file -Leaf } else { 'repository-wide' }
    if (@($items).Count -eq 1) {
        $f = $items[0]
        return "[radar] $(Kind $f.category): $($f.path) ($leaf)"
    }
    $kinds = ($items | ForEach-Object { Kind $_.category } | Select-Object -Unique) -join ', '
    "[radar] ${leaf}: $(@($items).Count) findings ($kinds)"
}
function Hash([string]$text) {
    $bytes = [System.Security.Cryptography.SHA256]::HashData([System.Text.Encoding]::UTF8.GetBytes($text))
    [System.Convert]::ToHexString($bytes).Substring(0, 12).ToLowerInvariant()
}
function Body([string]$file, $items) {
    $sections = for ($i = 0; $i -lt @($items).Count; $i++) {
        $f = $items[$i]
        $at = Where-Text $f
        $at = if ($at) { " | $at" } else { '' }
        $replacement = if ($f.suggestedReplacement) { "`n`nSuggested replacement: ``$($f.suggestedReplacement)``" } else { '' }
        $evidence = (@($f.evidence) | ForEach-Object { "- ``$_``" }) -join "`n"
        @"
### $($i + 1). $(Kind $f.category): ``$($f.path)``$at
Severity **$($f.severity)** | confidence **$($f.confidence)**

$($f.message)$replacement

<details><summary>Evidence ($(@($f.evidence).Count))</summary>

$evidence
</details>
"@
    }
    $where = if ($file) { "``$file``" } else { 'repository-wide' }
    $run = if ($RunUrl) { "`n`n**Scan run:** $RunUrl" } else { '' }
    # The hash covers the findings only (not the run link), so an unchanged file is not re-edited every scan.
    $content = @"
**File:** $where | **$(@($items).Count) finding(s)**

$($sections -join "`n`n")

### Fix instructions (for ``@claude fix this``)
- Fix every finding above in one change; they are listed together so the file ends up consistent.
- Follow the repository's ``CLAUDE.md``.
- Keep the build green: ``$BuildCommand``
- When the build passes, open a **draft** pull request with ``gh pr create --draft`` that mentions this issue.
"@
    "<!-- radar-key: file|$file -->`n<!-- radar-hash: $(Hash $content) -->`n$content$run"
}

# Group actionable findings by file, in report order.
$groups = [ordered]@{}
foreach ($f in @($report.findings) | Where-Object { $_.actionable }) {
    $file = [string]$f.file
    if (-not $groups.Contains($file)) { $groups[$file] = [System.Collections.Generic.List[object]]::new() }
    if (-not ($groups[$file] | Where-Object { $_.category -eq $f.category -and $_.path -eq $f.path })) { $groups[$file].Add($f) }
}

$existing = @{}
$legacy = [System.Collections.Generic.List[object]]::new()
if (-not $DryRun) {
    gh label create $label -R $Repo --color 1a64d6 --description 'Found by Ad API Radar' --force | Out-Null
    $issues = gh issue list -R $Repo --label $label --state all --limit 1000 --json number,state,body | ConvertFrom-Json
    foreach ($issue in $issues) {
        if ($issue.body -match '<!-- radar-key: (file\|.*?) -->') { $existing[$Matches[1]] = $issue }
        elseif ($issue.body -match '<!-- radar-key: ') { $legacy.Add($issue) }
    }
}

function With-BodyFile([string]$text, [scriptblock]$action) {
    $path = New-TemporaryFile
    try { Set-Content -LiteralPath $path -Value $text -Encoding utf8; & $action $path }
    finally { Remove-Item -LiteralPath $path -ErrorAction SilentlyContinue }
}

$created = 0; $reopened = 0; $updated = 0; $closed = 0; $kept = 0
foreach ($file in $groups.Keys) {
    $items = @($groups[$file])
    $key = "file|$file"
    $body = Body $file $items
    $title = Title $file $items
    $issue = $existing[$key]
    if ($null -eq $issue) {
        Write-Output "create  $title"
        if (-not $DryRun) { With-BodyFile $body { param($p) gh issue create -R $Repo --title $title --body-file $p --label $label | Out-Null } }
        $created++
        continue
    }
    $changed = -not $issue.body.Contains(($body -split "`n")[1])
    if ($issue.state -eq 'CLOSED') {
        Write-Output "reopen  #$($issue.number) $title"
        if (-not $DryRun) {
            gh issue reopen $issue.number -R $Repo --comment 'Radar still detects findings in this file.' | Out-Null
            With-BodyFile $body { param($p) gh issue edit $issue.number -R $Repo --title $title --body-file $p | Out-Null }
        }
        $reopened++
    } elseif ($changed) {
        Write-Output "update  #$($issue.number) $title"
        if (-not $DryRun) {
            With-BodyFile $body { param($p) gh issue edit $issue.number -R $Repo --title $title --body-file $p | Out-Null }
            gh issue comment $issue.number -R $Repo --body 'Radar updated the list of findings for this file.' | Out-Null
        }
        $updated++
    } else { $kept++ }
}
foreach ($key in $existing.Keys) {
    $issue = $existing[$key]
    if ($issue.state -eq 'OPEN' -and -not $groups.Contains($key.Substring(5))) {
        Write-Output "close   #$($issue.number) (no longer detected)"
        if (-not $DryRun) { gh issue close $issue.number -R $Repo --comment 'Radar no longer detects findings in this file.' | Out-Null }
        $closed++
    }
}
foreach ($issue in $legacy | Where-Object { $_.state -eq 'OPEN' }) {
    Write-Output "close   #$($issue.number) (superseded by one Issue per file)"
    if (-not $DryRun) { gh issue close $issue.number -R $Repo --comment 'Superseded: radar now opens one Issue per file, listing every finding in it.' | Out-Null }
    $closed++
}
Write-Output "Issues: $created created, $reopened reopened, $updated updated, $closed closed, $kept unchanged ($($groups.Count) files with actionable findings)."
