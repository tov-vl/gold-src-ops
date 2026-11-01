#Requires -Version 5.1
param([Parameter(Mandatory)][string]$Workspace)
$ErrorActionPreference='Stop'
$source=Join-Path $PSScriptRoot '../templates/recovery'
. (Join-Path $Workspace 'kit/updater/Update.Common.ps1')
. ([scriptblock]::Create([IO.File]::ReadAllText((Join-Path $source 'Migration.Core.ps1.in'))))
$script:checks=0
function Check([bool]$Value){if(-not $Value){throw 'Recovery assertion failed.'};$script:checks++}
function Reject([scriptblock]$Action){$failed=$false;try{& $Action|Out-Null}catch{$failed=$true};Check $failed}
function Assert-UpdateWritableOnlyByAdmin([string]$Path){if(-not(Test-Path -LiteralPath $Path)){throw 'Mock trust path missing.'}}
function Assert-UpdateWorkerIdle{if($script:busy){throw 'Mock worker busy.'}}
function Assert-UpdateTrust([string]$Root,[string]$Lab,$Config){
    foreach($entry in $Config.trustedFiles){if((Get-UpdateHash (Read-UpdateBytes $Root (Join-Path $Root ('bin/'+$entry.name)))) -cne $entry.sha256){throw 'Trust hash differs.'}}
}
function Get-LabWorkerInvariant($Task){return 'fixed-invariant'}
function Get-ScheduledTask {param($TaskName,$TaskPath);return [pscustomobject]@{State=$(if($TaskName -ceq 'GsoClientLab-Updater'){$script:updaterState}else{'Ready'})}}
function Get-UpdaterTaskVerification($Task,$ExpectedState){return @{passed=($Task.State -ceq $ExpectedState)}}
function Disable-ScheduledTask {param($TaskName,$TaskPath,$ErrorAction);$script:updaterState='Disabled'}
function Enable-ScheduledTask {param($TaskName,$TaskPath);$script:updaterState='Ready'}
function Assert-MigrationUpdaterInactive{}
function Confirm-MigrationSystemTick {if($script:tickFailure){throw 'Mock tick failed.'}}
# Stub-only replacement file for the dot-source transition. No privileged host operations.
$engineStub=@'
function Get-UpdateInboxInventory($Root,$Config,$State){return @{entries=0;limit=16;eligible=0;protected=0;retained=0;archiveLimit=128;candidates=@()}}
function Publish-UpdateState($Root,$Config,$State,$Blocked=$false){Write-UpdateJson (Join-Path $Root 'public/state.json') @{blocked=$Blocked;sequence=$State.sequence}}
'@
. ([scriptblock]::Create($engineStub))
$sandbox=Join-Path $Workspace ('checks/recovery-'+[guid]::NewGuid().ToString('N'))
function New-RecoveryFixture([string]$Name){
    $script:busy=$false;$script:tickFailure=$false;$script:updaterState='Ready'
    $dir=Join-Path $sandbox $Name;$root=Join-Path $dir 'updater';$lab=Join-Path $dir 'lab';$package=Join-Path $dir 'package'
    foreach($area in @('updater/bin','updater/private','updater/public','updater/inbox','lab/code','lab/queue','package')){New-Item -ItemType Directory -Path (Join-Path $dir $area) -Force|Out-Null}
    $hashes=[ordered]@{}
    foreach($name in Get-UpdateNames){$bytes=[Text.Encoding]::UTF8.GetBytes('# '+$name);[IO.File]::WriteAllBytes((Join-Path $lab ('code/'+$name)),$bytes);$hashes[$name]=Get-UpdateHash $bytes}
    Write-UpdateJson (Join-Path $lab 'policy.json') @{test='preserved'}
    $trusted=@()
    foreach($name in @('Update.Engine.ps1','Gateway.ps1')){
        $text=if($name -ceq 'Update.Engine.ps1'){$engineStub}else{'# old gateway'}
        [IO.File]::WriteAllText((Join-Path $root ('bin/'+$name)),$text,[Text.UTF8Encoding]::new($false))
        $trusted+=@{name=$name;sha256=(Get-UpdateHash (Read-UpdateBytes $root (Join-Path $root ('bin/'+$name))))}
        [IO.File]::WriteAllText((Join-Path $package $name),($text+"`n# replacement"),[Text.UTF8Encoding]::new($false))
    }
    $config=@{enabled=$true;target=$env:COMPUTERNAME;sessionId='10000000-0000-4000-8000-000000000001';publicKeyXml='public-pin';trustedFiles=$trusted;workerInvariant='fixed-invariant'}
    $state=@{sequence=16;revision=16;codeHashes=$hashes}
    Write-UpdateJson (Join-Path $root 'bin/config.json') $config
    Write-UpdateJson (Join-Path $root 'private/state.json') $state
    $expected=[pscustomobject]@{id=[guid]::NewGuid().ToString('D');targetName=$config.target;sessionId=$config.sessionId;sequence=16;revision=16;trustedFiles=$trusted;codeHashes=$hashes;
        publicKeySha256=(Get-UpdateHash ([Text.Encoding]::UTF8.GetBytes('public-pin')));
        engineBeforeSha256=$trusted[0].sha256;gatewayBeforeSha256=$trusted[1].sha256;
        engineAfterSha256=(Get-UpdateHash (Read-UpdateBytes $package (Join-Path $package 'Update.Engine.ps1')));
        gatewayAfterSha256=(Get-UpdateHash (Read-UpdateBytes $package (Join-Path $package 'Gateway.ps1')))}
    return @{root=$root;lab=$lab;package=$package;expected=$expected;stateHash=(Get-UpdateHash (Read-UpdateBytes $root (Join-Path $root 'private/state.json')));policyHash=(Get-UpdateHash (Read-UpdateBytes $lab (Join-Path $lab 'policy.json')))}
}
$fixture=New-RecoveryFixture 'success'
$result=Invoke-LabEngineMigration $fixture.root $fixture.lab $fixture.package $fixture.expected
Check ($result.status -ceq 'engine_migrated' -and -not $result.newPermissions -and -not $result.gameLaunched)
Check ((Get-UpdateHash (Read-UpdateBytes $fixture.root (Join-Path $fixture.root 'private/state.json'))) -ceq $fixture.stateHash)
Check ((Get-UpdateHash (Read-UpdateBytes $fixture.lab (Join-Path $fixture.lab 'policy.json'))) -ceq $fixture.policyHash)
Check ($script:updaterState -ceq 'Ready')
Check (-not (Test-Path (Join-Path $fixture.lab 'maintenance-revision-06.json')))
Check (Test-Path (Join-Path $fixture.root ('private/engine-migration-'+$fixture.expected.id+'/Gateway.ps1')))
Check (Test-Path (Join-Path $fixture.root ('private/engine-migration-'+$fixture.expected.id+'/inbox-before.json')))
Check ($result.inboxBefore -eq 0 -and $result.inbox.entries -eq 0)
Reject {Invoke-LabEngineMigration $fixture.root $fixture.lab $fixture.package $fixture.expected}
$fixture=New-RecoveryFixture 'active'
Write-UpdateJson (Join-Path $fixture.root 'private/active.json') @{status='claimed'}
Reject {Invoke-LabEngineMigration $fixture.root $fixture.lab $fixture.package $fixture.expected}
$fixture=New-RecoveryFixture 'pending'
$id=[guid]::NewGuid().ToString('D');Write-UpdateJson (Join-Path $fixture.root ('inbox/'+$id+'.json')) @{pending=$true}
Reject {Invoke-LabEngineMigration $fixture.root $fixture.lab $fixture.package $fixture.expected}
$fixture=New-RecoveryFixture 'wrong-state';$fixture.expected.sequence=15
Reject {Invoke-LabEngineMigration $fixture.root $fixture.lab $fixture.package $fixture.expected}
$fixture=New-RecoveryFixture 'busy';$script:busy=$true
Reject {Invoke-LabEngineMigration $fixture.root $fixture.lab $fixture.package $fixture.expected}
$fixture=New-RecoveryFixture 'rollback';$script:tickFailure=$true
$result=Invoke-LabEngineMigration $fixture.root $fixture.lab $fixture.package $fixture.expected
Check ($result.status -ceq 'rolled_back')
Check ((Get-UpdateHash (Read-UpdateBytes $fixture.root (Join-Path $fixture.root 'bin/Gateway.ps1'))) -ceq $fixture.expected.gatewayBeforeSha256)
Check ((Get-UpdateHash (Read-UpdateBytes $fixture.root (Join-Path $fixture.root 'bin/Update.Engine.ps1'))) -ceq $fixture.expected.engineBeforeSha256)
Check ((Get-UpdateHash (Read-UpdateBytes $fixture.root (Join-Path $fixture.root 'private/state.json'))) -ceq $fixture.stateHash)
Check ($script:updaterState -ceq 'Ready')
if($PSVersionTable.PSVersion.Major -ge 7){
    $tool=Join-Path $PSScriptRoot '../New-UpdaterRecovery.ps1'
    $profilePath=Join-Path $PSScriptRoot '../examples/profile.example.json'
    $profile=Get-Content $profilePath -Raw|ConvertFrom-Json
    $builderState=Join-Path $sandbox 'builder-state.json';$builderTrust=Join-Path $sandbox 'builder-trust.json';$builderOutput=Join-Path $sandbox 'prepared'
    $stateValue=@{sequence=16;revision=16;enabled=$true;blocked=$false;codeHashes=$fixture.expected.codeHashes}
    $trustValue=@{targetName=$profile.targetName;sessionId=$profile.sessionId;engineAfterSha256=('A'*64);publicKeySha256=('B'*64);
        trustedFiles=@(@('Gateway.Input.ps1','Gateway.ps1','Task-Budget.ps1','Update.Common.ps1','Update.Engine.ps1','Updater.ps1')|ForEach-Object {@{name=$_;sha256=('A'*64)}})}
    Write-UpdateJson $builderState $stateValue;Write-UpdateJson $builderTrust $trustValue
    $null=& $tool -ProfilePath $profilePath -PredecessorState $builderState -TrustBaselinePath $builderTrust -OutputDirectory $builderOutput
    $manifest=Get-Content (Join-Path $builderOutput 'package-hashes.json') -Raw|ConvertFrom-Json
    Check ($manifest.files.Count -eq 8 -and @(Get-ChildItem $builderOutput -File).Count -eq 9)
    foreach($entry in $manifest.files){Check ((Get-FileHash (Join-Path $builderOutput $entry.name)).Hash -ceq $entry.sha256)}
    Reject {& $tool -ProfilePath $profilePath -PredecessorState $builderState -TrustBaselinePath $builderTrust -OutputDirectory $builderOutput}
    $stateValue.enabled='true';Write-UpdateJson $builderState $stateValue
    Reject {& $tool -ProfilePath $profilePath -PredecessorState $builderState -TrustBaselinePath $builderTrust -OutputDirectory (Join-Path $sandbox 'invalid-state')}
    Check (-not(Test-Path (Join-Path $sandbox 'invalid-state')))
}
@{passed=$true;checks=$script:checks;realTasksChanged=$false;networkUsed=$false;nativeInputSent=$false;runtime=$PSVersionTable.PSVersion.ToString()}|ConvertTo-Json
