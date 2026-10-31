#requires -Version 5.1
param([Parameter(Mandatory=$true)][string]$Workspace)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$kit=Join-Path $Workspace 'kit'
. (Join-Path $kit 'code\Lab.Common.ps1')
. (Join-Path $kit 'code\Interaction.ps1')
$script:checks=0
function Check([bool]$Value,[string]$Name){if(-not $Value){throw $Name};$script:checks++}
function Refused([scriptblock]$Action,[string]$Name){$refused=$false;try{& $Action|Out-Null}catch{$refused=$true};Check $refused $Name}
$id='c0a83f65-4188-4482-916f-2ce81d5441e0'
$hash='A'*64
$now=[DateTime]::UtcNow.ToString('o')
foreach($panel in @('ranks','profile','topall','rules','primary','pistol')){
    foreach($language in @('ru','en')){
        $command=ConvertFrom-LabCommand ('capture '+$id+' '+$panel+' '+$language)
        $job=[pscustomobject]@{id=$id;action='capture-panel';createdUtc=$now;panel=$panel;language=$language}
        Assert-LabJob $job
        $program=New-LabPanelProgram $job
        Check ($command.panel -ceq $panel -and $command.language -ceq $language -and ($program -split "`r`n"|Where-Object{$_ -ceq 'snapshot'}).Count -eq 2) 'Legacy panel behavior changed.'
        Check ((Get-LabImageLimit $job) -eq 2) 'Legacy image boundary expanded.'
    }
}
foreach($language in @('ru','en')){
    $job=[pscustomobject]@{id=$id;action='verify-rules';createdUtc=$now;language=$language}
    $command=ConvertFrom-LabCommand ('verify-rules '+$id+' '+$language)
    Assert-LabJob $job
    $program=New-LabInteractionProgram $job
    Check ($command.language -ceq $language -and @($program -split "`r`n"|Where-Object{$_ -ceq 'snapshot'}).Count -eq 1) 'Interaction must begin with one rules frame.'
    Check (@($program -split "`r`n"|Where-Object{$_ -ceq 'rules'}).Count -eq 1 -and $program -notmatch '(?im)^(bind|alias|say|say_team|quit|disconnect)') 'Initial program has unauthorized commands.'
    Check ((Get-LabImageLimit $job) -eq 5 -and (Get-LabCaptureLabels $job).Count -eq 5) 'Interaction capture limit.'
}
foreach($command in @('status x',('verify-rules '+$id+' ru rules'),('verify-rules '+$id+' RU'),('image '+$id+' 6'),('run '+$id+' 1'),('step '+$id+' arbitrary '+$hash),('step '+$id+' ok-first '+('a'*64)),('step '+$id+' ok-first '+$hash+' extra'),('step '+$id+' ok-first '+$hash+"`n"))){
    Refused {ConvertFrom-LabCommand $command} ('Protocol accepted '+$command)
}
$job=[pscustomobject]@{id=$id;action='verify-rules';createdUtc=$now;language='ru'}
$bad=$job|ConvertTo-Json|ConvertFrom-Json;$bad|Add-Member panel rules
Refused {Assert-LabJob $bad} 'Extra interaction job field.'
$bad=$job|ConvertTo-Json|ConvertFrom-Json;$bad.createdUtc=[DateTime]::UtcNow.AddMinutes(-4).ToString('o')
Refused {Assert-LabJob $bad} 'Stale interaction job.'
$bad=$job|ConvertTo-Json|ConvertFrom-Json;$bad.language='de'
Refused {Assert-LabJob $bad} 'Unsupported language.'
for($number=1;$number -le 4;$number++){
    $phase=Get-LabInteractionPhase $number
    $images=@(1..$number|ForEach-Object{[pscustomobject]@{number=$_;sha256=$hash}})
    $result=[pscustomobject]@{id=$id;action='verify-rules';status='awaiting_action';nextAction=$phase;images=$images;frameCapturedUtc=[DateTime]::UtcNow.ToString('o');interaction=@{reopenNotBeforeUtc=[DateTime]::UtcNow.AddSeconds(-1).ToString('o')}}
    $request=ConvertFrom-LabCommand ('step '+$id+' '+$phase+' '+$hash)
    Assert-LabStepReady $job $result $request
    $step=[pscustomobject]@{id=$id;phase=$phase;frameSha256=$hash;createdUtc=[DateTime]::UtcNow.ToString('o')}
    Assert-LabInteractionStep $step $job $result
    Check $true 'Correct phase refused.'
    $wrong=$request.Clone();$wrong.phase='reopen';if($phase -ceq 'reopen'){$wrong.phase='controls'}
    Refused {Assert-LabStepReady $job $result $wrong} 'Out-of-order phase.'
    $wrong=$request.Clone();$wrong.frameSha256='B'*64
    Refused {Assert-LabStepReady $job $result $wrong} 'Wrong frame hash.'
    $result.frameCapturedUtc=[DateTime]::UtcNow.AddSeconds(-68).ToString('o')
    Assert-LabStepReady $job $result $request
    Check $true 'Observed 68-second review was rejected by the corrected frame budget.'
    $result.frameCapturedUtc=[DateTime]::UtcNow.AddSeconds(-121).ToString('o')
    Refused {Assert-LabStepReady $job $result $request} 'Stale frame accepted.'
    $result.frameCapturedUtc=[DateTime]::UtcNow.ToString('o');$result.status='interaction_running'
    Refused {Assert-LabStepReady $job $result $request} 'Running phase accepted another action.'
    $result.status='awaiting_action';$step.createdUtc=[DateTime]::UtcNow.AddSeconds(-31).ToString('o')
    Refused {Assert-LabInteractionStep $step $job $result} 'Old acknowledgement.'
    if($phase -ceq 'reopen'){
        $result.interaction.reopenNotBeforeUtc=[DateTime]::UtcNow.AddSeconds(5).ToString('o')
        Refused {Assert-LabStepReady $job $result $request} 'Alias cooldown bypassed.'
    }
}
Refused {Get-LabInteractionPhase 0} 'No initial frame required.'
Refused {Get-LabInteractionPhase 5} 'Extra phase allowed.'

$sandbox=Join-Path $Workspace ('checks\protocol-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path (Join-Path $sandbox 'cstrike'),(Join-Path $sandbox 'code'),(Join-Path $sandbox 'queue\steps'),(Join-Path $sandbox 'results')|Out-Null
$config=Join-Path $sandbox 'cstrike\config.cfg'
$bindings="bind `"F5`" `"snapshot`"`r`nbind `"u`" `"messagemode2`"`r`nbind `"s`" `"+back`"`r`nbind `"a`" `"+moveleft`"`r`n"
[IO.File]::WriteAllText($config,$bindings)
Assert-LabInteractionBindings $sandbox
Check ([IO.File]::ReadAllText($config) -ceq $bindings) 'Binding preflight mutated config.'
[IO.File]::AppendAllText($config,"bind `"F5`" `"quit`"`r`n")
Refused {Assert-LabInteractionBindings $sandbox} 'Last binding override ignored.'
Check ([IO.File]::ReadAllText($config).Contains('quit')) 'Refusal rewrote config.'
[IO.File]::WriteAllText($config,$bindings+"bind F5 quit`r`n")
Refused {Assert-LabInteractionBindings $sandbox} 'Unquoted override ignored.'
[IO.File]::WriteAllText($config,$bindings+"bind `"u`" `"messagemode2`";quit`r`n")
Refused {Assert-LabInteractionBindings $sandbox} 'Chained override ignored.'

# Execute the real broker against disposable data. No worker, network or window API is invoked.
Copy-Item -Path (Join-Path $kit 'code\*') -Destination (Join-Path $sandbox 'code')
$core=@(Get-ChildItem -LiteralPath (Join-Path $sandbox 'code') -File)
Write-LabJson (Join-Path $sandbox 'code-hashes.json') @{files=@($core|ForEach-Object{@{name=$_.Name;sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}})}
Write-LabJson (Join-Path $sandbox 'policy.json') @{targetName='GSO-CLIENT-LAB';expiresUtc=[DateTime]::UtcNow.AddHours(1).ToString('o');panelsEnabled=$true;serverAddress='192.168.240.10';serverPort=27025;serverName='GsoClientLab';serverMap='de_dust2'}
function Broker([string]$Command,[bool]$Accepted){
    $info=New-Object Diagnostics.ProcessStartInfo
    $info.FileName=(Get-Process -Id $PID).Path
    $info.UseShellExecute=$false;$info.CreateNoWindow=$true;$info.RedirectStandardOutput=$true;$info.RedirectStandardError=$true
    $info.Arguments='-NoLogo -NoProfile -File "'+(Join-Path $sandbox 'code\Broker.ps1')+'" -Root "'+$sandbox+'"'
    $info.EnvironmentVariables['COMPUTERNAME']='GSO-CLIENT-LAB'
    $info.EnvironmentVariables['SSH_ORIGINAL_COMMAND']=$Command
    $process=[Diagnostics.Process]::Start($info)
    try{
        $output=$process.StandardOutput.ReadToEndAsync();$errorText=$process.StandardError.ReadToEndAsync()
        if(-not $process.WaitForExit(10000)){$process.Kill();throw 'Offline broker timed out.'}
        $text=$output.GetAwaiter().GetResult();$errorOutput=$errorText.GetAwaiter().GetResult()
        Check (($process.ExitCode -eq 0) -eq $Accepted) ('Unexpected broker outcome: '+$Command+' '+$text+' '+$errorOutput)
        if($text.Length -gt 65536){throw 'Oversized broker metadata.'}
        return ($text|ConvertFrom-Json)
    }finally{$process.Dispose()}
}
Broker ('verify-rules '+$id+' ru') $true|Out-Null
Broker ('verify-rules '+$id+' ru') $false|Out-Null
$saved=Read-LabJson (Join-Path $sandbox ('queue\'+$id+'.json'))
Check ($saved.action -ceq 'verify-rules' -and $saved.language -ceq 'ru' -and -not $saved.PSObject.Properties['panel']) 'Broker job shape changed.'
New-Item -ItemType Directory -Path (Join-Path $sandbox ('results\'+$id))|Out-Null
$result=[pscustomobject]@{id=$id;action='verify-rules';status='awaiting_action';nextAction='ok-first';images=@(@{number=1;sha256=$hash});frameCapturedUtc=[DateTime]::UtcNow.ToString('o');interaction=@{reopenNotBeforeUtc=$null}}
Write-LabJson (Join-Path $sandbox ('results\'+$id+'\result.json')) $result
Broker ('step '+$id+' controls '+$hash) $false|Out-Null
Broker ('step '+$id+' ok-first '+$hash) $true|Out-Null
Broker ('step '+$id+' ok-first '+$hash) $false|Out-Null
Check (@(Get-ChildItem -LiteralPath (Join-Path $sandbox 'queue\steps') -Filter '*.json').Count -eq 1) 'Duplicate step wrote more than one input record.'
$step=Read-LabJson (Join-Path $sandbox ('queue\steps\'+$id+'.ok-first.json'))
Assert-LabInteractionStep $step $saved $result
Broker ('image '+$id+' 5') $false|Out-Null
Broker ('cancel '+$id) $true|Out-Null
Check (Test-Path -LiteralPath (Join-Path $sandbox ('queue\'+$id+'.cancel'))) 'Cancel marker missing.'
# An archived request remains readable and cannot be replayed after bounded maintenance.
$archive=Join-Path $sandbox 'before-revision-10\requests'
New-Item -ItemType Directory -Path $archive|Out-Null
[IO.File]::Move((Join-Path $sandbox ('queue\'+$id+'.json')),(Join-Path $archive ($id+'.json')))
$archived=Broker ('result '+$id) $true
Check ($archived.id -ceq $id) 'Archived result became inaccessible.'
Broker ('verify-rules '+$id+' ru') $false|Out-Null
# The new command is fixed, opt-in, and cannot share manual step acknowledgements.
$autoId=[guid]::NewGuid().ToString('D')
Broker ('auto-rules '+$autoId+' en') $true|Out-Null
$autoJob=Read-LabJson (Join-Path $sandbox ('queue\'+$autoId+'.json'))
Assert-LabJob $autoJob
Check (Get-LabAutonomousMode $sandbox $autoJob) 'Autonomous marker missing.'
Check ($autoJob.language -ceq 'en' -and $autoJob.action -ceq 'verify-rules') 'Autonomous job escaped the fixed schema.'
Broker ('auto-rules '+$autoId+' en') $false|Out-Null
Broker ('auto-rules '+[guid]::NewGuid().ToString('D')+' en extra') $false|Out-Null
Broker ('auto-rules '+[guid]::NewGuid().ToString('D')+' de') $false|Out-Null
New-Item -ItemType Directory -Path (Join-Path $sandbox ('results\'+$autoId))|Out-Null
$result.id=$autoId
Write-LabJson (Join-Path $sandbox ('results\'+$autoId+'\result.json')) $result
Broker ('step '+$autoId+' ok-first '+$hash) $false|Out-Null
Check (-not (Test-Path -LiteralPath (Join-Path $sandbox ('queue\steps\'+$autoId+'.ok-first.json')))) 'Manual step reached an autonomous job.'
New-Item -ItemType Directory -Path (Join-Path $sandbox 'before-revision-06\requests')|Out-Null
foreach($panel in @('ranks','profile','topall')){
    $panelId=[guid]::NewGuid().ToString('D')
    Broker ('auto-panel '+$panelId+' '+$panel+' ru') $true|Out-Null
    $panelJob=Read-LabJson (Join-Path $sandbox ('queue\'+$panelId+'.json'))
    Assert-LabRunnerJob $panelJob
    Check ($panelJob.action -ceq 'verify-panel' -and $panelJob.panel -ceq $panel -and (Get-LabAutonomousMode $sandbox $panelJob)) 'Fixed panel opt-in differs.'
    Check ((Get-LabRunnerImageLimit $panelJob) -eq 5 -and (Get-LabRunnerCaptureLabels $panelJob)[0] -ceq ($panel+'-first')) 'Panel evidence boundary differs.'
    $program=New-LabRunnerInteractionProgram $panelJob
    Check (@($program -split "`r`n"|Where-Object {$_ -ceq $panel}).Count -eq 1 -and @($program -split "`r`n"|Where-Object {$_ -ceq 'snapshot'}).Count -eq 1) 'Wrong initial panel program.'
    Broker ('auto-panel '+$panelId+' '+$panel+' ru') $false|Out-Null
    $modePath=Join-Path $sandbox ('queue\steps\'+$panelId+'.autonomous.json')
    $mode=Read-LabJson $modePath
    $mode.panel='rules';Write-LabJson $modePath $mode
    Refused {Get-LabAutonomousMode $sandbox $panelJob} 'Mismatched panel marker.'
    $bad=$panelJob|ConvertTo-Json|ConvertFrom-Json;$bad.panel='rules'
    Refused {Assert-LabRunnerJob $bad} 'Rules accepted through new panel schema.'
    $bad.panel=$panel;$bad|Add-Member NoteProperty command 'quit'
    Refused {Assert-LabRunnerJob $bad} 'Extra panel job field.'
}
foreach($suffix in @('rules ru','primary ru','ranks RU','profile en extra',"topall en`n",'ranks;quit en')){
    Broker ('auto-panel '+[guid]::NewGuid().ToString('D')+' '+$suffix) $false|Out-Null
}
[IO.File]::WriteAllText((Join-Path $sandbox ('before-revision-06\requests\'+$id+'.json')),'{}')
Refused {Get-LabPaths $sandbox $id} 'Ambiguous old/new archive accepted.'
Write-LabJson (Join-Path $sandbox 'maintenance-revision-06.json') @{revision=7}
Broker ('verify-rules '+[guid]::NewGuid().ToString('D')+' en') $false|Out-Null

$allErrors=@()
foreach($file in @(Get-ChildItem -LiteralPath $kit -Recurse -Filter '*.ps1')){
    $tokens=$null;$parseErrors=$null
    [System.Management.Automation.Language.Parser]::ParseFile($file.FullName,[ref]$tokens,[ref]$parseErrors)|Out-Null
    $allErrors+=@($parseErrors)
}
Check ($allErrors.Count -eq 0) 'Package script parser errors.'
Write-LabJson (Join-Path $Workspace ('checks\protocol-ps'+$PSVersionTable.PSVersion.Major+'.json')) @{checks=$checks;passed=$true;runtime=$PSVersionTable.PSVersion.ToString();sandbox=$sandbox;nativeInputSent=$false;networkUsed=$false}
Write-Output ('Protocol and broker checks passed: '+$checks+'; runtime '+$PSVersionTable.PSVersion+'. No native input or network.')
