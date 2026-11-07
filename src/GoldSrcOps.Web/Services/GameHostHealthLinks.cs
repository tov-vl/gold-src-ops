namespace GoldSrcOps.Web.Services;

internal sealed class GameHostHealthLinks
{
    public IReadOnlyList<KeyValuePair<string, Uri>> Dashboards { get; }

    public Uri? WarningDashboard(string component)
    {
        var label = component switch
        {
            "game-event-agent" => "Agent warnings",
            "player-backup" => "Backup",
            "game-host-storage" => "Disk / inode",
            "game-host-resources" => "RAM / CPU",
            _ => null
        };
        return Dashboards.FirstOrDefault(pair => string.Equals(pair.Key, label, StringComparison.Ordinal)).Value;
    }

    public GameHostHealthLinks(string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            Dashboards = [];
            return;
        }

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ||
            (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal) && !(string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal) && uri.IsLoopback)) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new InvalidOperationException("Health dashboard links require HTTPS or a loopback HTTP tunnel, without credentials, query or fragment.");
        }

        var origin = new Uri(uri.AbsoluteUri.TrimEnd('/') + "/");
        Dashboards = [.. new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Overview"] = "goldsrcops-game-host-overview",
            ["Agent / queue"] = "goldsrcops-game-event-agent",
            ["Agent warnings"] = "goldsrcops-game-event-agent-alerts",
            ["Backup"] = "goldsrcops-player-backup",
            ["Disk / inode"] = "goldsrcops-game-host-storage",
            ["RAM / CPU"] = "goldsrcops-game-host-resources"
        }.Select(pair => KeyValuePair.Create(pair.Key, new Uri(origin, "d/" + pair.Value)))];
    }
}
