namespace GoldSrcOps.Infrastructure.Commands;

internal sealed class GoldSrcRconAuthenticationException : Exception
{
    public GoldSrcRconAuthenticationException()
        : base("GoldSrc RCON authentication failed.")
    {
    }
}

internal sealed class GoldSrcRconProtocolException : Exception
{
    public GoldSrcRconProtocolException(string message)
        : base(message)
    {
    }
}
