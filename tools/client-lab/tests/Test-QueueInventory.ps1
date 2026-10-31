#requires -Version 5.1
param([Parameter(Mandatory=$true)][string]$Workspace)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
. (Join-Path $Workspace 'kit\code\Lab.Common.ps1')
. (Join-Path $Workspace 'kit\code\Interaction.ps1')
$root=Join-Path $Workspace ('checks\inventory-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path (Join-Path $root 'queue'),(Join-Path $root 'results')|Out-Null
function Add-Case([string]$Status,[bool]$Clean,[bool]$Complete){
    $id=[guid]::NewGuid().ToString('D')
    Write-LabJson (Join-Path $root ('queue\'+$id+'.json')) @{id=$id;action='verify-rules';language='ru';createdUtc=[DateTime]::UtcNow.ToString('o')}
    $directory=Join-Path $root ('results\'+$id)
    New-Item -ItemType Directory -Path $directory|Out-Null
    $record=@{id=$id;status=$Status;cleanupVerified=$Clean;configCleanupVerified=$Clean}
    if($Complete){$record.completedUtc=[DateTime]::UtcNow.ToString('o')}
    Write-LabJson (Join-Path $directory 'result.json') $record
    return $id
}
$eligible=Add-Case 'uncertain_error' $true $true
Add-Case 'autonomous_captured_unreviewed' $true $true|Out-Null
Add-Case 'uncertain_error' $false $true|Out-Null
Add-Case 'launch_requested' $false $false|Out-Null
$inventory=@(Get-LabTerminalArchiveCandidates $root)
if($inventory.Count -ne 1 -or $inventory[0].id -cne $eligible -or $inventory[0].sha256 -cne (Get-FileHash -LiteralPath (Join-Path $root ('queue\'+$eligible+'.json')) -Algorithm SHA256).Hash){throw 'Inventory selected an active or unclean request.'}
if((@($inventory[0].Keys|Sort-Object)-join ',') -cne 'id,sha256'){throw 'Inventory exposed raw request content.'}
$resultPath=Join-Path $root ('results\'+$eligible+'\result.json')
$saved=Read-LabJson $resultPath
$saved.id=[guid]::NewGuid().ToString('D');Write-LabJson $resultPath $saved
$rejected=$false;try{Get-LabTerminalArchiveCandidates $root|Out-Null}catch{$rejected=$true};if(-not $rejected){throw 'Mismatched result identity accepted.'}
$saved.id=$eligible;$saved.completedUtc=[DateTime]::UtcNow.AddMinutes(5).ToString('o');Write-LabJson $resultPath $saved
$rejected=$false;try{Get-LabTerminalArchiveCandidates $root|Out-Null}catch{$rejected=$true};if(-not $rejected){throw 'Future terminal record accepted.'}
$saved.completedUtc=[DateTime]::UtcNow.ToString('o');Write-LabJson $resultPath $saved
foreach($number in 1..5){Add-Case 'uncertain_error' $true $true|Out-Null}
$rejected=$false;try{Get-LabTerminalArchiveCandidates $root|Out-Null}catch{$rejected=$true};if(-not $rejected){throw 'Inventory exceeded its eight-record bound.'}
@{passed=$true;checks=5;runtime=$PSVersionTable.PSVersion.ToString();sandbox=$root;remoteMutations=$false}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $Workspace ('checks\inventory-ps'+$PSVersionTable.PSVersion.Major+'.json')) -Encoding utf8
'Queue inventory passed: 5 checks. No request was moved or deleted.'
