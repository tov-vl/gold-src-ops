using System.Net;
using AwesomeAssertions;
using GoldSrcOps.WebTests.Pages;
using Microsoft.Playwright;

namespace GoldSrcOps.WebTests.Browser;

public sealed partial class BrowserTokenBoundaryTests
{
    [BrowserFact]
    public async Task Player_guide_navigation_copy_and_layout_work_without_sign_in()
    {
        await using var factory = new BrowserTokenBoundaryWebApplicationFactory();
        factory.StartServer();
        var baseAddress = factory.ClientOptions.BaseAddress;

        foreach (var viewport in new[]
                 {
                     new ViewportSize { Width = 1440, Height = 900 },
                     new ViewportSize { Width = 1024, Height = 768 },
                     new ViewportSize { Width = 390, Height = 844 },
                     new ViewportSize { Width = 320, Height = 740 }
                 })
        {
            await Page.SetViewportSizeAsync(viewport.Width, viewport.Height);
            await Page.GotoAsync(baseAddress.AbsoluteUri);
            await Page.Locator(".primary-nav a[href='/play']").ClickAsync();
            await Expect(Page.Locator(".play-page")).ToBeVisibleAsync();
            await Expect(Page.Locator(".primary-nav a[href='/play']"))
                .ToHaveAttributeAsync("aria-current", "page");
            (await Page.Locator(".primary-nav a[href='/play']").EvaluateAsync<string>(
                "element => getComputedStyle(element).textDecorationLine"))
                .Should().Be("none");
            (await Page.Locator(".join-panel__steam").GetAttributeAsync("href"))
                .Should().Be("steam://connect/play.example.test:27015");
            await Page.EvaluateAsync(
                "() => { navigator.clipboard.writeText = async value => { window.__copiedCommand = value; }; }");
            await Page.Locator(".join-panel__copy").ClickAsync();
            await Expect(Page.Locator("#play-copy-status"))
                .ToHaveTextAsync("Команда подключения скопирована.");
            (await Page.EvaluateAsync<string>("window.__copiedCommand"))
                .Should().Be("connect play.example.test:27015");
            await Page.EvaluateAsync(
                "() => { navigator.clipboard.writeText = async () => { throw new Error('fixture-denied'); }; }");
            await Page.Locator(".join-panel__copy").ClickAsync();
            await Expect(Page.Locator("#play-copy-status"))
                .ToHaveTextAsync("Не удалось скопировать. Выдели команду и скопируй вручную.");
            (await Page.EvaluateAsync<bool>(
                "document.documentElement.scrollWidth > document.documentElement.clientWidth"))
                .Should().BeFalse();
            AssertTokenFree(await Page.ContentAsync());
            await AssertBrowserStorageIsEmptyAsync();
            await CaptureScreenshotIfRequestedAsync("player-guide", viewport);
        }
    }

    [BrowserFact]
    public async Task Player_guide_unavailable_states_keep_instructions_and_fit_mobile()
    {
        await Page.SetViewportSizeAsync(390, 844);
        foreach (var state in new[] { "offline", "unknown", "missing" })
        {
            await using var factory = new BrowserTokenBoundaryWebApplicationFactory(
                serverJoinResponse: PlayerGuideIntegrationTests.Server(state),
                serverJoinFailureStatus: string.Equals(state, "missing", StringComparison.Ordinal) ? HttpStatusCode.ServiceUnavailable : null);
            factory.StartServer();
            await Page.GotoAsync(new Uri(factory.ClientOptions.BaseAddress, "/play").AbsoluteUri);

            await Expect(Page.Locator("#first-steps-title")).ToHaveTextAsync("Три шага до первого боя");
            if (string.Equals(state, "missing", StringComparison.Ordinal))
            {
                await Expect(Page.Locator(".join-panel__notice"))
                    .ToHaveTextAsync("Подключение пока недоступно");
                await Expect(Page.Locator(".join-panel__steam")).ToHaveCountAsync(0);
                await Expect(Page.Locator(".join-panel__copy")).ToHaveCountAsync(0);
            }
            else
            {
                await Expect(Page.Locator(".join-panel__state")).ToHaveTextAsync(
                    string.Equals(state, "offline", StringComparison.Ordinal) ? "Сервер не отвечает" : "Свежий статус не подтвержден");
                await Expect(Page.Locator(".join-panel__facts dd").First)
                    .ToHaveTextAsync("Нет свежих данных");
            }
            (await Page.ContentAsync()).Should().NotContain("stale-map-must-not-render");
            (await Page.EvaluateAsync<bool>(
                "document.documentElement.scrollWidth > document.documentElement.clientWidth"))
                .Should().BeFalse();
            await CaptureScreenshotIfRequestedAsync($"player-guide-{state}", new ViewportSize { Width = 390, Height = 844 });
        }
    }
}
