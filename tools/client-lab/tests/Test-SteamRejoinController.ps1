#Requires -Version 7.0
param([Parameter(Mandatory)][string]$Workspace)
$ErrorActionPreference='Stop'
$tokens=$null;$errors=$null
$source=Join-Path $PSScriptRoot '../Invoke-LabSteamRejoin.ps1'
$ast=[Management.Automation.Language.Parser]::ParseFile($source,[ref]$tokens,[ref]$errors)
if($errors){throw $errors[0].Message}
$function=$ast.Find({
    param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq 'Assert-SteamClient'
},$false)
if(-not $function){throw 'No Steam client admission function.'}
. ([scriptblock]::Create($function.Extent.Text))
$activation=@{targetName='example-test'}
$panel={
    param($Operation)
    if($Operation -cne 'status'){throw 'Only synthetic readback is allowed.'}
    $script:status|ConvertTo-Json -Depth 10
}
$script:status=@{target='example-test';protocol=2;revision=16;pending=6;worker=@{
    state='ready';client=@{ready=$true;gameProcessCount=0;steamProcessCount=1};
    desktopPreflight=@{ready=$true;code='ready';utc=[DateTimeOffset]::UtcNow.ToString('o')};
    rendererPreflight=@{status='observed';gameClosed=$true;files=@(1..4|ForEach-Object {@{exists=$false}})}
}}
function Reject([scriptblock]$Action,[string]$Expected){
    try{& $Action}catch{if($_.Exception.Message -ceq $Expected){return};throw}
    throw 'Expected Steam client refusal.'
}
if((Assert-SteamClient) -ne 6){throw 'Initial queue count differs.'}
$status.pending=0
if((Assert-SteamClient) -ne 0){throw 'Empty queue refused.'}
$status.pending=7
Reject {Assert-SteamClient} 'Client readiness or queue changed.'
$status.pending=6
$status.worker.desktopPreflight.utc=[DateTimeOffset]::UtcNow.AddSeconds(-120).ToString('o')
Reject {Assert-SteamClient} 'Stale desktop evidence.'
$status.pending=7
if((Assert-SteamClient 7 -AfterVerifiedClose) -ne 7){throw 'Expected rejoin count refused.'}
Reject {Assert-SteamClient 6 -AfterVerifiedClose} 'Client readiness or queue changed.'
$status.worker.client.gameProcessCount=1
Reject {Assert-SteamClient 7 -AfterVerifiedClose} 'Client readiness or queue changed.'
$status.worker.client.gameProcessCount=0
$status.worker.desktopPreflight.utc=[DateTimeOffset]::UtcNow.AddMinutes(1).ToString('o')
Reject {Assert-SteamClient 7 -AfterVerifiedClose} 'Stale desktop evidence.'
'Private Steam client admission checks passed: fresh first job, aged second heartbeat, active game and future timestamps.'

# Run the real generated collector against bounded local fixtures.
$collectorRoot=Join-Path $Workspace 'checks/steam-collector'
New-Item -ItemType Directory -Path (Join-Path $collectorRoot 'code') -Force|Out-Null
Copy-Item -LiteralPath (Join-Path $Workspace 'kit/code/Lab.Common.ps1') -Destination (Join-Path $collectorRoot 'code')
$collector=Join-Path $collectorRoot 'Collect-Autonomous.ps1'
Copy-Item -LiteralPath (Join-Path $Workspace 'kit/Collect-Autonomous.ps1') -Destination $collector
$panelStub=@"
param([string]`$Operation,[string]`$JobId)
if(`$Operation -cne 'result'){throw 'Only local result readback is allowed.'}
[IO.File]::ReadAllText((Join-Path `$PSScriptRoot ('controller-results/'+`$JobId+'/result.json')))
"@
[IO.File]::WriteAllText((Join-Path $collectorRoot 'Invoke-Panel.ps1'),$panelStub)
$cases=@(
    @{action='capture-guns';count=1;mode='steam-spawn-observe-v1';complete=$true},
    @{action='capture-guns';count=4;complete=$true},
    @{action='capture-guns';count=1;complete=$false},
    @{action='verify-panel';count=5;complete=$true}
)
foreach($case in $cases){
    $id=[guid]::NewGuid().ToString('D')
    $directory=Join-Path $collectorRoot "controller-results/$id"
    New-Item -ItemType Directory -Path $directory -Force|Out-Null
    $images=foreach($number in 1..$case.count){
        $path=Join-Path $directory "image-$number.bmp"
        [IO.File]::WriteAllBytes($path,[byte[]]@(66,77,$number))
        @{number=$number;bytes=3;sha256=(Get-FileHash -LiteralPath $path).Hash}
    }
    $record=@{id=$id;action=$case.action;completedUtc=[DateTimeOffset]::UtcNow.ToString('o');images=@($images);
        status=$(if($case.action -ceq 'capture-guns'){'captured_unreviewed'}else{'autonomous_captured_unreviewed'});
        cleanupVerified=$true;configCleanupVerified=$true}
    if($case.mode){$record.readiness=@{mode=$case.mode}}
    [IO.File]::WriteAllText((Join-Path $directory 'result.json'),($record|ConvertTo-Json -Depth 5))
    if($case.complete){& $collector -JobId $id|Out-Null}
    else{Reject {& $collector -JobId $id|Out-Null} 'Evidence collection incomplete; reconcile the existing job without another launch.'}
    $receipt=Get-Content -LiteralPath (Join-Path $directory 'collection.json') -Raw|ConvertFrom-Json
    if($receipt.evidenceComplete -ne $case.complete -or $receipt.images.Count -ne $case.count){throw 'Collector evidence differs.'}
}
'Steam observation and existing capture frame-count checks passed.'
