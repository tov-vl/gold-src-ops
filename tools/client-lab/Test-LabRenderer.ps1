#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$ResultPath,
    [Parameter(Mandatory=$true)][string]$JobId,
    [Parameter(Mandatory=$true)][string]$OutputPath
)
$ErrorActionPreference='Stop'
Import-Module (Join-Path $PSScriptRoot 'Lab.Tooling.psm1') -Force
$output=Assert-LabOutputPath $OutputPath
# Load only repository-owned functions; no collected text is evaluated.
. ([scriptblock]::Create([IO.File]::ReadAllText((Join-Path $PSScriptRoot 'templates/code/Lab.Common.ps1.in'))))
. ([scriptblock]::Create([IO.File]::ReadAllText((Join-Path $PSScriptRoot 'templates/code/Diagnostics.ps1.in'))))
$item=Get-Item -LiteralPath $ResultPath
if($item.Length -gt 65536 -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Invalid renderer result file.'}
$sourceBytes=[IO.File]::ReadAllBytes($item.FullName)
if($sourceBytes.Length -gt 65536){throw 'Renderer result exceeds its limit.'}
$result=(New-Object Text.UTF8Encoding($false,$true)).GetString($sourceBytes)|ConvertFrom-Json
if($result.status -cnotin @('captured_unreviewed','autonomous_captured_unreviewed') -or $result.failureCode -or
    $result.configCleanupVerified -isnot [bool] -or -not $result.configCleanupVerified -or
    $result.rendererVerified -isnot [bool] -or $result.rendererVerified){throw 'Client trial is not eligible for renderer review.'}
$identity=Test-LabRendererEvidence $result $JobId
$receipt=[ordered]@{schema=1;jobId=$JobId;rendererIdentified=$true;sourceResultSha256=(Get-LabRendererHash $sourceBytes);
    identity=$identity;frameSha256=$result.rendererEvidence.frameSha256;legacyRendererFlagUnchanged=$true;
    claim='Engine-reported OpenGL identity bound to the owned process; no hardware acceleration or performance attestation.';reviewedUtc=[DateTime]::UtcNow.ToString('o')}
$bytes=[Text.Encoding]::UTF8.GetBytes(($receipt|ConvertTo-Json -Depth 8))
$stream=[IO.File]::Open($output,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
try{$stream.Write($bytes,0,$bytes.Length)}finally{$stream.Dispose()}
$receipt|ConvertTo-Json -Depth 8
