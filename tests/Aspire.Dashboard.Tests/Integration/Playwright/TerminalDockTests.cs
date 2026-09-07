// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Threading.Channels;
using Aspire.Dashboard.Tests.Integration.Playwright.Infrastructure;
using Aspire.Dashboard.Tests.Shared;
using Aspire.DashboardService.Proto.V1;
using Aspire.TestUtilities;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using Xunit;

namespace Aspire.Dashboard.Tests.Integration.Playwright;

[RequiresFeature(TestFeature.Playwright)]
public sealed class TerminalDockTests(TerminalDockTests.TerminalDockDashboardServerFixture fixture)
    : PlaywrightTestsBase<TerminalDockTests.TerminalDockDashboardServerFixture>(fixture)
{
    [Fact]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task ResizeHandle_KeyboardAndPointerResizingRespectFocusAndBounds()
    {
        await RunTestAsync(async page =>
        {
            await OpenDockAsync(page);
            var terminals = await page.Locator(".terminal-dock .xterm").ElementHandlesAsync();
            var handle = page.GetByRole(AriaRole.Separator, new() { Name = "Terminals", Exact = true });
            var viewportHeight = await page.EvaluateAsync<int>("window.innerHeight");
            var maximum = Math.Min(1200, viewportHeight);
            await Assertions.Expect(handle).ToHaveAttributeAsync("aria-valuemax", maximum.ToString());
            await handle.FocusAsync();

            foreach (var (key, expected) in new[]
            {
                ("ArrowUp", 330),
                ("ArrowDown", 320),
                ("Shift+ArrowUp", 370),
                ("Shift+ArrowDown", 320),
                ("Home", 120),
                ("End", maximum),
                ("ArrowUp", maximum),
                ("Home", 120),
                ("ArrowDown", 120)
            })
            {
                await page.Keyboard.PressAsync(key);
                await Assertions.Expect(handle).ToHaveAttributeAsync("aria-valuenow", expected.ToString());
                await Assertions.Expect(handle).ToHaveAttributeAsync("aria-valuetext", $"{expected} pixels high");
                await Assertions.Expect(handle).ToBeFocusedAsync();
                var dockBox = await page.Locator(".terminal-dock").BoundingBoxAsync();
                Assert.NotNull(dockBox);
                Assert.InRange(dockBox.Y, 0, viewportHeight);
                Assert.InRange(dockBox.Height, 120, maximum);
            }

            var handleBox = await handle.BoundingBoxAsync();
            Assert.NotNull(handleBox);
            var x = handleBox.X + 100;
            var y = handleBox.Y + handleBox.Height / 2;
            await page.Mouse.MoveAsync(x, y);
            await page.Mouse.DownAsync();
            await page.Mouse.MoveAsync(x, y - 60);
            await page.Mouse.UpAsync();
            var draggedHeight = (int)Math.Round(viewportHeight - (y - 60));
            await Assertions.Expect(handle).ToHaveAttributeAsync("aria-valuenow", draggedHeight.ToString());
            await Assertions.Expect(handle).ToBeFocusedAsync();
            await page.Keyboard.PressAsync("ArrowUp");
            await Assertions.Expect(handle).ToHaveAttributeAsync("aria-valuenow", (draggedHeight + 10).ToString());

            var input = page.Locator(".terminal-dock-pane.active .xterm-helper-textarea");
            await input.FocusAsync();
            foreach (var key in new[] { "ArrowUp", "ArrowDown", "Shift+ArrowUp", "Shift+ArrowDown", "Home", "End" })
            {
                await page.Keyboard.PressAsync(key);
                await Assertions.Expect(input).ToBeFocusedAsync();
                await Assertions.Expect(handle).ToHaveAttributeAsync("aria-valuenow", (draggedHeight + 10).ToString());
            }

            Assert.Empty(fixture.Client.ClosedTerminals);
            foreach (var terminal in terminals)
            {
                Assert.True(await terminal.EvaluateAsync<bool>("element => element.isConnected"));
            }
        });
    }

    [Fact]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task ResizeHandle_ViewportChangesKeepTheHandleReachable()
    {
        await RunTestAsync(async page =>
        {
            await OpenDockAsync(page);
            var handle = page.GetByRole(AriaRole.Separator, new() { Name = "Terminals", Exact = true });
            await handle.FocusAsync();
            await page.Keyboard.PressAsync("End");

            foreach (var viewportHeight in new[] { 240, 800 })
            {
                await page.SetViewportSizeAsync(1280, viewportHeight);
                await Assertions.Expect(handle).ToHaveAttributeAsync("aria-valuemax", viewportHeight.ToString());
                await page.Keyboard.PressAsync("End");
                await Assertions.Expect(handle).ToHaveAttributeAsync("aria-valuenow", viewportHeight.ToString());
                await Assertions.Expect(handle).ToBeFocusedAsync();
                var box = await handle.BoundingBoxAsync();
                Assert.NotNull(box);
                Assert.InRange(box.Y, 0, viewportHeight - box.Height);
            }
        });
    }

    [Fact]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task KeyboardNavigation_SelectsTabsWithoutInterceptingTerminalInput()
    {
        await RunTestAsync(async page =>
        {
            await OpenDockAsync(page);
            var terminals = await page.Locator(".terminal-dock .xterm").ElementHandlesAsync();
            await Tab(page, "first").FocusAsync();

            foreach (var (key, expected) in new[]
            {
                ("ArrowLeft", "third"),
                ("ArrowRight", "first"),
                ("End", "third"),
                ("Home", "first"),
                ("ArrowRight", "second"),
                ("Enter", "second"),
                ("Space", "second")
            })
            {
                await page.Keyboard.PressAsync(key);
                await Assertions.Expect(Tab(page, expected)).ToBeFocusedAsync();
                await Assertions.Expect(Tab(page, expected)).ToHaveAttributeAsync("aria-selected", "true");
            }

            await page.Keyboard.PressAsync("Tab");
            await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Close terminal 'second'", Exact = true }))
                .ToBeFocusedAsync();
            await page.Keyboard.PressAsync("Shift+Tab");
            await Assertions.Expect(Tab(page, "second")).ToBeFocusedAsync();
            await page.Keyboard.PressAsync("Shift+Tab");
            Assert.False(await page.EvaluateAsync<bool>("!!document.activeElement.closest('.terminal-dock-tabstrip')"));
            await page.Keyboard.PressAsync("Tab");
            await Assertions.Expect(Tab(page, "second")).ToBeFocusedAsync();

            var input = page.Locator(".terminal-dock-pane.active .xterm-helper-textarea");
            await input.FocusAsync();
            foreach (var key in new[] { "ArrowLeft", "ArrowRight", "Home", "End", "Delete" })
            {
                await page.Keyboard.PressAsync(key);
                await Assertions.Expect(input).ToBeFocusedAsync();
                await Assertions.Expect(Tab(page, "second")).ToHaveAttributeAsync("aria-selected", "true");
            }

            await page.Keyboard.PressAsync("F6");
            Assert.True(await input.EvaluateAsync<bool>(
                "input => input !== document.activeElement && input.closest('.terminal-dock-pane').contains(document.activeElement)"));
            await input.FocusAsync();
            await page.Keyboard.PressAsync("Shift+F6");
            Assert.True(await page.EvaluateAsync<bool>("!!document.activeElement.closest('.terminal-dock-tabstrip')"));

            Assert.Empty(fixture.Client.ClosedTerminals);
            foreach (var terminal in terminals)
            {
                Assert.True(await terminal.EvaluateAsync<bool>("element => element.isConnected && element.getBoundingClientRect().width > 0"));
            }
        });
    }

    [Fact]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task CloseTab_RestoresFocusOnlyAfterRemovalIncludingLastTab()
    {
        await RunTestAsync(async page =>
        {
            var (updates, closes) = await OpenDockAsync(page);
            await Tab(page, "second").ClickAsync();
            await Assertions.Expect(Tab(page, "second")).ToHaveAttributeAsync("aria-selected", "true");

            foreach (var (closed, next, key) in new[]
            {
                ("second", "third", "Delete"),
                ("third", "first", "Enter"),
                ("first", (string?)null, "Space")
            })
            {
                if (key != "Delete")
                {
                    await page.Keyboard.PressAsync("Tab");
                }
                await page.Keyboard.PressAsync(key);
                Assert.Equal(closed, await closes.Reader.ReadAsync().AsTask().DefaultTimeout());
                await Assertions.Expect(Tab(page, closed)).ToHaveAttributeAsync("aria-selected", "true");
                await Assertions.Expect(key == "Delete"
                    ? Tab(page, closed)
                    : page.GetByRole(AriaRole.Button, new() { Name = $"Close terminal '{closed}'", Exact = true }))
                    .ToBeFocusedAsync();
                await updates.Writer.WriteAsync(Change(TerminalChangeType.Removed, closed));

                if (next is not null)
                {
                    await Assertions.Expect(Tab(page, next)).ToBeFocusedAsync();
                    await Assertions.Expect(Tab(page, next)).ToHaveAttributeAsync("aria-selected", "true");
                }
                else
                {
                    await Assertions.Expect(page.Locator(".terminal-dock-collapse")).ToBeFocusedAsync();
                    await Assertions.Expect(page.Locator(".terminal-dock-panel-heading")).ToHaveTextAsync("No terminals");
                }
            }

            // The tablist is recreated after the empty state, but its dock-scoped listener remains usable.
            await updates.Writer.WriteAsync(Change(TerminalChangeType.Added, "replacement"));
            await Tab(page, "replacement").FocusAsync();
            await page.Keyboard.PressAsync("Delete");
            Assert.Equal("replacement", await closes.Reader.ReadAsync().AsTask().DefaultTimeout());
            await updates.Writer.WriteAsync(Change(TerminalChangeType.Removed, "replacement"));
            await Assertions.Expect(page.Locator(".terminal-dock-collapse")).ToBeFocusedAsync();
            Assert.Equal(["second", "third", "first", "replacement"], fixture.Client.ClosedTerminals.ToArray());
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task Removal_DoesNotStealFocusAfterMovingElsewhere(bool hideDock)
    {
        await RunTestAsync(async page =>
        {
            var (updates, closes) = await OpenDockAsync(page);
            await Tab(page, "first").FocusAsync();
            await page.Keyboard.PressAsync("Delete");
            Assert.Equal("first", await closes.Reader.ReadAsync().AsTask().DefaultTimeout());

            ILocator focusTarget;
            if (hideDock)
            {
                await page.Locator(".terminal-dock-collapse").ClickAsync();
                focusTarget = page.GetByRole(AriaRole.Button, new() { Name = "Toggle terminal (Shift+`)", Exact = true });
            }
            else
            {
                await Tab(page, "second").ClickAsync();
                focusTarget = page.Locator(".terminal-dock-pane.active .xterm-helper-textarea");
            }

            await focusTarget.FocusAsync();
            await updates.Writer.WriteAsync(Change(TerminalChangeType.Removed, "first"));
            await Assertions.Expect(page.Locator(".terminal-dock-tab-select")).ToHaveCountAsync(2);
            await Assertions.Expect(focusTarget).ToBeFocusedAsync();
        });
    }

    private async Task<(Channel<WatchTerminalsUpdate> Updates, Channel<string> Closes)> OpenDockAsync(IPage page)
    {
        var channels = fixture.StartSession();
        // Keep the real xterm views connected without needing a PTY; this fixture exercises dock input and focus.
        await page.RouteWebSocketAsync("**/api/apphost-terminal?*", route => route.OnMessage(_ => { }));
        await page.GotoAsync("/").DefaultTimeout();
        foreach (var id in new[] { "first", "second", "third" })
        {
            await channels.Updates.Writer.WriteAsync(Change(TerminalChangeType.Added, id));
        }
        await channels.Updates.Writer.WriteAsync(Change(TerminalChangeType.Activated, "first"));
        await Assertions.Expect(Tab(page, "first")).ToBeVisibleAsync();
        await Assertions.Expect(page.Locator(".terminal-dock .xterm")).ToHaveCountAsync(3);
        return channels;
    }

    private static ILocator Tab(IPage page, string name) => page.GetByRole(AriaRole.Tab, new() { Name = name, Exact = true });

    private static WatchTerminalsUpdate Change(TerminalChangeType type, string id) => new()
    {
        Change = new TerminalChangeNotification
        {
            ChangeType = type,
            Terminal = new TerminalDescriptor { TerminalId = id, Title = id }
        }
    };

    public sealed class TerminalDockDashboardServerFixture : DashboardServerFixture
    {
        private Channel<WatchTerminalsUpdate> _updates = Channel.CreateUnbounded<WatchTerminalsUpdate>();
        private Channel<string> _closes = Channel.CreateUnbounded<string>();

        public TestDashboardClient Client { get; }

        public TerminalDockDashboardServerFixture()
        {
            Client = new TestDashboardClient(
                isEnabled: true,
                terminalChannelProvider: () => Volatile.Read(ref _updates),
                closeTerminal: (id, token) => Volatile.Read(ref _closes).Writer.WriteAsync(id, token).AsTask());
        }

        public (Channel<WatchTerminalsUpdate> Updates, Channel<string> Closes) StartSession()
        {
            var updates = Channel.CreateUnbounded<WatchTerminalsUpdate>();
            var closes = Channel.CreateUnbounded<string>();
            Volatile.Write(ref _updates, updates);
            Volatile.Write(ref _closes, closes);
            Client.ClosedTerminals.Clear();
            return (updates, closes);
        }

        protected override void ConfigureServices(IServiceCollection services)
        {
            services.AddSingleton<IDashboardClient>(Client);
        }
    }
}
