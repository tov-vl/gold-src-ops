namespace GoldSrcOps.Web.Services;

public static class ServerTrendWindows
{
    public const string LastHour = "1h";
    public const string Last6Hours = "6h";
    public const string Last24Hours = "24h";
    public const string Last7Days = "7d";

    public static bool IsSupported(string window) => window is
        LastHour or Last6Hours or Last24Hours or Last7Days;
}
