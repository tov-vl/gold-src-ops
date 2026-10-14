using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using GoldSrcOps.Contracts.Monitoring;

namespace GoldSrcOps.WebTests.Pages;

public sealed class PublicPlayerSearchIntegrationTests
{
    [Theory]
    [InlineData("/leaderboard", "Найти игрока", "Найдено записей: 2.")]
    [InlineData("/leaderboard?lang=en", "Find a player", "Records found: 2.")]
    public async Task Post_renders_escaped_duplicates_with_progress_without_redirect_or_query(string path, string title, string found)
    {
        await using var factory = new PublicDashboardWebApplicationFactory(searchEnabled: true);
        using var client = factory.CreateClient();
        using var get = await client.GetAsync(path);
        var form = WebUtility.HtmlDecode(await get.Content.ReadAsStringAsync());
        form.Should().Contain(title);
        factory.SearchHandler.RequestCount.Should().Be(0);
        using var response = await SubmitAsync(client, path, form, " <script>player</script> ");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.Location.Should().BeNull();
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        response.Headers.GetValues("Referrer-Policy").Should().ContainSingle("no-referrer");
        var html = await response.Content.ReadAsStringAsync();
        WebUtility.HtmlDecode(html).Should().Contain(found).And.Contain("75");
        html.Should().Contain("&lt;script&gt;player&lt;/script&gt;").And.NotContain("<script>player</script>");
        Regex.Count(html, "class=\"player-search-result\"", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking).Should().Be(2);
        factory.SearchHandler.LastName.Should().Be("<script>player</script>");
        factory.SearchHandler.LastMethod.Should().Be(HttpMethod.Post);
        factory.SearchHandler.LastUri!.PathAndQuery.Should().Be("/api/public/players/search");
        factory.SearchHandler.RequestCount.Should().Be(1);
    }

    [Theory]
    [InlineData("fresh", 200, "No matches")]
    [InlineData("busy", 429, "Try again manually in 7 seconds")]
    [InlineData("unavailable", 503, "temporarily unavailable")]
    [InlineData("unavailable", 404, "temporarily unavailable")]
    public async Task Empty_busy_and_missing_backend_have_explicit_states_without_rows(string state, int status, string message)
    {
        await using var factory = new PublicDashboardWebApplicationFactory(searchEnabled: true);
        factory.SearchHandler.Status = (HttpStatusCode)status;
        factory.SearchHandler.Result = new(state, string.Equals(state, "fresh", StringComparison.Ordinal) ? DateTimeOffset.UtcNow : null, false, [], string.Equals(state, "busy", StringComparison.Ordinal) ? 7 : null);
        using var client = factory.CreateClient();
        const string path = "/leaderboard?lang=en";
        using var response = await SubmitAsync(client, path, await client.GetStringAsync(path), "Example");
        var html = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        html.Should().Contain(message).And.NotContain("class=\"player-search-result\"");
        factory.SearchHandler.RequestCount.Should().Be(1);
    }

    [Theory]
    [InlineData("STEAM_0:1:123")]
    [InlineData(" ")]
    [InlineData("ИгрокИгрокИгрокИгрок")]
    [InlineData("line\nbreak")]
    public async Task Invalid_name_never_reaches_API(string name)
    {
        await using var factory = new PublicDashboardWebApplicationFactory(searchEnabled: true);
        using var client = factory.CreateClient();
        using var response = await SubmitAsync(client, "/leaderboard?lang=en", await client.GetStringAsync("/leaderboard?lang=en"), name);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Contain("data-search-state=\"invalid\"");
        factory.SearchHandler.RequestCount.Should().Be(0);
    }

    [Fact]
    public async Task Disabled_feature_has_no_form_and_rejects_post()
    {
        await using var factory = new PublicDashboardWebApplicationFactory();
        using var client = factory.CreateClient();
        (await client.GetStringAsync("/leaderboard")).Should().NotContain("id=\"player-search\"");
        using var response = await client.PostAsync("/leaderboard", new FormUrlEncodedContent([]));
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        factory.SearchHandler.RequestCount.Should().Be(0);
    }

    [Theory]
    [InlineData("missing-token", 400)]
    [InlineData("large", 413)]
    [InlineData("chunked-large", 413)]
    [InlineData("long-field", 400)]
    [InlineData("wrong-content-type", 415)]
    [InlineData("wrong-charset", 415)]
    public async Task Boundary_rejects_unsafe_forms_without_API_call(string scenario, int status)
    {
        await using var factory = new PublicDashboardWebApplicationFactory(searchEnabled: true);
        using var client = factory.CreateClient();
        using HttpContent content = scenario switch
        {
            "large" => new FormUrlEncodedContent(new Dictionary<string, string>(StringComparer.Ordinal) { ["Model.Name"] = new string('x', 4096) }),
            "chunked-large" => new StreamContent(new NonSeekableStream(Encoding.UTF8.GetBytes("Model.Name=" + new string('x', 4096))))
            { Headers = { ContentType = new("application/x-www-form-urlencoded") } },
            "long-field" => new FormUrlEncodedContent(new Dictionary<string, string>(StringComparer.Ordinal) { ["Model.Name"] = new string('x', 257) }),
            "wrong-charset" => new StringContent("Model.Name=Example", Encoding.Unicode, "application/x-www-form-urlencoded"),
            "wrong-content-type" => new StringContent("{}"),
            _ => new FormUrlEncodedContent(new Dictionary<string, string>(StringComparer.Ordinal) { ["Model.Name"] = "Example", ["_handler"] = "player-search" })
        };
        using var response = await client.PostAsync("/leaderboard", content);
        response.StatusCode.Should().Be((HttpStatusCode)status);
        factory.SearchHandler.RequestCount.Should().Be(0);
    }

    private sealed class NonSeekableStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }

    private static async Task<HttpResponseMessage> SubmitAsync(HttpClient client, string path, string html, string name)
    {
        var token = Regex.Match(html, "name=\"__RequestVerificationToken\" value=\"(?<token>[^\"]+)\"", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture | RegexOptions.NonBacktracking).Groups["token"].Value;
        token.Should().NotBeEmpty();
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["__RequestVerificationToken"] = WebUtility.HtmlDecode(token),
            ["_handler"] = "player-search",
            ["Model.Name"] = name
        });
        return await client.PostAsync(path, content);
    }
}
