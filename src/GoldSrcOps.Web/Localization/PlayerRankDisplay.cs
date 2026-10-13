namespace GoldSrcOps.Web.Localization;

public static class PlayerRankDisplay
{
    public static IReadOnlyList<(string NameKey, int Kills)> Ranks { get; } = Array.AsReadOnly<(string, int)>(
        [("Recruit", 0), ("Fighter", 25), ("Veteran", 100), ("Elite", 250), ("Legend", 500)]);

    public static string Name(PublicPageText text, int rank) =>
        text[rank >= 0 && rank < Ranks.Count ? Ranks[rank].NameKey : "NoData"];

    public static string? Progress(PublicPageText text, int rank, int kills)
    {
        if (rank < 0 || rank >= Ranks.Count || kills < Ranks[rank].Kills)
            return null;
        if (rank == Ranks.Count - 1)
            return text["HighestRank"];
        var next = Ranks[rank + 1];
        return kills < next.Kills ? text.Format("KillsTo" + next.NameKey, next.Kills - kills) : null;
    }
}
