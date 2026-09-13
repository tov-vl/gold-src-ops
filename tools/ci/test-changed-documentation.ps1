#Requires -Version 7.0

[CmdletBinding()]
param(
    [string]$BaseRevision,

    [string]$HeadRevision,

    [string[]]$ChangedPath,

    [string]$RepositoryRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "../..")).Path
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if ($null -ne (Get-Variable -Name PSNativeCommandUseErrorActionPreference -ErrorAction SilentlyContinue)) {
    $PSNativeCommandUseErrorActionPreference = $false
}

$changedPathsSupplied = $PSBoundParameters.ContainsKey("ChangedPath")

function Test-DocumentationPath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    $normalizedPath = $Path.Replace("\", "/")
    $fileName = [IO.Path]::GetFileName($normalizedPath)
    return $normalizedPath.EndsWith(".md", [StringComparison]::OrdinalIgnoreCase) -or
        $fileName -in @("LICENSE", "LICENSE.txt", "LICENSE.md")
}

function Get-ChangedPaths {
    if ($changedPathsSupplied) {
        return @($ChangedPath | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    }

    $revisionPattern = '^[0-9a-fA-F]{40}$'
    if ($BaseRevision -notmatch $revisionPattern -or $HeadRevision -notmatch $revisionPattern) {
        throw "Both full Git revisions are required when ChangedPath is not supplied."
    }

    Push-Location -LiteralPath $RepositoryRoot
    try {
        $revisionRange = "$BaseRevision...$HeadRevision"
        $paths = @(& git -c core.quotepath=false diff --name-only --diff-filter=ACDMRTUXB $revisionRange -- 2>&1)
        if ($LASTEXITCODE -ne 0) {
            throw "git diff exited with code $LASTEXITCODE."
        }
        return @($paths | ForEach-Object { [string]$_ } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    }
    finally {
        Pop-Location
    }
}

function Get-TrackedMarkdownPaths {
    Push-Location -LiteralPath $RepositoryRoot
    try {
        $paths = @(& git -c core.quotepath=false ls-files -- "*.md" 2>&1)
        if ($LASTEXITCODE -ne 0) {
            throw "git ls-files exited with code $LASTEXITCODE."
        }
        return @($paths | ForEach-Object { [string]$_ } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    }
    finally {
        Pop-Location
    }
}

function Get-MarkdownTargets {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Content
    )

    $targets = [Collections.Generic.List[string]]::new()
    $insideFence = $false
    $fenceCharacter = [char]0

    foreach ($line in $Content -split "`r?`n") {
        if ($line -match '^\s*(?<fence>`{3,}|~{3,})') {
            $candidate = $Matches.fence
            if (-not $insideFence) {
                $insideFence = $true
                $fenceCharacter = $candidate[0]
            }
            elseif ($candidate[0] -eq $fenceCharacter) {
                $insideFence = $false
                $fenceCharacter = [char]0
            }
            continue
        }

        if ($insideFence) {
            continue
        }

        foreach ($match in [regex]::Matches(
                $line,
                '!?(?<!\\)\[[^\]]*\]\((?<target><[^>]+>|[^\s\)]+)(?:\s+[''\"][^''\"]*[''\"])?\)')) {
            $targets.Add($match.Groups['target'].Value)
        }

        if ($line -match '^\s*\[[^\]]+\]:\s*(?<target><[^>]+>|\S+)') {
            $targets.Add($Matches.target)
        }
    }

    return $targets
}

function Test-LocalMarkdownTarget {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Target
    )

    $trimmedTarget = $Target.Trim().Trim('<', '>')
    return -not (
        [string]::IsNullOrWhiteSpace($trimmedTarget) -or
        $trimmedTarget.StartsWith("#", [StringComparison]::Ordinal) -or
        $trimmedTarget.StartsWith("/", [StringComparison]::Ordinal) -or
        $trimmedTarget.StartsWith("//", [StringComparison]::Ordinal) -or
        $trimmedTarget -match '^[a-zA-Z][a-zA-Z0-9+.-]*:'
    )
}

$resolvedRoot = [IO.Path]::GetFullPath($RepositoryRoot).TrimEnd(
    [IO.Path]::DirectorySeparatorChar,
    [IO.Path]::AltDirectorySeparatorChar)
$rootPrefix = $resolvedRoot + [IO.Path]::DirectorySeparatorChar
$paths = @(Get-ChangedPaths)

if ($paths.Count -eq 0) {
    throw "The documentation fast path requires at least one changed path."
}

$unexpectedPaths = @($paths | Where-Object { -not (Test-DocumentationPath -Path $_) })
if ($unexpectedPaths.Count -gt 0) {
    throw "The documentation fast path received a non-documentation path: $($unexpectedPaths[0])"
}

$validationPaths = @(
    $paths
    Get-TrackedMarkdownPaths
) | Sort-Object -Unique

$checkedFiles = 0
$checkedLinks = 0
foreach ($relativePath in $validationPaths) {
    $normalizedPath = $relativePath.Replace("/", [IO.Path]::DirectorySeparatorChar)
    $fullPath = [IO.Path]::GetFullPath((Join-Path $resolvedRoot $normalizedPath))
    if (-not $fullPath.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Changed documentation path escapes the repository root: $relativePath"
    }
    if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
        continue
    }

    $bytes = [IO.File]::ReadAllBytes($fullPath)
    if ($bytes.Count -gt 0 -and $bytes[-1] -ne 10) {
        throw "Documentation file must end with a newline: $relativePath"
    }
    $checkedFiles++

    if (-not $relativePath.EndsWith(".md", [StringComparison]::OrdinalIgnoreCase)) {
        continue
    }

    $content = [IO.File]::ReadAllText($fullPath)
    $sourceDirectory = Split-Path -Parent $fullPath
    foreach ($rawTarget in Get-MarkdownTargets -Content $content) {
        if (-not (Test-LocalMarkdownTarget -Target $rawTarget)) {
            continue
        }

        $targetWithoutFragment = ($rawTarget.Trim().Trim('<', '>') -split '[?#]', 2)[0]
        if ([string]::IsNullOrWhiteSpace($targetWithoutFragment)) {
            continue
        }

        try {
            $decodedTarget = [Uri]::UnescapeDataString($targetWithoutFragment)
        }
        catch {
            throw "Documentation link has invalid escaping in ${relativePath}: $rawTarget"
        }

        $targetPath = [IO.Path]::GetFullPath((Join-Path $sourceDirectory $decodedTarget))
        if (-not $targetPath.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Documentation link escapes the repository root in ${relativePath}: $rawTarget"
        }
        if (-not (Test-Path -LiteralPath $targetPath)) {
            throw "Documentation link target does not exist in ${relativePath}: $rawTarget"
        }
        $checkedLinks++
    }
}

[pscustomobject]@{
    ChangedPaths = $paths.Count
    CheckedFiles = $checkedFiles
    CheckedLocalLinks = $checkedLinks
}
