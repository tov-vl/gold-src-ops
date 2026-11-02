#Requires -Version 5.1
param([Parameter(Mandatory)][string]$Workspace)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
. (Join-Path $Workspace 'kit/code/Lab.Common.ps1')
. (Join-Path $Workspace 'kit/code/Interaction.ps1')
$root=Join-Path $Workspace ('checks/readiness-frames-'+$PSVersionTable.PSVersion.Major)
New-Item -ItemType Directory -Path $root -ErrorAction Stop|Out-Null
$script:checks=0
function Check([bool]$Value){if(-not $Value){throw ('Readiness frame assertion: '+$script:case)};$script:checks++}
function WriteBitmap([string]$Path,[byte]$Pixel){
    $bytes=New-Object byte[] 6912054
    $bytes[0]=66;$bytes[1]=77
    [BitConverter]::GetBytes([uint32]$bytes.Length).CopyTo($bytes,2)
    [BitConverter]::GetBytes([uint32]54).CopyTo($bytes,10)
    [BitConverter]::GetBytes([uint32]40).CopyTo($bytes,14)
    [BitConverter]::GetBytes([int]1920).CopyTo($bytes,18)
    [BitConverter]::GetBytes([int]1200).CopyTo($bytes,22)
    [BitConverter]::GetBytes([uint16]1).CopyTo($bytes,26)
    [BitConverter]::GetBytes([uint16]24).CopyTo($bytes,28)
    $bytes[-1]=$Pixel
    [IO.File]::WriteAllBytes($Path,$bytes)
}
function Get-LabAutoElapsed($Clock){return $script:now}
function Wait-LabAutoTick([int]$Milliseconds){$script:now+=$Milliseconds/1000.0}
function Assert-LabAutoContext($Root,$Policy,$Job,$Clock,[switch]$BeforeInput){}
function Assert-LabInteractionOwner($Policy,$Result){}
foreach($language in @('ru','en')){
    foreach($case in @('complete','missing','malformed','ambiguous','collision','focus')){
        $script:case=$case;$script:now=0;$script:inputs=0
        $area=Join-Path $root ($language+'-'+$case)
        $script:game=Join-Path $area 'game';$output=Join-Path $area 'result'
        New-Item -ItemType Directory -Path (Join-Path $game 'cstrike'),$output -Force|Out-Null
        $policy=@{gameRoot=$game}
        $job=[pscustomobject]@{id=[guid]::NewGuid().ToString();action='capture-guns';language=$language;createdUtc=[DateTime]::UtcNow.ToString('o')}
        $old=Join-Path $game 'old.bmp';WriteBitmap $old 1
        $oldHash=(Get-FileHash $old).Hash
        $baseline=@{};$baseline[$old]=$true
        WriteBitmap (Join-Path $game 'spawn.bmp') 2
        $spawn=@{images=@()}
        Check (Receive-LabInteractionFrame $policy $spawn $baseline $output)
        [IO.File]::Move((Join-Path $output 'image-1.bin'),(Join-Path $output 'readiness-spawn.bmp'))
        $result=@{images=@();readiness=@{spawnSnapshot=$spawn.images[0];setupSnapshot=$null;setupAcknowledged=$false;setupActions=@();failureCode=$null}}
        $window=[pscustomobject]@{}
        $window|Add-Member ScriptMethod CheckReady {if($script:case -ceq 'focus'){throw 'Focus unavailable.'}}
        $window|Add-Member ScriptMethod GunsSetupAction {
            param($Phase)
            $script:inputs++
            if($Phase -like 'ack-*' -and $script:case -cne 'missing'){
                $path=Join-Path $script:game 'setup.bmp'
                if($script:case -ceq 'malformed'){[IO.File]::WriteAllText($path,'incomplete BMP')}
                else{WriteBitmap $path 3}
                if($script:case -ceq 'ambiguous'){WriteBitmap (Join-Path $script:game 'extra.bmp') 4}
            }
        }
        if($case -ceq 'collision'){[IO.File]::WriteAllText((Join-Path $output 'image-1.bin'),'retained evidence')}
        $failed=$false
        try{Invoke-LabGunsSetup $area $policy $job $result ([Diagnostics.Stopwatch]::StartNew()) $window $baseline (Join-Path $output 'result.json')}catch{$failed=$true}
        Check ((Get-FileHash $old).Hash -ceq $oldHash)
        Check ($result.images.Count -eq 0)
        if($case -ceq 'complete'){
            Check (-not $failed -and $result.readiness.setupAcknowledged)
            Check ($script:inputs -eq 5 -and $baseline.Count -eq 3)
            Check ((Get-FileHash (Join-Path $output 'readiness-setup.bmp')).Hash -ceq $result.readiness.setupSnapshot.sha256)
            Check (-not (Test-Path (Join-Path $output 'image-1.bin')))
            $script:case='claimed';$rejected=$false
            try{Invoke-LabGunsSetup $area $policy $job $result ([Diagnostics.Stopwatch]::StartNew()) $window $baseline (Join-Path $output 'result.json')}catch{$rejected=$true}
            Check ($rejected -and $script:inputs -eq 5)
        }else{
            Check ($failed -and -not $result.readiness.setupAcknowledged)
            Check ($script:inputs -eq $(if($case -ceq 'focus'){0}else{5}))
            if($case -ceq 'missing'){Check ($result.readiness.failureCode -ceq 'setup_snapshot_missing')}
            if($case -ceq 'collision'){Check ([IO.File]::ReadAllText((Join-Path $output 'image-1.bin')) -ceq 'retained evidence')}
        }
    }
}
# Match the compiled fixture's fixed callbacks to the generated local bootstrap.
$script:case='callback-contract'
$source=[IO.File]::ReadAllText((Join-Path $PSScriptRoot '../fixture_vgui_guard.sma'))
$callbacks=[regex]::Matches($source,'client_cmd\(player, "(gso_lab_[a-z_]+)"\)')
$program=New-LabGunsProgram $job
Check ($callbacks.Count -eq 3)
foreach($callback in $callbacks){
    $name=$callback.Groups[1].Value
    Check ($program.Contains('alias '+$name+' "') -and -not $name.Contains('_set'))
}
Write-Output ('Readiness BMP receiver checks passed: '+$checks+'. Real file receiver; synthetic BMPs, no native input or network.')
