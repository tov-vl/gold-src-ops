#Requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory = $true)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'Lab.Tooling.psm1') -Force
$output = Assert-LabOutputPath $OutputDirectory
& (Join-Path $PSScriptRoot 'New-LabBundle.ps1') -ProfilePath (Join-Path $PSScriptRoot 'examples/profile.example.json') -OutputDirectory $output | Out-Host
New-Item -ItemType Directory -Path (Join-Path $output 'checks') | Out-Null
foreach ($file in @(Get-ChildItem -LiteralPath (Join-Path $output 'kit') -Recurse -Filter '*.ps1')) {
    $tokens = $null; $errors = $null
    [void][Management.Automation.Language.Parser]::ParseFile($file.FullName, [ref]$tokens, [ref]$errors)
    if ($errors.Count) { throw ('Generated PowerShell parser failure: ' + $file.Name) }
}
& (Join-Path $PSScriptRoot 'tests/Test-ProfileAndArchive.ps1') -Workspace $output -ToolRoot $PSScriptRoot
$tests = @('Test-Protocol.ps1','Test-Autonomous.ps1','Test-Workflow.ps1','Test-WorkerCleanup.ps1','Test-ShellLaunch.ps1','Test-FocusProjection.ps1','Test-QueueInventory.ps1','Test-Updater.ps1')
foreach ($name in $tests) { & (Join-Path $PSScriptRoot ('tests/' + $name)) -Workspace $output }
if (-not $IsWindows) { throw 'Windows PowerShell and native policy checks require a Windows verification host.' }
$legacy = Join-Path $env:WINDIR 'System32/WindowsPowerShell/v1.0/powershell.exe'
foreach ($name in ($tests + @('Test-NativeCompilation.ps1'))) {
    & $legacy -NoLogo -NoProfile -NonInteractive -File (Join-Path $PSScriptRoot ('tests/' + $name)) -Workspace $output
    if ($LASTEXITCODE -ne 0) { throw ('Windows PowerShell check failed: ' + $name) }
}
$desktop = Join-Path ${env:ProgramFiles} ('dotnet/shared/Microsoft.WindowsDesktop.App/' + [Environment]::Version.ToString())
$modernCompilation = Test-Path -LiteralPath $desktop
if ($modernCompilation) { & (Join-Path $PSScriptRoot 'tests/Test-NativeCompilation.ps1') -Workspace $output }
else { Write-Host 'Optional PowerShell 7 native compilation skipped: matching Windows Desktop runtime absent; target PowerShell 5.1 compilation remains mandatory.' }
& dotnet run --project (Join-Path $PSScriptRoot 'Native.Checks.csproj') --configuration Release
if ($LASTEXITCODE -ne 0) { throw 'Native policy harness failed.' }
& dotnet format whitespace (Join-Path $PSScriptRoot 'Native.Checks.csproj') --verify-no-changes --no-restore --verbosity minimal
if ($LASTEXITCODE -ne 0) { throw 'Native source formatting differs.' }
$summary = @{passed=$true;runtimeChanges=$false;targetNetworkUsed=$false;nativeInputSent=$false;targetNativeCompilation=$true;modernNativeCompilation=$modernCompilation;tests=$tests;utc=[DateTime]::UtcNow.ToString('o')}
[IO.File]::WriteAllText((Join-Path $output 'checks/summary.json'), ($summary | ConvertTo-Json -Depth 4))
$summary | ConvertTo-Json
