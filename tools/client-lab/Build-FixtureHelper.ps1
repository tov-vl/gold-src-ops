#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$CacheDirectory,
    [Parameter(Mandatory=$true)][string]$OutputPath
)
$ErrorActionPreference='Stop'
if(-not $IsWindows){throw 'Use the pinned Windows AMXX toolchain.'}
Import-Module (Join-Path $PSScriptRoot 'Lab.Tooling.psm1') -Force
$output=Assert-LabOutputPath $OutputPath
if(Test-Path -LiteralPath $output){throw 'Never overwrite a fixture helper.'}
$scripting=Join-Path $CacheDirectory 'amxx-1.10.0.5481/addons/amxmodx/scripting'
foreach($entry in @(
    @{name='amxxpc.exe';hash='4702ED75E8EB4B4358D39E83423CA39884DA075C33BA758D05CDD6063DA4C1AF'},
    @{name='amxxpc32.dll';hash='B83199F48CF68EBC511232A585A706ABA537FE23383292B88C723F84E0B09FC0'}
)){
    $path=Join-Path $scripting $entry.name
    if((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -cne $entry.hash){throw 'Pinned compiler differs.'}
}
$includes=Join-Path $scripting 'include'
$reapi=Join-Path $CacheDirectory 'reapi-5.24.0.300/addons/amxmodx/scripting/include'
& (Join-Path $scripting 'amxxpc.exe') (Join-Path $PSScriptRoot 'fixture_vgui_guard.sma') ('-i'+$includes) ('-i'+$reapi) ('-o'+$output) -d0 -E | Out-Host
if($LASTEXITCODE -ne 0){throw 'Fixture helper compilation failed.'}
$hash=(Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash
if($hash -cne 'DF4FEC086371FC3DFC0EF68D6D326DA756C35BEEF5C31F8BA6414CB8CC8B5B68'){throw 'Fixture helper differs from the reviewed source pin; do not use the output.'}
@{status='compiled-not-installed';sha256=$hash;runtimeChanged=$false;downloaded=$false}|ConvertTo-Json
