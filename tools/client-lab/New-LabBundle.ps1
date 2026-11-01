#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ProfilePath,
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [string]$GunsBaselinePath
)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'Lab.Tooling.psm1') -Force
$profile = Read-LabProfile $ProfilePath
$output = Assert-LabOutputPath $OutputDirectory
if (Test-Path -LiteralPath $output) { throw 'Never overwrite a generated bundle; use a fresh directory.' }
$tokens = @{SOURCE_IPV4=$profile.sourceAddress;TARGET_IPV4=$profile.targetAddress;TARGET_NAME=$profile.targetName;FIXTURE_NAME=$profile.fixtureName}
$guns = @{}
if ($GunsBaselinePath) {
    $item=Get-Item -LiteralPath $GunsBaselinePath
    if($item.Length -gt 65536 -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Invalid weapons baseline file.'}
    $baseline = Get-Content -LiteralPath $GunsBaselinePath -Raw | ConvertFrom-Json
    if ($baseline.schema -ne 1 -or $baseline.imagesReviewed -isnot [bool] -or -not $baseline.imagesReviewed -or @($baseline.profiles).Count -ne 8) { throw 'Eight reviewed weapons baselines required.' }
    foreach ($entry in $baseline.profiles) {
        if ($entry.key -isnot [string] -or $entry.sha256 -isnot [string] -or
            $entry.key -cnotmatch '\A(primary-default|pistol-default|primary-selected|pistol-selected)-(ru|en)\z' -or
            $entry.sha256 -cnotmatch '\A[0-9A-F]{64}\z' -or $guns.ContainsKey($entry.key)) { throw 'Weapons baseline differs.' }
        $guns[$entry.key] = $entry.sha256
    }
    if(@($guns.Values|Select-Object -Unique).Count -ne 8){throw 'Weapons baseline identities must be distinct.'}
}
$orderedGuns=[ordered]@{}
if($guns.Count){foreach($language in @('ru','en')){foreach($name in @('primary-default','pistol-default','primary-selected','pistol-selected')){
    $key=$name+'-'+$language;$orderedGuns[$key]=$guns[$key]
}}}
$tokens.GUNS_BASELINES = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes(($orderedGuns | ConvertTo-Json -Compress)))
New-Item -ItemType Directory -Path $output -ErrorAction Stop | Out-Null
foreach ($area in @('code','controller','updater')) {
    $destination = if ($area -eq 'controller') { Join-Path $output 'kit' } else { Join-Path $output ('kit/' + $area) }
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    foreach ($source in @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot ('templates/' + $area)) -File)) {
        if ($source.Extension -cne '.in' -or ($source.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Unexpected source template.' }
        $name = $source.Name.Substring(0, $source.Name.Length - 3)
        $text = Expand-LabTemplate ([IO.File]::ReadAllText($source.FullName)) $tokens
        [IO.File]::WriteAllText((Join-Path $destination $name), $text, (New-Object Text.UTF8Encoding($false)))
    }
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'native/Rules.Input.cs') -Destination (Join-Path $output 'kit/code/Rules.Input.cs')
$files = @(Get-ChildItem -LiteralPath (Join-Path $output 'kit/code') -File | Sort-Object Name | ForEach-Object {
    @{name=$_.Name;sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash;bytes=$_.Length}
})
if ($files.Count -ne 9) { throw 'Exact nine-file runner required.' }
[IO.File]::WriteAllText((Join-Path $output 'code-hashes.json'), (@{files=$files} | ConvertTo-Json -Depth 5))
[IO.File]::WriteAllText((Join-Path $output 'profile.json'), ($profile | ConvertTo-Json -Depth 5))
$policy = @{targetName=$profile.targetName;targetAddress=$profile.targetAddress;sourceAddress=$profile.sourceAddress;port=2222;
    sessionId=$profile.sessionId;leaseMode='manual';expiresUtc=$null;gameRoot=$profile.gameRoot;steamRoot=$profile.steamRoot;
    gameUserSid=$profile.gameUserSid;gameSessionId=$profile.gameSessionId;panelsEnabled=$true;serverAddress=$profile.sourceAddress;
    serverPort=27025;serverName=$profile.fixtureName;serverMap='de_dust2'}
[IO.File]::WriteAllText((Join-Path $output 'policy.example.json'), ($policy | ConvertTo-Json -Depth 5))
# This is local preparation only. No permission, task, account, connection or game is created.
@{schema=1;status='prepared-not-installed';revision=16;directory=$output;runnerFiles=9;networkUsed=$false;runtimeChanged=$false} | ConvertTo-Json
