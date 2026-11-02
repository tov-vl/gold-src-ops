#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$CacheDirectory,
    [Parameter(Mandatory=$true)][string]$OutputPath,
    [switch]$MenuTest,
    [switch]$AmmoTest
)
$ErrorActionPreference='Stop'
if(-not $IsWindows){throw 'Use the pinned Windows AMXX toolchain.'}
if($MenuTest -and $AmmoTest){throw 'Select one fixed fixture test.'}
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
$source = Join-Path $PSScriptRoot $(if ($MenuTest) { 'tests/fixture-menu-smoke.sma' } elseif ($AmmoTest) { 'tests/fixture-ammo-smoke.sma' } else { 'fixture_vgui_guard.sma' })
& (Join-Path $scripting 'amxxpc.exe') $source ('-i'+$includes) ('-i'+$reapi) ('-i'+$PSScriptRoot) ('-o'+$output) -d0 -E | Out-Host
if($LASTEXITCODE -ne 0){throw 'Fixture helper compilation failed.'}
$hash=(Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash
if(-not ($MenuTest -or $AmmoTest) -and $hash -cne 'A35470A8499ED36950D699D01FC23354050504DD04A87E32E51CD70EC1C1E2AE'){throw 'Fixture helper differs from the reviewed source pin; do not use the output.'}
@{status='compiled-not-installed';sha256=$hash;runtimeChanged=$false;downloaded=$false}|ConvertTo-Json
