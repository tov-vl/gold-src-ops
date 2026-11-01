#Requires -Version 5.1
[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$Workspace)
$ErrorActionPreference='Stop'
. (Join-Path $Workspace 'kit/code/Lab.Common.ps1')
. (Join-Path $Workspace 'kit/code/Interaction.ps1')
Initialize-LabInteractionTypes (Join-Path $Workspace 'kit/code/Rules.Input.cs')
if(-not ('GsoLab.Interaction.RulesFrameGate' -as [type])){throw 'Native bridge type missing.'}
# Exercise the real baseline gate with synthetic BMPs, without any window APIs.
$directory=Join-Path $Workspace ('checks/baseline-pixels-'+$PSVersionTable.PSVersion.Major+'-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $directory|Out-Null
$result=@{images=@()}
foreach($number in 1..4){
    $bytes=New-Object byte[] 6912054
    $bytes[0]=66;$bytes[1]=77
    foreach($pair in @(@(2,6912054),@(10,54),@(14,40),@(18,1920),@(22,1200),@(34,6912000))){[Array]::Copy([BitConverter]::GetBytes([int]$pair[1]),0,$bytes,[int]$pair[0],4)}
    $bytes[26]=1;$bytes[28]=24
    # Menu ink on an otherwise black body, with a distinct color per frame.
    foreach($y in 600..601){foreach($x in 100..599){$bytes[54+($y*1920+$x)*3]=[byte](80+$number)}}
    $path=Join-Path $directory ('image-'+$number+'.bin')
    [IO.File]::WriteAllBytes($path,$bytes)
    $result.images+=@{number=$number;sha256=(Get-FileHash $path).Hash}
}
Assert-LabGunsBaseline $result $directory
if(-not $result.baselineStructureVerified){throw 'Synthetic menu structure was rejected.'}
# Four byte-distinct loading/scene frames cannot qualify as menu bodies.
foreach($number in 1..4){
    $path=Join-Path $directory ('image-'+$number+'.bin')
    $bytes=[IO.File]::ReadAllBytes($path)
    [Array]::Clear($bytes,54,$bytes.Length-54)
    $bytes[54]=[byte]$number
    [IO.File]::WriteAllBytes($path,$bytes)
    $result.images[$number-1].sha256=(Get-FileHash $path).Hash
}
$refused=$false
try{Assert-LabGunsBaseline $result $directory}catch{$refused=$true}
if(-not $refused){throw 'Loading/scene frames passed the weapons baseline gate.'}
Write-Host ('Native bridge compilation and synthetic baseline checks passed: ' + $PSVersionTable.PSVersion + '; no window or input calls.')
