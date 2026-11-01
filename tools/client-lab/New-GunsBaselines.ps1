#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$BundleDirectory,
    [Parameter(Mandatory)][string]$RuResultDirectory,
    [Parameter(Mandatory)][string]$EnResultDirectory,
    [Parameter(Mandatory)][string]$OutputPath,
    [switch]$ImagesReviewed
)
$ErrorActionPreference='Stop'
Import-Module (Join-Path $PSScriptRoot 'Lab.Tooling.psm1') -Force
if(-not $ImagesReviewed){throw 'Review all eight native menu frames first.'}
$output=Assert-LabOutputPath $OutputPath
if(Test-Path -LiteralPath $output){throw 'Never overwrite reviewed baselines.'}
. (Join-Path $BundleDirectory 'kit/code/Lab.Common.ps1')
. (Join-Path $BundleDirectory 'kit/code/Interaction.ps1')
Initialize-LabInteractionTypes (Join-Path $BundleDirectory 'kit/code/Rules.Input.cs')
$labels=@('primary-default','pistol-default','primary-selected','pistol-selected')
$profiles=@();$cases=@()
foreach($language in @('ru','en')){
    $directory=if($language -ceq 'ru'){$RuResultDirectory}else{$EnResultDirectory}
    $null=Assert-LabOutputPath $directory
    $result=Read-LabJson (Join-Path $directory 'result.json')
    if($result.action -cne 'capture-guns' -or $result.language -cne $language -or $result.status -cne 'captured_unreviewed' -or $result.failureCode -or
        $result.cleanupVerified -isnot [bool] -or -not $result.cleanupVerified -or
        $result.configCleanupVerified -isnot [bool] -or -not $result.configCleanupVerified -or
        ($result.labels -join ',') -cne ($labels -join ',') -or @($result.images).Count -ne 4){throw 'Complete weapons capture required.'}
    if($result.readiness.status -cne 'ready' -or $result.readiness.setupAcknowledged -isnot [bool] -or -not $result.readiness.setupAcknowledged -or
        $result.baselineStructureVerified -isnot [bool] -or -not $result.baselineStructureVerified -or $result.readiness.mode -cne 'native-fixed-v1'){throw 'Acknowledged weapons baseline required.'}
    $menus=@{}
    for($i=1;$i -le 4;$i++){
        $frame=$result.images[$i-1];$path=Join-Path $directory ('image-'+$i+'.bmp')
        $file=Get-Item -LiteralPath $path
        if($frame.number -ne $i -or $frame.format -cne 'bmp' -or $file.Length -lt 54 -or $file.Length -gt 16MB -or
            $file.Length -ne $frame.bytes -or ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -or
            (Get-FileHash -LiteralPath $path).Hash -cne $frame.sha256){throw 'Native baseline integrity differs.'}
        $bytes=[IO.File]::ReadAllBytes($path)
        $measurement=[GsoLab.Interaction.GunsFrameGate]::Analyze($bytes,$null)
        if(-not [GsoLab.Interaction.GunsFrameGate]::Analyze($bytes,$measurement.BodySha256).PanelMatches){throw 'Fixture backdrop or menu ink is missing.'}
        $menus[$labels[$i-1]]=$measurement.BodySha256
        $profiles+=@{key=$labels[$i-1]+'-'+$language;sha256=$measurement.BodySha256;frameSha256=$frame.sha256}
    }
    $canonical=($labels|ForEach-Object {$_+':'+$menus[$_]}) -join "`n"
    $cases+=@{panel='guns';language=$language;menuBaselines=$menus;bodySha256=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($canonical)))}
}
if(@($profiles.sha256|Select-Object -Unique).Count -ne 8){throw 'Menu/language profiles must be distinct.'}
$value=@{schema=1;imagesReviewed=$true;profiles=$profiles;baselineProfiles=$cases}
$stream=[IO.File]::Open($output,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
try{$bytes=[Text.Encoding]::UTF8.GetBytes(($value|ConvertTo-Json -Depth 8));$stream.Write($bytes,0,$bytes.Length);$stream.Flush($true)}finally{$stream.Dispose()}
@{status='reviewed-not-installed';profiles=8;nativeInputSent=$false;networkUsed=$false}|ConvertTo-Json
