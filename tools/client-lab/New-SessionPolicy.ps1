#requires -Version 7.0
param([Parameter(Mandatory=$true)][string]$ReviewPath,[Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
Import-Module (Join-Path $PSScriptRoot 'Lab.Tooling.psm1') -Force
$output=Assert-LabOutputPath $OutputDirectory
if(Test-Path -LiteralPath $output){throw 'Never overwrite a prepared session package.'}
$review=Get-Item -LiteralPath $ReviewPath
if($review.Length -gt 65536 -or ($review.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Invalid review record.'}
$expected=Get-Content -LiteralPath $ReviewPath -Raw|ConvertFrom-Json
if($expected.schema -ne 1 -or $expected.sequence -lt 1 -or $expected.revision -ne 16 -or
    $expected.gameUserSid -cnotmatch '\AS-1-5-21-(?:[0-9]{1,10}-){3}[0-9]{1,10}\z' -or
    $expected.id -cnotmatch '\A[0-9a-f-]{36}\z' -or
    $expected.policyBeforeSha256 -cnotmatch '\A[0-9A-F]{64}\z' -or
    $expected.publicKeySha256 -cnotmatch '\A[0-9A-F]{64}\z' -or
    @($expected.trustedFiles).Count -ne 6 -or @($expected.codeHashes.PSObject.Properties).Count -ne 9 -or
    @($expected.terminalResults).Count -gt 8){throw 'Reviewed installation boundary differs.'}
New-Item -ItemType Directory -Path $output|Out-Null
foreach($name in @('Apply-Session.ps1','Session.Core.ps1','Start-Session.ps1','Start-Session.cmd')){
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot ('templates/session/'+$name+'.in')) -Destination (Join-Path $output $name)
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'templates/recovery/Verify-Task.ps1.in') -Destination (Join-Path $output 'Verify-Task.ps1')
$expected|ConvertTo-Json -Depth 15|Set-Content -LiteralPath (Join-Path $output 'expected.json') -Encoding utf8NoBOM
$files=@(Get-ChildItem -LiteralPath $output -File|Sort-Object Name|ForEach-Object {@{name=$_.Name;sha256=(Get-FileHash -LiteralPath $_.FullName).Hash;bytes=$_.Length}})
if($files.Count -ne 6){throw 'Exact bootstrap file set required.'}
@{schema=1;files=$files}|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $output 'package-hashes.json') -Encoding utf8NoBOM
foreach($file in Get-ChildItem -LiteralPath $output -Filter '*.ps1'){
    $tokens=$null;$errors=$null
    [void][Management.Automation.Language.Parser]::ParseFile($file.FullName,[ref]$tokens,[ref]$errors)
    if($errors.Count){throw ('Installer parser failure: '+$file.Name)}
}
@{status='prepared-not-installed';id=$expected.id;files=7;gameLaunched=$false;targetChanged=$false;runnerUpdatePending=$true}|ConvertTo-Json
