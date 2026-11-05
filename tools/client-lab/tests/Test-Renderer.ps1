#requires -Version 5.1
param([Parameter(Mandatory=$true)][string]$Workspace)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
. (Join-Path $Workspace 'kit\code\Lab.Common.ps1')
. (Join-Path $Workspace 'kit\code\Diagnostics.ps1')
$script:checks=0
function Check([bool]$Value,[string]$Message){if(-not $Value){throw $Message};$script:checks++}
function Refused([scriptblock]$Action){$failed=$false;try{& $Action|Out-Null}catch{$failed=$true};Check $failed 'Expected renderer refusal.'}
$id=[guid]::NewGuid().ToString('D');$token=([guid]$id).ToString('N')
$frame="GSO_RENDERER_BEGIN_$token`nGL Vendor: Test vendor`nGL Renderer: Test renderer`nGL Version: 4.6 test`nGL Extensions: GL_test`nGSO_RENDERER_END_$token`n"
$identity=ConvertFrom-LabRendererFrame $frame $id
Check ($identity.backend -ceq 'OpenGL' -and $identity.renderer -ceq 'Test renderer') 'Renderer projection differs.'
$echoFrame=$frame.Replace("GSO_RENDERER_BEGIN_$token`n","GSO_RENDERER_BEGIN_$token `n").Replace("GSO_RENDERER_END_$token`n","GSO_RENDERER_END_$token `t`n")
Check ((ConvertFrom-LabRendererFrame $echoFrame $id).renderer -ceq 'Test renderer') 'Native echo whitespace was rejected.'
Refused {ConvertFrom-LabRendererFrame (' '+$echoFrame) $id}
$inlineEnd=$echoFrame.Replace("GL Extensions: GL_test`n",'GL Extensions: GL_test ')
Check ((ConvertFrom-LabRendererFrame $inlineEnd $id).vendor -ceq 'Test vendor') 'Extension-line end marker was rejected.'
Refused {ConvertFrom-LabRendererFrame ($echoFrame.Replace('Test vendor',('GSO_RENDERER_END_'+$token))) $id}
Refused {ConvertFrom-LabRendererFrame $frame ([guid]::NewGuid().ToString())}
Refused {ConvertFrom-LabRendererFrame ($frame.Replace('GL Vendor: Test vendor',"GL Vendor: one`nGL Vendor: two")) $id}
Refused {ConvertFrom-LabRendererFrame ($frame.Replace("GL Version: 4.6 test`n",'')) $id}
Refused {ConvertFrom-LabRendererFrame ($frame.Replace('Test renderer',([string][char]233))) $id}
Refused {ConvertFrom-LabRendererFrame ($frame.Replace('GL_test',('x'*16384))) $id}
Refused {ConvertFrom-LabRendererFrame ($frame.Replace('GL_test',"GL_test`nUnknown command: gl_dump")) $id}
Check ((Add-LabRendererProgram $id 'wait' $false) -ceq 'wait') 'Disabled probe changed program.'
Check ((Add-LabRendererProgram $id 'wait' $true).StartsWith("echo GSO_RENDERER_BEGIN_$token`r`ngl_dump`r`n")) 'Probe program differs.'
Check ((Add-LabRendererLaunchArguments '-applaunch 10 -gl -windowed' $true) -ceq '-applaunch 10 -gl -condebug -windowed') 'Probe arguments differ.'
Refused {Add-LabRendererLaunchArguments '-applaunch 10 -gl -condebug' $true}
$sandbox=Join-Path $Workspace ('checks\renderer-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path (Join-Path $sandbox 'cstrike')|Out-Null
$policy=@{gameRoot=$sandbox}
$probe=New-LabRendererProbe $policy $id
Check ($probe.enabled -and $probe.paths.Count -eq 4) 'Clean renderer preflight failed.'
$path=Join-Path $sandbox 'qconsole.log'
[IO.File]::WriteAllText($path,'foreign console output')
$before=(Get-FileHash -LiteralPath $path).Hash
$blocked=New-LabRendererProbe $policy $id
Check (-not $blocked.enabled -and $blocked.reason -ceq 'preexisting_log_preserved') 'Existing log was not preserved.'
Check ((Get-FileHash -LiteralPath $path).Hash -ceq $before) 'Preflight changed existing data.'
[IO.File]::Delete($path)
$launchUtc=[DateTime]::UtcNow.AddSeconds(-3)
$script:process=[pscustomobject]@{Id=1234;StartTime=[DateTime]::UtcNow.AddSeconds(-2)}
$result=@{id=$id;gamePid=1234;gameStartedUtc=$process.StartTime.ToUniversalTime().ToString('o');cleanupVerified=$false;rendererVerified=$false}
$script:ownerMatches=$true;$script:running=$true
$script:command='hl.exe -condebug +exec gso_lan_'+$token+'.cfg'
function Assert-LabInteractionOwner($Policy,$Result){if(-not $script:ownerMatches){throw 'Owner mismatch.'}}
function Get-LabProcess($Policy,[int]$ProcessId){if($script:running){return $script:process}}
function Get-CimInstance {return [pscustomobject]@{ProcessId=1234;CommandLine=$script:command}}
[IO.File]::WriteAllText($path,('unretained private startup output'+"`n"+$frame),[Text.Encoding]::ASCII)
$result.rendererEvidence=Get-LabRendererEvidence $policy $result $probe $launchUtc
Check ($result.rendererEvidence.status -ceq 'identified') 'Process-bound renderer was not identified.'
$created=(Get-Item -LiteralPath $path).CreationTimeUtc
[IO.File]::SetCreationTimeUtc($path,$launchUtc.AddMinutes(-1))
Check ((Get-LabRendererEvidence $policy $result $probe $launchUtc).status -ceq 'unconfirmed') 'Stale log was accepted.'
[IO.File]::SetCreationTimeUtc($path,$created)
Check (-not ($result.rendererEvidence|ConvertTo-Json -Depth 8).Contains('private startup')) 'Unrelated console data escaped.'
Check (-not (Remove-LabRendererLogs $policy $result $probe $launchUtc)) 'Live process log was deleted.'
$script:ownerMatches=$false
Check ((Get-LabRendererEvidence $policy $result $probe $launchUtc).status -ceq 'unconfirmed') 'Wrong owner was accepted.'
$script:ownerMatches=$true;$script:command='hl.exe -condebug +exec unrelated.cfg'
Check ((Get-LabRendererEvidence $policy $result $probe $launchUtc).status -ceq 'unconfirmed') 'Wrong request was accepted.'
$script:command='hl.exe -condebug +exec gso_lan_'+$token+'.cfg'
[IO.File]::WriteAllText($path,($frame+$frame))
Check ((Get-LabRendererEvidence $policy $result $probe $launchUtc).status -ceq 'unconfirmed') 'Repeated frame was accepted.'
[IO.File]::WriteAllText($path,$frame)
Check ((Get-LabRendererEvidence $policy $result $probe ([DateTime]::UtcNow.AddSeconds(10))).status -ceq 'unconfirmed') 'Stale process was accepted.'
[IO.File]::WriteAllText((Join-Path $sandbox 'console.log'),$frame)
Check ((Get-LabRendererEvidence $policy $result $probe $launchUtc).status -ceq 'unconfirmed') 'Ambiguous logs were accepted.'
[IO.File]::Delete((Join-Path $sandbox 'console.log'))
$script:running=$false;$result.cleanupVerified=$true
$result.rendererEvidence.cleanupVerified=Remove-LabRendererLogs $policy $result $probe $launchUtc
Check ($result.rendererEvidence.cleanupVerified -and -not (Test-Path -LiteralPath $path)) 'Owned log cleanup failed.'
$result.completedUtc=[DateTime]::UtcNow.ToString('o')
Check ((Test-LabRendererEvidence $result $id).version -ceq '4.6 test') 'Independent validation failed.'
Check (-not $result.rendererVerified) 'Legacy renderer flag changed.'
$result.rendererEvidence.identity.renderer='forged'
Refused {Test-LabRendererEvidence $result $id}
$result.rendererEvidence.identity.renderer='Test renderer'
$saved=$result.rendererEvidence.frameSha256;$result.rendererEvidence.frameSha256='0'*64
Refused {Test-LabRendererEvidence $result $id}
$result.rendererEvidence.frameSha256=$saved
$result.rendererEvidence.gamePid=999
Refused {Test-LabRendererEvidence $result $id}
$result.rendererEvidence.gamePid=1234
$result.status='autonomous_captured_unreviewed';$result.failureCode=$null;$result.configCleanupVerified=$true
if($PSVersionTable.PSVersion.Major -ge 7){
    $source=Join-Path $sandbox 'result.json';$receipt=Join-Path $sandbox 'review.json'
    Write-LabJson $source $result
    & (Join-Path $PSScriptRoot '../Test-LabRenderer.ps1') -ResultPath $source -JobId $id -OutputPath $receipt|Out-Null
    $review=Read-LabJson $receipt
    Check ($review.rendererIdentified -and $review.sourceResultSha256 -ieq (Get-FileHash -LiteralPath $source).Hash) 'Review is not bound to source bytes.'
    Refused {& (Join-Path $PSScriptRoot '../Test-LabRenderer.ps1') -ResultPath $source -JobId $id -OutputPath $receipt}
}
$result.rendererEvidence.observedUtc=[DateTime]::UtcNow.AddHours(1).ToString('o')
Refused {Test-LabRendererEvidence $result $id}
Check ([Text.Encoding]::UTF8.GetByteCount(($result|ConvertTo-Json -Depth 8)) -lt 65536) 'Renderer result exceeds broker limit.'
[IO.File]::WriteAllText($path,'replacement without owned markers')
Check (-not (Remove-LabRendererLogs $policy $result $probe $launchUtc) -and (Test-Path -LiteralPath $path)) 'Replacement log was deleted.'
[IO.File]::Delete($path)
$linked=Join-Path $sandbox 'linked'
New-Item -ItemType Directory -Path $linked|Out-Null
$junction=Join-Path $linked 'cstrike'
New-Item -ItemType Junction -Path $junction -Target (Join-Path $sandbox 'cstrike')|Out-Null
Check (-not (New-LabRendererProbe @{gameRoot=$linked} $id).enabled) 'Linked log directory was accepted.'
[IO.Directory]::Delete($junction)
$diagnosticRoot=Join-Path $sandbox 'retained'
New-Item -ItemType Directory -Path (Join-Path $diagnosticRoot 'queue')|Out-Null
New-Item -ItemType Directory -Path (Join-Path $diagnosticRoot ('results\'+$id)) -Force|Out-Null
Write-LabJson (Join-Path $diagnosticRoot ('queue\'+$id+'.json')) @{id=$id}
$retained=@{id=$id;status='uncertain_cleanup';gameStartedUtc=[DateTime]::UtcNow.AddMinutes(-1).ToString('o');completedUtc=[DateTime]::UtcNow.AddSeconds(1).ToString('o');rendererEvidence=(New-LabRendererUnknown $id 'client_output_not_proven' $true)}
Write-LabJson (Join-Path $diagnosticRoot ('results\'+$id+'\result.json')) $retained
$longEnd=$echoFrame.Replace("GL Extensions: GL_test`n",('GL Extensions: '+('GL_test '*200)))
[IO.File]::WriteAllText($path,("PRIVATE_STARTUP_SENTINEL`n"+$longEnd))
$originalLogHash=(Get-FileHash -LiteralPath $path).Hash
$script:diagnosticBusy=$false
function Get-Process([string]$Name){if($script:diagnosticBusy){return [pscustomobject]@{Id=999}}}
$diagnostic=Get-LabRetainedRendererDiagnostics $diagnosticRoot $policy
Check ($diagnostic.status -ceq 'observed' -and $diagnostic.jobId -ceq $id -and -not $diagnostic.rendererAccepted) 'Retained diagnostic identity differs.'
Check ($diagnostic.files[0].read -and $diagnostic.files[0].markerContextsBase64.Count -eq 2 -and $diagnostic.files[0].glLinesBase64.Count -eq 3) 'Retained marker metadata missing.'
Check ($diagnostic.files[0].markerLayout[1].Contains('prefix=extensions')) 'Long extension/marker boundary was lost.'
$contexts=@($diagnostic.files[0].markerContextsBase64|ForEach-Object {[Text.Encoding]::ASCII.GetString([Convert]::FromBase64String($_))})
Check (-not ($contexts -join ' ').Contains('PRIVATE_STARTUP_SENTINEL')) 'Unrelated startup data escaped.'
Check ((Get-FileHash -LiteralPath $path).Hash -ceq $originalLogHash) 'Diagnostic reader changed log bytes.'
$wire=@{worker=@{rendererDiagnostics=$diagnostic}}|ConvertTo-Json -Depth 5
$roundtrip=$wire|ConvertFrom-Json
Check ($roundtrip.worker.rendererDiagnostics.files[0].markerContextsBase64[0] -ceq $diagnostic.files[0].markerContextsBase64[0] -and [Text.Encoding]::UTF8.GetByteCount($wire) -lt 65536) 'Status protocol lost or overflowed diagnostics.'
$script:diagnosticBusy=$true
Check ((Get-LabRetainedRendererDiagnostics $diagnosticRoot $policy).status -ceq 'client_busy') 'Busy diagnostic read was allowed.'
$script:diagnosticBusy=$false
$retained.gameStartedUtc=(Get-Item -LiteralPath $path).CreationTimeUtc.AddSeconds(1).ToString('o')
$retained.completedUtc=[DateTime]::UtcNow.AddSeconds(2).ToString('o')
Write-LabJson (Join-Path $diagnosticRoot ('results\'+$id+'\result.json')) $retained
Check (-not (Get-LabRetainedRendererDiagnostics $diagnosticRoot $policy).files[0].read) 'Unrelated earlier log was read.'
$retained.gameStartedUtc=[DateTime]::UtcNow.AddMinutes(-1).ToString('o')
Write-LabJson (Join-Path $diagnosticRoot ('results\'+$id+'\result.json')) $retained
$other=[guid]::NewGuid().ToString('D')
New-Item -ItemType Directory -Path (Join-Path $diagnosticRoot ('results\'+$other))|Out-Null
Write-LabJson (Join-Path $diagnosticRoot ('queue\'+$other+'.json')) @{id=$other}
$duplicate=Read-LabJson (Join-Path $diagnosticRoot ('results\'+$id+'\result.json'))
$duplicate.id=$other;$duplicate.rendererEvidence.jobId=$other
Write-LabJson (Join-Path $diagnosticRoot ('results\'+$other+'\result.json')) $duplicate
Check ((Get-LabRetainedRendererDiagnostics $diagnosticRoot $policy).status -ceq 'ambiguous') 'Ambiguous retained requests were read.'
[IO.File]::Delete((Join-Path $diagnosticRoot ('queue\'+$other+'.json')))
[IO.File]::WriteAllText($path,('x'*(4MB+1)))
Check (-not (Get-LabRetainedRendererDiagnostics $diagnosticRoot $policy).files[0].read) 'Oversized diagnostic log was read.'
$linkedRoot=Join-Path $sandbox 'linked-diagnostic'
New-Item -ItemType Directory -Path $linkedRoot|Out-Null
$queueLink=Join-Path $linkedRoot 'queue'
New-Item -ItemType Junction -Path $queueLink -Target (Join-Path $diagnosticRoot 'queue')|Out-Null
Check ((Get-LabRetainedRendererDiagnostics $linkedRoot $policy).status -ceq 'unavailable') 'Linked diagnostic queue was read.'
[IO.Directory]::Delete($queueLink)
$retained.rendererEvidence.probeEnabled=$false
Write-LabJson (Join-Path $diagnosticRoot ('results\'+$id+'\result.json')) $retained
Check ((Get-LabRetainedRendererDiagnostics $diagnosticRoot $policy).status -ceq 'not_required') 'Unrelated retained result was read.'
[IO.File]::Delete($path)
Write-LabJson (Join-Path $Workspace ('checks\renderer-ps'+$PSVersionTable.PSVersion.Major+'.json')) @{passed=$true;checks=$checks;targetNetworkUsed=$false;runtime=$PSVersionTable.PSVersion.ToString()}
'Renderer policy checks passed: '+$checks
