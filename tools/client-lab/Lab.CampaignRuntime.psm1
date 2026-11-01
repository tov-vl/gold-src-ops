#Requires -Version 7.0
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Invoke-CampaignProcess([string]$File, [string[]]$Arguments, [int]$Seconds = 30) {
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $File
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.Environment['DOCKER_CONTEXT'] = 'desktop-linux'
    foreach ($name in @('DOCKER_HOST','DOCKER_TLS_VERIFY','DOCKER_CERT_PATH')) { $null = $start.Environment.Remove($name) }
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($start)
    $output = $process.StandardOutput.ReadToEndAsync()
    $errors = $process.StandardError.ReadToEndAsync()
    try {
        if (-not $process.WaitForExit($Seconds * 1000)) { throw 'Bounded command timed out; reconcile without retry.' }
        $text = $output.GetAwaiter().GetResult()
        $errorText = $errors.GetAwaiter().GetResult()
        if ($text.Length -gt 1MB -or $errorText.Length -gt 1MB -or $process.ExitCode -ne 0) { throw 'Bounded command failed; inspect private evidence.' }
        return $text.Trim()
    } finally {
        if (-not $process.HasExited) { $process.Kill($true) }
        $process.Dispose()
    }
}

function Read-CampaignRuntimeJson([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return $null }
    $item = Get-Item -LiteralPath $Path -Force
    if ($item.Length -gt 1MB -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Invalid runtime metadata.' }
    return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json -AsHashtable
}

function Invoke-CampaignDocker([string[]]$Arguments) {
    return Invoke-CampaignProcess 'docker' (@('--context','desktop-linux') + $Arguments) 50
}

function Assert-CampaignDocker {
    $endpoint = Invoke-CampaignDocker @('context','inspect','desktop-linux','--format','{{.Endpoints.docker.Host}}')
    if ($endpoint -cne 'npipe:////./pipe/dockerDesktopLinuxEngine') { throw 'Only the local desktop Linux engine is supported.' }
}

function Invoke-CampaignPanel($Context, [string]$Operation, [string[]]$Extra = @()) {
    $arguments = @('-NoLogo','-NoProfile','-NonInteractive','-File',(Join-Path $Context.run 'Invoke-Panel.ps1'),'-Operation',$Operation)
    if ($Operation -cne 'status') { $arguments += @('-JobId',$Context.case.jobId) }
    return Invoke-CampaignProcess $Context.campaign.runtime.pwsh ($arguments + $Extra)
}

function Test-CampaignIdle($Status, [DateTime]$Since) {
    $utc = ConvertTo-CampaignRuntimeUtc $Status.worker.client.utc
    return $Status.worker.client.ready -is [bool] -and $Status.worker.client.ready -and
        $Status.worker.state -ceq 'ready' -and $Status.worker.client.gameProcessCount -eq 0 -and
        $utc -ge $Since -and $utc -le [DateTime]::UtcNow.AddSeconds(5) -and $utc -ge [DateTime]::UtcNow.AddSeconds(-90)
}

function ConvertTo-CampaignRuntimeUtc($Value) {
    if ($Value -is [DateTime]) { return $Value.ToUniversalTime() }
    if ($Value -is [DateTimeOffset]) { return $Value.UtcDateTime }
    return [DateTimeOffset]::Parse([string]$Value,[Globalization.CultureInfo]::InvariantCulture).UtcDateTime
}

function Assert-CampaignWeaponsReadiness($Runtime,[string]$Panel){
    if($Panel -ceq 'guns' -and ($Runtime.readinessEnabled -isnot [bool] -or -not $Runtime.readinessEnabled -or $Runtime.nativeSetupEnabled -isnot [bool] -or -not $Runtime.nativeSetupEnabled)){throw 'Weapons readiness fixture required.'}
}

function Invoke-LabCampaignRuntime([string]$Operation, $Context) {
    if (-not $IsWindows) { throw 'Windows controller required.' }
    switch -CaseSensitive ($Operation) {
        preflight {
            Assert-CampaignDocker
            if (Invoke-CampaignDocker @('ps','-aq','--filter','label=purpose=client-lab')) { throw 'A lab fixture already exists.' }
            if (Get-NetUDPEndpoint -LocalPort 27025 -ErrorAction SilentlyContinue) { throw 'The lab listener is occupied.' }
            $activation = Read-CampaignRuntimeJson (Join-Path $Context.run 'activation.json')
            $status = (Invoke-CampaignPanel $Context 'status') | ConvertFrom-Json -AsHashtable
            if ($status.protocol -ne 2 -or $status.revision -ne 16 -or $status.target -cne $activation.targetName -or
                $status.leaseMode -cne 'manual' -or $null -ne $status.expiresUtc -or
                $Context.case.panel -cnotin $status.autonomousPanels -or $status.pending -ge 8 -or
                $status.pending -ne @($status.terminalArchiveCandidates).Count -or
                -not (Test-CampaignIdle $status ([DateTime]::UtcNow.AddSeconds(-90)))) { throw 'Client readiness or queue differs.' }
            $updateText = Invoke-CampaignProcess $Context.campaign.runtime.pwsh @('-NoLogo','-NoProfile','-NonInteractive','-File',
                (Join-Path $Context.root 'engine/Invoke-FramedUpdate.ps1'),'-Operation','status')
            $update = $updateText | ConvertFrom-Json -AsHashtable
            if ($update.enabled -isnot [bool] -or -not $update.enabled -or $update.blocked -isnot [bool] -or $update.blocked -or
                $update.revision -ne 16 -or $update.codeHashes.Count -ne 9) { throw 'Installed runner is not ready.' }
            foreach ($entry in $update.codeHashes.GetEnumerator()) {
                $code = Join-Path $Context.run ('code/' + $entry.Key)
                if ($entry.Key -notmatch '\A[A-Za-z][A-Za-z0-9.-]+\z' -or (Get-FileHash -LiteralPath $code).Hash -cne $entry.Value) { throw 'Installed runner hash differs.' }
            }
            return @{ ready = $true }
        }
        { $_ -cin @('start','relay','stop') } {
            Assert-CampaignDocker
            if ((Get-FileHash -LiteralPath $Context.campaign.runtime.python).Hash -cne $Context.campaign.runtime.pythonSha256) { throw 'Python identity differs.' }
            $null = Invoke-CampaignProcess $Context.campaign.runtime.python @('-B',(Join-Path $PSScriptRoot 'lab_fixture.py'),$Operation,'--run-directory',$Context.run,'--execute') 180
            if ($Operation -ceq 'start') {
                $watch = [Diagnostics.Stopwatch]::StartNew()
                do {
                    $runtime = Read-CampaignRuntimeJson (Join-Path $Context.run 'evidence/runtime.json')
                    if ($runtime -and $runtime.status -ceq 'failed') { throw 'Fixture failed.' }
                    if ($runtime -and $runtime.status -ceq 'ready' -and $runtime.runtimeVerified -is [bool] -and $runtime.runtimeVerified) {
                        Assert-CampaignWeaponsReadiness $runtime $Context.case.panel
                        return
                    }
                    Start-Sleep -Milliseconds 500
                } while ($watch.Elapsed.TotalSeconds -lt 60)
                throw 'Fixture readiness deadline.'
            }
            if ($Operation -ceq 'relay') {
                $watch = [Diagnostics.Stopwatch]::StartNew()
                do {
                    $relay = Read-CampaignRuntimeJson (Join-Path $Context.run 'relay-lan.json')
                    if ($relay -and $relay.status -ceq 'ready') { return }
                    Start-Sleep -Milliseconds 200
                } while ($watch.Elapsed.TotalSeconds -lt 5)
                throw 'Relay readiness deadline.'
            }
            return
        }
        dispatch {
            $null = Invoke-CampaignPanel $Context 'auto-panel' @('-Panel',$Context.case.panel,'-Language',$Context.case.language)
            return
        }
        cancel { $null = Invoke-CampaignPanel $Context 'cancel'; return }
        result { return (Invoke-CampaignPanel $Context 'result') | ConvertFrom-Json -AsHashtable }
        images {
            $directory = Join-Path $Context.run ('controller-results/' + $Context.case.jobId)
            $result = Read-CampaignRuntimeJson (Join-Path $directory 'result.json')
            if ($result.id -cne $Context.case.jobId -or $result.images.Count -ne 5) { throw 'Image result differs.' }
            for ($i = 1; $i -le 5; $i++) {
                # Existing images are retained and subsequently hash-checked, never overwritten.
                if (-not (Test-Path -LiteralPath (Join-Path $directory "image-$i.bmp"))) {
                    $null = Invoke-CampaignPanel $Context 'image' @('-ImageNumber',$i.ToString())
                }
            }
            if($Context.case.panel -ceq 'guns'){
                foreach($name in @('primary-exit','primary-reopened','pistol-selected-close','pistol-exit')){
                    if(-not (Test-Path -LiteralPath (Join-Path $directory ('checkpoint-'+$name+'.bmp')))){
                        $null=Invoke-CampaignPanel $Context 'checkpoint' @('-Checkpoint',$name)
                    }
                }
            }
            return
        }
        absence {
            Assert-CampaignDocker
            $plan = Read-CampaignRuntimeJson (Join-Path $Context.run 'plan.json')
            if ($plan.container -cnotmatch '\Agso-client-lab-[a-f0-9]{32}\z' -or $plan.network -cne ($plan.container + '-net')) { throw 'Fixture identity differs.' }
            if ((Invoke-CampaignDocker @('ps','-aq','--filter',('name=^/' + $plan.container + '$'))) -or
                (Invoke-CampaignDocker @('network','ls','-q','--filter',('name=^' + $plan.network + '$')))) { throw 'Owned fixture remains.' }
            if (Get-NetUDPEndpoint -LocalPort 27025 -ErrorAction SilentlyContinue) { throw 'Relay listener remains.' }
            $relay = Read-CampaignRuntimeJson (Join-Path $Context.run 'relay-process.json')
            if ($relay -and (Get-Process -Id $relay.pid -ErrorAction SilentlyContinue)) { throw 'Relay process absence is unconfirmed.' }
            return @{ verified = $true; utc = [DateTime]::UtcNow.ToString('o') }
        }
        idle {
            $result = Read-CampaignRuntimeJson (Join-Path $Context.run ('controller-results/' + $Context.case.jobId + '/result.json'))
            $since = ConvertTo-CampaignRuntimeUtc $result.completedUtc
            $watch = [Diagnostics.Stopwatch]::StartNew()
            do {
                $status = (Invoke-CampaignPanel $Context 'status') | ConvertFrom-Json -AsHashtable
                if (Test-CampaignIdle $status $since) { return $status }
                Start-Sleep -Seconds 3
            } while ($watch.Elapsed.TotalSeconds -lt 75)
            throw 'Fresh target idle state missing.'
        }
        wait { Start-Sleep -Seconds 2; return }
        default { throw 'Unknown fixed campaign operation.' }
    }
}

Export-ModuleMember -Function Invoke-LabCampaignRuntime
