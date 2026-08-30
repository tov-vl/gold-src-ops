#Requires -Version 7.0

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Tag,

    [string]$GitHubOutputPath,

    [ValidateSet('stable', 'release-candidate')]
    [string]$ExpectedChannel,

    [string]$ExpectedBaseVersion
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$tagPattern = '\Av(?<major>0|[1-9][0-9]*)\.(?<minor>0|[1-9][0-9]*)\.(?<patch>0|[1-9][0-9]*)(?<candidate>-rc\.(?<candidateNumber>[1-9][0-9]*))?\z'
$match = [regex]::Match(
    $Tag,
    $tagPattern,
    [Text.RegularExpressions.RegexOptions]::CultureInvariant)

if (-not $match.Success) {
    throw "Image publication tag must match v<major>.<minor>.<patch> or v<major>.<minor>.<patch>-rc.<positive integer> without leading zeroes."
}

$baseVersion = '{0}.{1}.{2}' -f
    $match.Groups['major'].Value,
    $match.Groups['minor'].Value,
    $match.Groups['patch'].Value
$channel = if ($match.Groups['candidate'].Success) {
    'release-candidate'
}
else {
    'stable'
}
$version = $Tag.Substring(1)

if (-not [string]::IsNullOrWhiteSpace($ExpectedChannel) -and
    $channel -cne $ExpectedChannel) {
    throw "Image publication tag '$Tag' belongs to channel '$channel', expected '$ExpectedChannel'."
}

if (-not [string]::IsNullOrWhiteSpace($ExpectedBaseVersion) -and
    $baseVersion -cne $ExpectedBaseVersion) {
    throw "Image publication tag '$Tag' has base version '$baseVersion', expected '$ExpectedBaseVersion'."
}

$result = [pscustomobject]@{
    Tag = $Tag
    Version = $version
    BaseVersion = $baseVersion
    Channel = $channel
}

if (-not [string]::IsNullOrWhiteSpace($GitHubOutputPath)) {
    $outputLines = @(
        "tag=$Tag",
        "version=$version",
        "base_version=$baseVersion",
        "channel=$channel")

    foreach ($line in $outputLines) {
        [IO.File]::AppendAllText(
            $GitHubOutputPath,
            $line + [Environment]::NewLine,
            [Text.UTF8Encoding]::new($false))
    }
}

$result
