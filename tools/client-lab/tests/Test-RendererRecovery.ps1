#requires -Version 5.1
param([Parameter(Mandatory=$true)][string]$Workspace)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
. (Join-Path $Workspace 'kit/code/Lab.Common.ps1')
. (Join-Path $Workspace 'kit/code/Diagnostics.ps1')
$checks=0
function Check([bool]$Value,[string]$Name){if(-not $Value){throw $Name};$script:checks++}
function Refused([scriptblock]$Action){$failed=$false;try{& $Action|Out-Null}catch{$failed=$true};Check $failed 'Invalid recovery review was accepted.'}
function Assert-LabLease($Policy){}
function Get-Process([int]$Id){if($Id -ne $PID){throw 'Unexpected process read.'};return [pscustomobject]@{SessionId=7}}
function Get-LabClientObservation($Policy){return [pscustomobject]@{query=$script:query;truncated=$false;game=$script:games}}
function Move-LabRendererRecoveryFile([string]$Source,[string]$Destination){
    $script:moves++
    if($script:moveFailure -ceq 'before'){throw 'Injected move failure.'}
    [IO.File]::Move($Source,$Destination)
    if($script:moveFailure -ceq 'after'){throw 'Injected post-move uncertainty.'}
}
$sandbox=Join-Path $Workspace ('checks/renderer-recovery-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $sandbox|Out-Null
Check ((Invoke-LabRendererRecovery 'absent' @{} '').status -ceq 'not_requested') 'Disabled recovery touched state.'
foreach($case in @('success','hash','created','modified','nonce','extra-log','destination','busy','query','active-request','cleanup','pid','start','completed','move-before','move-after','claim-only','receipt-tamper')){
    $root=Join-Path $sandbox $case;$game=Join-Path $root 'game'
    $id=[guid]::NewGuid().ToString('D');$op=[guid]::NewGuid().ToString('D');$nonce=([guid]$id).ToString('N')
    foreach($d in @('queue',('results/'+$id),'game/cstrike')){New-Item -ItemType Directory -Path (Join-Path $root $d) -Force|Out-Null}
    $source=Join-Path $game 'qconsole.log';$archive=Join-Path $game ('renderer-retained-'+$op+'.log')
    $text="Startup`r`nGSO_RENDERER_BEGIN_$nonce `r`nGL Vendor: Test vendor`r`nGL Renderer: Test renderer`r`nGL Version: 4.2 test`r`nGL Extensions: GL_test GSO_RENDERER_END_$nonce `r`nOther startup output`r`n"
    if($case -ceq 'nonce'){$text=$text.Replace($nonce,'00000000000000000000000000000000')}
    [IO.File]::WriteAllBytes($source,[Text.Encoding]::ASCII.GetBytes($text))
    $start=[DateTime]::UtcNow.AddSeconds(-10);$created=$start.AddSeconds(1);$modified=$start.AddSeconds(7);$end=$start.AddSeconds(8)
    [IO.File]::SetCreationTimeUtc($source,$created);[IO.File]::SetLastWriteTimeUtc($source,$modified)
    $item=Get-Item -LiteralPath $source
    $result=[pscustomobject]@{id=$id;status='uncertain_cleanup';gamePid=1234;gameStartedUtc=$start.ToString('o');completedUtc=$end.ToString('o');cleanupVerified=$true;configCleanupVerified=$true;
        rendererEvidence=@{schema=1;jobId=$id;status='unconfirmed';probeEnabled=$true;cleanupVerified=$false}}
    [IO.File]::WriteAllText((Join-Path $root ('queue/'+$id+'.json')),(@{id=$id;action='verify-panel'}|ConvertTo-Json))
    $resultPath=Join-Path $root ('results/'+$id+'/result.json')
    $plan=[pscustomobject]@{schema=1;operationId=$op;jobId=$id;sha256=(Get-FileHash -LiteralPath $source).Hash.ToLowerInvariant();bytes=[long]$item.Length;
        createdUtc=$item.CreationTimeUtc.ToString('o');modifiedUtc=$item.LastWriteTimeUtc.ToString('o');gamePid=1234;gameStartedUtc=$start.ToString('o');completedUtc=$end.ToString('o')}
    if($case -ceq 'success'){
        foreach($badCase in @('extra','float-bytes','pid','future','empty-id','same-id','hash','schema')){
            $bad=$plan|ConvertTo-Json|ConvertFrom-Json
            switch($badCase){
                extra {$bad|Add-Member path 'C:\unapproved'}
                float-bytes {$bad.bytes=1.5}
                pid {$bad.gamePid=0}
                future {$bad.completedUtc=[DateTime]::UtcNow.AddMinutes(1).ToString('o')}
                empty-id {$bad.operationId=[guid]::Empty.ToString('D')}
                same-id {$bad.operationId=$bad.jobId}
                hash {$bad.sha256='Z'*64}
                schema {$bad.schema='1'}
            }
            Refused {Assert-LabRendererRecoveryPlan $bad}
        }
    }
    $script:games=@();$script:query='ok';$script:moves=0;$script:moveFailure=''
    switch($case){
        hash {$plan.sha256='a'*64}
        created {$plan.createdUtc=$created.AddSeconds(1).ToString('o')}
        modified {$plan.modifiedUtc=$modified.AddSeconds(-1).ToString('o')}
        extra-log {[IO.File]::WriteAllText((Join-Path $game 'console.log'),'preserve')}
        destination {[IO.File]::WriteAllText($archive,'preserve destination')}
        busy {$script:games=@(@{id=1234})}
        query {$script:query='unavailable'}
        active-request {$result.status='launch_claimed'}
        cleanup {$result.cleanupVerified=$false}
        pid {$plan.gamePid=1235}
        start {$plan.gameStartedUtc=$start.AddSeconds(-1).ToString('o')}
        completed {$plan.completedUtc=$end.AddSeconds(1).ToString('o')}
        move-before {$script:moveFailure='before'}
        move-after {$script:moveFailure='after'}
    }
    [IO.File]::WriteAllText($resultPath,($result|ConvertTo-Json -Depth 5))
    $resultHash=(Get-FileHash -LiteralPath $resultPath).Hash
    $originalHash=(Get-FileHash -LiteralPath $source).Hash
    $encoded=[Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes(($plan|ConvertTo-Json -Compress)))
    $policy=[pscustomobject]@{gameRoot=$game;gameUserSid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value;gameSessionId=7}
    if($case -ceq 'claim-only'){[IO.File]::WriteAllText((Join-Path $root ('results/'+$op+'.renderer-recovery.claim')),'preserved claim')}
    $report=Invoke-LabRendererRecovery $root $policy $encoded
    Check ((Get-FileHash -LiteralPath $resultPath).Hash -ceq $resultHash) ('Old result changed: '+$case)
    if($case -cin @('success','receipt-tamper')){
        Check ($report.status -ceq 'archived' -and $script:moves -eq 1 -and -not (Test-Path -LiteralPath $source)) ('Archive was not confirmed: '+$case)
        Check ((Get-FileHash -LiteralPath $archive).Hash -ceq $originalHash) 'Archive bytes changed.'
        if($case -ceq 'receipt-tamper'){
            $receipt=Join-Path $root ('results/'+$op+'.renderer-recovery.json')
            $saved=Read-LabJson $receipt;$saved.planSha256='b'*64;Write-LabJson $receipt $saved
            Check ((Invoke-LabRendererRecovery $root $policy $encoded).status -ceq 'blocked_recovery') 'Changed receipt was accepted.'
        }else{
            [IO.File]::WriteAllText($source,'new unrelated log')
            Check ((Invoke-LabRendererRecovery $root $policy $encoded).status -ceq 'archived') 'Archive receipt was not reusable read-only.'
            Check ([IO.File]::ReadAllText($source) -ceq 'new unrelated log' -and $script:moves -eq 1) 'A later log was moved or overwritten.'
        }
    }elseif($case -ceq 'move-after'){
        Check ($report.status -ceq 'uncertain_move' -and $script:moves -eq 1 -and (Get-FileHash -LiteralPath $archive).Hash -ceq $originalHash) 'Uncertain move lost bytes.'
    }else{
        Check ($report.status -cin @('blocked_recovery','uncertain_move') -and (Get-FileHash -LiteralPath $source).Hash -ceq $originalHash) ('Recovery altered a refused source: '+$case)
    }
    $count=$script:moves
    [void](Invoke-LabRendererRecovery $root $policy $encoded)
    Check ($script:moves -eq $count) ('Recovery was retried: '+$case)
}
@{passed=$true;checks=$checks;cases=18;targetChanged=$false;gameLaunched=$false;runtime=$PSVersionTable.PSVersion.ToString()}|ConvertTo-Json
