# Keeps ONE GitHub Issue for the whole scan, listing every actionable finding grouped by file, so a single
# `@claude fix this` produces one pull request that fixes everything and is built and checked as a whole.
# The Issue is created when findings appear, updated in place when the list changes, reopened if findings
# return after it was closed, and closed when nothing actionable is left. Older radar Issues (one per file
# or per finding) are closed as superseded. Default fix rules belong in the target repository's CLAUDE.md.
# Needs the gh CLI with GH_TOKEN (issues: write). -DryRun prints the plan and the Issue title only.
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
$key = 'scan'

function Kind([string]$category) {
    $kind = ($category -replace '_', ' ').ToLowerInvariant()
    $kind.Substring(0, 1).ToUpperInvariant() + $kind.Substring(1)
}
function Hash([string]$text) {
    $bytes = [System.Security.Cryptography.SHA256]::HashData([System.Text.Encoding]::UTF8.GetBytes($text))
    [System.Convert]::ToHexString($bytes).Substring(0, 12).ToLowerInvariant()
}
function Finding-Text($f, [int]$number) {
    $at = if ($f.lines -and @($f.lines).Count -gt 1) { " | lines $(@($f.lines) -join ', ')" } elseif ($f.line -gt 0) { " | line $($f.line)" } else { '' }
    $replacement = if ($f.suggestedReplacement) { "`n`nSuggested replacement: ``$($f.suggestedReplacement)``" } else { '' }
    $evidence = (@($f.evidence) | ForEach-Object { "- ``$_``" }) -join "`n"
    @"
#### $number. $(Kind $f.category): ``$($f.path)``$at
Severity **$($f.severity)** | confidence **$($f.confidence)**

$($f.message)$replacement

<details><summary>Evidence ($(@($f.evidence).Count))</summary>

$evidence
</details>
"@
}

# Actionable findings grouped by file, in report order; a repeated finding counts once.
$groups = [ordered]@{}
foreach ($f in @($report.findings) | Where-Object { $_.actionable }) {
    $file = [string]$f.file
    if (-not $groups.Contains($file)) { $groups[$file] = [System.Collections.Generic.List[object]]::new() }
    if (-not ($groups[$file] | Where-Object { $_.category -eq $f.category -and $_.path -eq $f.path })) { $groups[$file].Add($f) }
}
$total = [int]($groups.Values | ForEach-Object { $_.Count } | Measure-Object -Sum).Sum
$versions = if ($report.target -and $report.next -and @($report.next).Count -gt 0) { "$($report.target) -> $(@($report.next)[-1])" } else { [string]$report.target }
$title = if ($versions) { "[radar] API upgrade ${versions}: $total findings in $($groups.Count) files" } else { "[radar] API upgrade: $total findings in $($groups.Count) files" }

function Body {
    $table = ($groups.Keys | ForEach-Object {
        $kinds = ($groups[$_] | ForEach-Object { Kind $_.category } | Select-Object -Unique) -join ', '
        $name = if ($_) { "``$_``" } else { 'repository-wide' }
        "| $name | $($groups[$_].Count) | $kinds |"
    }) -join "`n"
    $number = 0
    $sections = foreach ($file in $groups.Keys) {
        $name = if ($file) { "``$file``" } else { 'Repository-wide' }
        $items = foreach ($f in $groups[$file]) { $number++; Finding-Text $f $number }
        "### $name`n`n$($items -join "`n`n")"
    }
    # The hash covers the findings only (not the run link), so an unchanged scan does not edit the Issue.
    $content = @"
Radar found **$total actionable findings in $($groups.Count) files** for the upgrade $versions. Fix them all in **one** pull request.

| File | Findings | Kinds |
|---|---|---|
$table

$($sections -join "`n`n")

### Fix instructions (for ``@claude fix this``)
- Fix every finding above in one pull request; they are listed together so the whole upgrade builds and is reviewed as one change.
- Follow the repository's ``CLAUDE.md``.
- Keep the build green: ``$BuildCommand``
- When every check passes, open a **draft** pull request with ``gh pr create --draft`` that mentions this issue.
"@
    $run = if ($RunUrl) { "`n`n**Scan run:** $RunUrl" } else { '' }
    "<!-- radar-key: $key -->`n<!-- radar-hash: $(Hash $content) -->`n$content$run"
}

function With-BodyFile([string]$text, [scriptblock]$action) {
    $path = New-TemporaryFile
    try { Set-Content -LiteralPath $path -Value $text -Encoding utf8; & $action $path }
    finally { Remove-Item -LiteralPath $path -ErrorAction SilentlyContinue }
}

$issue = $null
$older = [System.Collections.Generic.List[object]]::new()
if (-not $DryRun) {
    gh label create $label -R $Repo --color 1a64d6 --description 'Found by Ad API Radar' --force | Out-Null
    $issues = gh issue list -R $Repo --label $label --state all --limit 1000 --json number,state,body | ConvertFrom-Json
    foreach ($candidate in $issues) {
        if ($candidate.body -match "<!-- radar-key: $key -->") { if ($null -eq $issue -or $candidate.state -eq 'OPEN') { $issue = $candidate } }
        elseif ($candidate.body -match '<!-- radar-key: ') { $older.Add($candidate) }
    }
}

$action = 'unchanged'
if ($groups.Count -eq 0) {
    if ($issue -and $issue.state -eq 'OPEN') {
        $action = "close   #$($issue.number) (no actionable findings left)"
        if (-not $DryRun) { gh issue close $issue.number -R $Repo --comment 'Radar finds no actionable findings any more.' | Out-Null }
    } else { $action = 'nothing to do (no actionable findings)' }
} else {
    $body = Body
    if ($null -eq $issue) {
        $action = "create  $title"
        if (-not $DryRun) { With-BodyFile $body { param($p) gh issue create -R $Repo --title $title --body-file $p --label $label | Out-Null } }
    } else {
        $changed = -not $issue.body.Contains(($body -split "`n")[1])
        if ($issue.state -eq 'CLOSED') {
            $action = "reopen  #$($issue.number) $title"
            if (-not $DryRun) { gh issue reopen $issue.number -R $Repo --comment 'Radar finds actionable findings again.' | Out-Null }
        } elseif ($changed) {
            $action = "update  #$($issue.number) $title"
        }
        if (-not $DryRun -and ($changed -or $issue.state -eq 'CLOSED')) {
            With-BodyFile $body { param($p) gh issue edit $issue.number -R $Repo --title $title --body-file $p | Out-Null }
            if ($changed -and $issue.state -eq 'OPEN') { gh issue comment $issue.number -R $Repo --body 'Radar updated the list of findings.' | Out-Null }
        }
    }
}
Write-Output $action
foreach ($old in $older | Where-Object { $_.state -eq 'OPEN' }) {
    Write-Output "close   #$($old.number) (superseded by the single scan Issue)"
    if (-not $DryRun) { gh issue close $old.number -R $Repo --comment 'Superseded: radar now keeps one Issue listing every finding.' | Out-Null }
}
Write-Output "Scan Issue: $total actionable findings in $($groups.Count) files."
