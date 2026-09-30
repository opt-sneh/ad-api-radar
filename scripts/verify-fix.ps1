param(
    [Parameter(Mandatory = $true)][string]$Bench,
    [Parameter(Mandatory = $true)][string]$Patch,
    [Parameter(Mandatory = $true)][string]$Project,
    [string]$Name = (Get-Date -Format 'yyyyMMdd-HHmmss'),
    [switch]$SkipBaseline
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$root = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$benchRoot = [System.IO.Path]::GetFullPath((Join-Path $root 'data\bench'))
$verifyRoot = [System.IO.Path]::GetFullPath((Join-Path $root 'data\verify'))
function Resolve-InputPath([string]$value) {
    if ([System.IO.Path]::IsPathRooted($value)) { return [System.IO.Path]::GetFullPath($value) }
    return [System.IO.Path]::GetFullPath((Join-Path $root $value))
}
function Build-Errors([string]$project, [string]$copyRoot) {
    $output = @(& dotnet build $project -v q -nologo 2>&1 | ForEach-Object { $_.ToString() })
    $buildExit = $LASTEXITCODE
    $errors = [System.Collections.Generic.List[string]]::new()
    foreach ($line in $output) {
        if ($line -notmatch '^(?<file>.+?)\s*:\s*error (?<code>[A-Z]+\d+): (?<message>.*)$') { continue }
        $file = $Matches.file.Trim() -replace '\(\d+,\d+(?:,\d+,\d+)?\)$', ''
        $relative = if ([System.IO.Path]::IsPathRooted($file)) {
            $prefix = $copyRoot.TrimEnd('\') + '\'
            if ($file.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
                $file.Substring($prefix.Length)
            } else { $file }
        } else { $file }
        $message = ($Matches.message -replace '\s+\[[^\]]+\.csproj\]$', '').Trim()
        $errors.Add(($relative.Replace('\', '/').ToLowerInvariant() + ' | ' + $Matches.code + ' | ' + $message))
    }
    return [pscustomobject]@{ ExitCode = $buildExit; Errors = @($errors.ToArray()) }
}

$source = Resolve-InputPath $Bench
$patchFile = Resolve-InputPath $Patch
$destination = [System.IO.Path]::GetFullPath((Join-Path $verifyRoot $Name))
$oldCeiling = $env:GIT_CEILING_DIRECTORIES
$exitCode = 0
try {
    if (-not $source.StartsWith($benchRoot + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Bench must be under $benchRoot"
    }
    if (-not (Test-Path -LiteralPath $source -PathType Container)) { throw "Bench folder not found: $source" }
    if (-not (Test-Path -LiteralPath $patchFile -PathType Leaf)) { throw "Patch not found: $patchFile" }
    if (-not $destination.StartsWith($verifyRoot + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Destination must be under $verifyRoot"
    }
    if (Test-Path -LiteralPath $destination) { throw "Refusing to overwrite: $destination" }
    $projectFile = [System.IO.Path]::GetFullPath((Join-Path $destination $Project))
    if (-not $projectFile.StartsWith($destination + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw 'Project must be relative to the bench folder.'
    }

    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    robocopy $source $destination /MIR /XD bin obj | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "robocopy failed with exit code $LASTEXITCODE" }
    Push-Location $destination
    try {
        if (-not $SkipBaseline) { $baseline = Build-Errors $Project $destination }

        $targetLine = Get-Content -LiteralPath $patchFile | Where-Object { $_.StartsWith('+++ b/') } | Select-Object -First 1
        if (-not $targetLine) { throw 'Patch has no +++ b/ target.' }
        $patchedFile = [System.IO.Path]::GetFullPath((Join-Path $destination $targetLine.Substring(6)))
        if (-not $patchedFile.StartsWith($destination + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw 'Patch target escapes the verify copy.'
        }
        $beforeHash = if (Test-Path -LiteralPath $patchedFile -PathType Leaf) { (Get-FileHash -LiteralPath $patchedFile -Algorithm SHA256).Hash } else { $null }

        $env:GIT_CEILING_DIRECTORIES = $verifyRoot
        git apply --check $patchFile
        if ($LASTEXITCODE -ne 0) { throw "git apply --check failed with exit code $LASTEXITCODE" }
        git apply $patchFile
        if ($LASTEXITCODE -ne 0) { throw "git apply failed with exit code $LASTEXITCODE" }
        $afterHash = if (Test-Path -LiteralPath $patchedFile -PathType Leaf) { (Get-FileHash -LiteralPath $patchedFile -Algorithm SHA256).Hash } else { $null }
        if ($beforeHash -eq $afterHash) { throw "Patch target did not change: $patchedFile" }

        $patched = Build-Errors $Project $destination
        if ($SkipBaseline) {
            Write-Output "Patched errors: $($patched.Errors.Count)"
            if ($patched.ExitCode -ne 0) { throw "dotnet build failed with exit code $($patched.ExitCode)" }
            Write-Output 'VERIFY PASS (exit code 0)'
        } else {
            Write-Output "Baseline errors: $($baseline.Errors.Count) (exit $($baseline.ExitCode)); patched errors: $($patched.Errors.Count) (exit $($patched.ExitCode))"
            # A failed build we cannot read (restore/tooling/unparsed diagnostics) proves nothing; neither does a newly failing one.
            if ($patched.ExitCode -ne 0 -and $patched.Errors.Count -eq 0) { throw 'INCONCLUSIVE: patched build failed with no parseable compiler errors' }
            if ($baseline.ExitCode -ne 0 -and $baseline.Errors.Count -eq 0) { throw 'INCONCLUSIVE: baseline build failed with no parseable compiler errors' }
            if ($patched.ExitCode -ne 0 -and $baseline.ExitCode -eq 0) { throw 'BUILD_FAILED: baseline built, patched build failed' }
            $remaining = @{}
            foreach ($buildError in $baseline.Errors) { $remaining[$buildError] = [int]$remaining[$buildError] + 1 }
            $newErrors = [System.Collections.Generic.List[string]]::new()
            foreach ($buildError in $patched.Errors) {
                if ($remaining.ContainsKey($buildError) -and $remaining[$buildError] -gt 0) { $remaining[$buildError]-- }
                else { $newErrors.Add($buildError) }
            }
            if ($newErrors.Count -eq 0) {
                if ($patched.ExitCode -ne 0) {
                    Write-Output "INCONCLUSIVE NO_NEW_ERRORS (patched build exited $($patched.ExitCode); $($baseline.Errors.Count) pre-existing errors)"
                    $exitCode = 1
                } else {
                    Write-Output "VERIFY PASS VERIFIED_BUILD (no new errors; $($baseline.Errors.Count) pre-existing)"
                }
            } else {
                Write-Output "VERIFY FAIL: $($newErrors.Count) new errors"
                foreach ($buildError in $newErrors) { Write-Output $buildError }
                $exitCode = 1
            }
        }
    }
    finally { Pop-Location }
}
catch {
    $exitCode = if ($LASTEXITCODE -is [int] -and $LASTEXITCODE -ne 0) { $LASTEXITCODE } else { 1 }
    Write-Output "VERIFY FAIL (exit code $exitCode): $_"
}
finally {
    if ($null -eq $oldCeiling) { Remove-Item Env:GIT_CEILING_DIRECTORIES -ErrorAction SilentlyContinue }
    else { $env:GIT_CEILING_DIRECTORIES = $oldCeiling }
}
exit $exitCode
