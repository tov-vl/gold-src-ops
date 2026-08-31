#Requires -Version 7.0

<#
.SYNOPSIS
Verifies the production OIDC authorization matrix through the public API.

.DESCRIPTION
Checks anonymous rejection, Reader read access, Reader mutation denial,
missing-role denial, and Bearer rejection for expired, foreign-issuer, and
wrong-audience tokens. Tokens are accepted only as SecureString values or
prompted without echo. Response bodies and token claims are never read.

.PARAMETER BaseUrl
The public GoldSrcOps HTTPS origin. Paths, credentials, query strings, and
fragments are rejected.

.PARAMETER ReaderAccessToken
A valid access token whose only GoldSrcOps application role is Reader.

.PARAMETER MissingRoleAccessToken
A valid access token from the configured issuer and audience without a
GoldSrcOps application role.

.PARAMETER ExpiredAccessToken
An expired access token originally issued by the configured production issuer
for the GoldSrcOps audience. Its expiration must be more than 30 seconds in the
past so it is outside the API's bounded clock-skew allowance.

.PARAMETER WrongIssuerAccessToken
An optional signed token from a foreign issuer. When omitted, the script
creates an ephemeral RS256 token with the expected audience and a foreign
issuer. Its private key exists only in process memory.

.PARAMETER WrongAudienceAccessToken
A valid, unexpired token from the configured issuer with an audience other than
the GoldSrcOps API. An OIDC ID token from the same authorization flow is a
suitable test input.

.PARAMETER EvidencePath
An optional absolute JSON path outside the repository. The evidence contains
only request names, paths, status codes, Bearer-challenge presence, and times.
#>

[CmdletBinding()]
param(
    [Uri]$BaseUrl = "https://api.goldsrcops.com",

    [Security.SecureString]$ReaderAccessToken,

    [Security.SecureString]$MissingRoleAccessToken,

    [Security.SecureString]$ExpiredAccessToken,

    [Security.SecureString]$WrongIssuerAccessToken,

    [Security.SecureString]$WrongAudienceAccessToken,

    [string]$EvidencePath,

    [Parameter(DontShow = $true)]
    [switch]$AllowInsecureLoopback,

    [Parameter(DontShow = $true)]
    [scriptblock]$RequestTransport
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$ownedTokens = [Collections.Generic.List[Security.SecureString]]::new()
$httpHandler = $null
$httpClient = $null
$scenarios = $null

function ConvertFrom-SecureValue {
    param(
        [Parameter(Mandatory = $true)]
        [Security.SecureString]$Value
    )

    $valuePointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($Value)
    try {
        return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($valuePointer)
    }
    finally {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($valuePointer)
    }
}

function ConvertTo-Base64Url {
    param(
        [Parameter(Mandatory = $true)]
        [byte[]]$Value
    )

    return [Convert]::ToBase64String($Value).
        TrimEnd([char[]]"=").
        Replace("+", "-").
        Replace("/", "_")
}

function New-EphemeralForeignToken {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Audience
    )

    $rsa = [Security.Cryptography.RSA]::Create(2048)
    $plainToken = $null
    try {
        $now = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
        $header = [ordered]@{
            alg = "RS256"
            kid = "foreign-issuer-smoke"
            typ = "JWT"
        } | ConvertTo-Json -Compress
        $payload = [ordered]@{
            iss = "https://foreign-issuer.invalid/"
            aud = $Audience
            sub = "foreign-issuer-smoke"
            iat = $now
            nbf = $now
            exp = $now + 300
            "https://goldsrcops.com/roles" = @("Reader")
        } | ConvertTo-Json -Compress

        $headerPart = ConvertTo-Base64Url -Value ([Text.Encoding]::UTF8.GetBytes($header))
        $payloadPart = ConvertTo-Base64Url -Value ([Text.Encoding]::UTF8.GetBytes($payload))
        $signingInput = "$headerPart.$payloadPart"
        $signature = $rsa.SignData(
            [Text.Encoding]::ASCII.GetBytes($signingInput),
            [Security.Cryptography.HashAlgorithmName]::SHA256,
            [Security.Cryptography.RSASignaturePadding]::Pkcs1)
        $signaturePart = ConvertTo-Base64Url -Value $signature
        $plainToken = "$signingInput.$signaturePart"

        return ConvertTo-SecureString -String $plainToken -AsPlainText -Force
    }
    finally {
        $plainToken = $null
        $rsa.Dispose()
    }
}

function Resolve-RequiredToken {
    param(
        [Security.SecureString]$Value,

        [Parameter(Mandatory = $true)]
        [string]$Prompt
    )

    if ($null -eq $Value) {
        $Value = Read-Host $Prompt -AsSecureString
        if ($Value.Length -eq 0) {
            $Value.Dispose()
            throw "$Prompt must not be empty."
        }

        $ownedTokens.Add($Value)
        return $Value
    }

    if ($Value.Length -eq 0) {
        throw "$Prompt must not be empty."
    }

    return $Value
}

function Assert-BaseUrl {
    if (-not $BaseUrl.IsAbsoluteUri) {
        throw "BaseUrl must be an absolute URI."
    }

    $isSecure = $BaseUrl.Scheme -eq "https"
    $isAllowedLoopback = $AllowInsecureLoopback -and
        $BaseUrl.Scheme -eq "http" -and
        $BaseUrl.IsLoopback
    if (-not $isSecure -and -not $isAllowedLoopback) {
        throw "BaseUrl must use HTTPS. HTTP is allowed only for the deterministic loopback contract test."
    }

    if (-not [string]::IsNullOrEmpty($BaseUrl.UserInfo) -or
        -not [string]::IsNullOrEmpty($BaseUrl.Query) -or
        -not [string]::IsNullOrEmpty($BaseUrl.Fragment) -or
        $BaseUrl.AbsolutePath -ne "/") {
        throw "BaseUrl must be an origin without credentials, a path, a query, or a fragment."
    }
}

function Invoke-HttpTransport {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name,

        [Parameter(Mandatory = $true)]
        [ValidateSet("GET", "POST")]
        [string]$Method,

        [Parameter(Mandatory = $true)]
        [Uri]$Uri,

        [Security.SecureString]$AccessToken
    )

    $plainToken = $null
    $request = $null
    $response = $null
    try {
        $request = [Net.Http.HttpRequestMessage]::new(
            [Net.Http.HttpMethod]::new($Method),
            $Uri)

        if ($null -ne $AccessToken) {
            $plainToken = ConvertFrom-SecureValue -Value $AccessToken
            $request.Headers.Authorization = [Net.Http.Headers.AuthenticationHeaderValue]::new(
                "Bearer",
                $plainToken)
            $plainToken = $null
        }

        if ($Method -eq "POST") {
            $request.Content = [Net.Http.StringContent]::new(
                "{}",
                [Text.Encoding]::UTF8,
                "application/json")
        }

        $response = $httpClient.Send(
            $request,
            [Net.Http.HttpCompletionOption]::ResponseHeadersRead)
        $hasBearerChallenge = @(
            $response.Headers.WwwAuthenticate |
                Where-Object Scheme -EQ "Bearer"
        ).Count -gt 0

        return [pscustomobject]@{
            StatusCode = [int]$response.StatusCode
            HasBearerChallenge = $hasBearerChallenge
        }
    }
    catch {
        throw "OIDC live request '$Name' failed before a response was received. " +
            "Verify DNS, TLS, API availability, and local network access."
    }
    finally {
        $plainToken = $null
        if ($null -ne $response) {
            $response.Dispose()
        }

        if ($null -ne $request) {
            $request.Dispose()
        }
    }
}

function Write-SanitizedEvidence {
    param(
        [Parameter(Mandatory = $true)]
        [Collections.Generic.List[object]]$Results
    )

    if ([string]::IsNullOrWhiteSpace($EvidencePath)) {
        return
    }

    if (-not [IO.Path]::IsPathFullyQualified($EvidencePath)) {
        throw "EvidencePath must be absolute."
    }

    $fullEvidencePath = [IO.Path]::GetFullPath($EvidencePath)
    $repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../.."))
    $repositoryPrefix = $repositoryRoot.TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if ($fullEvidencePath -eq $repositoryRoot -or
        $fullEvidencePath.StartsWith($repositoryPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "EvidencePath must remain outside the Git repository."
    }

    $evidenceDirectory = [IO.Path]::GetDirectoryName($fullEvidencePath)
    if ([string]::IsNullOrWhiteSpace($evidenceDirectory) -or
        -not [IO.Directory]::Exists($evidenceDirectory)) {
        throw "The EvidencePath parent directory must already exist."
    }

    $evidence = [ordered]@{
        Action = "OidcAuthorizationMatrix"
        BaseUrl = $BaseUrl.GetLeftPart([UriPartial]::Authority)
        CompletedAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
        Results = $Results
    }
    $temporaryPath = Join-Path $evidenceDirectory (
        ".{0}.{1}.tmp" -f [IO.Path]::GetFileName($fullEvidencePath), [Guid]::NewGuid().ToString("N"))

    try {
        [IO.File]::WriteAllText(
            $temporaryPath,
            ($evidence | ConvertTo-Json -Depth 10),
            [Text.UTF8Encoding]::new($false))
        [IO.File]::Move($temporaryPath, $fullEvidencePath, $true)

        if (-not $IsWindows) {
            [IO.File]::SetUnixFileMode(
                $fullEvidencePath,
                [IO.UnixFileMode]::UserRead -bor [IO.UnixFileMode]::UserWrite)
        }
    }
    finally {
        if (Test-Path -LiteralPath $temporaryPath) {
            Remove-Item -LiteralPath $temporaryPath -Force
        }
    }
}

try {
    Assert-BaseUrl

    $readerToken = Resolve-RequiredToken `
        -Value $ReaderAccessToken `
        -Prompt "GoldSrcOps Reader access token"
    $missingRoleToken = Resolve-RequiredToken `
        -Value $MissingRoleAccessToken `
        -Prompt "GoldSrcOps access token without an application role"
    $expiredToken = Resolve-RequiredToken `
        -Value $ExpiredAccessToken `
        -Prompt "Expired GoldSrcOps access token"
    $wrongAudienceToken = Resolve-RequiredToken `
        -Value $WrongAudienceAccessToken `
        -Prompt "Auth0 token with a non-GoldSrcOps audience"

    $foreignIssuerToken = $WrongIssuerAccessToken
    if ($null -eq $foreignIssuerToken) {
        $foreignIssuerToken = New-EphemeralForeignToken `
            -Audience $BaseUrl.GetLeftPart([UriPartial]::Authority)
        $ownedTokens.Add($foreignIssuerToken)
    }
    elseif ($foreignIssuerToken.Length -eq 0) {
        throw "WrongIssuerAccessToken must not be empty."
    }

    $scenarios = @(
        [pscustomobject]@{
            Name = "AnonymousReader"
            Method = "GET"
            Path = "/api/dashboard/overview"
            AccessToken = $null
            ExpectedStatus = 401
            RequireBearerChallenge = $true
        },
        [pscustomobject]@{
            Name = "ReaderRead"
            Method = "GET"
            Path = "/api/dashboard/overview"
            AccessToken = $readerToken
            ExpectedStatus = 200
            RequireBearerChallenge = $false
        },
        [pscustomobject]@{
            Name = "ReaderMetrics"
            Method = "GET"
            Path = "/metrics"
            AccessToken = $readerToken
            ExpectedStatus = 200
            RequireBearerChallenge = $false
        },
        [pscustomobject]@{
            Name = "ReaderMutation"
            Method = "POST"
            Path = "/api/servers"
            AccessToken = $readerToken
            ExpectedStatus = 403
            RequireBearerChallenge = $false
        },
        [pscustomobject]@{
            Name = "MissingRole"
            Method = "GET"
            Path = "/api/dashboard/overview"
            AccessToken = $missingRoleToken
            ExpectedStatus = 403
            RequireBearerChallenge = $false
        },
        [pscustomobject]@{
            Name = "ExpiredToken"
            Method = "GET"
            Path = "/api/dashboard/overview"
            AccessToken = $expiredToken
            ExpectedStatus = 401
            RequireBearerChallenge = $true
        },
        [pscustomobject]@{
            Name = "ForeignIssuer"
            Method = "GET"
            Path = "/api/dashboard/overview"
            AccessToken = $foreignIssuerToken
            ExpectedStatus = 401
            RequireBearerChallenge = $true
        },
        [pscustomobject]@{
            Name = "WrongAudience"
            Method = "GET"
            Path = "/api/dashboard/overview"
            AccessToken = $wrongAudienceToken
            ExpectedStatus = 401
            RequireBearerChallenge = $true
        }
    )

    if ($null -eq $RequestTransport) {
        $httpHandler = [Net.Http.HttpClientHandler]::new()
        $httpHandler.AllowAutoRedirect = $false
        $httpClient = [Net.Http.HttpClient]::new($httpHandler, $false)
        $httpClient.Timeout = [TimeSpan]::FromSeconds(15)
    }

    $results = [Collections.Generic.List[object]]::new()
    foreach ($scenario in $scenarios) {
        $uri = [Uri]::new($BaseUrl, $scenario.Path)
        $response = if ($null -eq $RequestTransport) {
            Invoke-HttpTransport `
                -Name $scenario.Name `
                -Method $scenario.Method `
                -Uri $uri `
                -AccessToken $scenario.AccessToken
        }
        else {
            & $RequestTransport `
                $scenario.Name `
                $scenario.Method `
                $uri `
                $scenario.AccessToken
        }

        if ($null -eq $response -or
            $null -eq $response.PSObject.Properties["StatusCode"] -or
            $null -eq $response.PSObject.Properties["HasBearerChallenge"]) {
            throw "Request transport returned an invalid result for '$($scenario.Name)'."
        }

        $actualStatus = [int]$response.StatusCode
        $hasBearerChallenge = [bool]$response.HasBearerChallenge
        if ($actualStatus -ne $scenario.ExpectedStatus) {
            throw "OIDC scenario '$($scenario.Name)' returned HTTP $actualStatus; " +
                "expected $($scenario.ExpectedStatus). Response content was omitted."
        }

        if ($scenario.RequireBearerChallenge -and -not $hasBearerChallenge) {
            throw "OIDC scenario '$($scenario.Name)' returned HTTP 401 without a Bearer challenge."
        }

        $result = [pscustomobject][ordered]@{
            Name = $scenario.Name
            Method = $scenario.Method
            Path = $scenario.Path
            ExpectedStatus = $scenario.ExpectedStatus
            ActualStatus = $actualStatus
            BearerChallenge = $hasBearerChallenge
            CompletedAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
        }
        $results.Add($result)
        $result
    }

    Write-SanitizedEvidence -Results $results
    Write-Host "OIDC live authorization matrix passed."
}
finally {
    $scenarios = $null

    if ($null -ne $httpClient) {
        $httpClient.Dispose()
    }

    if ($null -ne $httpHandler) {
        $httpHandler.Dispose()
    }

    foreach ($ownedToken in $ownedTokens) {
        $ownedToken.Dispose()
    }

    $ownedTokens.Clear()
}
