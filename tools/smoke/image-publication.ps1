#Requires -Version 7.0

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$resolverPath = Join-Path $PSScriptRoot "../ci/resolve-image-tag.ps1"
$promotionResolverPath = Join-Path $PSScriptRoot "../ci/resolve-image-promotion.ps1"

function Assert-Equal {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Actual,

        [Parameter(Mandatory = $true)]
        [object]$Expected,

        [Parameter(Mandatory = $true)]
        [string]$Context
    )

    if ($Actual -cne $Expected) {
        throw "$Context Expected '$Expected', got '$Actual'."
    }
}

function Assert-ValidTag {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Tag,

        [Parameter(Mandatory = $true)]
        [string]$ExpectedVersion,

        [Parameter(Mandatory = $true)]
        [string]$ExpectedBaseVersion,

        [Parameter(Mandatory = $true)]
        [string]$ExpectedChannel
    )

    $result = & $resolverPath -Tag $Tag

    Assert-Equal -Actual $result.Tag -Expected $Tag -Context "$Tag tag mismatch."
    Assert-Equal -Actual $result.Version -Expected $ExpectedVersion -Context "$Tag version mismatch."
    Assert-Equal -Actual $result.BaseVersion -Expected $ExpectedBaseVersion -Context "$Tag base version mismatch."
    Assert-Equal -Actual $result.Channel -Expected $ExpectedChannel -Context "$Tag channel mismatch."
}

function Assert-InvalidTag {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Tag
    )

    try {
        $null = & $resolverPath -Tag $Tag
    }
    catch {
        if ($_.Exception.Message -notlike "Image publication tag must match*") {
            throw
        }

        return
    }

    throw "Invalid image publication tag '$Tag' was accepted."
}

function Assert-PromotionMode {
    param(
        [Parameter(Mandatory = $true)]
        [hashtable]$Arguments,

        [Parameter(Mandatory = $true)]
        [string]$ExpectedMode
    )

    $actualMode = & $promotionResolverPath @Arguments
    Assert-Equal -Actual $actualMode -Expected $ExpectedMode -Context 'Image promotion mode mismatch.'
}

function Assert-InvalidPromotion {
    param(
        [Parameter(Mandatory = $true)]
        [hashtable]$Arguments,

        [Parameter(Mandatory = $true)]
        [string]$ExpectedMessage
    )

    try {
        $null = & $promotionResolverPath @Arguments
    }
    catch {
        if ($_.Exception.Message -notlike $ExpectedMessage) {
            throw
        }

        return
    }

    throw 'Invalid image promotion state was accepted.'
}

$validCases = @(
    @{ Tag = 'v0.0.1'; Version = '0.0.1'; BaseVersion = '0.0.1'; Channel = 'stable' },
    @{ Tag = 'v2.3.0'; Version = '2.3.0'; BaseVersion = '2.3.0'; Channel = 'stable' },
    @{ Tag = 'v2.3.0-rc.1'; Version = '2.3.0-rc.1'; BaseVersion = '2.3.0'; Channel = 'release-candidate' },
    @{ Tag = 'v10.20.30-rc.42'; Version = '10.20.30-rc.42'; BaseVersion = '10.20.30'; Channel = 'release-candidate' })

foreach ($case in $validCases) {
    Assert-ValidTag `
        -Tag $case.Tag `
        -ExpectedVersion $case.Version `
        -ExpectedBaseVersion $case.BaseVersion `
        -ExpectedChannel $case.Channel
}

$invalidTags = @(
    '2.3.0',
    'v2.3',
    'v2.3.0.0',
    'v02.3.0',
    'v2.03.0',
    'v2.3.00',
    'v2.3.0-beta.1',
    'v2.3.0-rc',
    'v2.3.0-rc.0',
    'v2.3.0-rc.01',
    'v2.3.0-rc.1.1',
    "v2.3.0-rc.1`n")

foreach ($tag in $invalidTags) {
    Assert-InvalidTag -Tag $tag
}

try {
    $null = & $resolverPath -Tag 'v2.3.0' -ExpectedChannel 'release-candidate'
    throw 'Stable image tag was accepted as a release candidate.'
}
catch {
    if ($_.Exception.Message -notlike "Image publication tag 'v2.3.0' belongs to channel*") {
        throw
    }
}

try {
    $null = & $resolverPath -Tag 'v2.3.0-rc.1' -ExpectedBaseVersion '2.4.0'
    throw 'Release-candidate image tag was accepted for a different base version.'
}
catch {
    if ($_.Exception.Message -notlike "Image publication tag 'v2.3.0-rc.1' has base version*") {
        throw
    }
}

$outputPath = [IO.Path]::GetTempFileName()

try {
    $outputResult = & $resolverPath `
        -Tag 'v2.3.0-rc.7' `
        -GitHubOutputPath $outputPath `
        -ExpectedChannel 'release-candidate' `
        -ExpectedBaseVersion '2.3.0'
    $outputs = @{}

    foreach ($line in Get-Content -LiteralPath $outputPath) {
        $separator = $line.IndexOf('=')
        if ($separator -le 0) {
            throw "Malformed GitHub output line '$line'."
        }

        $outputs[$line.Substring(0, $separator)] = $line.Substring($separator + 1)
    }

    Assert-Equal -Actual $outputResult.Channel -Expected 'release-candidate' -Context 'Output result channel mismatch.'
    Assert-Equal -Actual $outputs['tag'] -Expected 'v2.3.0-rc.7' -Context 'GitHub tag output mismatch.'
    Assert-Equal -Actual $outputs['version'] -Expected '2.3.0-rc.7' -Context 'GitHub version output mismatch.'
    Assert-Equal -Actual $outputs['base_version'] -Expected '2.3.0' -Context 'GitHub base-version output mismatch.'
    Assert-Equal -Actual $outputs['channel'] -Expected 'release-candidate' -Context 'GitHub channel output mismatch.'
}
finally {
    Remove-Item -LiteralPath $outputPath -Force -ErrorAction SilentlyContinue
}

$sourceDigest = 'sha256:' + ('a' * 64)
$existingDigest = 'sha256:' + ('b' * 64)
$otherDigest = 'sha256:' + ('c' * 64)
$ociIndexMediaType = 'application/vnd.oci.image.index.v1+json'

Assert-PromotionMode `
    -Arguments @{ SourceDigest = $sourceDigest } `
    -ExpectedMode 'promote'
Assert-PromotionMode `
    -Arguments @{
        SourceDigest = $sourceDigest
        ExistingDigest = $sourceDigest
        AllowExistingReference = $true
    } `
    -ExpectedMode 'verify'
Assert-PromotionMode `
    -Arguments @{
        SourceDigest = $sourceDigest
        ExistingDigest = $existingDigest
        ExistingMediaType = $ociIndexMediaType
        ExistingChildDigestsJson = ConvertTo-Json -Compress @($sourceDigest)
        AllowExistingReference = $true
        AllowSingleManifestRecovery = $true
    } `
    -ExpectedMode 'recover'

Assert-InvalidPromotion `
    -Arguments @{
        SourceDigest = $sourceDigest
        ExistingDigest = $sourceDigest
    } `
    -ExpectedMessage '*immutable image tag already exists*'
Assert-InvalidPromotion `
    -Arguments @{
        SourceDigest = $sourceDigest
        ExistingDigest = $existingDigest
        ExistingMediaType = $ociIndexMediaType
        ExistingChildDigestsJson = ConvertTo-Json -Compress @($sourceDigest)
        AllowExistingReference = $true
    } `
    -ExpectedMessage '*does not match the verified source digest*'
Assert-InvalidPromotion `
    -Arguments @{
        SourceDigest = $sourceDigest
        ExistingDigest = $existingDigest
        ExistingMediaType = 'application/vnd.oci.image.manifest.v1+json'
        ExistingChildDigestsJson = ConvertTo-Json -Compress @($sourceDigest)
        AllowExistingReference = $true
        AllowSingleManifestRecovery = $true
    } `
    -ExpectedMessage '*requires an OCI index or Docker manifest list*'
Assert-InvalidPromotion `
    -Arguments @{
        SourceDigest = $sourceDigest
        ExistingDigest = $existingDigest
        ExistingMediaType = $ociIndexMediaType
        ExistingChildDigestsJson = ConvertTo-Json -Compress @($sourceDigest, $otherDigest)
        AllowExistingReference = $true
        AllowSingleManifestRecovery = $true
    } `
    -ExpectedMessage '*requires exactly one child equal to the verified source digest*'
Assert-InvalidPromotion `
    -Arguments @{
        SourceDigest = $sourceDigest
        ExistingDigest = $existingDigest
        ExistingMediaType = $ociIndexMediaType
        ExistingChildDigestsJson = ConvertTo-Json -Compress @($otherDigest)
        AllowExistingReference = $true
        AllowSingleManifestRecovery = $true
    } `
    -ExpectedMessage '*requires exactly one child equal to the verified source digest*'

$workflowPath = Join-Path $PSScriptRoot '../../.github/workflows/ci.yml'
$workflow = Get-Content -LiteralPath $workflowPath -Raw
$requiredWorkflowFragments = @(
    'release_tag:',
    'recover_single_manifest_wrapper:',
    '-File ./tools/ci/resolve-image-promotion.ps1',
    '--prefer-index=false')

foreach ($fragment in $requiredWorkflowFragments) {
    if (-not $workflow.Contains($fragment, [StringComparison]::Ordinal)) {
        throw "Image publication workflow is missing required fragment '$fragment'."
    }
}

Write-Host "Image publication smoke passed: $($validCases.Count) valid tags, $($invalidTags.Count) invalid tags, 2 compatibility cases, 8 promotion cases, and $($requiredWorkflowFragments.Count) workflow contracts."
