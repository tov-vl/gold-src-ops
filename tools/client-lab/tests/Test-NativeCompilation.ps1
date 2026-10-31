#Requires -Version 5.1
[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$Workspace)
$ErrorActionPreference='Stop'
. (Join-Path $Workspace 'kit/code/Lab.Common.ps1')
. (Join-Path $Workspace 'kit/code/Interaction.ps1')
Initialize-LabInteractionTypes (Join-Path $Workspace 'kit/code/Rules.Input.cs')
if(-not ('GsoLab.Interaction.RulesFrameGate' -as [type])){throw 'Native bridge type missing.'}
Write-Host ('Native bridge compilation passed: ' + $PSVersionTable.PSVersion + '; no native calls.')
