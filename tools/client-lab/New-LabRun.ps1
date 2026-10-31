#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$BundleDirectory,
    [Parameter(Mandatory = $true)][string]$ActivationPath,
    [Parameter(Mandatory = $true)][string]$AddonDirectory,
    [Parameter(Mandatory = $true)][string]$HelperBinaryPath,
    [Parameter(Mandatory = $true)][ValidateSet('ru','en')][string]$Language,
    [Parameter(Mandatory = $true)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'Lab.Tooling.psm1') -Force
$profile = Read-LabProfile (Join-Path $BundleDirectory 'profile.json')
$output = Assert-LabOutputPath $OutputDirectory
if (Test-Path -LiteralPath $output) { throw 'Use a fresh run directory; never reuse an earlier fixture.' }
$activation = Get-Content -LiteralPath $ActivationPath -Raw | ConvertFrom-Json
if ($activation.protocol -ne 2 -or $activation.revision -ne 16 -or $activation.targetName -cne $profile.targetName -or
    $activation.sessionId -cne $profile.sessionId -or $activation.leaseMode -cne 'manual' -or $null -ne $activation.expiresUtc) {
    throw 'Verified installed activation does not match this local profile.'
}
$manifest = Get-Content -LiteralPath (Join-Path $BundleDirectory 'code-hashes.json') -Raw | ConvertFrom-Json
if (@($manifest.files).Count -ne 9 -or @($manifest.files.name | Select-Object -Unique).Count -ne 9) { throw 'Invalid bundle manifest.' }
foreach ($entry in $manifest.files) {
    if ($entry.name -cnotin @('Lab.Common.ps1','Broker.ps1','Worker.ps1','Expire.ps1','Uninstall.ps1','Worker-Quiet.js','Diagnostics.ps1','Interaction.ps1','Rules.Input.cs')) { throw 'Unexpected runner file.' }
    $file = Get-Item -LiteralPath (Join-Path $BundleDirectory ('kit/code/' + $entry.name))
    if (($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -or (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash -cne $entry.sha256) { throw 'Prepared runner was modified.' }
}
$inputs = @(
    @{name='goldsrcops.amxx';path=(Join-Path $AddonDirectory 'goldsrcops.amxx');hash='224D38A33FD6EE19C96D6917C748051E5B4FAB3642D0256F703F416D6F8C513F'},
    @{name='goldsrcops.txt';path=(Join-Path $AddonDirectory 'goldsrcops.txt');hash='C37970E8C3AF420CDA33A6D948A9FE6DE3C12F63A9470A9C14DA3B7045C7FEFB'},
    @{name='fixture_vgui_guard.amxx';path=$HelperBinaryPath;hash='583151EE35B12F005B2D45F82AEF7A1AC38265E5B2F47B2103C89D1D561B56F6'}
)
foreach ($inputFile in $inputs) {
    $item = Get-Item -LiteralPath $inputFile.path
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -or $item.Length -gt 1MB -or
        (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash -cne $inputFile.hash) { throw 'Pinned local fixture input differs.' }
}
$id = [guid]::NewGuid().ToString('D')
$container = 'gso-client-lab-' + ([guid]$id).ToString('N')
$tokens = @{SOURCE_IPV4=$profile.sourceAddress;TARGET_IPV4=$profile.targetAddress;TARGET_NAME=$profile.targetName;FIXTURE_NAME=$profile.fixtureName;CONTAINER_NAME=$container}
foreach ($directory in @($output,(Join-Path $output 'inputs'),(Join-Path $output 'evidence'))) { New-Item -ItemType Directory -Path $directory | Out-Null }
Copy-Item -LiteralPath (Join-Path $BundleDirectory 'kit/code') -Destination $output -Recurse
foreach ($name in @('Invoke-Panel.ps1','Collect-Autonomous.ps1')) { Copy-Item -LiteralPath (Join-Path $BundleDirectory ('kit/' + $name)) -Destination $output }
Copy-Item -LiteralPath $ActivationPath -Destination (Join-Path $output 'activation.json')
foreach ($inputFile in $inputs) { Copy-Item -LiteralPath $inputFile.path -Destination (Join-Path $output ('inputs/' + $inputFile.name)) }
foreach ($source in @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'templates/fixture') -File)) {
    $name = $source.Name.Substring(0, $source.Name.Length - 3)
    [IO.File]::WriteAllText((Join-Path $output $name), (Expand-LabTemplate ([IO.File]::ReadAllText($source.FullName)) $tokens))
}
$plan = @{schema=1;id=$id;language=$Language;container=$container;network=($container+'-net');
    image='sha256:9bffd1d5a9dbecceaee569911ffba6bd5e28326be7879dd7cbeed90b6d7f4574';assets=$profile.assets}
[IO.File]::WriteAllText((Join-Path $output 'plan.json'), ($plan | ConvertTo-Json -Depth 5))
@{id=$id;language=$Language;directory=$output;status='prepared-not-started';networkUsed=$false;runtimeChanged=$false} | ConvertTo-Json
