#Requires -Version 7.0
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'

function Test-LabFirstLaunch($Result,[string]$JobId) {
    return $Result.id -ceq $JobId -and $Result.action -ceq 'verify-panel' -and
        $Result.panel -ceq 'guns' -and $Result.language -ceq 'en' -and
        $Result.status -ceq 'autonomous_captured_unreviewed' -and $Result.completedUtc -and
        $Result.launchCommandDispatched -eq $true -and $Result.launchRequested -eq $true -and
        $Result.cleanupVerified -eq $true -and $Result.configCleanupVerified -eq $true -and
        $Result.cleanup.game -ceq 'verified' -and $Result.cleanup.config -ceq 'verified' -and
        $Result.rendererEvidence.status -ceq 'identified' -and
        $Result.rendererEvidence.identity.backend -ceq 'OpenGL' -and
        $Result.interaction.continuity.phase -ceq 'ready-for-owned-close' -and
        $Result.interaction.continuity.roundReadySha256 -cmatch '\A[A-F0-9]{64}\z' -and
        @($Result.images).Count -eq 5
}

function Test-LabRejoinWindow($Runtime,[DateTimeOffset]$Now) {
    if(-not $Runtime -or $Runtime['status'] -cne 'ready' -or $Runtime['runtimeVerified'] -ne $true -or
        $Runtime['lanRejoin'] -ne $true -or $Runtime['vacantForRejoin'] -ne $true -or
        $Runtime['rejoinClosed'] -eq $true -or -not $Runtime['rejoinOpenedUnix']){return $false}
    $age=$Now.ToUnixTimeMilliseconds()/1000.0-[double]$Runtime['rejoinOpenedUnix']
    # The isolated fixture keeps the rejoin window open for 100 seconds.
    # Leave at least 75 seconds for the minute-based worker and Steam.
    return $age -ge 0 -and $age -lt 25
}

function Wait-LabLanTerminal($Context,[scriptblock]$Port) {
    $limit=[Diagnostics.Stopwatch]::StartNew()
    do{
        try{$result=& $Port result $Context}catch{$result=$null}
        if($result){
            if($result.id -cne $Context.case.jobId){throw 'Readback belongs to another job.'}
            if($result['completedUtc']){return $result}
        }
        & $Port wait $Context|Out-Null
    }while($limit.Elapsed.TotalMinutes -lt 9)
    throw 'Queued launch requires reconciliation.'
}

function Invoke-LabLanRejoin($First,$Second,[scriptblock]$Port) {
    $pre=& $Port preflight $First
    if($pre.ready -ne $true -or $pre.pending -lt 0 -or $pre.pending -gt 6){throw 'LAN rejoin preflight failed.'}
    $claim=Join-Path $First.run 'rejoin-dispatch.claim'
    $stream=[IO.File]::Open($claim,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
    $stream.Dispose()
    try{
        & $Port start $First|Out-Null
        & $Port relay $First|Out-Null
        try{& $Port guns-round $First|Out-Null}catch{Write-Warning 'First dispatch requires readback. No retry.'}
        $firstResult=Wait-LabLanTerminal $First $Port
        if(-not (Test-LabFirstLaunch $firstResult $First.case.jobId)){throw 'First client did not admit rejoin.'}
        $deadline=[DateTimeOffset]::UtcNow.AddSeconds(25)
        do{
            $window=& $Port window $First
            if(Test-LabRejoinWindow $window ([DateTimeOffset]::UtcNow)){break}
            if($window -and $window['rejoinClosed']){throw 'Server rejoin window closed.'}
            & $Port wait $First|Out-Null
        }while([DateTimeOffset]::UtcNow -lt $deadline)
        $status=& $Port status $Second
        if($status.target -cne $pre.target -or $status.pending -ne ($pre.pending+1) -or
           $status.worker.state -cne 'ready' -or
           -not (Test-LabRejoinWindow (& $Port window $First) ([DateTimeOffset]::UtcNow))){
            throw 'Second client admission differs.'
        }
        try{& $Port guns-rejoin $Second|Out-Null}catch{Write-Warning 'Second dispatch requires readback. No retry.'}
        $null=Wait-LabLanTerminal $Second $Port
    }finally{
        & $Port stop $First|Out-Null
        & $Port absence $First|Out-Null
    }
}

Export-ModuleMember -Function Invoke-LabLanRejoin,Test-LabFirstLaunch,Test-LabRejoinWindow
