param(
    [string]$Sha = '015fb0c7e73c1c2fd2d5a1ff787f9f24c383c5e2',
    [string]$Repo = 'C:\server\code\optmyzr',
    [string]$Path = 'code/backend'
)

$ErrorActionPreference = 'Stop'
$root = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$target = Join-Path $root ('data\bench\' + $Sha.Substring(0, 11))

if ((Test-Path -LiteralPath $target) -and @(Get-ChildItem -LiteralPath $target -Force).Count -gt 0) {
    throw "Refusing to overwrite non-empty benchmark folder: $target"
}

New-Item -ItemType Directory -Path $target -Force | Out-Null
$archive = Join-Path $target 'snapshot.tar'
try {
    git -C $Repo -c filter.lfs.process= -c filter.lfs.smudge= -c filter.lfs.required=false archive --format=tar "--output=$archive" $Sha $Path
    if ($LASTEXITCODE -ne 0) { throw "git archive failed with exit code $LASTEXITCODE" }
    & (Join-Path $env:SystemRoot 'System32\tar.exe') -xf $archive -C $target
    if ($LASTEXITCODE -ne 0) { throw "tar extraction failed with exit code $LASTEXITCODE" }
}
finally {
    Remove-Item -LiteralPath $archive -ErrorAction SilentlyContinue
}

Write-Output $target
