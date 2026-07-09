namespace GoldSrcOps.Infrastructure.Commands;

internal sealed record GoldSrcRconRequest(
    string Host,
    int Port,
    string Password,
    string CommandText,
    TimeSpan Timeout);
