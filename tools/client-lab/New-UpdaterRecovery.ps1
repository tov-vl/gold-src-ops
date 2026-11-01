#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ProfilePath,
    [Parameter(Mandatory)][string]$PredecessorState,
    [Parameter(Mandatory)][string]$TrustBaselinePath,
    [Parameter(Mandatory)][string]$OutputDirectory
)
$ErrorActionPreference='Stop'
Import-Module (Join-Path $PSScriptRoot 'Lab.Tooling.psm1') -Force
$profile=Read-LabProfile $ProfilePath
$output=Assert-LabOutputPath $OutputDirectory
if(Test-Path -LiteralPath $output){throw 'Never overwrite a recovery package.'}
foreach($path in @($PredecessorState,$TrustBaselinePath)){
    $item=Get-Item -LiteralPath $path
    if($item.Length -gt 65536 -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Invalid predecessor input.'}
}
$state=Get-Content -LiteralPath $PredecessorState -Raw|ConvertFrom-Json
$trust=Get-Content -LiteralPath $TrustBaselinePath -Raw|ConvertFrom-Json
$names=@('Gateway.Input.ps1','Gateway.ps1','Task-Budget.ps1','Update.Common.ps1','Update.Engine.ps1','Updater.ps1')
$runnerNames=@('Broker.ps1','Diagnostics.ps1','Expire.ps1','Interaction.ps1','Lab.Common.ps1','Rules.Input.cs','Uninstall.ps1','Worker-Quiet.js','Worker.ps1')
if($state.revision -ne 16 -or ($state.sequence -isnot [int] -and $state.sequence -isnot [long]) -or $state.sequence -lt 1 -or $state.sequence -gt 10000 -or
    $state.enabled -isnot [bool] -or -not $state.enabled -or $state.blocked -isnot [bool] -or $state.blocked -or
    (@($state.codeHashes.PSObject.Properties.Name|Sort-Object)-join ',') -cne ($runnerNames -join ',') -or
    $trust.targetName -cne $profile.targetName -or $trust.sessionId -cne $profile.sessionId -or
    (@($trust.trustedFiles.name|Sort-Object)-join ',') -cne ($names -join ',') -or
    $trust.engineAfterSha256 -cnotmatch '\A[0-9A-F]{64}\z' -or $trust.publicKeySha256 -cnotmatch '\A[0-9A-F]{64}\z'){throw 'Reviewed trust predecessor required.'}
foreach($entry in $trust.trustedFiles){if($entry.sha256 -cnotmatch '\A[0-9A-F]{64}\z'){throw 'Invalid trust hash.'}}
foreach($name in $runnerNames){if($state.codeHashes.$name -cnotmatch '\A[0-9A-F]{64}\z'){throw 'Invalid runner hash.'}}
$expected=@{schema=1;id=[guid]::NewGuid().ToString('D');targetName=$profile.targetName;sessionId=$profile.sessionId;
    sequence=[int]$state.sequence;revision=[int]$state.revision;gameSessionId=$profile.gameSessionId;
    sourceAddress=$profile.sourceAddress;targetAddress=$profile.targetAddress;codeHashes=$state.codeHashes;
    publicKeySha256=$trust.publicKeySha256;trustedFiles=$trust.trustedFiles;engineBeforeSha256=$trust.engineAfterSha256}
foreach($entry in $expected.trustedFiles){if($entry.name -ceq 'Update.Engine.ps1'){$entry.sha256=$expected.engineBeforeSha256}}
$expected.gatewayBeforeSha256=@($trust.trustedFiles|Where-Object name -CEQ 'Gateway.ps1')[0].sha256
if($trust.PSObject.Properties['gatewayAfterSha256']){
    if($trust.gatewayAfterSha256 -cnotmatch '\A[0-9A-F]{64}\z'){throw 'Invalid adopted gateway hash.'}
    $expected.gatewayBeforeSha256=$trust.gatewayAfterSha256
    foreach($entry in $expected.trustedFiles){if($entry.name -ceq 'Gateway.ps1'){$entry.sha256=$expected.gatewayBeforeSha256}}
}
$templates=@(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'templates/recovery') -File)
if((@($templates.Name|Sort-Object)-join ',') -cne 'Apply-Migration.ps1.in,Migration.Core.ps1.in,Start-Update.cmd.in,Start-Update.ps1.in,Verify-Task.ps1.in' -or
    @($templates|Where-Object {$_.Attributes -band [IO.FileAttributes]::ReparsePoint}).Count){throw 'Exact local recovery template set required.'}
New-Item -ItemType Directory -Path $output|Out-Null
foreach($source in $templates){
    $name=$source.Name.Substring(0,$source.Name.Length-3)
    [IO.File]::WriteAllText((Join-Path $output $name),[IO.File]::ReadAllText($source.FullName),[Text.UTF8Encoding]::new($false))
}
foreach($name in @('Update.Engine.ps1','Gateway.ps1')){
    [IO.File]::WriteAllText((Join-Path $output $name),[IO.File]::ReadAllText((Join-Path $PSScriptRoot ('templates/updater/'+$name+'.in'))),[Text.UTF8Encoding]::new($false))
}
$expected.engineAfterSha256=(Get-FileHash -LiteralPath (Join-Path $output 'Update.Engine.ps1')).Hash
$expected.gatewayAfterSha256=(Get-FileHash -LiteralPath (Join-Path $output 'Gateway.ps1')).Hash
[IO.File]::WriteAllText((Join-Path $output 'expected.json'),($expected|ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false))
$files=@(Get-ChildItem -LiteralPath $output -File|Sort-Object Name|ForEach-Object {@{name=$_.Name;sha256=(Get-FileHash -LiteralPath $_.FullName).Hash}})
[IO.File]::WriteAllText((Join-Path $output 'package-hashes.json'),(@{schema=1;files=$files}|ConvertTo-Json -Depth 5),[Text.UTF8Encoding]::new($false))
@{status='prepared-not-installed';id=$expected.id;files=$files.Count;networkUsed=$false;newPermissions=$false;gameLaunched=$false}|ConvertTo-Json
