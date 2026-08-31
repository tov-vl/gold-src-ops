#Requires -Version 7.0

Set-StrictMode -Version Latest

$script:PostgresBackupArchiveName = "goldsrcops-postgresql.dump"
$script:PostgresBackupPendingTag = "goldsrcops-postgresql-pending"
$script:PostgresBackupRecoverableTag = "goldsrcops-postgresql-recoverable"

function Assert-BackupCondition {
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

function Read-DeploymentEnvironment {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    $resolvedPath = (Resolve-Path -LiteralPath $Path).Path
    $values = [Collections.Generic.Dictionary[string, string]]::new(
        [StringComparer]::Ordinal)

    foreach ($line in [IO.File]::ReadAllLines($resolvedPath)) {
        $trimmed = $line.Trim()
        if ([string]::IsNullOrWhiteSpace($trimmed) -or $trimmed.StartsWith('#')) {
            continue
        }

        $separator = $trimmed.IndexOf('=')
        Assert-BackupCondition `
            -Condition ($separator -gt 0) `
            -Message "Deployment environment entries must use NAME=VALUE syntax."

        $name = $trimmed.Substring(0, $separator).Trim()
        $value = $trimmed.Substring($separator + 1).Trim()
        Assert-BackupCondition `
            -Condition ($name -match '\A[A-Z][A-Z0-9_]*\z') `
            -Message "Deployment environment contains an invalid setting name."
        Assert-BackupCondition `
            -Condition (-not $values.ContainsKey($name)) `
            -Message "Deployment environment contains a duplicate '$name' setting."

        $values.Add($name, $value)
    }

    return [pscustomobject]@{
        Path = $resolvedPath
        Values = $values
    }
}

function Get-RequiredDeploymentValue {
    param(
        [Parameter(Mandatory = $true)]
        [Collections.Generic.Dictionary[string, string]]$Values,

        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    $value = $null
    Assert-BackupCondition `
        -Condition ($Values.TryGetValue($Name, [ref]$value) -and
            -not [string]::IsNullOrWhiteSpace($value)) `
        -Message "Deployment environment is missing '$Name'."

    return $value
}

function Test-PathInsideRepository {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    $repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../.."))
    $fullPath = [IO.Path]::GetFullPath($Path)
    $rootWithSeparator = $repoRoot.TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar

    return $fullPath.StartsWith($rootWithSeparator, [StringComparison]::OrdinalIgnoreCase)
}

function Assert-BackupSecretFile {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$Name,

        [switch]$AllowLocalTestResources
    )

    $resolvedPath = (Resolve-Path -LiteralPath $Path).Path
    Assert-BackupCondition `
        -Condition (Test-Path -LiteralPath $resolvedPath -PathType Leaf) `
        -Message "$Name file does not exist."
    Assert-BackupCondition `
        -Condition ((Get-Item -LiteralPath $resolvedPath).Length -gt 0) `
        -Message "$Name file is empty."

    if (-not $AllowLocalTestResources) {
        Assert-BackupCondition `
            -Condition (-not (Test-PathInsideRepository -Path $resolvedPath)) `
            -Message "$Name file must live outside the repository."
    }

    if (-not $IsWindows) {
        $mode = [IO.File]::GetUnixFileMode($resolvedPath)
        $forbiddenMode =
            [IO.UnixFileMode]::GroupRead -bor
            [IO.UnixFileMode]::GroupWrite -bor
            [IO.UnixFileMode]::GroupExecute -bor
            [IO.UnixFileMode]::OtherRead -bor
            [IO.UnixFileMode]::OtherWrite -bor
            [IO.UnixFileMode]::OtherExecute

        Assert-BackupCondition `
            -Condition (($mode -band $forbiddenMode) -eq 0) `
            -Message "$Name file must not be accessible by group or other users."
    }

    return $resolvedPath
}

function Assert-ResticEnvironmentFile {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [switch]$AllowLocalTestResources
    )

    $resolvedPath = Assert-BackupSecretFile `
        -Path $Path `
        -Name "Restic backend environment" `
        -AllowLocalTestResources:$AllowLocalTestResources
    $forbiddenNames = @(
        "RESTIC_PASSWORD",
        "RESTIC_PASSWORD_COMMAND",
        "RESTIC_PASSWORD_FILE",
        "RESTIC_REPOSITORY",
        "RESTIC_REPOSITORY_FILE"
    )
    $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)

    foreach ($line in [IO.File]::ReadAllLines($resolvedPath)) {
        $trimmed = $line.Trim()
        if ([string]::IsNullOrWhiteSpace($trimmed) -or $trimmed.StartsWith('#')) {
            continue
        }

        $separator = $trimmed.IndexOf('=')
        Assert-BackupCondition `
            -Condition ($separator -gt 0) `
            -Message "Restic backend environment entries must use NAME=VALUE syntax."

        $name = $trimmed.Substring(0, $separator).Trim()
        Assert-BackupCondition `
            -Condition ($name -match '\A[A-Z][A-Z0-9_]*\z') `
            -Message "Restic backend environment contains an invalid setting name."
        Assert-BackupCondition `
            -Condition ($names.Add($name)) `
            -Message "Restic backend environment contains a duplicate '$name' setting."
        Assert-BackupCondition `
            -Condition ($name -notin $forbiddenNames) `
            -Message "Restic backend environment must not override repository or password settings."
    }

    if (-not $AllowLocalTestResources) {
        Assert-BackupCondition `
            -Condition ($names.Contains("AWS_ACCESS_KEY_ID") -and
                $names.Contains("AWS_SECRET_ACCESS_KEY")) `
            -Message "The S3 backup backend requires scoped AWS_ACCESS_KEY_ID and AWS_SECRET_ACCESS_KEY entries."
    }

    return $resolvedPath
}

function Assert-ImmutableBackupImage {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Image,

        [Parameter(Mandatory = $true)]
        [string]$Name,

        [switch]$AllowLocalTestResources
    )

    if ($AllowLocalTestResources) {
        Assert-BackupCondition `
            -Condition (-not [string]::IsNullOrWhiteSpace($Image)) `
            -Message "$Name image must not be empty."
        return
    }

    Assert-BackupCondition `
        -Condition ($Image -match '\A[^@\s]+@sha256:[0-9a-f]{64}\z') `
        -Message "$Name image must use an immutable sha256 reference."
}

function Assert-OffHostResticRepository {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Repository,

        [string]$ControlPlaneHost,

        [switch]$AllowLocalTestResources
    )

    if ($AllowLocalTestResources) {
        return
    }

    Assert-BackupCondition `
        -Condition ($Repository.StartsWith("s3:https://", [StringComparison]::OrdinalIgnoreCase)) `
        -Message "The production backup repository must use an S3-compatible HTTPS endpoint."

    $endpointAndPath = $Repository.Substring(3)
    $endpoint = $null
    Assert-BackupCondition `
        -Condition ([Uri]::TryCreate($endpointAndPath, [UriKind]::Absolute, [ref]$endpoint) -and
            $endpoint.Scheme -eq [Uri]::UriSchemeHttps -and
            [string]::IsNullOrEmpty($endpoint.UserInfo)) `
        -Message "The backup repository must be an absolute S3 HTTPS URI without embedded credentials."
    Assert-BackupCondition `
        -Condition ($endpoint.Host -notin @("localhost", "127.0.0.1", "::1") -and
            $endpoint.Host -notmatch '(?i)(^|\.)example\.(com|net|org)$') `
        -Message "The backup repository still uses a local or placeholder endpoint."
    if (-not [string]::IsNullOrWhiteSpace($ControlPlaneHost)) {
        Assert-BackupCondition `
            -Condition (-not $endpoint.Host.Equals(
                    $ControlPlaneHost,
                    [StringComparison]::OrdinalIgnoreCase)) `
            -Message "The backup repository must not resolve to the control-plane hostname."
    }
    Assert-BackupCondition `
        -Condition ($endpoint.AbsolutePath.Trim('/').Length -gt 0) `
        -Message "The backup repository must include an off-host bucket and repository path."
}

function Get-PostgresBackupConfiguration {
    param(
        [Parameter(Mandatory = $true)]
        [string]$EnvironmentFile,

        [string]$LocalRepositoryPath,

        [switch]$AllowLocalTestResources
    )

    Assert-BackupCondition `
        -Condition ($AllowLocalTestResources -or [string]::IsNullOrWhiteSpace($LocalRepositoryPath)) `
        -Message "Local backup repositories are allowed only for isolated tests."

    $environment = Read-DeploymentEnvironment -Path $EnvironmentFile
    $values = $environment.Values
    $resticImage = Get-RequiredDeploymentValue -Values $values -Name "GOLDSRCOPS_RESTIC_IMAGE"
    $repository = Get-RequiredDeploymentValue -Values $values -Name "GOLDSRCOPS_BACKUP_REPOSITORY"
    $backupHost = Get-RequiredDeploymentValue -Values $values -Name "GOLDSRCOPS_BACKUP_HOST"
    $controlPlaneHost = if ($AllowLocalTestResources) {
        $null
    }
    else {
        Get-RequiredDeploymentValue -Values $values -Name "GOLDSRCOPS_HOSTNAME"
    }
    $passwordFile = Get-RequiredDeploymentValue -Values $values -Name "GOLDSRCOPS_RESTIC_PASSWORD_FILE"
    $backendEnvironmentFile = Get-RequiredDeploymentValue `
        -Values $values `
        -Name "GOLDSRCOPS_RESTIC_ENVIRONMENT_FILE"

    Assert-ImmutableBackupImage `
        -Image $resticImage `
        -Name "Restic" `
        -AllowLocalTestResources:$AllowLocalTestResources
    Assert-OffHostResticRepository `
        -Repository $repository `
        -ControlPlaneHost $controlPlaneHost `
        -AllowLocalTestResources:$AllowLocalTestResources
    Assert-BackupCondition `
        -Condition ($backupHost -match '\A[A-Za-z0-9](?:[A-Za-z0-9.-]{0,251}[A-Za-z0-9])?\z') `
        -Message "GOLDSRCOPS_BACKUP_HOST must be a stable, bounded host identifier."

    $resolvedPasswordFile = Assert-BackupSecretFile `
        -Path $passwordFile `
        -Name "Restic password" `
        -AllowLocalTestResources:$AllowLocalTestResources
    $resolvedBackendEnvironmentFile = Assert-ResticEnvironmentFile `
        -Path $backendEnvironmentFile `
        -AllowLocalTestResources:$AllowLocalTestResources

    $resolvedLocalRepository = $null
    if (-not [string]::IsNullOrWhiteSpace($LocalRepositoryPath)) {
        $resolvedLocalRepository = (Resolve-Path -LiteralPath $LocalRepositoryPath).Path
        Assert-BackupCondition `
            -Condition (Test-Path -LiteralPath $resolvedLocalRepository -PathType Container) `
            -Message "The local test repository directory does not exist."
        $repository = "/repository"
    }

    return [pscustomobject]@{
        AllowLocalTestResources = [bool]$AllowLocalTestResources
        BackupHost = $backupHost
        EnvironmentFile = $environment.Path
        LocalRepositoryPath = $resolvedLocalRepository
        Repository = $repository
        ResticEnvironmentFile = $resolvedBackendEnvironmentFile
        ResticImage = $resticImage
        ResticPasswordFile = $resolvedPasswordFile
        Values = $values
    }
}

function New-NativeProcessStartInfo {
    param(
        [Parameter(Mandatory = $true)]
        [string]$FilePath,

        [Parameter(Mandatory = $true)]
        [string[]]$Arguments,

        [switch]$RedirectInput,

        [switch]$RedirectOutput
    )

    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $FilePath
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardInput = [bool]$RedirectInput
    $startInfo.RedirectStandardOutput = [bool]$RedirectOutput
    $startInfo.RedirectStandardError = $true

    foreach ($argument in $Arguments) {
        [void]$startInfo.ArgumentList.Add($argument)
    }

    return $startInfo
}

function Invoke-NativeCapture {
    param(
        [Parameter(Mandatory = $true)]
        [string]$FilePath,

        [Parameter(Mandatory = $true)]
        [string[]]$Arguments,

        [switch]$AllowFailure
    )

    $process = [Diagnostics.Process]::new()
    $process.StartInfo = New-NativeProcessStartInfo `
        -FilePath $FilePath `
        -Arguments $Arguments `
        -RedirectOutput

    try {
        Assert-BackupCondition -Condition $process.Start() -Message "Could not start '$FilePath'."
        $outputTask = $process.StandardOutput.ReadToEndAsync()
        $errorTask = $process.StandardError.ReadToEndAsync()
        $process.WaitForExit()
        $output = $outputTask.GetAwaiter().GetResult().Trim()
        $errorOutput = $errorTask.GetAwaiter().GetResult().Trim()

        if (-not $AllowFailure -and $process.ExitCode -ne 0) {
            $detail = if ([string]::IsNullOrWhiteSpace($errorOutput)) {
                "No diagnostic output was returned."
            }
            else {
                $errorOutput
            }

            throw "Command '$FilePath' failed with exit code $($process.ExitCode). $detail"
        }

        return [pscustomobject]@{
            Error = $errorOutput
            ExitCode = $process.ExitCode
            Output = $output
        }
    }
    finally {
        $process.Dispose()
    }
}

function Get-CurrentUnixDockerUser {
    $userId = (Invoke-NativeCapture -FilePath "id" -Arguments @("-u")).Output
    $groupId = (Invoke-NativeCapture -FilePath "id" -Arguments @("-g")).Output

    Assert-BackupCondition `
        -Condition ($userId -match '\A[0-9]+\z' -and $groupId -match '\A[0-9]+\z') `
        -Message "Could not resolve the current Unix user for the restic container."

    return "${userId}:${groupId}"
}

function Invoke-NativeStreamPipeline {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ProducerFilePath,

        [Parameter(Mandatory = $true)]
        [string[]]$ProducerArguments,

        [Parameter(Mandatory = $true)]
        [string]$ConsumerFilePath,

        [Parameter(Mandatory = $true)]
        [string[]]$ConsumerArguments
    )

    $producer = [Diagnostics.Process]::new()
    $consumer = [Diagnostics.Process]::new()
    $producerStarted = $false
    $consumerStarted = $false
    $streamError = ""
    $producer.StartInfo = New-NativeProcessStartInfo `
        -FilePath $ProducerFilePath `
        -Arguments $ProducerArguments `
        -RedirectOutput
    $consumer.StartInfo = New-NativeProcessStartInfo `
        -FilePath $ConsumerFilePath `
        -Arguments $ConsumerArguments `
        -RedirectInput `
        -RedirectOutput

    try {
        Assert-BackupCondition `
            -Condition $consumer.Start() `
            -Message "Could not start pipeline consumer '$ConsumerFilePath'."
        $consumerStarted = $true
        $consumerOutputTask = $consumer.StandardOutput.ReadToEndAsync()
        $consumerErrorTask = $consumer.StandardError.ReadToEndAsync()

        Assert-BackupCondition `
            -Condition $producer.Start() `
            -Message "Could not start pipeline producer '$ProducerFilePath'."
        $producerStarted = $true
        $producerErrorTask = $producer.StandardError.ReadToEndAsync()

        try {
            $producer.StandardOutput.BaseStream.CopyTo($consumer.StandardInput.BaseStream)
        }
        catch {
            $streamError = $_.Exception.Message
            if ($producerStarted -and -not $producer.HasExited) {
                $producer.Kill($true)
            }
        }
        finally {
            try {
                $consumer.StandardInput.Close()
            }
            catch {
                if ([string]::IsNullOrWhiteSpace($streamError)) {
                    $streamError = $_.Exception.Message
                }
            }
        }

        $producer.WaitForExit()
        $consumer.WaitForExit()

        return [pscustomobject]@{
            ConsumerError = $consumerErrorTask.GetAwaiter().GetResult().Trim()
            ConsumerExitCode = $consumer.ExitCode
            ConsumerOutput = $consumerOutputTask.GetAwaiter().GetResult().Trim()
            ProducerError = $producerErrorTask.GetAwaiter().GetResult().Trim()
            ProducerExitCode = $producer.ExitCode
            StreamError = $streamError
        }
    }
    catch {
        if ($producerStarted -and -not $producer.HasExited) {
            $producer.Kill($true)
        }

        if ($consumerStarted -and -not $consumer.HasExited) {
            $consumer.Kill($true)
        }

        throw
    }
    finally {
        $producer.Dispose()
        $consumer.Dispose()
    }
}

function New-ResticDockerArguments {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Configuration,

        [Parameter(Mandatory = $true)]
        [string]$ContainerName,

        [Parameter(Mandatory = $true)]
        [string[]]$ResticArguments,

        [switch]$Interactive
    )

    $arguments = [Collections.Generic.List[string]]::new()
    $arguments.Add("run")
    $arguments.Add("--rm")
    $arguments.Add("--name")
    $arguments.Add($ContainerName)
    if (-not $IsWindows) {
        $arguments.Add("--user")
        $arguments.Add((Get-CurrentUnixDockerUser))
    }
    $arguments.Add("--read-only")
    $arguments.Add("--cap-drop")
    $arguments.Add("ALL")
    $arguments.Add("--security-opt")
    $arguments.Add("no-new-privileges")
    $arguments.Add("--tmpfs")
    $arguments.Add("/tmp:rw,noexec,nosuid,size=64m")
    $arguments.Add("--env-file")
    $arguments.Add($Configuration.ResticEnvironmentFile)
    $arguments.Add("--env")
    $arguments.Add("RESTIC_REPOSITORY=$($Configuration.Repository)")
    $arguments.Add("--env")
    $arguments.Add("RESTIC_PASSWORD_FILE=/run/secrets/restic-password")
    $arguments.Add("--env")
    $arguments.Add("RESTIC_CACHE_DIR=/tmp/restic-cache")
    $arguments.Add("--env")
    $arguments.Add("RESTIC_HOST=$($Configuration.BackupHost)")
    $arguments.Add("--mount")
    $arguments.Add(
        "type=bind,source=$($Configuration.ResticPasswordFile),target=/run/secrets/restic-password,readonly")

    if ($Interactive) {
        $arguments.Add("--interactive")
    }

    if ($null -ne $Configuration.LocalRepositoryPath) {
        $arguments.Add("--network")
        $arguments.Add("none")
        $arguments.Add("--mount")
        $arguments.Add(
            "type=bind,source=$($Configuration.LocalRepositoryPath),target=/repository")
    }
    else {
        $arguments.Add("--network")
        $arguments.Add("bridge")
    }

    $arguments.Add($Configuration.ResticImage)
    foreach ($argument in $ResticArguments) {
        $arguments.Add($argument)
    }

    return $arguments.ToArray()
}

function Invoke-ResticCapture {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Configuration,

        [Parameter(Mandatory = $true)]
        [string[]]$Arguments,

        [switch]$AllowFailure
    )

    $containerName = "goldsrcops-restic-$([Guid]::NewGuid().ToString('N').Substring(0, 12))"
    $dockerArguments = New-ResticDockerArguments `
        -Configuration $Configuration `
        -ContainerName $containerName `
        -ResticArguments $Arguments

    return Invoke-NativeCapture `
        -FilePath "docker" `
        -Arguments $dockerArguments `
        -AllowFailure:$AllowFailure
}

function Get-BackupSnapshotId {
    param(
        [Parameter(Mandatory = $true)]
        [string]$JsonLines
    )

    $snapshotId = $null
    foreach ($line in $JsonLines -split '\r?\n') {
        if ([string]::IsNullOrWhiteSpace($line)) {
            continue
        }

        try {
            $message = $line | ConvertFrom-Json -Depth 20
            if ($message.message_type -eq "summary" -and
                -not [string]::IsNullOrWhiteSpace([string]$message.snapshot_id)) {
                $snapshotId = [string]$message.snapshot_id
            }
        }
        catch {
            continue
        }
    }

    return $snapshotId
}

function Get-RetaggedBackupSnapshotId {
    param(
        [Parameter(Mandatory = $true)]
        [string]$JsonLines,

        [Parameter(Mandatory = $true)]
        [string]$OriginalSnapshotId
    )

    $snapshotId = $null
    foreach ($line in $JsonLines -split '\r?\n') {
        if ([string]::IsNullOrWhiteSpace($line)) {
            continue
        }

        try {
            $message = $line | ConvertFrom-Json -Depth 20
            if ($message.message_type -eq "changed" -and
                $message.old_snapshot_id -eq $OriginalSnapshotId -and
                -not [string]::IsNullOrWhiteSpace([string]$message.new_snapshot_id)) {
                $snapshotId = [string]$message.new_snapshot_id
            }
        }
        catch {
            continue
        }
    }

    return $snapshotId
}

function Get-RecoverableBackupSnapshot {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Configuration,

        [string]$SnapshotId
    )

    $arguments = [Collections.Generic.List[string]]::new()
    $arguments.Add("snapshots")
    $arguments.Add("--json")
    $arguments.Add("--host")
    $arguments.Add($Configuration.BackupHost)
    $arguments.Add("--tag")
    $arguments.Add($script:PostgresBackupRecoverableTag)
    if (-not [string]::IsNullOrWhiteSpace($SnapshotId)) {
        Assert-BackupCondition `
            -Condition ($SnapshotId -match '\A[0-9a-f]{8,64}\z') `
            -Message "SnapshotId must be a hexadecimal restic snapshot identifier."
        $arguments.Add($SnapshotId)
    }

    $result = Invoke-ResticCapture -Configuration $Configuration -Arguments $arguments.ToArray()
    $snapshots = @($result.Output | ConvertFrom-Json -Depth 30)
    Assert-BackupCondition `
        -Condition ($snapshots.Count -gt 0) `
        -Message "No recoverable PostgreSQL backup snapshot matched the request."

    if (-not [string]::IsNullOrWhiteSpace($SnapshotId)) {
        Assert-BackupCondition `
            -Condition ($snapshots.Count -eq 1) `
            -Message "SnapshotId must identify exactly one recoverable PostgreSQL backup."
        return $snapshots[0]
    }

    return $snapshots |
        Sort-Object { [DateTimeOffset]::Parse($_.time, [Globalization.CultureInfo]::InvariantCulture) } -Descending |
        Select-Object -First 1
}

function Enter-PostgresBackupLock {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    $fullPath = [IO.Path]::GetFullPath($Path)
    $directory = Split-Path -Parent $fullPath
    if (-not (Test-Path -LiteralPath $directory -PathType Container)) {
        [void](New-Item -ItemType Directory -Path $directory)
        if (-not $IsWindows) {
            [IO.File]::SetUnixFileMode(
                $directory,
                [IO.UnixFileMode]::UserRead -bor
                [IO.UnixFileMode]::UserWrite -bor
                [IO.UnixFileMode]::UserExecute)
        }
    }

    try {
        return [IO.File]::Open(
            $fullPath,
            [IO.FileMode]::OpenOrCreate,
            [IO.FileAccess]::ReadWrite,
            [IO.FileShare]::None)
    }
    catch [IO.IOException] {
        throw "Another PostgreSQL backup or restore action already holds '$fullPath'."
    }
}

function Get-DefaultPostgresBackupLockFile {
    if ($IsWindows) {
        return Join-Path ([IO.Path]::GetTempPath()) "goldsrcops-postgres-recovery.lock"
    }

    return "/var/lock/goldsrcops-postgres-recovery.lock"
}

function Write-BackupEvidence {
    param(
        [Parameter(Mandatory = $true)]
        [hashtable]$Evidence,

        [string]$Path
    )

    if ([string]::IsNullOrWhiteSpace($Path)) {
        return
    }

    $fullPath = [IO.Path]::GetFullPath($Path)
    Assert-BackupCondition `
        -Condition (-not (Test-PathInsideRepository -Path $fullPath)) `
        -Message "Operational evidence must be written outside the repository."

    $directory = Split-Path -Parent $fullPath
    if (-not (Test-Path -LiteralPath $directory -PathType Container)) {
        [void](New-Item -ItemType Directory -Path $directory)
    }

    $temporaryPath = "$fullPath.$([Guid]::NewGuid().ToString('N')).tmp"
    try {
        $Evidence | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $temporaryPath -Encoding utf8NoBOM
        if (-not $IsWindows) {
            [IO.File]::SetUnixFileMode(
                $temporaryPath,
                [IO.UnixFileMode]::UserRead -bor [IO.UnixFileMode]::UserWrite)
        }

        Move-Item -LiteralPath $temporaryPath -Destination $fullPath -Force
    }
    finally {
        if (Test-Path -LiteralPath $temporaryPath) {
            Remove-Item -LiteralPath $temporaryPath -Force
        }
    }
}
