namespace GoldSrcOps.AlertReceiver.Configuration;

internal sealed class AlertReceiverOptions
{
    public const string SectionName = "Receiver";
    public const int MaxAuthorizationLength = 512;

    public ReceiverMode Mode { get; init; } = ReceiverMode.CatchUp;

    public string Authorization { get; init; } = string.Empty;
}

internal enum ReceiverMode
{
    CatchUp,
    Live,
}
