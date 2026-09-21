#Requires -Version 7.0

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("pull_request", "push", "workflow_dispatch")]
    [string]$EventName,

    [string]$Ref,

    [string]$BaseRevision,

    [string]$HeadRevision,

    [string[]]$ChangedPath,

    [string]$GitHubOutputPath,

    [string]$RepositoryRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "../..")).Path
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if ($null -ne (Get-Variable -Name PSNativeCommandUseErrorActionPreference -ErrorAction SilentlyContinue)) {
    $PSNativeCommandUseErrorActionPreference = $false
}

function Test-DocumentationPath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    $normalizedPath = $Path.Replace("\", "/")
    if ($normalizedPath.StartsWith("/", [StringComparison]::Ordinal) -or
        $normalizedPath -match '^[a-zA-Z]:' -or
        $normalizedPath -match '(^|/)\.\.?(/|$)') {
        return $false
    }

    $fileName = [IO.Path]::GetFileName($normalizedPath)
    return $normalizedPath.EndsWith(".md", [StringComparison]::OrdinalIgnoreCase) -or
        $fileName -in @("LICENSE", "LICENSE.txt", "LICENSE.md")
}

function Write-GitHubOutput {
    param(
        [Parameter(Mandatory = $true)]
        [Collections.IDictionary]$Values
    )

    if ([string]::IsNullOrWhiteSpace($GitHubOutputPath)) {
        return
    }

    $lines = foreach ($entry in $Values.GetEnumerator()) {
        "{0}={1}" -f $entry.Key, $entry.Value
    }
    $content = ($lines -join [Environment]::NewLine) + [Environment]::NewLine
    [IO.File]::AppendAllText($GitHubOutputPath, $content)
}

function New-ScopeResult {
    param(
        [Parameter(Mandatory = $true)]
        [bool]$DocsOnly,

        [bool]$StablePromotion = $false,

        [Parameter(Mandatory = $true)]
        [int]$ChangedCount,

        [Parameter(Mandatory = $true)]
        [string]$Reason,

        [string]$ResolvedBaseRevision,

        [string]$ResolvedHeadRevision
    )

    $mode = if ($DocsOnly) {
        "docs-only"
    }
    elseif ($StablePromotion) {
        "stable-promotion"
    }
    else {
        "full"
    }
    Write-GitHubOutput -Values ([ordered]@{
            docs_only = $DocsOnly.ToString().ToLowerInvariant()
            stable_promotion = $StablePromotion.ToString().ToLowerInvariant()
            mode = $mode
            changed_count = $ChangedCount
            reason = $Reason
            base_revision = $ResolvedBaseRevision
            head_revision = $ResolvedHeadRevision
        })

    return [pscustomobject]@{
        DocsOnly = $DocsOnly
        StablePromotion = $StablePromotion
        Mode = $mode
        ChangedCount = $ChangedCount
        Reason = $Reason
        BaseRevision = $ResolvedBaseRevision
        HeadRevision = $ResolvedHeadRevision
    }
}

if ($EventName -eq "workflow_dispatch") {
    return New-ScopeResult -DocsOnly $false -ChangedCount 0 -Reason "manual-run"
}

if ($EventName -eq "push") {
    if ([string]::IsNullOrWhiteSpace($Ref)) {
        return New-ScopeResult -DocsOnly $false -ChangedCount 0 -Reason "unresolved-ref"
    }
    if ($Ref.StartsWith("refs/tags/", [StringComparison]::Ordinal)) {
        if ($Ref -match '^refs/tags/v(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)$') {
            return New-ScopeResult `
                -DocsOnly $false `
                -StablePromotion $true `
                -ChangedCount 0 `
                -Reason "stable-release-tag"
        }
        return New-ScopeResult -DocsOnly $false -ChangedCount 0 -Reason "release-tag"
    }
    if ($Ref -cne "refs/heads/main") {
        return New-ScopeResult -DocsOnly $false -ChangedCount 0 -Reason "unsupported-push-ref"
    }
}

$paths = @()
if ($PSBoundParameters.ContainsKey("ChangedPath")) {
    $paths = @($ChangedPath | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
}
else {
    $revisionPattern = '^[0-9a-fA-F]{40}$'
    if ($BaseRevision -notmatch $revisionPattern -or
        $HeadRevision -notmatch $revisionPattern -or
        $BaseRevision -eq ('0' * 40)) {
        return New-ScopeResult `
            -DocsOnly $false `
            -ChangedCount 0 `
            -Reason "unresolved-revision" `
            -ResolvedBaseRevision $BaseRevision `
            -ResolvedHeadRevision $HeadRevision
    }

    try {
        Push-Location -LiteralPath $RepositoryRoot
        try {
            $revisionRange = "$BaseRevision...$HeadRevision"
            $paths = @(& git -c core.quotepath=false diff --name-only --diff-filter=ACDMRTUXB $revisionRange -- 2>&1)
            if ($LASTEXITCODE -ne 0) {
                throw "git diff exited with code $LASTEXITCODE."
            }
        }
        finally {
            Pop-Location
        }
        $paths = @($paths | ForEach-Object { [string]$_ } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    }
    catch {
        Write-Warning "The change range could not be resolved; the full CI path will run."
        return New-ScopeResult `
            -DocsOnly $false `
            -ChangedCount 0 `
            -Reason "diff-failed" `
            -ResolvedBaseRevision $BaseRevision `
            -ResolvedHeadRevision $HeadRevision
    }
}

if ($paths.Count -eq 0) {
    return New-ScopeResult `
        -DocsOnly $false `
        -ChangedCount 0 `
        -Reason "empty-diff" `
        -ResolvedBaseRevision $BaseRevision `
        -ResolvedHeadRevision $HeadRevision
}

$docsOnly = @($paths | Where-Object { -not (Test-DocumentationPath -Path $_) }).Count -eq 0
$reason = if ($docsOnly) { "documentation-only" } else { "executable-or-config-change" }
return New-ScopeResult `
    -DocsOnly $docsOnly `
    -ChangedCount $paths.Count `
    -Reason $reason `
    -ResolvedBaseRevision $BaseRevision `
    -ResolvedHeadRevision $HeadRevision
