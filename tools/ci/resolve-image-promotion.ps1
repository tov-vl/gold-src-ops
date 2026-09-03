#Requires -Version 7.0

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$SourceDigest,

    [string]$ExistingDigest,

    [string]$ExistingMediaType,

    [string]$ExistingChildDigestsJson = '[]',

    [switch]$AllowExistingReference,

    [switch]$AllowSingleManifestRecovery,

    [string]$GitHubOutputPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$digestPattern = '\Asha256:[0-9a-f]{64}\z'
$indexMediaTypes = @(
    'application/vnd.oci.image.index.v1+json',
    'application/vnd.docker.distribution.manifest.list.v2+json')

function Assert-Digest {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Digest,

        [Parameter(Mandatory = $true)]
        [string]$Context
    )

    if (-not [regex]::IsMatch($Digest, $digestPattern)) {
        throw "$Context must be a lowercase sha256 digest."
    }
}

Assert-Digest -Digest $SourceDigest -Context 'Source digest'

try {
    $parsedChildDigests = ConvertFrom-Json -InputObject $ExistingChildDigestsJson -NoEnumerate
}
catch {
    throw 'Existing child digests must be a JSON array.'
}

if ($parsedChildDigests -isnot [array]) {
    throw 'Existing child digests must be a JSON array.'
}

$childDigests = @($parsedChildDigests)
foreach ($childDigest in $childDigests) {
    if ($childDigest -isnot [string]) {
        throw 'Every existing child digest must be a string.'
    }

    Assert-Digest -Digest $childDigest -Context 'Existing child digest'
}

$hasExistingReference = -not [string]::IsNullOrWhiteSpace($ExistingDigest)

if (-not $hasExistingReference) {
    if (-not [string]::IsNullOrWhiteSpace($ExistingMediaType) -or $childDigests.Count -ne 0) {
        throw 'Existing manifest metadata requires an existing digest.'
    }

    $mode = 'promote'
}
else {
    Assert-Digest -Digest $ExistingDigest -Context 'Existing digest'

    if (-not $AllowExistingReference) {
        throw 'The immutable image tag already exists and cannot be reused automatically.'
    }

    if ($ExistingDigest -ceq $SourceDigest) {
        $mode = 'verify'
    }
    elseif (-not $AllowSingleManifestRecovery) {
        throw 'The existing image digest does not match the verified source digest.'
    }
    elseif ($indexMediaTypes -cnotcontains $ExistingMediaType) {
        throw 'Single-manifest recovery requires an OCI index or Docker manifest list.'
    }
    elseif ($childDigests.Count -ne 1 -or $childDigests[0] -cne $SourceDigest) {
        throw 'Single-manifest recovery requires exactly one child equal to the verified source digest.'
    }
    else {
        $mode = 'recover'
    }
}

if (-not [string]::IsNullOrWhiteSpace($GitHubOutputPath)) {
    [IO.File]::AppendAllText(
        $GitHubOutputPath,
        "mode=$mode" + [Environment]::NewLine,
        [Text.UTF8Encoding]::new($false))
}

$mode
