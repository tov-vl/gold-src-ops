#Requires -Version 7.0
param([string]$Workspace, [string]$ToolRoot)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $ToolRoot 'Lab.Tooling.psm1') -Force
$script:checks = 0
function Check([bool]$Value) { if (-not $Value) { throw 'Profile/archive assertion failed.' }; $script:checks++ }
function Reject([scriptblock]$Action) { $failed=$false; try { & $Action | Out-Null } catch { $failed=$true }; Check $failed }
$original = Get-Content -LiteralPath (Join-Path $ToolRoot 'examples/profile.example.json') -Raw
Check ((Read-LabProfile (Join-Path $ToolRoot 'examples/profile.example.json')).targetName -ceq 'GSO-CLIENT-LAB')
foreach ($case in @('public','loopback','noncanonical','same','injection','lowercase-host','path','fraction','extra','sid','session')) {
    $value = $original | ConvertFrom-Json
    switch ($case) {
        public {$value.sourceAddress='203.0.113.10'}
        loopback {$value.targetAddress='127.0.0.1'}
        noncanonical {$value.sourceAddress='192.168.240.010'}
        same {$value.targetAddress=$value.sourceAddress}
        injection {$value.targetName="target'; Write-Error injected; '"}
        lowercase-host {$value.targetName='gso-client-lab'}
        path {$value.gameRoot='C:\Games\..\Windows'}
        fraction {$value.gameSessionId=1.5}
        extra {$value | Add-Member surprise true}
        sid {$value.gameUserSid='S-1-5-18'}
        session {$value.sessionId=[guid]::Empty.ToString()}
    }
    $path = Join-Path $Workspace ('checks/' + $case + '.json')
    [IO.File]::WriteAllText($path, ($value | ConvertTo-Json -Depth 4))
    Reject { Read-LabProfile $path }
}
Reject { Assert-LabOutputPath (Join-Path $ToolRoot 'generated') }
Reject { Expand-LabTemplate '{{UNKNOWN}}' @{} }
$id = '20000000-0000-4000-8000-000000000001'
$resultDirectory = Join-Path $Workspace 'archive-results'
New-Item -ItemType Directory -Path (Join-Path $resultDirectory $id) | Out-Null
$inventory = Join-Path $Workspace 'inventory.json'
[IO.File]::WriteAllText($inventory, (@{terminalArchiveCandidates=@(@{id=$id;sha256=('A'*64)})} | ConvertTo-Json -Depth 5))
$resultPath = Join-Path $resultDirectory ($id + '/result.json')
$result = @{id=$id;status='autonomous_captured_unreviewed';cleanupVerified=$true;configCleanupVerified=$true;completedUtc=[DateTime]::UtcNow.ToString('o')}
[IO.File]::WriteAllText($resultPath, ($result | ConvertTo-Json))
$output = Join-Path $Workspace 'archive-plan.json'
& (Join-Path $ToolRoot 'New-ArchivePlan.ps1') -InventoryPath $inventory -ResultDirectory $resultDirectory -Id @($id) -OutputPath $output | Out-Null
Check (@(Get-Content -LiteralPath $output -Raw | ConvertFrom-Json).Count -eq 1)
Check ((Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json).status -ceq 'autonomous_captured_unreviewed')
Reject { & (Join-Path $ToolRoot 'New-ArchivePlan.ps1') -InventoryPath $inventory -ResultDirectory $resultDirectory -Id @($id) -OutputPath $output }
$result.cleanupVerified=$false
[IO.File]::WriteAllText($resultPath, ($result | ConvertTo-Json))
Reject { & (Join-Path $ToolRoot 'New-ArchivePlan.ps1') -InventoryPath $inventory -ResultDirectory $resultDirectory -Id @($id) -OutputPath (Join-Path $Workspace 'rejected-plan.json') }
Reject { & (Join-Path $ToolRoot 'New-ArchivePlan.ps1') -InventoryPath $inventory -ResultDirectory $resultDirectory -Id @($id,$id) -OutputPath (Join-Path $Workspace 'duplicate-plan.json') }
$activation=@{protocol=2;revision=16;targetName='GSO-CLIENT-LAB';sessionId='10000000-0000-4000-8000-000000000001';leaseMode='manual';expiresUtc=$null}
$activationPath=Join-Path $Workspace 'test-activation.json'
$runPath=Join-Path $Workspace 'rejected-run'
foreach($case in @('wrong-target','wrong-session','wrong-revision','timed-lease','expiry')){
    $value=$activation|ConvertTo-Json|ConvertFrom-Json
    switch($case){
        wrong-target {$value.targetName='OTHER-CLIENT'}
        wrong-session {$value.sessionId='30000000-0000-4000-8000-000000000001'}
        wrong-revision {$value.revision=15}
        timed-lease {$value.leaseMode='timed'}
        expiry {$value.expiresUtc=[DateTime]::UtcNow.AddHours(1).ToString('o')}
    }
    [IO.File]::WriteAllText($activationPath,($value|ConvertTo-Json))
    Reject { & (Join-Path $ToolRoot 'New-LabRun.ps1') -BundleDirectory $Workspace -ActivationPath $activationPath -AddonDirectory $Workspace -HelperBinaryPath $activationPath -Language en -OutputDirectory $runPath }
    Check (-not (Test-Path -LiteralPath $runPath))
}
[IO.File]::WriteAllText($activationPath,($activation|ConvertTo-Json))
$codePath=Join-Path $Workspace 'kit/code/Worker.ps1'
$originalCode=[IO.File]::ReadAllBytes($codePath)
try{
    [IO.File]::WriteAllText($codePath,'# Modified runner')
    Reject { & (Join-Path $ToolRoot 'New-LabRun.ps1') -BundleDirectory $Workspace -ActivationPath $activationPath -AddonDirectory $Workspace -HelperBinaryPath $activationPath -Language en -OutputDirectory $runPath }
    Check (-not (Test-Path -LiteralPath $runPath))
}finally{[IO.File]::WriteAllBytes($codePath,$originalCode)}
@{passed=$true;checks=$checks;runtimeChanged=$false} | ConvertTo-Json
