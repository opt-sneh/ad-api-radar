# Turns radar findings into GitHub Issues: one open Issue per actionable finding, no duplicates.
# Issues whose finding is no longer detected are closed; closed Issues whose finding is back are reopened.
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
if (-not $Repo) { throw 'Pass -Repo owner/name or set GITHUB_REPOSITORY.' }
$report = Get-Content -LiteralPath $Findings -Raw | ConvertFrom-Json
$label = 'radar'

function Key($f) { "$($f.category)|$($f.path)|$($f.file)" }
function Title($f) {
    $kind = ($f.category -replace '_', ' ').ToLowerInvariant()
    $kind = $kind.Substring(0, 1).ToUpperInvariant() + $kind.Substring(1)
    $file = Split-Path $f.file -Leaf
    if ($file) { "[radar] ${kind}: $($f.path) ($file)" } else { "[radar] ${kind}: $($f.path)" }
}
function Body($f) {
    $lines = if ($f.lines -and $f.lines.Count -gt 1) { " (lines $($f.lines -join ', '))" } else { '' }
    $where = if ($f.line -gt 0) { "``$($f.file):$($f.line)``$lines" } elseif ($f.file) { "``$($f.file)``" } else { 'repository-wide' }
    $evidence = ($f.evidence | ForEach-Object { "- ``$_``" }) -join "`n"
    $replacement = if ($f.suggestedReplacement) { "`n**Suggested replacement:** ``$($f.suggestedReplacement)``" } else { '' }
    $run = if ($RunUrl) { "`n**Scan run:** $RunUrl" } else { '' }
    @"
<!-- radar-key: $(Key $f) -->
**$($f.category)** · severity **$($f.severity)** · confidence **$($f.confidence)**

$($f.message)

**Where:** $where$replacement$run

<details><summary>Evidence ($(@($f.evidence).Count))</summary>

$evidence
</details>

### Fix instructions (for ``@claude fix this``)
- Make the smallest change that resolves this finding. Do not change unrelated code.
- Keep the build green: ``$BuildCommand``
- If the fix needs a newer API version, you may bump the ``Google.Ads.GoogleAds`` package; newer SDKs still ship the older version namespaces, so move only the files this finding touches.
- When the build passes, open a **draft** pull request with ``gh pr create --draft`` that mentions this issue.
"@
}

$wanted = [ordered]@{}
foreach ($f in $report.findings | Where-Object { $_.actionable }) {
    $k = Key $f
    if (-not $wanted.Contains($k)) { $wanted[$k] = $f }
}

$existing = @{}
if (-not $DryRun) {
    gh label create $label -R $Repo --color 1a64d6 --description 'Found by Ad API Radar' --force | Out-Null
    $issues = gh issue list -R $Repo --label $label --state all --limit 1000 --json number,state,body | ConvertFrom-Json
    foreach ($issue in $issues) {
        if ($issue.body -match '<!-- radar-key: (.+?) -->') { $existing[$Matches[1]] = $issue }
    }
}

$created = 0; $reopened = 0; $closed = 0; $kept = 0
foreach ($k in $wanted.Keys) {
    $f = $wanted[$k]
    $issue = $existing[$k]
    if ($null -eq $issue) {
        Write-Output "create  $(Title $f)"
        if (-not $DryRun) {
            $bodyFile = New-TemporaryFile
            try {
                Set-Content -LiteralPath $bodyFile -Value (Body $f) -Encoding utf8
                gh issue create -R $Repo --title (Title $f) --body-file $bodyFile --label $label | Out-Null
            } finally { Remove-Item -LiteralPath $bodyFile -ErrorAction SilentlyContinue }
        }
        $created++
    } elseif ($issue.state -eq 'CLOSED') {
        Write-Output "reopen  #$($issue.number) $(Title $f)"
        if (-not $DryRun) { gh issue reopen $issue.number -R $Repo --comment 'Radar still detects this finding.' | Out-Null }
        $reopened++
    } else { $kept++ }
}
foreach ($k in $existing.Keys) {
    $issue = $existing[$k]
    if ($issue.state -eq 'OPEN' -and -not $wanted.Contains($k)) {
        Write-Output "close   #$($issue.number) (no longer detected)"
        if (-not $DryRun) { gh issue close $issue.number -R $Repo --comment 'Radar no longer detects this finding.' | Out-Null }
        $closed++
    }
}
Write-Output "Issues: $created created, $reopened reopened, $closed closed, $kept unchanged ($($wanted.Count) actionable findings)."
