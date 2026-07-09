namespace GoldSrcOps.Contracts.Commands;

public sealed record SayCommandRequest(
    string Message,
    string? RequestedBy);
