#Requires -Version 7.0

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$matrixScript = Join-Path $PSScriptRoot "oidc-live.ps1"
$runId = [Guid]::NewGuid().ToString("N")
$temporaryDirectory = Join-Path ([IO.Path]::GetTempPath()) "goldsrcops-oidc-$runId"
$evidencePath = Join-Path $temporaryDirectory "oidc-evidence.json"
$secretValues = @(
    "reader-$runId",
    "missing-role-$runId",
    "expired-$runId",
    "wrong-audience-$runId"
)
$secureTokens = [Collections.Generic.List[Security.SecureString]]::new()

function New-TestToken {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Value
    )

    $token = ConvertTo-SecureString -String $Value -AsPlainText -Force
    $secureTokens.Add($token)
    return $token
}

function Assert-Condition {
    param(
        [Parameter(Mandatory = $true)]
        [bool]$Condition,

        [Parameter(Mandatory = $true)]
        [string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

try {
    New-Item -ItemType Directory -Path $temporaryDirectory | Out-Null

    $readerToken = New-TestToken -Value $secretValues[0]
    $missingRoleToken = New-TestToken -Value $secretValues[1]
    $expiredToken = New-TestToken -Value $secretValues[2]
    $wrongAudienceToken = New-TestToken -Value $secretValues[3]

    $expectedResponses = @{
        AnonymousReader = [pscustomobject]@{ StatusCode = 401; HasBearerChallenge = $true }
        ReaderRead = [pscustomobject]@{ StatusCode = 200; HasBearerChallenge = $false }
        ReaderMetrics = [pscustomobject]@{ StatusCode = 200; HasBearerChallenge = $false }
        ReaderMutation = [pscustomobject]@{ StatusCode = 403; HasBearerChallenge = $false }
        MissingRole = [pscustomobject]@{ StatusCode = 403; HasBearerChallenge = $false }
        ExpiredToken = [pscustomobject]@{ StatusCode = 401; HasBearerChallenge = $true }
        ForeignIssuer = [pscustomobject]@{ StatusCode = 401; HasBearerChallenge = $true }
        WrongAudience = [pscustomobject]@{ StatusCode = 401; HasBearerChallenge = $true }
    }
    $transport = {
        param($name, $method, $uri, $accessToken)

        if ($uri.Scheme -ne "http" -or -not $uri.IsLoopback) {
            throw "The contract test received a non-loopback URI."
        }

        if ($name -eq "AnonymousReader") {
            if ($null -ne $accessToken) {
                throw "The anonymous scenario unexpectedly received a token."
            }
        }
        elseif ($null -eq $accessToken -or $accessToken -isnot [Security.SecureString]) {
            throw "Scenario '$name' did not receive a SecureString token."
        }

        if ($method -notin @("GET", "POST")) {
            throw "Scenario '$name' used an unexpected HTTP method."
        }

        return $expectedResponses[$name]
    }

    $parameters = @{
        BaseUrl = [Uri]"http://127.0.0.1:5142/"
        ReaderAccessToken = $readerToken
        MissingRoleAccessToken = $missingRoleToken
        ExpiredAccessToken = $expiredToken
        WrongAudienceAccessToken = $wrongAudienceToken
        EvidencePath = $evidencePath
        AllowInsecureLoopback = $true
        RequestTransport = $transport
    }
    $results = @(& $matrixScript @parameters)

    $expectedNames = @(
        "AnonymousReader",
        "ReaderRead",
        "ReaderMetrics",
        "ReaderMutation",
        "MissingRole",
        "ExpiredToken",
        "ForeignIssuer",
        "WrongAudience"
    )
    Assert-Condition `
        -Condition ($results.Count -eq $expectedNames.Count) `
        -Message "The OIDC matrix returned an unexpected number of results."
    Assert-Condition `
        -Condition ((@($results.Name) -join ",") -ceq ($expectedNames -join ",")) `
        -Message "The OIDC matrix scenario order changed unexpectedly."

    $evidenceText = [IO.File]::ReadAllText($evidencePath)
    $evidence = $evidenceText | ConvertFrom-Json
    Assert-Condition `
        -Condition ($evidence.Action -ceq "OidcAuthorizationMatrix") `
        -Message "The OIDC evidence action is invalid."
    Assert-Condition `
        -Condition (@($evidence.Results).Count -eq $expectedNames.Count) `
        -Message "The OIDC evidence result count is invalid."

    foreach ($secretValue in $secretValues) {
        Assert-Condition `
            -Condition (-not $evidenceText.Contains($secretValue, [StringComparison]::Ordinal)) `
            -Message "The OIDC evidence contains token material."
    }

    $failureObserved = $false
    $failingTransport = {
        param($name, $method, $uri, $accessToken)

        if ($name -eq "ReaderRead") {
            return [pscustomobject]@{ StatusCode = 401; HasBearerChallenge = $true }
        }

        return $expectedResponses[$name]
    }

    try {
        & $matrixScript `
            -BaseUrl ([Uri]"http://127.0.0.1:5142/") `
            -ReaderAccessToken $readerToken `
            -MissingRoleAccessToken $missingRoleToken `
            -ExpiredAccessToken $expiredToken `
            -WrongAudienceAccessToken $wrongAudienceToken `
            -AllowInsecureLoopback `
            -RequestTransport $failingTransport *> $null
    }
    catch {
        $failureObserved = $true
    }

    Assert-Condition `
        -Condition $failureObserved `
        -Message "The OIDC matrix did not fail on an unexpected status code."

    Write-Host "OIDC live-smoke contract test passed."
}
finally {
    foreach ($secureToken in $secureTokens) {
        $secureToken.Dispose()
    }

    $secureTokens.Clear()

    if (Test-Path -LiteralPath $temporaryDirectory) {
        $resolvedTemporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
        $resolvedDirectory = [IO.Path]::GetFullPath($temporaryDirectory)
        if (-not $resolvedDirectory.StartsWith(
                $resolvedTemporaryRoot,
                [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to remove an OIDC smoke directory outside the temporary path."
        }

        Remove-Item -LiteralPath $resolvedDirectory -Recurse -Force
    }
}
