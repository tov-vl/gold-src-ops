using AwesomeAssertions;
using GoldSrcOps.Web.Localization;

namespace GoldSrcOps.WebTests.Pages;

public sealed class PlayerRankDisplayTests
{
    [Theory]
    [InlineData(0, 0, "До Бойца: 25", "To Fighter: 25")]
    [InlineData(0, 24, "До Бойца: 1", "To Fighter: 1")]
    [InlineData(1, 25, "До Ветерана: 75", "To Veteran: 75")]
    [InlineData(1, 37, "До Ветерана: 63", "To Veteran: 63")]
    [InlineData(1, 99, "До Ветерана: 1", "To Veteran: 1")]
    [InlineData(2, 100, "До Элиты: 150", "To Elite: 150")]
    [InlineData(2, 249, "До Элиты: 1", "To Elite: 1")]
    [InlineData(3, 250, "До Легенды: 250", "To Legend: 250")]
    [InlineData(3, 499, "До Легенды: 1", "To Legend: 1")]
    [InlineData(4, 500, "Высший ранг", "Highest rank")]
    [InlineData(4, int.MaxValue, "Высший ранг", "Highest rank")]
    public void Progress_preserves_rank_boundaries_in_both_languages(int rank, int kills, string russian, string english)
    {
        PlayerRankDisplay.Progress(PublicPageText.For("ru"), rank, kills).Should().Be(russian);
        PlayerRankDisplay.Progress(PublicPageText.For("en"), rank, kills).Should().Be(english);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(5, 500)]
    [InlineData(0, -1)]
    [InlineData(0, 25)]
    [InlineData(1, 24)]
    [InlineData(1, int.MaxValue)]
    [InlineData(4, 499)]
    public void Unknown_or_inconsistent_snapshot_never_invents_progress(int rank, int kills)
    {
        PlayerRankDisplay.Progress(PublicPageText.For("ru"), rank, kills).Should().BeNull();
    }
}
