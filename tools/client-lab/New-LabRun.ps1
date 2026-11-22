#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$BundleDirectory,
    [Parameter(Mandatory = $true)][string]$ActivationPath,
    [Parameter(Mandatory = $true)][string]$AddonDirectory,
    [Parameter(Mandatory = $true)][string]$HelperBinaryPath,
    [Parameter(Mandatory = $true)][ValidateSet('ru','en')][string]$Language,
    [ValidateSet('motd','guns','guns-rejoin','guns-steam-rejoin')][string]$Scenario='motd',
    [string]$ContinuityHelperPath,
    [string]$SteamObserverPath,
    [string]$NVaultPath,
    [string]$NavPath,
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
$steam=$Scenario -ceq 'guns-steam-rejoin'
$rejoin=$steam -or $Scenario -ceq 'guns-rejoin'
if($steam -ne [bool]$SteamObserverPath){throw 'Steam rejoin requires its pinned observer only for the Steam profile.'}
if($rejoin -and ($Language -cne 'en' -or -not $ContinuityHelperPath -or -not $NVaultPath -or -not $NavPath)){
    throw 'Rejoin requires EN and the three pinned local fixture inputs.'
}
if(-not $rejoin -and ($ContinuityHelperPath -or $NVaultPath -or $NavPath)){throw 'Additional fixture inputs require a rejoin profile.'}
$addonPin=if($rejoin){'71AEF4EC88C1F97528B749AB9D0E3111C28FE8DB981E0742B2313F06D9741848'}else{'257F90BA56199D36ED3699D5299B482771D1BD550204946792BDB7276DBCB40D'}
$guardPin=(Get-FileHash -LiteralPath $HelperBinaryPath).Hash
$currentGuard='37EE8A8DD9BA1D6B36A7C172AF8ED4ECC0C2AEEBF84154CF44026D4C3E743612'
$allowedGuards=@($currentGuard)
if(-not $steam){$allowedGuards+='C69BF148B9BC9924C512084DC49983513F1565A068A0AD8EDBEFE1C492FD0B6D'}
if(-not $rejoin){$allowedGuards+='05CCFB65A9ABC9C5B65C429C7FA7F928E1595E138D3885C2F7E08CA46B3F5095'}
if($guardPin -cnotin $allowedGuards){
    throw 'Pinned fixture helper differs.'
}
$inputs = @(
    @{name='goldsrcops.amxx';path=(Join-Path $AddonDirectory 'goldsrcops.amxx');hash=$addonPin},
    @{name='goldsrcops.txt';path=(Join-Path $AddonDirectory 'goldsrcops.txt');hash='71B1EA3A49331E16E1383817849F04D702EE559C1BE11C3E825172F0C5C939B7'},
    @{name='fixture_vgui_guard.amxx';path=$HelperBinaryPath;hash=$guardPin}
)
$continuityPin='8ED394B7E437F4A484A5F48DF180E7B1F20F4D136B79A429D0E5BDF7832D6A84'
if($rejoin){
    $inputs+=@(
        @{name='fixture_continuity.amxx';path=$ContinuityHelperPath;hash=$continuityPin},
        @{name='nvault_amxx_i386.so';path=$NVaultPath;hash='E6A24A81B205410E9B5EB62CA3B1E2BB8F49A0480D5E84B177D7A57C512A0AF6'},
        @{name='de_dust2.nav';path=$NavPath;hash='9A44381363572F05D42FA4F82B293F54E49C93D60A4471D2FE8396ECCEB77753'})
}
$steamObserverPin='7B4D6352450FF5854E82D3BE944709EAF3505121601B10F966B813C06A7FE94A'
if($steam){$inputs+=@{name='fixture_steam_observer.amxx';path=$SteamObserverPath;hash=$steamObserverPin}}
foreach ($inputFile in $inputs) {
    $item = Get-Item -LiteralPath $inputFile.path
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -or $item.Length -gt 1MB -or
        (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash -cne $inputFile.hash) { throw 'Pinned local fixture input differs.' }
}
$id = [guid]::NewGuid().ToString('D')
$container = 'gso-client-lab-' + ([guid]$id).ToString('N')
$tokens = @{SOURCE_IPV4=$profile.sourceAddress;TARGET_IPV4=$profile.targetAddress;TARGET_NAME=$profile.targetName;FIXTURE_NAME=$profile.fixtureName;CONTAINER_NAME=$container;
    GUNS_READINESS=[int]($Scenario -cne 'motd');LAN_REJOIN=[int]$rejoin;PLUGIN_SHA256=$addonPin.ToLowerInvariant();
    GUARD_SHA256=$guardPin.ToLowerInvariant();CONTINUITY_SHA256=$continuityPin.ToLowerInvariant();STEAM_REJOIN=[int]$steam;STEAM_OBSERVER_SHA256=$steamObserverPin.ToLowerInvariant()}
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
if($steam){$plan.profile='steam-rejoin'}
[IO.File]::WriteAllText((Join-Path $output 'plan.json'), ($plan | ConvertTo-Json -Depth 5))
if($rejoin){
    $engine=Join-Path $output 'engine'
    New-Item -ItemType Directory -Path $engine|Out-Null
    Copy-Item -LiteralPath (Join-Path $BundleDirectory 'kit/Invoke-FramedUpdate.ps1') -Destination $engine
    Copy-Item -LiteralPath (Join-Path $BundleDirectory 'kit/updater') -Destination $engine -Recurse
    $pair=@{schema=1;runId=$id;first=[guid]::NewGuid().ToString('D');second=[guid]::NewGuid().ToString('D')}
    [IO.File]::WriteAllText((Join-Path $output 'rejoin-plan.json'),($pair|ConvertTo-Json))
}
@{id=$id;language=$Language;directory=$output;status='prepared-not-started';networkUsed=$false;runtimeChanged=$false} | ConvertTo-Json
