#Requires -Version 5.1

<#
.SYNOPSIS
Runs a guarded end-to-end RCON smoke check through the GoldSrcOps API.

.DESCRIPTION
Preflights an owned server, queues one generated SAY command, and waits for its
terminal command status. The script never accepts or reads the RCON password.

.PARAMETER ServerId
The id of an existing GoldSrcOps server registration.

.PARAMETER BaseUrl
The loopback GoldSrcOps API base URL.

.PARAMETER AcknowledgeOwnedServer
Confirms that you own or administer the selected GoldSrc server.

.PARAMETER AccessToken
An optional JWT supplied as a SecureString. When omitted, the script prompts
for the token without echoing it.

.PARAMETER TimeoutSeconds
How long to wait for the queued command to reach a terminal status.

.PARAMETER PollIntervalMilliseconds
How often to read the queued command status.
#>

[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = "Medium")]
param(
    [Parameter(Mandatory = $true)]
    [Guid]$ServerId,

    [Uri]$BaseUrl = "http://localhost:5142",

    [switch]$AcknowledgeOwnedServer,

    [Security.SecureString]$AccessToken,

    [ValidateRange(1, 300)]
    [int]$TimeoutSeconds = 30,

    [ValidateRange(100, 5000)]
    [int]$PollIntervalMilliseconds = 500
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

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

function Invoke-GoldSrcOpsApi {
    param(
        [Parameter(Mandatory = $true)]
        [ValidateSet("Get", "Post")]
        [string]$Method,

        [Parameter(Mandatory = $true)]
        [Uri]$Uri,

        [Parameter(Mandatory = $true)]
        [System.Collections.IDictionary]$Headers,

        [string]$Body
    )

    $parameters = @{
        Method = $Method
        Uri = $Uri
        Headers = $Headers
        TimeoutSec = 10
        ErrorAction = "Stop"
    }

    if ($PSBoundParameters.ContainsKey("Body")) {
        $parameters.ContentType = "application/json"
        $parameters.Body = $Body
    }

    try {
        return Invoke-RestMethod @parameters
    }
    catch {
        $statusSuffix = ""
        if ($null -ne $_.Exception.Response) {
            try {
                $statusSuffix = " with HTTP status $([int]$_.Exception.Response.StatusCode)"
            }
            catch {
                $statusSuffix = ""
            }
        }

        throw "GoldSrcOps API request failed for $Method $($Uri.AbsolutePath)$statusSuffix. " +
            "Verify the API, token, and local API logs. The response body was omitted."
    }
}

if (-not $AcknowledgeOwnedServer) {
    throw "Refusing live RCON preflight without -AcknowledgeOwnedServer. " +
        "Use it only for a server you own or administer."
}

if ($ServerId -eq [Guid]::Empty) {
    throw "ServerId must not be empty."
}

if (-not $BaseUrl.IsAbsoluteUri) {
    throw "BaseUrl must be an absolute HTTP or HTTPS URL."
}

if ($BaseUrl.Scheme -notin @("http", "https")) {
    throw "BaseUrl must use HTTP or HTTPS."
}

if (-not $BaseUrl.IsLoopback) {
    throw "BaseUrl must target a loopback address. This helper does not send JWTs to remote APIs."
}

if (-not [string]::IsNullOrEmpty($BaseUrl.UserInfo) -or
    -not [string]::IsNullOrEmpty($BaseUrl.Query) -or
    -not [string]::IsNullOrEmpty($BaseUrl.Fragment)) {
    throw "BaseUrl must not contain credentials, a query, or a fragment."
}

$ownsAccessToken = $false
if ($null -eq $AccessToken) {
    $AccessToken = Read-Host "GoldSrcOps Operator JWT" -AsSecureString
    $ownsAccessToken = $true
}

if ($AccessToken.Length -eq 0) {
    if ($ownsAccessToken) {
        $AccessToken.Dispose()
    }

    throw "The GoldSrcOps Operator JWT must not be empty."
}

$plainAccessToken = $null
$headers = $null

try {
    $plainAccessToken = ConvertFrom-SecureValue -Value $AccessToken
    $headers = @{ Authorization = "Bearer $plainAccessToken" }
    $plainAccessToken = $null

    $baseUrlText = $BaseUrl.AbsoluteUri.TrimEnd("/")
    $serverUrl = [Uri]"$baseUrlText/api/servers/$ServerId"
    $credentialsUrl = [Uri]"$baseUrlText/api/servers/$ServerId/credentials"

    $server = Invoke-GoldSrcOpsApi -Method Get -Uri $serverUrl -Headers $headers
    if ([Guid]$server.id -ne $ServerId) {
        throw "The API returned a different server id during preflight."
    }

    if (-not [bool]$server.isEnabled) {
        throw "Server '$ServerId' is disabled. Enable it before a live RCON smoke check."
    }

    if ($null -eq $server.rconPort) {
        throw "Server '$ServerId' has no RCON port configured."
    }

    $rconPort = [int]$server.rconPort
    if ($rconPort -lt 1 -or $rconPort -gt 65535) {
        throw "Server '$ServerId' has an invalid RCON port."
    }

    $credentials = @(Invoke-GoldSrcOpsApi -Method Get -Uri $credentialsUrl -Headers $headers)
    $configuredRconCredentials = @($credentials | Where-Object {
        [string]::Equals(
            [string]$_.kind,
            "RconPassword",
            [StringComparison]::OrdinalIgnoreCase) -and [bool]$_.isConfigured
    })

    if ($configuredRconCredentials.Count -eq 0) {
        throw "Server '$ServerId' has no configured RCON credential metadata."
    }

    $timestamp = [DateTimeOffset]::UtcNow.ToString("yyyyMMdd-HHmmssZ")
    $nonce = [Guid]::NewGuid().ToString("N").Substring(0, 8)
    $message = "GoldSrcOps smoke $timestamp $nonce"
    $target = "$($server.name) at $($server.host):$rconPort"

    Write-Host ""
    Write-Host "Live RCON smoke target"
    Write-Host "  Server id: $ServerId"
    Write-Host "  Name:      $($server.name)"
    Write-Host "  Endpoint:  $($server.host):$rconPort"
    Write-Host "  Command:   say $message"
    Write-Host ""

    if (-not $PSCmdlet.ShouldProcess($target, "Queue the generated SAY command")) {
        Write-Host "No command was queued."
        return
    }

    $typedServerId = ([string](Read-Host "Type the server id to confirm live dispatch")).Trim()
    if (-not [string]::Equals(
        $typedServerId,
        $ServerId.ToString(),
        [StringComparison]::OrdinalIgnoreCase)) {
        throw "Confirmation did not match server '$ServerId'. No command was queued."
    }

    $commandUrl = [Uri]"$baseUrlText/api/servers/$ServerId/commands/say"
    $body = @{ message = $message } | ConvertTo-Json -Compress
    $stopwatch = [Diagnostics.Stopwatch]::StartNew()
    $commandRequest = @{
        Method = "Post"
        Uri = $commandUrl
        Headers = $headers
        Body = $body
    }
    $execution = Invoke-GoldSrcOpsApi @commandRequest

    $commandId = [Guid]$execution.id
    if ($commandId -eq [Guid]::Empty -or
        [Guid]$execution.serverId -ne $ServerId -or
        -not [string]::Equals(
            [string]$execution.type,
            "Say",
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "The API returned an invalid command identity after queueing."
    }

    $executionUrl = [Uri]"$baseUrlText/api/commands/$commandId"
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)

    while ([string]$execution.status -in @("Pending", "Running")) {
        if ([DateTimeOffset]::UtcNow -ge $deadline) {
            throw "Timed out waiting for command '$commandId'. Inspect its status before any manual retry."
        }

        Start-Sleep -Milliseconds $PollIntervalMilliseconds
        $execution = Invoke-GoldSrcOpsApi -Method Get -Uri $executionUrl -Headers $headers
        if ([Guid]$execution.id -ne $commandId -or
            [Guid]$execution.serverId -ne $ServerId -or
            -not [string]::Equals(
                [string]$execution.type,
                "Say",
                [StringComparison]::OrdinalIgnoreCase)) {
            throw "The API returned an inconsistent command record while polling '$commandId'."
        }
    }

    $stopwatch.Stop()
    $status = [string]$execution.status
    if ($status -notin @("Succeeded", "Failed")) {
        throw "Live RCON smoke command '$commandId' returned unexpected status '$status'."
    }

    $result = [pscustomobject][ordered]@{
        CommandId = $commandId
        ServerId = $ServerId
        Status = $status
        ElapsedMilliseconds = [long][Math]::Round($stopwatch.Elapsed.TotalMilliseconds)
    }

    $result

    if ($status -ne "Succeeded") {
        throw "Live RCON smoke command '$commandId' ended with status '$status'. Inspect the command record and API logs."
    }
}
finally {
    $plainAccessToken = $null

    if ($null -ne $headers) {
        $headers.Clear()
        $headers = $null
    }

    if ($ownsAccessToken -and $null -ne $AccessToken) {
        $AccessToken.Dispose()
    }
}
