#requires -Version 5.1
param([Parameter(Mandatory=$true)][string]$Workspace,[string]$ToolRoot)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
if(-not $ToolRoot){$ToolRoot=Split-Path $PSScriptRoot -Parent}
$core=Join-Path $Workspace 'checks/session-core.ps1'
New-Item -ItemType Directory -Path (Join-Path $Workspace 'checks') -Force|Out-Null
Copy-Item -LiteralPath (Join-Path $ToolRoot 'templates/session/Session.Core.ps1.in') -Destination $core
. $core
$checks=0
function Check([bool]$Value){if(-not $Value){throw 'Session bootstrap regression.'};$script:checks++}
function Refused([scriptblock]$Action){$failed=$false;try{& $Action|Out-Null}catch{$failed=$true};Check $failed}
$sid='S-1-5-21-100-200-300-1002'
$id='c0a83f65-4188-4482-916f-2ce81d5441e0'
$policy=[pscustomobject]@{gameUserSid=$sid;gameSessionId=8;targetName='GSO-CLIENT-LAB';targetAddress='192.168.240.11';sourceAddress='192.168.240.10';port=2222;sessionId=$id;leaseMode='manual';expiresUtc=$null}
$pb=[Text.Encoding]::UTF8.GetBytes(($policy|ConvertTo-Json -Compress))
$config=[pscustomobject]@{target=$policy.targetName;sessionId=$id;enabled=$true;policySha256=(Get-SessionHash $pb);publicKeyXml='synthetic-public-pin';pinnedHashes=@{unchanged='A'}}
$cb=[Text.Encoding]::UTF8.GetBytes(($config|ConvertTo-Json -Depth 5 -Compress))
$expected=[pscustomobject]@{gameUserSid=$sid;oldSession=8;targetName=$policy.targetName;targetAddress=$policy.targetAddress;sourceAddress=$policy.sourceAddress;sessionId=$id;policyBeforeSha256=(Get-SessionHash $pb);publicKeySha256=(Get-SessionHash ([Text.Encoding]::UTF8.GetBytes($config.publicKeyXml)))}
foreach($session in @(1,8,9,33)){
    $change=New-SessionBinding $pb $cb $expected $sid $session
    $after=Read-SessionJson $change.policy;$afterConfig=Read-SessionJson $change.config
    Check ($after.gameSessionMode -ceq 'current-user-interactive' -and $after.gameSessionId -eq 8)
    Check ($afterConfig.policySha256 -ceq (Get-SessionHash $change.policy))
    $after.PSObject.Properties.Remove('gameSessionMode');$afterConfig.policySha256=$config.policySha256
    Check (($after|ConvertTo-Json -Compress) -ceq ($policy|ConvertTo-Json -Compress))
    Check (($afterConfig|ConvertTo-Json -Depth 5 -Compress) -ceq ($config|ConvertTo-Json -Depth 5 -Compress))
}
Refused {New-SessionBinding $pb $cb $expected 'S-1-5-21-100-200-300-1003' 9}
Refused {New-SessionBinding $pb $cb $expected $sid 0}
Refused {New-SessionBinding $pb $cb $expected $sid -1}
foreach($field in @('gameUserSid','oldSession','targetName','targetAddress','sourceAddress','sessionId','policyBeforeSha256','publicKeySha256')){
    $bad=$expected|ConvertTo-Json|ConvertFrom-Json
    if($field -ceq 'oldSession'){$bad.$field=9}else{$bad.$field='changed'}
    Refused {New-SessionBinding $pb $cb $bad $sid 9}
}
$change=New-SessionBinding $pb $cb $expected $sid 9
Refused {New-SessionBinding $change.policy $change.config $expected $sid 9}
$dir=Join-Path $Workspace ('checks/session-bootstrap-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $dir|Out-Null
$p=Join-Path $dir 'policy.json';$c=Join-Path $dir 'config.json';$backup=Join-Path $dir 'backup'
[IO.File]::WriteAllBytes($p,$pb);[IO.File]::WriteAllBytes($c,$cb)
New-SessionBackup $p $c $backup
Refused {New-SessionBackup $p $c $backup}
[IO.File]::AppendAllText($p,' ')
Refused {Invoke-SessionCommit $p $c $backup $change}
Check (-not (Test-Path -LiteralPath (Join-Path $backup 'commit.claim')))
[IO.File]::WriteAllBytes($p,$pb)
Invoke-SessionCommit $p $c $backup $change
Check ((Get-SessionHash ([IO.File]::ReadAllBytes($p))) -ceq (Get-SessionHash $change.policy))
Check ((Get-SessionHash ([IO.File]::ReadAllBytes($c))) -ceq (Get-SessionHash $change.config))
Refused {Invoke-SessionCommit $p $c $backup $change}
Check ((Get-SessionHash ([IO.File]::ReadAllBytes((Join-Path $backup 'policy-before.json')))) -ceq (Get-SessionHash $pb))
# A failed renderer log is retained, never replayed or rewritten by this operation.
$request=[pscustomobject]@{id=$id;action='verify-panel';panel='profile';language='en'}
$result=[pscustomobject]@{id=$id;status='uncertain_cleanup';completedUtc=[DateTime]::UtcNow.ToString('o');cleanupVerified=$true;configCleanupVerified=$true;gamePid=123;gameStartedUtc='2026-01-01T00:00:00Z';rendererEvidence=@{cleanupVerified=$false;probeEnabled=$true}}
$req=Join-Path $dir 'request.json';$res=Join-Path $dir 'result.json'
[IO.File]::WriteAllText($req,($request|ConvertTo-Json));[IO.File]::WriteAllText($res,($result|ConvertTo-Json -Depth 4))
$terminal=[pscustomobject]@{id=$id;sha256=$null;retainedFailure=$true;gamePid=123;gameStartedUtc=$result.gameStartedUtc;completedUtc=$result.completedUtc}
$hash=Assert-SessionTerminalFiles $req $res $terminal ''
Check ($hash -ceq (Get-SessionHash ([IO.File]::ReadAllBytes($res))))
$result.cleanupVerified=$false;[IO.File]::WriteAllText($res,($result|ConvertTo-Json -Depth 4))
Refused {Assert-SessionTerminalFiles $req $res $terminal ''}
$result.cleanupVerified=$true;$result.status='launch_claimed';[IO.File]::WriteAllText($res,($result|ConvertTo-Json -Depth 4))
Refused {Assert-SessionTerminalFiles $req $res $terminal ''}
$result.status='uncertain_cleanup';[IO.File]::WriteAllText($res,($result|ConvertTo-Json -Depth 4))
Refused {Assert-SessionTerminalFiles $req $res $terminal ('A'*64)}
@{passed=$true;checks=$checks;gameLaunched=$false;installedPolicyChanged=$false}|ConvertTo-Json
