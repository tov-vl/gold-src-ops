#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$RunDirectory,
    [Parameter(Mandatory)][string]$PythonPath,
    [string]$RetainedRendererReceiptsPath,
    [switch]$Execute,
    [switch]$VerifyEvidence
)
$ErrorActionPreference='Stop'
Import-Module (Join-Path $PSScriptRoot 'Lab.Tooling.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'Lab.CampaignRuntime.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'Lab.LanRejoin.psm1') -Force
$root=Assert-LabOutputPath $RunDirectory
$python=Get-Item -LiteralPath $PythonPath
if($python.PSIsContainer -or ($python.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Local Python executable required.'}
if($Execute -and $VerifyEvidence){throw 'Choose execution or offline evidence verification.'}
$verification=Join-Path $PSScriptRoot 'verify_lan_rejoin.py'
if($VerifyEvidence){
    & $python.FullName -B $verification $root
    if($LASTEXITCODE -ne 0){throw 'LAN rejoin evidence failed.'}
    return
}
$plan=Get-Content -LiteralPath (Join-Path $root 'plan.json') -Raw|ConvertFrom-Json
$pair=Get-Content -LiteralPath (Join-Path $root 'rejoin-plan.json') -Raw|ConvertFrom-Json
Assert-LabShape $pair @('schema','runId','first','second')
if($pair.schema -ne 1 -or $pair.runId -cne $plan.id -or $plan.language -cne 'en' -or $pair.first -ceq $pair.second){
    throw 'Unexpected LAN rejoin plan.'
}
foreach($id in @($pair.first,$pair.second)){
    if($id -isnot [string] -or $id -cnotmatch '\A[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}\z' -or [guid]$id -eq [guid]::Empty){
        throw 'Invalid LAN rejoin job identity.'
    }
}
if(-not $Execute){
    @{status='prepared-not-started';runtimeChanged=$false;first=$pair.first;second=$pair.second}|ConvertTo-Json
    return
}
$campaign=@{runtime=@{python=$python.FullName;pythonSha256=(Get-FileHash -LiteralPath $python.FullName).Hash;pwsh=(Join-Path $PSHOME 'pwsh.exe')}}
$first=@{root=$root;run=$root;campaign=$campaign;case=@{jobId=$pair.first;panel='guns';lanRejoin=$true}}
$second=@{root=$root;run=$root;campaign=$campaign;case=@{jobId=$pair.second;panel='guns';lanRejoin=$true}}
if($RetainedRendererReceiptsPath){
    $file=Get-Item -LiteralPath $RetainedRendererReceiptsPath
    if($file.PSIsContainer -or $file.Length -gt 16384 -or ($file.Attributes -band [IO.FileAttributes]::ReparsePoint)){
        throw 'Invalid retained renderer receipt file.'
    }
    $receipts=Get-Content -LiteralPath $file.FullName -Raw|ConvertFrom-Json -AsHashtable -NoEnumerate
    if($receipts -isnot [array] -or $receipts.Count -notin (1..6)){throw 'Expected a bounded list of retained renderer receipts.'}
    foreach($receipt in $receipts){if($receipt -isnot [System.Collections.IDictionary]){throw 'Invalid retained renderer receipt.'}}
    $first.case.retainedRendererReceipts=$receipts
}
$port={
    param($operation,$context)
    if($operation -ceq 'window'){
        $file=Get-Item -LiteralPath (Join-Path $context.run 'evidence/runtime.json')
        if($file.Length -gt 65536 -or ($file.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Invalid local fixture status.'}
        return Get-Content -LiteralPath $file.FullName -Raw|ConvertFrom-Json -AsHashtable
    }
    Invoke-LabCampaignRuntime $operation $context
}
Invoke-LabLanRejoin $first $second $port
foreach($id in @($pair.first,$pair.second)){
    & (Join-Path $root 'Collect-Autonomous.ps1') -JobId $id|Out-Host
    $resultPath=Join-Path $root "controller-results/$id/result.json"
    $result=Get-Content -LiteralPath $resultPath -Raw|ConvertFrom-Json
    foreach($item in @($result.interaction.checkpoints|Where-Object {$_})){
        & (Join-Path $root 'Invoke-Panel.ps1') -Operation checkpoint -JobId $id -Checkpoint $item.name|Out-Null
    }
    & (Join-Path $PSScriptRoot 'Test-LabRenderer.ps1') -ResultPath $resultPath -JobId $id -OutputPath (Join-Path $root "RENDERER-$id.json")|Out-Null
}
$null=Invoke-LabCampaignRuntime idle $second
& $python.FullName -B $verification $root --write
if($LASTEXITCODE -ne 0){throw 'LAN rejoin evidence failed.'}
