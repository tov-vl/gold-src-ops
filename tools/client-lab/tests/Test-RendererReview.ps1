#Requires -Version 7.0
param([Parameter(Mandatory)][string]$Workspace,[string]$ToolRoot=(Join-Path $PSScriptRoot '..'))
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $ToolRoot 'Lab.RendererReview.psm1') -Force
$root=Join-Path $Workspace ('renderer-review-'+[guid]::NewGuid().ToString('N'))
$null=New-Item -ItemType Directory -Path $root
$id=[guid]::NewGuid().ToString('D');$token=([guid]$id).ToString('N')
$frame="GSO_RENDERER_BEGIN_$token`nGL Vendor: Fixture`nGL Renderer: Synthetic`nGL Version: 4.6`nGSO_RENDERER_END_$token`n"
$bytes=[Text.Encoding]::ASCII.GetBytes($frame)
$started=[DateTime]::UtcNow.AddSeconds(-10).ToString('o')
$result=@{id=$id;status='autonomous_captured_unreviewed';failureCode=$null;gamePid=1234;gameStartedUtc=$started;
    completedUtc=[DateTime]::UtcNow.AddSeconds(-1).ToString('o');cleanupVerified=$true;configCleanupVerified=$true;rendererVerified=$false;
    rendererEvidence=@{schema=1;jobId=$id;status='identified';reason='process_bound_gl_dump';probeEnabled=$true;cleanupVerified=$true;
        gamePid=1234;gameStartedUtc=$started;observedUtc=[DateTime]::UtcNow.AddSeconds(-5).ToString('o');
        identity=@{backend='OpenGL';vendor='Fixture';renderer='Synthetic';version='4.6'};frameBase64=[Convert]::ToBase64String($bytes);
        frameSha256=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant();frameBytes=$bytes.Length}}
$path=Join-Path $root 'result.json'
[IO.File]::WriteAllText($path,($result|ConvertTo-Json -Depth 8))
$hash=(Get-FileHash $path).Hash
$review=Get-LabRendererReview $path $id
$checks=0
function Check([bool]$Value){if(-not $Value){throw 'Renderer review assertion failed.'};$script:checks++}
function Reject([scriptblock]$Action){$failed=$false;try{& $Action|Out-Null}catch{$failed=$true};Check $failed}
Check ($review.rendererIdentified -and $review.sourceResultSha256 -ceq $hash.ToLowerInvariant())
foreach($time in @([DateTime]::UtcNow.ToString('o'),[DateTime]::UtcNow,[DateTimeOffset]::UtcNow)){
    $review.reviewedUtc=$time;Assert-LabRendererReceipt $review $review;$checks++
}
$review.reviewedUtc=[DateTime]::UtcNow.AddMinutes(1)
Reject {Assert-LabRendererReceipt $review $review}
$review.reviewedUtc='not a date';Reject {Assert-LabRendererReceipt $review $review}
$review=Get-LabRendererReview $path $id
foreach($name in @('jobId','sourceResultSha256','frameSha256','rendererIdentified','legacyRendererFlagUnchanged','claim')){
    $receipt=($review|ConvertTo-Json -Depth 8)|ConvertFrom-Json -AsHashtable
    $receipt[$name]='invalid';Reject {Assert-LabRendererReceipt $receipt $review}
}
$receipt=($review|ConvertTo-Json -Depth 8)|ConvertFrom-Json -AsHashtable
$receipt.identity.vendor='Other';Reject {Assert-LabRendererReceipt $receipt $review}
Check ((Get-FileHash $path).Hash -ceq $hash)
Reject {Get-LabRendererReview $path ([guid]::NewGuid().ToString('D'))}
[IO.File]::AppendAllText($path,' ')
$changed=Get-LabRendererReview $path $id
Reject {Assert-LabRendererReceipt $review $changed}
@{passed=$true;checks=$checks;targetNetworkUsed=$false;runtimeChanged=$false}|ConvertTo-Json|
    Set-Content -LiteralPath (Join-Path $Workspace 'renderer-review-summary.json') -Encoding utf8NoBOM
@{passed=$true;checks=$checks}|ConvertTo-Json
