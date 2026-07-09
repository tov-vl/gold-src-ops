using GoldSrcOps.Application.Commands;
using GoldSrcOps.Application.Credentials;
using GoldSrcOps.Contracts.Commands;
using GoldSrcOps.Contracts.Credentials;
using GoldSrcOps.Domain.Commands;
using GoldSrcOps.Domain.Servers;
using Microsoft.AspNetCore.Http.HttpResults;

namespace GoldSrcOps.Api.Endpoints;

public static class CommandEndpoints
{
    private const int MaxMapNameLength = 128;
    private const int MaxSayMessageLength = 512;

    public static IEndpointRouteBuilder MapCommandEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var serverGroup = endpoints.MapGroup("/api/servers/{serverId:guid}")
            .WithTags("Commands");

        serverGroup.MapPut("/credentials/rcon", SetRconCredentialAsync)
            .WithName("SetServerRconCredential");

        serverGroup.MapGet("/credentials", ListCredentialsAsync)
            .WithName("ListServerCredentials");

        serverGroup.MapPost("/commands/change-map", QueueChangeMapAsync)
            .WithName("QueueChangeMapCommand");

        serverGroup.MapPost("/commands/restart", QueueRestartAsync)
            .WithName("QueueRestartCommand");

        serverGroup.MapPost("/commands/say", QueueSayAsync)
            .WithName("QueueSayCommand");

        serverGroup.MapPost("/commands/raw", QueueRawAsync)
            .WithName("QueueRawCommand");

        serverGroup.MapGet("/commands", ListServerCommandsAsync)
            .WithName("ListServerCommands");

        endpoints.MapGet("/api/commands/{commandId:guid}", GetCommandAsync)
            .WithTags("Commands")
            .WithName("GetCommandExecution");

        return endpoints;
    }

    private static async Task<Results<Ok<ServerCredentialResponse>, NotFound, ValidationProblem>> SetRconCredentialAsync(
        Guid serverId,
        SetRconCredentialRequest request,
        ServerCredentialsService credentials,
        CancellationToken cancellationToken)
    {
        var errors = Validate(request);
        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        var credential = await credentials.SetAsync(
            serverId,
            new SetServerCredentialCommand(ServerCredentialKind.RconPassword, request.SecretReference),
            cancellationToken);

        return credential is null ? TypedResults.NotFound() : TypedResults.Ok(Map(credential));
    }

    private static async Task<Results<Ok<IReadOnlyList<ServerCredentialResponse>>, NotFound>> ListCredentialsAsync(
        Guid serverId,
        ServerCredentialsService credentials,
        CancellationToken cancellationToken)
    {
        var result = await credentials.ListAsync(serverId, cancellationToken);
        return result is null
            ? TypedResults.NotFound()
            : TypedResults.Ok<IReadOnlyList<ServerCredentialResponse>>(result.Select(Map).ToArray());
    }

    private static async Task<Results<Created<CommandExecutionResponse>, NotFound, ValidationProblem, ProblemHttpResult>>
        QueueChangeMapAsync(
            Guid serverId,
            ChangeMapCommandRequest request,
            CommandExecutionService commands,
            CancellationToken cancellationToken)
    {
        var errors = Validate(request);
        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        var result = await commands.QueueAsync(
            serverId,
            new CreateCommandExecutionCommand(ServerCommandType.ChangeMap, request.Map, request.RequestedBy),
            cancellationToken);

        return MapCreateResult(result);
    }

    private static async Task<Results<Created<CommandExecutionResponse>, NotFound, ValidationProblem, ProblemHttpResult>>
        QueueRestartAsync(
            Guid serverId,
            RestartServerCommandRequest request,
            CommandExecutionService commands,
            CancellationToken cancellationToken)
    {
        var errors = ValidateRequestedBy(request.RequestedBy);
        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        var result = await commands.QueueAsync(
            serverId,
            new CreateCommandExecutionCommand(ServerCommandType.Restart, Payload: null, request.RequestedBy),
            cancellationToken);

        return MapCreateResult(result);
    }

    private static async Task<Results<Created<CommandExecutionResponse>, NotFound, ValidationProblem, ProblemHttpResult>>
        QueueSayAsync(
            Guid serverId,
            SayCommandRequest request,
            CommandExecutionService commands,
            CancellationToken cancellationToken)
    {
        var errors = Validate(request);
        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        var result = await commands.QueueAsync(
            serverId,
            new CreateCommandExecutionCommand(ServerCommandType.Say, request.Message, request.RequestedBy),
            cancellationToken);

        return MapCreateResult(result);
    }

    private static async Task<Results<Created<CommandExecutionResponse>, NotFound, ValidationProblem, ProblemHttpResult>>
        QueueRawAsync(
            Guid serverId,
            RawCommandRequest request,
            CommandExecutionService commands,
            CancellationToken cancellationToken)
    {
        var errors = Validate(request);
        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        var result = await commands.QueueAsync(
            serverId,
            new CreateCommandExecutionCommand(ServerCommandType.Raw, request.CommandText, request.RequestedBy),
            cancellationToken);

        return MapCreateResult(result);
    }

    private static async Task<Results<Ok<IReadOnlyList<CommandExecutionResponse>>, NotFound, ValidationProblem>>
        ListServerCommandsAsync(
            Guid serverId,
            int? limit,
            CommandExecutionService commands,
            CancellationToken cancellationToken)
    {
        var errors = ValidateCommandHistoryLimit(limit);
        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        var result = await commands.ListByServerAsync(serverId, limit, cancellationToken);
        return result is null
            ? TypedResults.NotFound()
            : TypedResults.Ok<IReadOnlyList<CommandExecutionResponse>>(result.Select(Map).ToArray());
    }

    private static async Task<Results<Ok<CommandExecutionResponse>, NotFound>> GetCommandAsync(
        Guid commandId,
        CommandExecutionService commands,
        CancellationToken cancellationToken)
    {
        var command = await commands.GetAsync(commandId, cancellationToken);
        return command is null ? TypedResults.NotFound() : TypedResults.Ok(Map(command));
    }

    private static Results<Created<CommandExecutionResponse>, NotFound, ValidationProblem, ProblemHttpResult>
        MapCreateResult(CommandExecutionCreateResult result)
    {
        return result.Kind switch
        {
            CommandExecutionCreateResultKind.Created =>
                TypedResults.Created($"/api/commands/{result.Command!.Id}", Map(result.Command)),
            CommandExecutionCreateResultKind.ServerNotFound => TypedResults.NotFound(),
            CommandExecutionCreateResultKind.MissingRconCredential => MissingRconCredentialProblem(),
            _ => throw new InvalidOperationException($"Unsupported command create result '{result.Kind}'.")
        };
    }

    private static ProblemHttpResult MissingRconCredentialProblem() =>
        TypedResults.Problem(
            title: "RCON credential is not configured.",
            detail: "Configure /api/servers/{serverId}/credentials/rcon before queuing server commands.",
            statusCode: StatusCodes.Status409Conflict);

    private static Dictionary<string, string[]> Validate(SetRconCredentialRequest request)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        if (string.IsNullOrWhiteSpace(request.SecretReference))
        {
            errors[nameof(request.SecretReference)] = ["SecretReference is required."];
        }
        else if (request.SecretReference.Trim().Length > ServerCredential.MaxSecretReferenceLength)
        {
            errors[nameof(request.SecretReference)] =
                [$"SecretReference must not exceed {ServerCredential.MaxSecretReferenceLength} characters."];
        }

        return errors;
    }

    private static Dictionary<string, string[]> Validate(ChangeMapCommandRequest request)
    {
        var errors = ValidateRequestedBy(request.RequestedBy);
        ValidateRequiredText(errors, nameof(request.Map), request.Map, MaxMapNameLength);

        return errors;
    }

    private static Dictionary<string, string[]> Validate(SayCommandRequest request)
    {
        var errors = ValidateRequestedBy(request.RequestedBy);
        ValidateRequiredText(errors, nameof(request.Message), request.Message, MaxSayMessageLength);

        return errors;
    }

    private static Dictionary<string, string[]> Validate(RawCommandRequest request)
    {
        var errors = ValidateRequestedBy(request.RequestedBy);
        ValidateRequiredText(errors, nameof(request.CommandText), request.CommandText, CommandExecution.MaxPayloadLength);

        return errors;
    }

    private static Dictionary<string, string[]> ValidateRequestedBy(string? requestedBy)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        if (requestedBy is not null && requestedBy.Trim().Length > CommandExecution.MaxRequestedByLength)
        {
            errors[nameof(requestedBy)] =
                [$"RequestedBy must not exceed {CommandExecution.MaxRequestedByLength} characters."];
        }

        return errors;
    }

    private static Dictionary<string, string[]> ValidateCommandHistoryLimit(int? limit)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        if (limit is <= 0 or > CommandExecutionService.MaxCommandHistoryLimit)
        {
            errors[nameof(limit)] =
                [$"Limit must be between 1 and {CommandExecutionService.MaxCommandHistoryLimit}."];
        }

        return errors;
    }

    private static void ValidateRequiredText(
        Dictionary<string, string[]> errors,
        string fieldName,
        string value,
        int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors[fieldName] = [$"{fieldName} is required."];
        }
        else if (value.Trim().Length > maxLength)
        {
            errors[fieldName] = [$"{fieldName} must not exceed {maxLength} characters."];
        }
    }

    private static ServerCredentialResponse Map(ServerCredentialDto credential) =>
        new(
            credential.Id,
            credential.ServerId,
            credential.Kind.ToString(),
            credential.IsConfigured,
            credential.CreatedAtUtc,
            credential.UpdatedAtUtc);

    private static CommandExecutionResponse Map(CommandExecutionDto command) =>
        new(
            command.Id,
            command.ServerId,
            command.Type.ToString(),
            command.Status.ToString(),
            command.Payload,
            command.RequestedBy,
            command.RequestedAtUtc,
            command.StartedAtUtc,
            command.CompletedAtUtc,
            command.ResultSummary,
            command.FailureReason);
}
