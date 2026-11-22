#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$RunDirectory,
    [Parameter(Mandatory)][string]$PythonPath,
    [Parameter(Mandatory)][switch]$Execute
)
$ErrorActionPreference='Stop'
Import-Module (Join-Path $PSScriptRoot 'Lab.Tooling.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'Lab.CampaignRuntime.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'Lab.LanRejoin.psm1') -Force
$root=Assert-LabOutputPath $RunDirectory
$plan=Get-Content -LiteralPath (Join-Path $root 'plan.json') -Raw|ConvertFrom-Json -AsHashtable
$pair=Get-Content -LiteralPath (Join-Path $root 'rejoin-plan.json') -Raw|ConvertFrom-Json -AsHashtable
if($plan.profile -cne 'steam-rejoin' -or $plan.language -cne 'en' -or $pair.runId -cne $plan.id -or $pair.first -ceq $pair.second){
    throw 'An exact private Steam pair is required.'
}
foreach($id in @($pair.first,$pair.second)){
    if($id -cnotmatch '\A[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}\z' -or [guid]$id -eq [guid]::Empty){throw 'Invalid paired job identity.'}
}
$python=Get-Item -LiteralPath $PythonPath
if($python.PSIsContainer -or ($python.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Local Python executable required.'}
$campaign=@{runtime=@{python=$python.FullName;pythonSha256=(Get-FileHash -LiteralPath $python.FullName).Hash;pwsh=(Join-Path $PSHOME 'pwsh.exe')}}
$context=@{root=$root;run=$root;campaign=$campaign;case=@{jobId=$pair.first;panel='guns'}}
$panel=Join-Path $root 'Invoke-Panel.ps1'
$activation=Get-Content -LiteralPath (Join-Path $root 'activation.json') -Raw|ConvertFrom-Json -AsHashtable

function Read-Runtime {
    $file=Get-Item -LiteralPath (Join-Path $root 'evidence/runtime.json')
    if($file.Length -gt 65536 -or ($file.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Invalid private fixture status.'}
    return Get-Content -LiteralPath $file.FullName -Raw|ConvertFrom-Json -AsHashtable
}

function Assert-SteamClient([int]$Slots,[switch]$AfterVerifiedClose) {
    $status=(& $panel -Operation status)|ConvertFrom-Json -AsHashtable
    $w=$status.worker
    $pending=$status.pending
    $queueReady=if($AfterVerifiedClose){$pending -eq $Slots}else{$pending -in (0..6)}
    if($status.target -cne $activation.targetName -or $status.protocol -ne 2 -or $status.revision -ne 16 -or
        ($pending -isnot [int] -and $pending -isnot [long]) -or -not $queueReady -or
        $w.state -cne 'ready' -or $w.client.ready -ne $true -or
        $w.client.gameProcessCount -ne 0 -or $w.client.steamProcessCount -ne 1 -or
        $w.desktopPreflight.ready -ne $true -or $w.desktopPreflight.code -cne 'ready'){
        throw 'Client readiness or queue changed.'
    }
    $age=[DateTimeOffset]::UtcNow-([DateTimeOffset]$w.desktopPreflight.utc)
    # During a long first job the heartbeat can age until the next worker tick.
    # After a verified close, the second job's own worker tick checks the desktop again.
    if($age.TotalSeconds -lt -5 -or (-not $AfterVerifiedClose -and $age.TotalSeconds -gt 75)){
        throw 'Stale desktop evidence.'
    }
    $renderer=$w.rendererPreflight
    if($renderer.status -cne 'observed' -or $renderer.gameClosed -ne $true -or @($renderer.files).Count -ne 4 -or
        @($renderer.files|Where-Object {$_.exists -ne $false}).Count){throw 'Renderer paths are occupied or unverified.'}
    return [int]$pending
}

function Confirm-Runner {
    $update=(& (Join-Path $root 'engine/Invoke-FramedUpdate.ps1') -Operation status)|ConvertFrom-Json -AsHashtable
    if($update.enabled -ne $true -or $update.blocked -ne $false -or $update.revision -ne 16 -or $update.codeHashes.Count -ne 9){
        throw 'Signed runner readback is unavailable.'
    }
    foreach($name in @('Broker.ps1','Worker.ps1','Interaction.ps1','Lab.Common.ps1','Diagnostics.ps1','Rules.Input.cs','Expire.ps1','Uninstall.ps1','Worker-Quiet.js')){
        if((Get-FileHash -LiteralPath (Join-Path $root "code/$name")).Hash -cne $update.codeHashes[$name]){
            throw "Installed runner differs at $name."
        }
    }
}

function Invoke-SteamClient([string]$Operation,[string]$Id) {
    try {
        $ack=(& $panel -Operation $Operation -JobId $Id)|ConvertFrom-Json -AsHashtable
        if($ack.id -cne $Id -or $ack.status -cne 'queued'){throw 'Dispatch receipt differs.'}
    } catch {
        Write-Warning 'Dispatch requires readback. One attempt only.'
    }
    $watch=[Diagnostics.Stopwatch]::StartNew()
    do{
        try{$r=(& $panel -Operation result -JobId $Id)|ConvertFrom-Json -AsHashtable}catch{$r=$null}
        if($r){
            if($r.id -cne $Id){throw 'Result belongs to another job.'}
            if($r.completedUtc){return $r}
        }
        $runtime=Read-Runtime
        if($runtime.status -cne 'ready'){throw 'Fixture stopped while the submitted request requires reconciliation.'}
        Start-Sleep -Milliseconds 800
    }while($watch.Elapsed.TotalMinutes -lt 8)
    throw 'Client result deadline. Reconcile by job ID.'
}

$fixture=Read-Runtime
if($fixture.status -cne 'ready' -or $fixture.steamAnonymousLogon -ne $true -or $fixture.runtimeVerified -ne $true -or
    $fixture.steamRejoin -ne $true -or $fixture.emptyAtStart -ne $true -or $fixture.steamAdmissions -ne 0){
    throw 'Verified finite Steam fixture required.'
}
$age=[DateTimeOffset]::UtcNow.ToUnixTimeSeconds()-[double]$fixture.readyUnix
if($age -lt -5 -or $age -gt 1800){throw 'Insufficient owned fixture lease remains.'}
Confirm-Runner
$initialPending=Assert-SteamClient
$claim=[IO.File]::Open((Join-Path $root 'steam-dispatch.claim'),[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
$claim.Dispose()
try{
    Invoke-LabCampaignRuntime relay $context|Out-Null
    $first=Invoke-SteamClient 'guns-steam-round' $pair.first
    if(-not (Test-LabFirstLaunch $first $pair.first) -or $first.fixture.kind -cne 'private-steam-challenge'){
        throw ('First launch did not pass selection, OpenGL and cleanup: '+$first.status+' '+$first['failureCode'])
    }
    $deadline=[DateTimeOffset]::UtcNow.AddSeconds(24)
    do{
        $fixture=Read-Runtime
        if((Test-LabRejoinWindow $fixture ([DateTimeOffset]::UtcNow)) -and $fixture.steamAdmissions -eq 1 -and $fixture.observerDisconnected){break}
        if($fixture.status -cne 'ready' -or $fixture.rejoinClosed){throw 'Private rejoin closed before dispatch.'}
        Start-Sleep -Milliseconds 250
    }while([DateTimeOffset]::UtcNow -lt $deadline)
    $null=Assert-SteamClient ($initialPending+1) -AfterVerifiedClose
    if(-not (Test-LabRejoinWindow (Read-Runtime) ([DateTimeOffset]::UtcNow))){throw 'No fresh vacant rejoin window.'}
    $second=Invoke-SteamClient 'guns-observe' $pair.second
    $fixture=Read-Runtime
    if($second.status -cne 'captured_unreviewed' -or $second.fixture.kind -cne 'private-steam-challenge' -or
        $second.gameStartedUtc -eq $first.gameStartedUtc -or $second.rendererEvidence.status -cne 'identified' -or
        $second.rendererEvidence.identity.backend -cne 'OpenGL' -or $second.cleanupVerified -ne $true -or
        $second.configCleanupVerified -ne $true -or $second.readiness.status -cne 'snapshot-received' -or
        $second.readiness.callback -cne 'gso_lab_steam_observed' -or $second.readiness.setupAcknowledged -ne $false -or
        @($second.readiness.setupActions).Count -ne 0 -or @($second.images).Count -ne 1){
        throw ('Observation client did not pass OpenGL and cleanup: '+$second.status+' '+$second['failureCode'])
    }
    $deadline=[DateTimeOffset]::UtcNow.AddSeconds(5)
    while(-not $fixture.ContainsKey('steamSpawnInventory') -and [DateTimeOffset]::UtcNow -lt $deadline){
        Start-Sleep -Milliseconds 250
        $fixture=Read-Runtime
    }
    if($fixture.steamAdmissions -ne 2 -or -not $fixture.ContainsKey('steamSpawnInventory')){
        throw 'Steam validation or first eligible spawn is missing.'
    }
    $inventory=[long]$fixture.steamSpawnInventory
    foreach($id in @($pair.first,$pair.second)){
        & (Join-Path $root 'Collect-Autonomous.ps1') -JobId $id|Out-Host
        $result=Join-Path $root "controller-results/$id/result.json"
        & (Join-Path $PSScriptRoot 'Test-LabRenderer.ps1') -ResultPath $result -JobId $id -OutputPath (Join-Path $root "RENDERER-$id.json")|Out-Null
    }
    $receipt=[ordered]@{schema=1;first=$pair.first;second=$pair.second;steamApprovedConnections=2;
        sameAccount=$true;newUserId=$true;inventory=('0x{0:x8}' -f $inventory);ak47=[bool]($inventory -band (1 -shl 28));
        desertEagle=[bool]($inventory -band (1 -shl 26));mp5=[bool]($inventory -band (1 -shl 19));
        usp=[bool]($inventory -band (1 -shl 16));observedFrameSha256=$second.images[0].sha256;openGlOnBoth=$true;
        cleanupVerified=$true;repeatSelectionSent=$false}
    $receipt.passed=$receipt.ak47 -and $receipt.desertEagle -and -not ($receipt.mp5 -or $receipt.usp)
    $json=$receipt|ConvertTo-Json
    [IO.File]::WriteAllText((Join-Path $root 'STEAM-REJOIN-ACCEPTANCE.json'),$json)
    $json
    if(-not $receipt.passed){throw 'The observed inventory differs from the selected pair.'}
}finally{
    Invoke-LabCampaignRuntime stop $context|Out-Null
    Invoke-LabCampaignRuntime absence $context|Out-Null
}
