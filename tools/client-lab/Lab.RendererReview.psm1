#Requires -Version 7.0
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
Import-Module (Join-Path $PSScriptRoot 'Lab.Tooling.psm1')
# Load only fixed repository-owned functions, never collected text.
. ([scriptblock]::Create([IO.File]::ReadAllText((Join-Path $PSScriptRoot 'templates/code/Lab.Common.ps1.in'))))
. ([scriptblock]::Create([IO.File]::ReadAllText((Join-Path $PSScriptRoot 'templates/code/Diagnostics.ps1.in'))))

function Get-LabRendererReview([string]$ResultPath,[string]$JobId) {
    $path=Assert-LabOutputPath $ResultPath
    $item=Get-Item -LiteralPath $path
    if($item.PSIsContainer -or $item.Length -gt 65536){throw 'Invalid renderer result file.'}
    $bytes=[IO.File]::ReadAllBytes($path)
    if($bytes.Length -gt 65536){throw 'Renderer result exceeds its limit.'}
    $result=[Text.UTF8Encoding]::new($false,$true).GetString($bytes)|ConvertFrom-Json
    if($result.status -cnotin @('captured_unreviewed','autonomous_captured_unreviewed') -or $result.failureCode -or
        $result.configCleanupVerified -isnot [bool] -or -not $result.configCleanupVerified -or
        $result.rendererVerified -isnot [bool] -or $result.rendererVerified){throw 'Client trial is not eligible for renderer review.'}
    $identity=Test-LabRendererEvidence $result $JobId
    return [ordered]@{schema=1;jobId=$JobId;rendererIdentified=$true;sourceResultSha256=(Get-LabRendererHash $bytes);
        identity=$identity;frameSha256=$result.rendererEvidence.frameSha256;legacyRendererFlagUnchanged=$true;
        claim='Engine-reported OpenGL identity bound to the owned process; no hardware acceleration or performance attestation.';reviewedUtc=[DateTime]::UtcNow.ToString('o')}
}

function Assert-LabRendererReceipt($Receipt,$Expected) {
    if($Receipt.schema -ne 1 -or $Receipt.jobId -cne $Expected.jobId -or $Receipt.rendererIdentified -isnot [bool] -or -not $Receipt.rendererIdentified -or
        $Receipt.sourceResultSha256 -cne $Expected.sourceResultSha256 -or $Receipt.frameSha256 -cne $Expected.frameSha256 -or
        $Receipt.legacyRendererFlagUnchanged -isnot [bool] -or -not $Receipt.legacyRendererFlagUnchanged -or $Receipt.claim -cne $Expected.claim){throw 'Renderer receipt binding differs.'}
    foreach($key in @('backend','vendor','renderer','version')){if($Receipt.identity[$key] -cne $Expected.identity[$key]){throw 'Renderer receipt projection differs.'}}
    $reviewed=if($Receipt.reviewedUtc -is [DateTimeOffset]){$Receipt.reviewedUtc.UtcDateTime}else{ConvertTo-LabUtc $Receipt.reviewedUtc}
    if($reviewed -gt [DateTime]::UtcNow.AddSeconds(5)){throw 'Renderer receipt time differs.'}
}

Export-ModuleMember -Function Get-LabRendererReview,Assert-LabRendererReceipt
