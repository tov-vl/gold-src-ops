#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('prepare','status','hold','steam-free','run','resume','cancel','review','verify-evidence')][string]$Operation,
    [Parameter(Mandatory)][string]$CampaignDirectory,
    [ValidateSet('ranks','profile','topall','guns')][string]$Panel,
    [ValidateSet('ru','en')][string]$Language,
    [string]$BundleDirectory, [string]$ActivationPath, [string]$AddonDirectory,
    [string]$HelperBinaryPath, [string]$BaselinePath, [string]$PythonPath,
    [ValidatePattern('\A[0-9A-F]{64}\z')][string]$EvidenceSha256,
    [switch]$ImagesReviewed, [switch]$Execute,
    [ValidateSet('motd','guns')][string]$Scenario = 'motd'
)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'Lab.Tooling.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'Lab.Campaign.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'Lab.CampaignRuntime.psm1') -Force
$root = Assert-LabOutputPath $CampaignDirectory
$needsCase = $Operation -cin @('run','resume','cancel','review','verify-evidence')
if ($needsCase -and (-not $Panel -or -not $Language)) { throw 'Select one fixed panel and language.' }
if (-not $needsCase -and ($Panel -or $Language)) { throw 'This operation does not select a case.' }
if ($Operation -cne 'prepare' -and ($BundleDirectory -or $ActivationPath -or $AddonDirectory -or $HelperBinaryPath -or $BaselinePath -or $PythonPath)) { throw 'Preparation arguments require prepare.' }
if($Operation -cne 'prepare' -and $Scenario -cne 'motd'){throw 'Scenario selection requires prepare.'}
if ($Operation -cne 'review' -and ($ImagesReviewed -or $EvidenceSha256)) { throw 'Review arguments require review.' }
if ($Operation -cin @('run','resume','cancel','review','steam-free') -and -not $Execute) {
    @{ operation = $Operation; panel = $Panel; language = $Language; execute = $false; runtimeChanged = $false } | ConvertTo-Json
    return
}
$port = { param($action,$context) Invoke-LabCampaignRuntime $action $context }
switch -CaseSensitive ($Operation) {
    prepare {
        if (Test-Path -LiteralPath $root) { throw 'Use a fresh campaign directory.' }
        foreach ($path in @($BundleDirectory,$ActivationPath,$AddonDirectory,$HelperBinaryPath,$BaselinePath,$PythonPath)) {
            if ([string]::IsNullOrWhiteSpace($path) -or -not (Test-Path -LiteralPath $path)) { throw 'All local preparation inputs are required.' }
        }
        $baseline = Get-Content -LiteralPath $BaselinePath -Raw | ConvertFrom-Json -AsHashtable
        $cases = @()
        $null = New-Item -ItemType Directory -Path $root
        # A partial preparation is retained for inspection; it is never reused or started.
        $panels=if($Scenario -ceq 'guns'){@('guns')}else{@('ranks','profile','topall')}
        foreach ($panelName in $panels) {
            foreach ($languageName in @('ru','en')) {
                $entry = @($baseline.baselineProfiles | Where-Object { $_.panel -ceq $panelName -and $_.language -ceq $languageName })
                if ($entry.Count -ne 1 -or $entry[0].bodySha256 -cnotmatch '\A[0-9A-F]{64}\z') { throw 'Six reviewed baseline hashes required.' }
                $key = "$panelName-$languageName"
                $null = & (Join-Path $PSScriptRoot 'New-LabRun.ps1') -BundleDirectory $BundleDirectory -ActivationPath $ActivationPath `
                    -AddonDirectory $AddonDirectory -HelperBinaryPath $HelperBinaryPath -Language $languageName -OutputDirectory (Join-Path $root $key)
                $case=@{ key = $key; panel = $panelName; language = $languageName; baselineSha256 = $entry[0].bodySha256 }
                if($Scenario -ceq 'guns'){$case.menuBaselines=$entry[0].menuBaselines}
                $cases += $case
            }
        }
        $engine = Join-Path $root 'engine'
        $null = New-Item -ItemType Directory -Path $engine
        Copy-Item -LiteralPath (Join-Path $BundleDirectory 'kit/Invoke-FramedUpdate.ps1') -Destination $engine
        Copy-Item -LiteralPath (Join-Path $BundleDirectory 'kit/updater') -Destination $engine -Recurse
        Initialize-LabCampaign $root $cases @{
            python = [IO.Path]::GetFullPath($PythonPath); pythonSha256 = (Get-FileHash -LiteralPath $PythonPath).Hash
            pwsh = (Join-Path $PSHOME 'pwsh.exe')
        }
    }
    status { }
    hold { Set-LabCampaignSteam $root $false }
    steam-free { Set-LabCampaignSteam $root $true }
    run { $null = Invoke-LabCampaignCase $root $Panel $Language run $port }
    resume { $null = Invoke-LabCampaignCase $root $Panel $Language resume $port }
    cancel { Request-LabCampaignCancel $root $Panel $Language $port }
    review { $null = Approve-LabCampaignCase $root $Panel $Language $EvidenceSha256 ([bool]$ImagesReviewed) }
    verify-evidence { Get-LabCampaignEvidence $root $Panel $Language | ConvertTo-Json -Depth 8; return }
}
Get-LabCampaignReport $root | ConvertTo-Json -Depth 8
