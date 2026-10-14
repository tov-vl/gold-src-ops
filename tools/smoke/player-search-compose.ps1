#Requires -Version 7.0
Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../.."))
$temporary = Join-Path ([IO.Path]::GetTempPath()) ("player-search-compose-" + [guid]::NewGuid().ToString("N"))
$null = New-Item -ItemType Directory -Path $temporary
$emptyEnv = Join-Path $temporary "empty.env"
[IO.File]::WriteAllText($emptyEnv, "")
try {
    $baseline = $null
    foreach ($flags in @(@($null, $null), @("true", "false"), @("false", "true"), @("true", "true"))) {
        $info = [Diagnostics.ProcessStartInfo]::new("docker")
        $info.UseShellExecute = $false
        $info.RedirectStandardOutput = $true
        $info.RedirectStandardError = $true
        $info.WorkingDirectory = $root
        foreach ($key in @($info.Environment.Keys)) {
            if ($key.StartsWith("GOLDSRCOPS_") -or $key.StartsWith("COMPOSE_")) { $null = $info.Environment.Remove($key) }
        }
        foreach ($line in [IO.File]::ReadAllLines((Join-Path $root "ops/production/deployment.env.example"))) {
            if ($line -match '^([A-Z0-9_]+)=(.*)$' -and $Matches[1] -notlike 'GOLDSRCOPS_PUBLIC_PLAYER_SEARCH_*') {
                $info.Environment[$Matches[1]] = $Matches[2]
            }
        }
        if ($null -ne $flags[0]) { $info.Environment["GOLDSRCOPS_PUBLIC_PLAYER_SEARCH_API_ENABLED"] = $flags[0] }
        if ($null -ne $flags[1]) { $info.Environment["GOLDSRCOPS_PUBLIC_PLAYER_SEARCH_WEB_ENABLED"] = $flags[1] }
        foreach ($arg in @("compose", "--env-file", $emptyEnv, "-f", "ops/production/compose.yml", "--profile", "runtime", "config", "--format", "json")) {
            $info.ArgumentList.Add($arg)
        }
        $process = [Diagnostics.Process]::Start($info)
        try {
            $stdout = $process.StandardOutput.ReadToEndAsync()
            $stderr = $process.StandardError.ReadToEndAsync()
            if (-not $process.WaitForExit(30000)) { $process.Kill($true); throw "Compose validation timed out." }
            if ($process.ExitCode -ne 0) { throw "Compose validation failed: $($stderr.GetAwaiter().GetResult())" }
            $config = $stdout.GetAwaiter().GetResult() | ConvertFrom-Json -AsHashtable
        }
        finally { $process.Dispose() }
        $expectedApi = if ($null -eq $flags[0]) { "false" } else { $flags[0] }
        $expectedWeb = if ($null -eq $flags[1]) { "false" } else { $flags[1] }
        if ($config.services.api.environment.PublicPlayerSearch__Enabled -cne $expectedApi -or
            $config.services.web.environment.PublicPlayerSearch__Enabled -cne $expectedWeb) {
            throw "Search flags are not independent and default-off."
        }
        $config.services.api.environment.Remove("PublicPlayerSearch__Enabled")
        $config.services.web.environment.Remove("PublicPlayerSearch__Enabled")
        $invariants = $config | ConvertTo-Json -Depth 100 -Compress
        if ($null -eq $baseline) { $baseline = $invariants }
        elseif ($invariants -cne $baseline) { throw "Search flags changed unrelated Compose settings." }
    }
    Write-Output "PLAYER_SEARCH_COMPOSE_SMOKE=passed cases=4"
}
finally {
    # These two exact paths were created by this invocation; never follow a link.
    foreach ($path in @($emptyEnv, $temporary)) {
        $item = Get-Item -LiteralPath $path -Force
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Unexpected temporary path replacement." }
        Remove-Item -LiteralPath $item.FullName
    }
}
