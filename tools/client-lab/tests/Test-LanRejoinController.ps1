#Requires -Version 7.0
param([Parameter(Mandatory)][string]$Workspace,[Parameter(Mandatory)][string]$ToolRoot)
$ErrorActionPreference='Stop'
Import-Module (Join-Path $ToolRoot 'Lab.LanRejoin.psm1') -Force
$port={
    param($operation,$context)
    $case=$context.test
    $case.calls.Add($operation)
    switch($operation){
        preflight { return @{ready=$true;pending=6;target='FIXTURE-HOST'} }
        'guns-round' {if($case.name -ceq 'ambiguous-first'){throw 'Dispatch interrupted.'};return}
        result {
            $result=@{id=$context.case.jobId;action='verify-panel';panel='guns';language='en';
                status='autonomous_captured_unreviewed';completedUtc=[DateTime]::UtcNow.ToString('o');
                launchCommandDispatched=$true;launchRequested=$true;cleanupVerified=($case.name -cne 'unclosed-first');
                configCleanupVerified=$true;cleanup=@{game='verified';config='verified'};
                rendererEvidence=@{status='identified';identity=@{backend='OpenGL'}};
                interaction=@{continuity=@{phase='ready-for-owned-close';roundReadySha256=('A'*64)}};
                images=@(1,2,3,4,5)}
            return $result
        }
        window {
            return @{status='ready';lanRejoin=$true;runtimeVerified=$true;
                vacantForRejoin=($case.name -cne 'occupied');rejoinClosed=($case.name -ceq 'occupied');
                rejoinOpenedUnix=[DateTimeOffset]::UtcNow.ToUnixTimeSeconds()-1}
        }
        status {return @{target='FIXTURE-HOST';pending=$(if($case.name -ceq 'foreign-job'){8}else{7});worker=@{state='ready'}}}
    }
}
foreach($name in @('success','ambiguous-first','unclosed-first','occupied','foreign-job')){
    $run=Join-Path $Workspace ('checks/lan-controller-'+[guid]::NewGuid().ToString('N'))
    $null=New-Item -ItemType Directory -Path $run
    $state=@{name=$name;calls=[Collections.Generic.List[string]]::new()}
    $first=@{run=$run;case=@{jobId=[guid]::NewGuid().ToString('D')};test=$state}
    $second=@{run=$run;case=@{jobId=[guid]::NewGuid().ToString('D')};test=$state}
    $success=$name -cin @('success','ambiguous-first')
    $failed=$false
    try{Invoke-LabLanRejoin $first $second $port}catch{if($success){throw};$failed=$true}
    if($failed -eq $success -or @($state.calls|Where-Object {$_ -ceq 'guns-round'}).Count -ne 1 -or
       @($state.calls|Where-Object {$_ -ceq 'guns-rejoin'}).Count -ne [int]$success -or
       ($state.calls[-2..-1] -join ',') -cne 'stop,absence'){
        throw "LAN rejoin orchestration differs for $name."
    }
    if($success -and ($state.calls -join ',') -cne 'preflight,start,relay,guns-round,result,window,status,window,guns-rejoin,result,stop,absence'){
        throw 'LAN rejoin phases happened out of order.'
    }
    $state.calls.Clear()
    try{Invoke-LabLanRejoin $first $second $port;throw 'Claim was reused.'}catch{
        if(($state.calls -join ',') -cne 'preflight'){throw 'A used run dispatched again.'}
    }
}
'LAN rejoin controller checks passed.'
