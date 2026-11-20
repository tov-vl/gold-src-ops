#Requires -Version 5.1
param([Parameter(Mandatory)][string]$Workspace)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$code=Join-Path $Workspace 'kit/code'
. (Join-Path $code 'Lab.Common.ps1')
. (Join-Path $code 'Interaction.ps1')
function Check($value,[string]$Message){if(-not $value){throw $Message}}
function Reject([scriptblock]$Block){$failed=$false;try{& $Block}catch{$failed=$true};Check $failed 'Unexpected admission.'}
$root=Join-Path $Workspace ('checks/lan-rejoin-'+[guid]::NewGuid().ToString('N'))
$secondRoot=Join-Path $root 'second'
$null=New-Item -ItemType Directory -Path $root,$secondRoot
$first=[pscustomobject]@{id=[guid]::NewGuid().ToString('D');action='verify-panel';panel='guns';language='en';
    createdUtc=[DateTime]::UtcNow.ToString('o');rejoinPhase='round'}
$second=[pscustomobject]@{id=[guid]::NewGuid().ToString('D');action='capture-guns';language='en';
    createdUtc=$first.createdUtc;rejoinPhase='rejoin'}
foreach($job in @($first,$second)){Assert-LabRunnerJob $job}
foreach($entry in @(@('guns-round',$first),@('guns-rejoin',$second))){
    $request=ConvertFrom-LabRunnerCommand ($entry[0]+' '+$entry[1].id)
    Check ($request.operation -ceq $entry[0] -and $request.language -ceq 'en') 'Fixed broker command differs.'
    Reject {ConvertFrom-LabRunnerCommand ($entry[0]+' '+$entry[1].id+' en')}
}
$second.rejoinPhase='round'
Reject {Assert-LabRunnerJob $second}
$second.rejoinPhase='rejoin'
Check ((New-LabGunsProgram $first).Contains('gso_lab_continuity_received; snapshot')) 'First-job receipt omitted.'
Check (-not (New-LabGunsProgram $second).Contains('continuity')) 'Second job inherited first-job callbacks.'
Check ((New-LabGunsProgram $first) -notmatch '(?i)\b(bind|unbind|retry|disconnect)\b') 'Unexpected console action.'
Check ((Get-LabAutonomousProfile $first) -cne (Get-LabAutonomousProfile $second)) 'Profiles are interchangeable.'

$null=New-Item -ItemType Directory -Path (Join-Path $root 'queue'),(Join-Path $root "results/$($first.id)")
Write-LabJson (Join-Path $root "queue/$($first.id).json") $first
$terminalPath=Join-Path $root "results/$($first.id)/result.json"
$terminal=@{id=$first.id;status='rejected_preflight';completedUtc=[DateTime]::UtcNow.ToString('o');
    cleanupVerified=$false;configCleanupVerified=$false;launchRequested=$false;launchCommandDispatched=$false;
    gamePid=$null;gameStartedUtc=$null;cleanup=@{game='not_required';config='not_created'}}
Write-LabJson $terminalPath $terminal
Check (@(Get-LabTerminalArchiveCandidates $root).Count -eq 0) 'Retained result became archivable.'
Check ((Get-LabTerminalArchiveCandidates $root -RetainedPrelaunch) -ceq $first.id) 'Terminal prelaunch result omitted.'
$audit=@(Get-LabQueueAudit $root)
Check ($audit.Count -eq 1 -and $audit[0].id -ceq $first.id -and $audit[0].state -ceq 'preflight_rejected' -and
    -not $audit[0].expired) 'Retained queue metadata differs.'
$terminal.launchCommandDispatched=$true
Write-LabJson $terminalPath $terminal
Check (@(Get-LabTerminalArchiveCandidates $root -RetainedPrelaunch).Count -eq 0) 'Uncertain launch marked terminal.'
$terminal.launchCommandDispatched=$false
$terminal.completedUtc=[DateTime]::UtcNow.AddMinutes(-5).ToString('o')
Write-LabJson $terminalPath $terminal
Reject {Get-LabTerminalArchiveCandidates $root -RetainedPrelaunch}

function Assert-LabAutoContext {param($Root,$Policy,$Job,$Clock,[switch]$BeforeInput)}
function Assert-LabInteractionOwner {}
function Get-LabAutoElapsed {return 21.0}
function Wait-LabAutoTick {}
function Write-LabJson {}
function Save-LabOwnedWindowFrame {
    param($Window,$Result,$Destination)
    $Result.images+=@{number=1;source='owned-client-dc';sha256=('C'*64)}
}
$window=[pscustomobject]@{}
$window|Add-Member -MemberType ScriptMethod -Name CheckReady -Value {}
$window|Add-Member -MemberType ScriptMethod -Name ReopenGuns -Value {}
$window|Add-Member -MemberType ScriptMethod -Name Snapshot -Value {throw 'Paired capture sent a function key.'}
$ready=@{clock=@{};window=$window}
$setup=@{setupSnapshot=@{sha256=('A'*64)};setupObservedElapsed=20.5}
$firstResult=@{readiness=$setup;interaction=@{warmup=@{acknowledged=$false}};images=@()}
$resultPath=Join-Path $root 'result.json'
Open-LabGunsMenu $root @{} $first $firstResult $ready $resultPath -Preview
$null=New-LabAutoPhaseClaim $root $first 'guns-warmup-close' ('C'*64)
Reject {New-LabAutoPhaseClaim $secondRoot $second 'guns-warmup-close' ('C'*64)}
$firstResult.interaction.warmup=@{acknowledged=$true;roundSha256=('B'*64);observedElapsed=20.5}
Open-LabGunsMenu $root @{} $first $firstResult $ready $resultPath
Receive-LabGunsMenu $root @{} $first $firstResult $ready @{} $resultPath
$secondResult=@{readiness=$setup;images=@()}
Receive-LabGunsMenu $root @{} $second $secondResult $ready @{} (Join-Path $secondRoot 'result.json')
$expected=@{'guns-warmup-open'='A';'guns-warmup-close'='C';'guns-menu-open'='B';'guns-capture-primary-default'='B'}
foreach($phase in $expected.Keys){
    $claim=Get-Content -LiteralPath (Join-Path $root "auto-phase-$phase.json") -Raw|ConvertFrom-Json
    Check ($claim.phase -ceq $phase -and $claim.id -ceq $first.id -and
        $claim.frameSha256 -ceq ($expected[$phase]*64) -and -not $claim.automaticRetryAllowed) 'Real phase claim differs.'
}
foreach($result in @($firstResult,$secondResult)){
    Check ($result.images.Count -eq 1 -and $result.images[0].source -ceq 'owned-client-dc') 'Paired menu capture differs.'
}
'LAN rejoin runner checks passed.'
