#requires -Version 5.1
param([Parameter(Mandatory=$true)][string]$Workspace)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
. (Join-Path $Workspace 'kit\code\Lab.Common.ps1')
. (Join-Path $Workspace 'kit\code\Diagnostics.ps1')
$script:checks=0
function Check([bool]$Value,[string]$Message){if(-not $Value){throw $Message};$script:checks++}
function Refused([scriptblock]$Action){$failed=$false;try{& $Action|Out-Null}catch{$failed=$true};Check $failed 'Expected fail-closed result.'}
$id=[guid]::NewGuid().ToString('D')
$script:started=0
function Start-Process([string]$FilePath,[string]$ArgumentList,[string]$WindowStyle,[switch]$PassThru){
    Check ($FilePath -ceq (Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe')) 'Unexpected executable.'
    Check ($ArgumentList -ceq ('-NoLogo -NoProfile -NonInteractive -STA -WindowStyle Hidden -File "C:\ProgramData\GsoClientLab\code\Worker.ps1" -Root "C:\ProgramData\GsoClientLab" -ShellJobId '+$id)) 'Unexpected fixed helper arguments.'
    Check ($WindowStyle -ceq 'Hidden' -and $PassThru) 'Helper must stay hidden.'
    $script:started++;return [pscustomobject]@{mock=$true}
}
[void](Start-LabDesktopHelper 'C:\ProgramData\GsoClientLab' $id)
Refused {Start-LabDesktopHelper 'C:\other' $id}
Refused {Start-LabDesktopHelper 'C:\ProgramData\GsoClientLab' ($id+' & command')}
Check ($started -eq 1) 'Rejected inputs started a process.'
Refused {Assert-LabDesktopRequest 'C:\other' @{} $id}
$script:read=0
function Read-LabJson([string]$Path,[int]$Limit){$script:read++;return [pscustomobject]@{id=$script:receiptId;accepted=$script:accepted;stage=$script:stage}}
function New-Helper([bool]$First,[bool]$Second,[int]$Code){
    $p=[pscustomobject]@{calls=0;killed=0;first=$First;second=$Second;ExitCode=$Code}
    $p|Add-Member ScriptMethod WaitForExit {param($Milliseconds);$this.calls++;if($this.calls -eq 1){return $this.first};return $this.second}
    $p|Add-Member ScriptMethod Kill {$this.killed++}
    return $p
}
$script:receiptId=$id;$script:accepted=$true;$script:stage='desktop_returned'
$p=New-Helper $true $true 0
Confirm-LabDesktopHelper $p 'C:\unused' $id
Check ($p.calls -eq 1 -and $p.killed -eq 0 -and $read -eq 1) 'Successful dispatch differs.'
$p=New-Helper $false $true 0
Refused {Confirm-LabDesktopHelper $p 'C:\unused' $id}
Check ($p.killed -eq 1 -and $p.calls -eq 2 -and $read -eq 1) 'Timed-out helper was retried or receipt read.'
$p=New-Helper $true $true 1
Refused {Confirm-LabDesktopHelper $p 'C:\unused' $id}
Check ($read -eq 1) 'Failed helper receipt must not authorize dispatch.'
$script:accepted=$false
Refused {Confirm-LabDesktopHelper (New-Helper $true $true 0) 'C:\unused' $id}
$script:accepted=$true;$script:receiptId=[guid]::NewGuid().ToString()
Refused {Confirm-LabDesktopHelper (New-Helper $true $true 0) 'C:\unused' $id}
$script:receiptId=$id;$script:stage='desktop_failed'
Refused {Confirm-LabDesktopHelper (New-Helper $true $true 0) 'C:\unused' $id}
$worker=[IO.File]::ReadAllText((Join-Path $Workspace 'kit\code\Worker.ps1'))
Check ($worker.Contains('if($ShellJobId){Invoke-LabDesktopChild $Root $policy $ShellJobId;return}')) 'Child must not drain queue.'
$source=[IO.File]::ReadAllText((Join-Path $Workspace 'kit\code\Diagnostics.ps1'))
Check ($source.Contains('[IO.FileMode]::CreateNew') -and $source.Contains("'shell-launch.claim'")) 'Dispatch must have a permanent claim.'
Check ($source.Contains("'open',1") -and -not $source.Contains('runas')) 'Dispatch cannot elevate.'
Check ($source -notmatch 'SendInput|AttachThreadInput|SendKeys|SetForegroundWindow') 'Global activation or input appeared.'
$script:candidates=@([pscustomobject]@{Id=1234;SessionId=7;StartTime=[DateTime]::UtcNow})
$policy=@{gameRoot='C:\fixed-game';gameSessionId=7}
$launchUtc=[DateTime]::UtcNow.AddSeconds(-1)
$script:line='hl.exe -game cstrike +exec gso_lan_'+([guid]$id).ToString('N')+'.cfg'
$script:ownerMatches=$true
function Get-LabProcess($Policy){return $script:candidates}
function Get-CimInstance {return [pscustomobject]@{ExecutablePath='C:\fixed-game\hl.exe';SessionId=7;CommandLine=$script:line}}
function Assert-LabInteractionOwner($Policy,$Result){if(-not $script:ownerMatches){throw 'Mock owner differs.'}}
$result=@{gamePid=$null;gameStartedUtc=$null}
Find-LabDispatchedGame $policy $result $launchUtc $id
Check ($result.gamePid -eq 1234 -and $result.gameStartedUtc -ceq $candidates[0].StartTime.ToUniversalTime().ToString('o')) 'Reconciliation did not pin process identity.'
$script:line='hl.exe +exec unrelated.cfg'
Refused {Find-LabDispatchedGame $policy @{} $launchUtc $id}
$script:line='hl.exe +exec gso_lan_'+([guid]$id).ToString('N')+'.cfg'
$script:ownerMatches=$false
Refused {Find-LabDispatchedGame $policy @{} $launchUtc $id}
$script:ownerMatches=$true
$script:candidates+=@([pscustomobject]@{Id=2345;SessionId=7;StartTime=[DateTime]::UtcNow})
Refused {Find-LabDispatchedGame $policy @{} $launchUtc $id}
Write-LabJson (Join-Path $Workspace ('checks\shell-ps'+$PSVersionTable.PSVersion.Major+'.json')) @{passed=$true;checks=$checks;processesStarted=$false;nativeInputSent=$false;comInvoked=$false;runtime=$PSVersionTable.PSVersion.ToString()}
'Shell launch policy checks passed: '+$checks
