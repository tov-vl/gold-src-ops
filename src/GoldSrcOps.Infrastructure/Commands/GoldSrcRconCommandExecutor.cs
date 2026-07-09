using System.Net.Sockets;
using GoldSrcOps.Application.Commands;
using Microsoft.Extensions.Logging;

namespace GoldSrcOps.Infrastructure.Commands;

internal sealed partial class GoldSrcRconCommandExecutor : IRconCommandExecutor
{
    private readonly ISecretReferenceResolver _secretResolver;
    private readonly IGoldSrcRconClient _rconClient;
    private readonly GoldSrcRconOptions _options;
    private readonly ILogger<GoldSrcRconCommandExecutor> _logger;

    public GoldSrcRconCommandExecutor(
        ISecretReferenceResolver secretResolver,
        IGoldSrcRconClient rconClient,
        GoldSrcRconOptions options,
        ILogger<GoldSrcRconCommandExecutor> logger)
    {
        _secretResolver = secretResolver;
        _rconClient = rconClient;
        _options = options;
        _logger = logger;
    }

    public async Task<RconCommandExecutionResult> ExecuteAsync(
        RconCommandExecutionRequest request,
        CancellationToken cancellationToken)
    {
        var resolvedSecret = await _secretResolver.ResolveAsync(request.CredentialSecretReference, cancellationToken);
        if (resolvedSecret.Kind == SecretReferenceResolutionResultKind.Unsupported)
        {
            return RconCommandExecutionResult.Failed("RCON credential secret reference is not supported.");
        }

        if (resolvedSecret.Kind != SecretReferenceResolutionResultKind.Resolved ||
            string.IsNullOrWhiteSpace(resolvedSecret.Secret))
        {
            return RconCommandExecutionResult.Failed("RCON credential secret could not be resolved.");
        }

        try
        {
            var response = await _rconClient.ExecuteAsync(
                new GoldSrcRconRequest(
                    request.Host,
                    request.Port,
                    resolvedSecret.Secret,
                    request.CommandText,
                    _options.Timeout),
                cancellationToken);

            return RconCommandExecutionResult.Succeeded(NormalizeResult(response, resolvedSecret.Secret));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TimeoutException)
        {
            return RconCommandExecutionResult.TimedOut("RCON command timed out.");
        }
        catch (GoldSrcRconAuthenticationException)
        {
            return RconCommandExecutionResult.AuthenticationFailed("RCON authentication failed.");
        }
        catch (GoldSrcRconProtocolException exception)
        {
            LogProtocolError(_logger, exception, request.CommandId, request.ServerId);

            return RconCommandExecutionResult.Failed("RCON protocol error.");
        }
        catch (SocketException exception)
        {
            LogSocketError(_logger, exception, request.CommandId, request.ServerId);

            return RconCommandExecutionResult.Failed("RCON command failed.");
        }
    }

    private string NormalizeResult(string? value, string secret)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "RCON command completed.";
        }

        var sanitized = value.Trim().Replace(secret, "[credential]", StringComparison.Ordinal);
        return sanitized.Length <= _options.MaxResponseLength
            ? sanitized
            : sanitized[.._options.MaxResponseLength];
    }

    [LoggerMessage(
        EventId = 1001,
        Level = LogLevel.Warning,
        Message = "GoldSrc RCON protocol error while dispatching command {CommandId} for server {ServerId}.")]
    private static partial void LogProtocolError(
        ILogger logger,
        Exception exception,
        Guid commandId,
        Guid serverId);

    [LoggerMessage(
        EventId = 1002,
        Level = LogLevel.Warning,
        Message = "GoldSrc RCON socket error while dispatching command {CommandId} for server {ServerId}.")]
    private static partial void LogSocketError(
        ILogger logger,
        Exception exception,
        Guid commandId,
        Guid serverId);
}
