#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$CacheDirectory,
    [Parameter(Mandatory=$true)][string]$OutputPath,
    [switch]$MenuTest,
    [switch]$AmmoTest,
    [switch]$Continuity,
    [switch]$ContinuityTest,
    [switch]$SteamObserver,
    [switch]$SteamObserverTest
)
$ErrorActionPreference='Stop'
if(-not $IsWindows){throw 'Use the pinned Windows AMXX toolchain.'}
if(@($MenuTest,$AmmoTest,$Continuity,$ContinuityTest,$SteamObserver,$SteamObserverTest|Where-Object {$_}).Count -gt 1){throw 'Select one fixed fixture variant.'}
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
$source = Join-Path $PSScriptRoot $(if ($MenuTest) { 'tests/fixture-menu-smoke.sma' }
    elseif ($AmmoTest) { 'tests/fixture-ammo-smoke.sma' }
    elseif ($ContinuityTest) { 'tests/fixture_continuity_smoke.sma' }
    elseif ($Continuity) { 'fixture_continuity.sma' }
    elseif ($SteamObserverTest) { 'tests/fixture_steam_observer_smoke.sma' }
    elseif ($SteamObserver) { 'fixture_steam_observer.sma' }
    else { 'fixture_vgui_guard.sma' })
& (Join-Path $scripting 'amxxpc.exe') $source ('-i'+$includes) ('-i'+$reapi) ('-i'+$PSScriptRoot) ('-o'+$output) -d0 -E | Out-Host
if($LASTEXITCODE -ne 0){throw 'Fixture helper compilation failed.'}
$hash=(Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash
$expected=if($Continuity){'8ED394B7E437F4A484A5F48DF180E7B1F20F4D136B79A429D0E5BDF7832D6A84'}
    elseif($SteamObserver){'7B4D6352450FF5854E82D3BE944709EAF3505121601B10F966B813C06A7FE94A'}
    elseif(-not ($MenuTest -or $AmmoTest -or $ContinuityTest -or $SteamObserverTest)){'37EE8A8DD9BA1D6B36A7C172AF8ED4ECC0C2AEEBF84154CF44026D4C3E743612'}
if($expected -and $hash -cne $expected){throw 'Fixture helper differs from the reviewed source pin; do not use the output.'}
@{status='compiled-not-installed';sha256=$hash;runtimeChanged=$false;downloaded=$false}|ConvertTo-Json
