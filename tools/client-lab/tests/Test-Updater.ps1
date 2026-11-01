#requires -Version 5.1
param([Parameter(Mandatory=$true)][string]$Workspace,[string]$ReportPath)
$ErrorActionPreference='Stop'
. (Join-Path $Workspace 'kit\updater\Update.Common.ps1')
. (Join-Path $Workspace 'kit\updater\Task-Budget.ps1')
. (Join-Path $Workspace 'kit\updater\Update.Engine.ps1')
$script:checks=0
function Check([bool]$Condition,[string]$Name){if(-not $Condition){throw ('Check failed: '+$Name)};$script:checks++}
function Reject([scriptblock]$Action,[string]$Name){$rejected=$false;try{& $Action|Out-Null}catch{$rejected=$true};Check $rejected $Name}
$sandbox=Join-Path $Workspace ('checks\mock-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $sandbox|Out-Null
$rsa=New-Object Security.Cryptography.RSACryptoServiceProvider(3072)
$rsa.PersistKeyInCsp=$false
$script:bytesByName=@{}
$hashes=[ordered]@{}
$pins=[ordered]@{}
foreach($name in (Get-UpdateNames)){
    $data=[Text.Encoding]::UTF8.GetBytes('# '+$name+"`r`n")
    $script:bytesByName[$name]=$data
    $hashes[$name]=Get-UpdateHash $data
    if($name -cin (Get-UpdatePinnedNames)){$pins[$name]=$hashes[$name]}
}
$config=[pscustomobject]@{target=$env:COMPUTERNAME;sessionId='10000000-0000-4000-8000-000000000001';publicKeyXml=$rsa.ToXmlString($false);pinnedHashes=[pscustomobject]$pins;enabled=$true}
$state=[pscustomobject]@{schema=1;sequence=0;revision=10;codeHashes=[pscustomobject]$hashes;workerArguments='//B //Nologo //T:540 C:\ProgramData\GsoClientLab\code\Worker-Quiet.js';taskSeconds=600}
function New-TestManifest {
    $files=@();$baseline=@()
    foreach($name in (Get-UpdateNames)){
        $data=$script:bytesByName[$name]
        $files+=@{name=$name;bytes=$data.Length;sha256=(Get-UpdateHash $data)}
        $baseline+=@{name=$name;sha256=$hashes[$name]}
    }
    $now=[DateTime]::UtcNow
    return @{schema=1;id=[guid]::NewGuid().ToString();target=$config.target;sessionId=$config.sessionId;sequence=1;revision=10;createdUtc=$now.ToString('o');expiresUtc=$now.AddHours(1).ToString('o');baseline=$baseline;files=$files;archive=@();watchdogSeconds=540}
}
function New-TestEnvelope($Manifest) {
    $manifestBytes=[Text.Encoding]::UTF8.GetBytes(($Manifest|ConvertTo-Json -Depth 8 -Compress))
    $payload=@(Get-UpdateNames|ForEach-Object{@{name=$_;contentBase64=[Convert]::ToBase64String($script:bytesByName[$_])}})
    return [Text.Encoding]::UTF8.GetBytes((@{schema=1;manifestBase64=[Convert]::ToBase64String($manifestBytes);signatureBase64=[Convert]::ToBase64String($rsa.SignData($manifestBytes,'SHA256'));files=$payload}|ConvertTo-Json -Depth 8 -Compress))
}
try{
    $manifest=New-TestManifest
    $valid=New-TestEnvelope $manifest
    $verified=Test-UpdateEnvelope $valid $config $state
    Check ($verified.manifest.id -ceq $manifest.id -and $verified.payload.Count -eq 9) 'valid signed package'
    $modified=ConvertFrom-UpdateBytes $valid
    $modified.manifestBase64=[Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes('{}'))
    Reject {Test-UpdateEnvelope ([Text.Encoding]::UTF8.GetBytes(($modified|ConvertTo-Json -Depth 8 -Compress))) $config $state} 'signature rejects changed manifest'
    $modified=ConvertFrom-UpdateBytes $valid
    $modified.files[0].contentBase64=[Convert]::ToBase64String([byte[]](1,2,3))
    Reject {Test-UpdateEnvelope ([Text.Encoding]::UTF8.GetBytes(($modified|ConvertTo-Json -Depth 8 -Compress))) $config $state} 'payload tamper rejected'
    foreach($case in @('target','session','sequence','revision','expired','future','lifetime','file-path','baseline','pin','extra-field','budget','fractional-sequence','duplicate-files','duplicate-archive')){
        $test=New-TestManifest
        switch($case){
            target{$test.target='another-host'}
            session{$test.sessionId=[guid]::NewGuid().ToString()}
            sequence{$test.sequence=2}
            revision{$test.revision=9}
            expired{$test.expiresUtc=[DateTime]::UtcNow.AddSeconds(-1).ToString('o')}
            future{$test.createdUtc=[DateTime]::UtcNow.AddHours(1).ToString('o')}
            lifetime{$test.expiresUtc=[DateTime]::UtcNow.AddDays(2).ToString('o')}
            file-path{$test.files[0].name='..\policy.json'}
            baseline{$test.baseline[0].sha256=('A'*64)}
            pin{$test.files[0].sha256=('A'*64)}
            extra-field{$test.command='whoami'}
            budget{$test.watchdogSeconds=9999}
            fractional-sequence{$test.sequence=1.5}
            duplicate-files{$test.files[1]=$test.files[0]}
            duplicate-archive{$entry=@{id=[guid]::NewGuid().ToString();sha256=('A'*64)};$test.archive=@($entry,$entry)}
        }
        Reject {Test-UpdateEnvelope (New-TestEnvelope $test) $config $state} $case
    }
    $nextState=$state|ConvertTo-Json -Depth 6|ConvertFrom-Json
    $nextState.sequence=1
    Reject {Test-UpdateEnvelope $valid $config $nextState} 'replay sequence rejected'
    $oldPinned=$script:bytesByName['Lab.Common.ps1']
    try{
        $script:bytesByName['Lab.Common.ps1']=[Text.Encoding]::UTF8.GetBytes('# changed shared code')
        $test=New-TestManifest
        Reject {Test-UpdateEnvelope (New-TestEnvelope $test) $config $state} 'validly signed pinned dependency update rejected'
    }finally{$script:bytesByName['Lab.Common.ps1']=$oldPinned}
    $other=New-Object Security.Cryptography.RSACryptoServiceProvider(3072)
    $other.PersistKeyInCsp=$false
    try{$wrong=$config|ConvertTo-Json -Depth 6|ConvertFrom-Json;$wrong.publicKeyXml=$other.ToXmlString($false);Reject {Test-UpdateEnvelope $valid $wrong $state} 'wrong signer rejected'}finally{$other.Dispose()}
    Reject {Test-UpdateEnvelope (New-Object byte[] 1048577) $config $state} 'package byte bound'
    Reject {Assert-UpdatePath $sandbox ([IO.Path]::GetTempPath())} 'path escape rejected'
    $linkTarget=Join-Path $sandbox 'real';$link=Join-Path $sandbox 'linked'
    New-Item -ItemType Directory -Path $linkTarget|Out-Null
    New-Item -ItemType Junction -Path $link -Target $linkTarget|Out-Null
    try{Reject {Assert-UpdatePath $sandbox $link} 'linked path rejected'}finally{Remove-Item -LiteralPath $link -Force}

    # The engine receives disposable roots and mocked task/process functions only.
    function Assert-UpdateWritableOnlyByAdmin([string]$Path) { if(-not (Test-Path -LiteralPath $Path)){throw 'Mock trust path missing.'} }
    function Assert-UpdateTrust([string]$Root,[string]$Lab,$Config) {
        if($script:trustFailure){throw 'Injected trust drift.'}
        if((Get-UpdateHash (Read-UpdateBytes $Lab (Join-Path $Lab 'policy.json'))) -cne $Config.policySha256){throw 'Policy drift.'}
    }
    function Assert-UpdateWorkerIdle { if($script:busy){throw 'Mock worker busy.'} }
    function Get-ScheduledTask {
        param($TaskName,$TaskPath)
        if($TaskName -ceq 'GsoClientLab-Expire'){return [pscustomobject]@{State='Disabled'}}
        return ($script:task|ConvertTo-Json -Depth 10|ConvertFrom-Json)
    }
    function Disable-ScheduledTask {param($TaskName,$TaskPath,$ErrorAction);$script:task.State='Disabled';$script:task.Settings.Enabled=$false}
    function Enable-ScheduledTask {param($TaskName,$TaskPath);$script:task.State='Ready';$script:task.Settings.Enabled=$true}
    function Set-ScheduledTask {
        param($TaskName,$TaskPath,$Action,$Settings)
        if($script:failEveryBudget){throw 'Injected persistent task update failure.'}
        if($script:failBudget){$script:failBudget=$false;throw 'Injected one-time task update failure.'}
        $script:task.Actions=@($Action);$script:task.Settings=$Settings
    }
    function New-EngineFixture([string]$Name) {
        $directory=Join-Path $sandbox $Name
        $root=Join-Path $directory 'updater';$lab=Join-Path $directory 'lab'
        foreach($relative in @('updater\bin','updater\private','updater\public','updater\inbox','lab\code','lab\queue','lab\results','lab\before-revision-10\requests')){New-Item -ItemType Directory -Path (Join-Path $directory $relative) -Force|Out-Null}
        foreach($name in (Get-UpdateNames)){[IO.File]::WriteAllBytes((Join-Path $lab ('code\'+$name)),$script:bytesByName[$name])}
        Write-UpdateJson (Join-Path $lab 'code-hashes.json') @{files=@(Get-UpdateNames|ForEach-Object{@{name=$_;sha256=$hashes[$_]}})}
        Write-UpdateJson (Join-Path $lab 'policy.json') @{test='unchanged'}
        $script:task=[pscustomobject]@{TaskName='GsoClientLab-Worker';TaskPath='\';State='Ready';Actions=@([pscustomobject]@{Execute='C:\Windows\System32\wscript.exe';Arguments=$state.workerArguments;WorkingDirectory=''});Settings=[pscustomobject]@{Enabled=$true;ExecutionTimeLimit='PT10M';MultipleInstances='IgnoreNew'};Principal=[pscustomobject]@{UserId='limited-user';RunLevel='Limited'};Triggers=@([pscustomobject]@{StartBoundary='fixed'})}
        $localConfig=$config|ConvertTo-Json -Depth 8|ConvertFrom-Json
        $localConfig|Add-Member -NotePropertyName workerInvariant -NotePropertyValue (Get-LabWorkerInvariant $script:task)
        $localConfig|Add-Member -NotePropertyName policySha256 -NotePropertyValue (Get-UpdateHash (Read-UpdateBytes $lab (Join-Path $lab 'policy.json')))
        Write-UpdateJson (Join-Path $root 'bin\config.json') $localConfig
        Write-UpdateJson (Join-Path $root 'private\state.json') $state
        $script:trustFailure=$false;$script:busy=$false;$script:failBudget=$false;$script:failEveryBudget=$false
        return @{root=$root;lab=$lab;config=$localConfig}
    }
    function Stage-Test($Fixture,$Manifest){$data=New-TestEnvelope $Manifest;[IO.File]::WriteAllBytes((Join-Path $Fixture.root ('inbox\'+$Manifest.id+'.json')),$data)}
    $fixture=New-EngineFixture 'success'
    $test=New-TestManifest
    Stage-Test $fixture $test
    Invoke-UpdateTick $fixture.root $fixture.lab
    $result=Read-UpdateJson $fixture.root (Join-Path $fixture.root ('public\'+$test.id+'.json'))
    Check ($result.status -ceq 'installed') 'engine commits valid update'
    Check ((Read-UpdateJson $fixture.root (Join-Path $fixture.root 'private\state.json')).sequence -eq 1) 'sequence committed'
    Check ($script:task.State -ceq 'Ready') 'worker enabled after commit'
    Check (-not (Test-Path -LiteralPath (Join-Path $fixture.root 'private\active.json'))) 'active claim removed on success'
    $reportHash=Get-UpdateHash (Read-UpdateBytes $fixture.root (Join-Path $fixture.root ('public\'+$test.id+'.json')))
    Invoke-UpdateTick $fixture.root $fixture.lab
    Check ((Get-UpdateHash (Read-UpdateBytes $fixture.root (Join-Path $fixture.root ('public\'+$test.id+'.json')))) -ceq $reportHash) 'completed identity not executed twice'
    $retainedPath=Join-Path $fixture.root ('private\completed-packages\'+$test.id+'.json')
    Check (Test-Path -LiteralPath $retainedPath) 'successful package is retained outside inbox'
    Check ((Get-UpdateHash (Read-UpdateBytes $fixture.root $retainedPath)) -ceq $result.sha256) 'retained bytes match trusted receipt'
    Check (-not (Test-Path -LiteralPath (Join-Path $fixture.root ('inbox\'+$test.id+'.json')))) 'verified inbox copy retired'
    Check ((Read-UpdateJson $fixture.root (Join-Path $fixture.root 'public\state.json')).inbox.entries -eq 0) 'fresh public inbox count'

    function Add-TerminalPackage($Fixture,[string]$Mode='installed'){
        $manifest=New-TestManifest;$bytes=New-TestEnvelope $manifest
        $path=Join-Path $Fixture.root ('inbox\'+$manifest.id+'.json')
        [IO.File]::WriteAllBytes($path,$bytes)
        $report=@{id=$manifest.id;status=$Mode;sha256=(Get-UpdateHash $bytes);sequence=1;revision=10;gameLaunched=$false;utc=[DateTime]::UtcNow.ToString('o')}
        Write-UpdateJson (Join-Path $Fixture.root ('public\'+$manifest.id+'.json')) $report
        return $manifest.id
    }
    $fixture=New-EngineFixture 'full-inbox'
    $retireState=$state|ConvertTo-Json -Depth 6|ConvertFrom-Json;$retireState.sequence=1
    $ids=@(1..16|ForEach-Object {Add-TerminalPackage $fixture})
    $snapshot=Get-UpdateInboxInventory $fixture.root $config $retireState
    Check ($snapshot.entries -eq 16 -and $snapshot.eligible -eq 16) 'exact full inbox inventory'
    Move-UpdateCompletedPackages $fixture.root $config $retireState
    $snapshot=Get-UpdateInboxInventory $fixture.root $config $retireState
    Check ($snapshot.entries -eq 8 -and $snapshot.retained -eq 8) 'at most eight retirements per tick'
    Check (@(Get-ChildItem (Join-Path $fixture.root 'public') -Filter '*.json').Count -eq 16) 'all used-ID reports remain'
    Move-UpdateCompletedPackages $fixture.root $config $retireState
    Check ((Get-UpdateInboxInventory $fixture.root $config $retireState).entries -eq 0) 'second bounded tick frees remaining terminal slots'
    Move-UpdateCompletedPackages $fixture.root $config $retireState
    Check ((Get-UpdateInboxInventory $fixture.root $config $retireState).retained -eq 16) 'completed packages are never overwritten'
    $fixture=New-EngineFixture 'expired-history'
    $historical=New-TestManifest
    $historical.createdUtc=[DateTime]::UtcNow.AddDays(-3).ToString('o');$historical.expiresUtc=[DateTime]::UtcNow.AddDays(-2).ToString('o')
    $historicalBytes=New-TestEnvelope $historical
    [IO.File]::WriteAllBytes((Join-Path $fixture.root ('inbox\'+$historical.id+'.json')),$historicalBytes)
    Write-UpdateJson (Join-Path $fixture.root ('public\'+$historical.id+'.json')) @{id=$historical.id;status='installed';sha256=(Get-UpdateHash $historicalBytes);sequence=1;revision=10;gameLaunched=$false;utc=[DateTime]::UtcNow.AddDays(-3).ToString('o')}
    $script:busy=$true;Move-UpdateCompletedPackages $fixture.root $config $retireState
    Check ((Get-UpdateInboxInventory $fixture.root $config $retireState).entries -eq 1) 'busy worker defers without mutation or permanent block'
    $script:busy=$false;Move-UpdateCompletedPackages $fixture.root $config $retireState
    Check ((Get-UpdateInboxInventory $fixture.root $config $retireState).retained -eq 1) 'expired successful signed package can be retained without execution'
    $fixture=New-EngineFixture 'protected-inbox'
    $rejectedId=Add-TerminalPackage $fixture 'rejected_preflight'
    $pending=New-TestManifest;Stage-Test $fixture $pending
    $tamperedId=Add-TerminalPackage $fixture
    [IO.File]::AppendAllText((Join-Path $fixture.root ('inbox\'+$tamperedId+'.json')),' ')
    $futureId=Add-TerminalPackage $fixture
    $testReceiptPath=Join-Path $fixture.root ('public\'+$futureId+'.json')
    $receipt=Read-UpdateJson $fixture.root $testReceiptPath;$receipt.utc=[DateTime]::UtcNow.AddHours(1).ToString('o');Write-UpdateJson $testReceiptPath $receipt
    $snapshot=Get-UpdateInboxInventory $fixture.root $config $retireState
    Check ($snapshot.protected -eq 4 -and $snapshot.eligible -eq 0) 'failed pending changed and future entries retained'
    Move-UpdateCompletedPackages $fixture.root $config $retireState
    Check (@(Get-ChildItem (Join-Path $fixture.root 'inbox') -File).Count -eq 4) 'no protected entry is retired'
    $legacyId=Add-TerminalPackage $fixture
    Write-UpdateJson (Join-Path $fixture.root ('public\'+$legacyId+'.json')) @{id=$legacyId;status='installed';utc=[DateTime]::UtcNow.ToString('o')}
    Check ((Get-UpdateInboxInventory $fixture.root $config $retireState).protected -eq 5) 'legacy receipt without digest remains protected'
    $invalidId=Add-TerminalPackage $fixture
    $path=Join-Path $fixture.root ('inbox\'+$invalidId+'.json')
    $envelope=Read-UpdateJson $fixture.root $path 1048576;$envelope.signatureBase64=[Convert]::ToBase64String((New-Object byte[] 384));Write-UpdateJson $path $envelope
    $testReceiptPath=Join-Path $fixture.root ('public\'+$invalidId+'.json');$receipt=Read-UpdateJson $fixture.root $testReceiptPath
    $receipt.sha256=Get-UpdateHash (Read-UpdateBytes $fixture.root $path);Write-UpdateJson $testReceiptPath $receipt
    Check ((Get-UpdateInboxInventory $fixture.root $config $retireState).protected -eq 6) 'receipt digest alone cannot retire an invalid signature'
    $fixture=New-EngineFixture 'archive-collision'
    $collisionId=Add-TerminalPackage $fixture
    $archive=New-Item -ItemType Directory (Join-Path $fixture.root 'private\completed-packages')
    $collisionPath=Join-Path $archive.FullName ($collisionId+'.json')
    [IO.File]::WriteAllText($collisionPath,'retained')
    Reject {Move-UpdateCompletedPackages $fixture.root $config $retireState} 'archive collision rejects without overwrite'
    Check ([IO.File]::ReadAllText($collisionPath) -ceq 'retained') 'previous archive bytes preserved'
    Write-UpdateJson (Join-Path $fixture.root 'private\state.json') $retireState
    Invoke-UpdateTick $fixture.root $fixture.lab
    Check (Test-Path (Join-Path $fixture.root 'private\inbox-retirement-blocked.json')) 'uncertain retirement blocks future ticks'
    $blockedHash=Get-UpdateHash (Read-UpdateBytes $fixture.root (Join-Path $fixture.root 'private\inbox-retirement-blocked.json'))
    Invoke-UpdateTick $fixture.root $fixture.lab
    Check ((Get-UpdateHash (Read-UpdateBytes $fixture.root (Join-Path $fixture.root 'private\inbox-retirement-blocked.json'))) -ceq $blockedHash) 'blocked retirement is not retried'
    $fixture=New-EngineFixture 'busy'
    $script:busy=$true;$test=New-TestManifest;Stage-Test $fixture $test
    Invoke-UpdateTick $fixture.root $fixture.lab
    Check ((Read-UpdateJson $fixture.root (Join-Path $fixture.root ('public\'+$test.id+'.json'))).status -ceq 'rejected_preflight') 'busy worker preflight'
    Check ($script:task.State -ceq 'Ready') 'busy worker untouched'
    $fixture=New-EngineFixture 'rollback'
    $test=New-TestManifest
    $script:bytesByName['Worker.ps1']=[Text.Encoding]::UTF8.GetBytes('# Updated limited worker')
    $test.files=@($test.files|ForEach-Object{if($_.name -ceq 'Worker.ps1'){@{name=$_.name;bytes=$script:bytesByName[$_.name].Length;sha256=(Get-UpdateHash $script:bytesByName[$_.name])}}else{$_}})
    Stage-Test $fixture $test
    $script:failBudget=$true
    Invoke-UpdateTick $fixture.root $fixture.lab
    $result=Read-UpdateJson $fixture.root (Join-Path $fixture.root ('public\'+$test.id+'.json'))
    Check ($result.status -ceq 'rolled_back') 'partial failure rolled back'
    Check ((Get-UpdateHash (Read-UpdateBytes $fixture.lab (Join-Path $fixture.lab 'code\Worker.ps1'))) -ceq $hashes['Worker.ps1']) 'old worker restored'
    Check ((Read-UpdateJson $fixture.root (Join-Path $fixture.root 'private\state.json')).sequence -eq 0) 'sequence restored'
    Check ($script:task.State -ceq 'Ready') 'rollback returns limited worker'
    $script:bytesByName['Worker.ps1']=[Text.Encoding]::UTF8.GetBytes("# Worker.ps1`r`n")
    $fixture=New-EngineFixture 'failed-rollback'
    $test=New-TestManifest;Stage-Test $fixture $test
    $script:failEveryBudget=$true
    Invoke-UpdateTick $fixture.root $fixture.lab
    Check ((Read-UpdateJson $fixture.root (Join-Path $fixture.root ('public\'+$test.id+'.json'))).status -ceq 'blocked_reconciliation') 'uncertain rollback reported'
    Check ($script:task.State -ceq 'Disabled') 'uncertain rollback leaves worker disabled'
    Check (Test-Path -LiteralPath (Join-Path $fixture.root 'private\active.json')) 'uncertain rollback retains claim'
    Check ((Read-UpdateJson $fixture.root (Join-Path $fixture.root 'public\state.json')).blocked) 'uncertain rollback blocks future updates'
    $fixture=New-EngineFixture 'interrupted'
    Write-UpdateJson (Join-Path $fixture.root 'private\active.json') @{id=[guid]::NewGuid().ToString();status='claimed'}
    Invoke-UpdateTick $fixture.root $fixture.lab
    Check ((Read-UpdateJson $fixture.root (Join-Path $fixture.root 'public\state.json')).blocked) 'interrupted update blocks forward progress'
    $fixture=New-EngineFixture 'archive'
    $test=New-TestManifest;$jobId=[guid]::NewGuid().ToString()
    Write-UpdateJson (Join-Path $fixture.lab ('queue\'+$jobId+'.json')) @{id=$jobId;action='verify-rules'}
    New-Item -ItemType Directory -Path (Join-Path $fixture.lab ('results\'+$jobId))|Out-Null
    Write-UpdateJson (Join-Path $fixture.lab ('results\'+$jobId+'\result.json')) @{id=$jobId;status='uncertain_error';cleanupVerified=$true;configCleanupVerified=$true;completedUtc=[DateTime]::UtcNow.ToString('o')}
    $requestHash=Get-UpdateHash (Read-UpdateBytes $fixture.lab (Join-Path $fixture.lab ('queue\'+$jobId+'.json')))
    $test.archive=@(@{id=$jobId;sha256=$requestHash});Stage-Test $fixture $test
    Invoke-UpdateTick $fixture.root $fixture.lab
    Check ((Read-UpdateJson $fixture.root (Join-Path $fixture.root ('public\'+$test.id+'.json'))).status -ceq 'installed') 'terminal request maintenance succeeds'
    Check (-not (Test-Path -LiteralPath (Join-Path $fixture.lab ('queue\'+$jobId+'.json')))) 'request leaves active queue'
    Check ((Get-UpdateHash (Read-UpdateBytes $fixture.lab (Join-Path $fixture.lab ('before-revision-10\requests\'+$jobId+'.json')))) -ceq $requestHash) 'archived request bytes preserved'
    Check (Test-Path -LiteralPath (Join-Path $fixture.lab ('results\'+$jobId+'\result.json'))) 'terminal evidence retained'
    foreach($terminal in @('interaction_captured_unreviewed','autonomous_captured_unreviewed')){
        $fixture=New-EngineFixture $terminal
        $test=New-TestManifest;$jobId=[guid]::NewGuid().ToString()
        Write-UpdateJson (Join-Path $fixture.lab ('queue\'+$jobId+'.json')) @{id=$jobId;action='auto-rules'}
        New-Item -ItemType Directory -Path (Join-Path $fixture.lab ('results\'+$jobId))|Out-Null
        $evidence=Join-Path $fixture.lab ('results\'+$jobId+'\result.json')
        Write-UpdateJson $evidence @{id=$jobId;status=$terminal;cleanupVerified=$true;configCleanupVerified=$true;completedUtc=[DateTime]::UtcNow.ToString('o')}
        $evidenceHash=Get-UpdateHash (Read-UpdateBytes $fixture.lab $evidence)
        $test.archive=@(@{id=$jobId;sha256=(Get-UpdateHash (Read-UpdateBytes $fixture.lab (Join-Path $fixture.lab ('queue\'+$jobId+'.json'))))})
        Stage-Test $fixture $test
        Invoke-UpdateTick $fixture.root $fixture.lab
        Check ((Read-UpdateJson $fixture.root (Join-Path $fixture.root ('public\'+$test.id+'.json'))).status -ceq 'installed') 'unreviewed completed request can leave active queue'
        Check ((Get-UpdateHash (Read-UpdateBytes $fixture.lab $evidence)) -ceq $evidenceHash) 'maintenance never edits acceptance evidence'
    }
    foreach($invalidFlag in @('false','true',0,1,$false)){
        $fixture=New-EngineFixture ('cleanup-flag-'+[guid]::NewGuid().ToString('N'))
        $test=New-TestManifest;$jobId=[guid]::NewGuid().ToString()
        $requestPath=Join-Path $fixture.lab ('queue\'+$jobId+'.json')
        Write-UpdateJson $requestPath @{id=$jobId;action='auto-rules'}
        New-Item -ItemType Directory -Path (Join-Path $fixture.lab ('results\'+$jobId))|Out-Null
        Write-UpdateJson (Join-Path $fixture.lab ('results\'+$jobId+'\result.json')) @{id=$jobId;status='autonomous_captured_unreviewed';cleanupVerified=$invalidFlag;configCleanupVerified=$true;completedUtc=[DateTime]::UtcNow.ToString('o')}
        $test.archive=@(@{id=$jobId;sha256=(Get-UpdateHash (Read-UpdateBytes $fixture.lab $requestPath))})
        Stage-Test $fixture $test
        Invoke-UpdateTick $fixture.root $fixture.lab
        Check ((Read-UpdateJson $fixture.root (Join-Path $fixture.root ('public\'+$test.id+'.json'))).status -ceq 'rejected_preflight') 'only boolean true confirms cleanup'
        Check (Test-Path -LiteralPath $requestPath) 'unconfirmed cleanup request remains queued'
    }
    $fixture=New-EngineFixture 'invalid-signature'
    $test=New-TestManifest;Stage-Test $fixture $test
    $path=Join-Path $fixture.root ('inbox\'+$test.id+'.json')
    $bad=Read-UpdateJson $fixture.root $path 1048576
    $bad.signatureBase64=[Convert]::ToBase64String((New-Object byte[] 384))
    Write-UpdateJson $path $bad
    Invoke-UpdateTick $fixture.root $fixture.lab
    Check ((Read-UpdateJson $fixture.root (Join-Path $fixture.root ('public\'+$test.id+'.json'))).status -ceq 'rejected_preflight') 'engine rejects wrong signature before task mutation'
    Check ($script:task.State -ceq 'Ready') 'signature failure leaves task untouched'
    $report=@{passed=$true;checks=$script:checks;runtime=$PSVersionTable.PSVersion.ToString();sandbox=$sandbox;realTasksChanged=$false;networkUsed=$false;nativeInputSent=$false}
    if($ReportPath){$report|ConvertTo-Json|Set-Content -LiteralPath $ReportPath -Encoding UTF8}
    $report|ConvertTo-Json
}finally{$rsa.Dispose()}
