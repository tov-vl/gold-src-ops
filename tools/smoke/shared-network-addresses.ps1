#Requires -Version 7.0
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$temporary = Join-Path ([IO.Path]::GetTempPath()) ('shared-network-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($temporary) | Out-Null
try {
    $production = [IO.File]::ReadAllText((Join-Path $root 'ops/production/deployment.env.example'))
    $receiver = [IO.File]::ReadAllText((Join-Path $root 'ops/alert-receiver/deployment.env.example'))
    $cases = @(
        @{ Name = 'valid'; Web = '172.31.247.6'; Receiver = '172.31.246.5'; Expected = 0 },
        @{ Name = 'duplicate-web'; Web = '172.31.247.2'; Receiver = '172.31.246.5'; Expected = 1 },
        @{ Name = 'wrong-web-subnet'; Web = '172.31.246.6'; Receiver = '172.31.246.5'; Expected = 1 },
        @{ Name = 'missing-web'; Web = ''; Receiver = '172.31.246.5'; Expected = 1 },
        @{ Name = 'duplicate-receiver'; Web = '172.31.247.6'; Receiver = '172.31.246.3'; Expected = 1 },
        @{ Name = 'wrong-receiver-subnet'; Web = '172.31.247.6'; Receiver = '172.31.248.5'; Expected = 1 },
        @{ Name = 'missing-receiver'; Web = '172.31.247.6'; Receiver = ''; Expected = 1 }
    )
    foreach ($case in $cases) {
        $productionPath = Join-Path $temporary 'production.env'
        $receiverPath = Join-Path $temporary 'receiver.env'
        [IO.File]::WriteAllText($productionPath, $production.Replace('GOLDSRCOPS_WEB_TELEMETRY_IP=172.31.247.6', 'GOLDSRCOPS_WEB_TELEMETRY_IP=' + $case.Web))
        [IO.File]::WriteAllText($receiverPath, $receiver.Replace('GOLDSRCOPS_ALERT_RECEIVER_CONTROL_PLANE_IP=172.31.246.5', 'GOLDSRCOPS_ALERT_RECEIVER_CONTROL_PLANE_IP=' + $case.Receiver))
        $output = & pwsh -NoProfile -File (Join-Path $root 'ops/alert-receiver/control-plane-preflight.ps1') -ProductionEnvironmentFile $productionPath -ReceiverEnvironmentFile $receiverPath -ContractOnly 2>&1
        if (($LASTEXITCODE -eq 0) -ne ($case.Expected -eq 0)) {
            throw "Unexpected preflight result for $($case.Name): $($output -join [Environment]::NewLine)"
        }
        Write-Host "$($case.Name): passed"
    }
    & pwsh -NoProfile -File (Join-Path $root 'ops/alert-receiver/preflight.ps1') -EnvironmentFile (Join-Path $root 'ops/alert-receiver/deployment.env.example') -ContractOnly
    if ($LASTEXITCODE -ne 0) { throw 'Standalone receiver contract regressed.' }
}
finally {
    foreach ($name in @('production.env', 'receiver.env')) {
        $path = Join-Path $temporary $name
        if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path }
    }
    [IO.Directory]::Delete($temporary)
}
