Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-LabShape($Value, [string[]]$Names) {
    if ($null -eq $Value -or (@($Value.PSObject.Properties.Name | Sort-Object) -join ',') -cne (@($Names | Sort-Object) -join ',')) {
        throw 'Unexpected local profile shape.'
    }
}

function Assert-LabPrivateIPv4([string]$Value) {
    $address = $null
    if ($Value -cnotmatch '\A(?:0|[1-9][0-9]{0,2})(?:\.(?:0|[1-9][0-9]{0,2})){3}\z' -or
        -not [Net.IPAddress]::TryParse($Value, [ref]$address) -or $address.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork) {
        throw 'A canonical private IPv4 address is required.'
    }
    $bytes = $address.GetAddressBytes()
    if (-not ($bytes[0] -eq 10 -or ($bytes[0] -eq 172 -and $bytes[1] -ge 16 -and $bytes[1] -le 31) -or
        ($bytes[0] -eq 192 -and $bytes[1] -eq 168))) { throw 'Only an explicitly selected private LAN address is supported.' }
    if ($bytes[3] -in @(0,255)) { throw 'A host address is required.' }
}

function Assert-LabLocalPath([string]$Value) {
    if ($Value -cnotmatch '\A[A-Za-z]:\\[A-Za-z0-9 _().\\-]+\z' -or $Value -match '(?:^|\\)\.\.(?:\\|$)' -or $Value.EndsWith('\')) {
        throw 'A plain absolute local Windows path is required.'
    }
}

function Read-LabProfile([string]$Path) {
    $item = Get-Item -LiteralPath $Path
    if ($item.Length -gt 16384 -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Invalid local profile file.' }
    $profile = [IO.File]::ReadAllText($item.FullName) | ConvertFrom-Json
    Assert-LabShape $profile @('schema','sourceAddress','targetAddress','targetName','fixtureName','sessionId','gameRoot','steamRoot','gameUserSid','gameSessionId','assets')
    if (($profile.schema -isnot [int] -and $profile.schema -isnot [long]) -or $profile.schema -ne 1) { throw 'Unsupported profile schema.' }
    foreach ($name in @('sourceAddress','targetAddress')) {
        if ($profile.$name -isnot [string]) { throw 'Profile address must be text.' }
        Assert-LabPrivateIPv4 $profile.$name
    }
    if ($profile.sourceAddress -ceq $profile.targetAddress) { throw 'Controller and client must be distinct.' }
    foreach ($name in @('targetName','fixtureName')) {
        if ($profile.$name -isnot [string] -or $profile.$name -cnotmatch '\A[A-Za-z][A-Za-z0-9-]{1,62}[A-Za-z0-9]\z') { throw 'Invalid fixed host or fixture name.' }
    }
    if ($profile.targetName -cne $profile.targetName.ToUpperInvariant()) { throw 'Use the uppercase Windows computer name.' }
    if ($profile.sessionId -isnot [string] -or $profile.sessionId -cnotmatch '\A[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\z' -or
        [guid]$profile.sessionId -eq [guid]::Empty) { throw 'Invalid local lease identity.' }
    foreach ($name in @('gameRoot','steamRoot')) { Assert-LabLocalPath $profile.$name }
    if ($profile.gameUserSid -isnot [string] -or $profile.gameUserSid -cnotmatch '\AS-1-5-21-(?:[0-9]{1,10}-){3}[0-9]{1,10}\z' -or
        ($profile.gameSessionId -isnot [int] -and $profile.gameSessionId -isnot [long]) -or $profile.gameSessionId -lt 1 -or $profile.gameSessionId -gt [int]::MaxValue) { throw 'An exact interactive user/session is required.' }
    Assert-LabShape $profile.assets @('cacheDirectory','runtimeDirectory','steamSdkPath')
    foreach ($name in @('cacheDirectory','runtimeDirectory','steamSdkPath')) { Assert-LabLocalPath $profile.assets.$name }
    return $profile
}

function Assert-LabOutputPath([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path)
    $repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..')).TrimEnd('\','/')
    if ($full.StartsWith($repository + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -and
        -not $full.StartsWith((Join-Path $repository 'artifacts') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Generated configuration and evidence must be outside tracked source, or under ignored artifacts.'
    }
    $current = $full
    while ($current) {
        if (Test-Path -LiteralPath $current) {
            if ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Linked output ancestor.' }
        }
        $current = [IO.Path]::GetDirectoryName($current)
    }
    return $full
}

function Expand-LabTemplate([string]$Text, [hashtable]$Tokens) {
    $result = $Text
    foreach ($name in $Tokens.Keys) { $result = $result.Replace('{{' + $name + '}}', [string]$Tokens[$name]) }
    if ($result -match '\{\{[A-Z_]+\}\}') { throw 'Unresolved template token.' }
    return $result
}

Export-ModuleMember -Function Read-LabProfile, Assert-LabOutputPath, Expand-LabTemplate
