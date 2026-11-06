#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$ResultPath,
    [Parameter(Mandatory=$true)][string]$JobId,
    [Parameter(Mandatory=$true)][string]$OutputPath
)
$ErrorActionPreference='Stop'
Import-Module (Join-Path $PSScriptRoot 'Lab.Tooling.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'Lab.RendererReview.psm1') -Force
$output=Assert-LabOutputPath $OutputPath
$receipt=Get-LabRendererReview $ResultPath $JobId
$bytes=[Text.Encoding]::UTF8.GetBytes(($receipt|ConvertTo-Json -Depth 8))
$stream=[IO.File]::Open($output,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
try{$stream.Write($bytes,0,$bytes.Length)}finally{$stream.Dispose()}
$receipt|ConvertTo-Json -Depth 8
