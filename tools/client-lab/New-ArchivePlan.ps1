#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$InventoryPath,
    [Parameter(Mandatory = $true)][string]$ResultDirectory,
    [Parameter(Mandatory = $true)][string[]]$Id,
    [Parameter(Mandatory = $true)][string]$OutputPath
)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'Lab.Tooling.psm1') -Force
$output = Assert-LabOutputPath $OutputPath
if ($Id.Count -lt 1 -or $Id.Count -gt 8 -or @($Id | Select-Object -Unique).Count -ne $Id.Count) { throw 'Select one to eight distinct terminal jobs.' }
$item = Get-Item -LiteralPath $InventoryPath
if ($item.Length -gt 65536 -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Invalid inventory.' }
$inventory = [IO.File]::ReadAllText($item.FullName) | ConvertFrom-Json
if (@($inventory.terminalArchiveCandidates).Count -gt 8) { throw 'Inventory exceeds queue capacity.' }
$plan = @()
foreach ($jobId in $Id) {
    if ($jobId -cnotmatch '\A[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\z' -or [guid]$jobId -eq [guid]::Empty) { throw 'Invalid archive identity.' }
    $candidate = @($inventory.terminalArchiveCandidates | Where-Object { $_.id -ceq $jobId })
    if ($candidate.Count -ne 1 -or $candidate[0].sha256 -cnotmatch '\A[0-9A-F]{64}\z' -or
        (@($candidate[0].PSObject.Properties.Name | Sort-Object) -join ',') -cne 'id,sha256') { throw 'Terminal request hash is missing or ambiguous.' }
    $path = Join-Path $ResultDirectory ($jobId + '/result.json')
    $file = Get-Item -LiteralPath $path
    if ($file.Length -gt 65536 -or ($file.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Invalid result.' }
    $result = [IO.File]::ReadAllText($file.FullName) | ConvertFrom-Json
    if ($result.id -cne $jobId -or $result.cleanupVerified -isnot [bool] -or -not $result.cleanupVerified -or
        $result.configCleanupVerified -isnot [bool] -or -not $result.configCleanupVerified -or
        $result.status -cnotin @('captured_unreviewed','interaction_captured_unreviewed','autonomous_captured_unreviewed','uncertain_timeout','uncertain_launch','uncertain_capture','uncertain_error','rejected_preflight')) {
        throw 'Active, uncertain-cleanup or mismatched result cannot be archived.'
    }
    if ($result.completedUtc -is [DateTime]) { $completed = $result.completedUtc.ToUniversalTime() }
    elseif ($result.completedUtc -is [string] -and $result.completedUtc -cmatch 'Z$') {
        $completed = [DateTime]::Parse($result.completedUtc, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::RoundtripKind).ToUniversalTime()
    }
    else { throw 'Terminal result must have a valid UTC timestamp.' }
    if ($completed -gt [DateTime]::UtcNow.AddSeconds(10)) { throw 'Future terminal result.' }
    $plan += @{id=$jobId;sha256=$candidate[0].sha256}
}
$bytes = [Text.Encoding]::UTF8.GetBytes((ConvertTo-Json -InputObject $plan -Depth 4))
$stream = [IO.File]::Open($output, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
try { $stream.Write($bytes, 0, $bytes.Length); $stream.Flush($true) } finally { $stream.Dispose() }
# This plan moves no data. The privileged signed updater revalidates hashes, idle state and cleanup.
@{status='prepared-not-dispatched';jobs=$plan.Count;resultsDeleted=$false;networkUsed=$false} | ConvertTo-Json
