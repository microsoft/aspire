// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Threading.Channels;
using Aspire.Dashboard.Terminal;
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
    public async Task Tabs_ReorderWithoutRemountingAndScrollWithoutWrapping()
    {
        await RunTestAsync(async page =>
        {
            var (updates, _) = await OpenDockAsync(page);
            var viewers = await page.Locator(".terminal-dock textarea").ElementHandlesAsync();
            await Tab(page, "third").DragToAsync(Tab(page, "first"), new() { TargetPosition = new() { X = 2, Y = 10 } });
            var tabs = page.Locator(".terminal-dock-tab-title");
            await Assertions.Expect(tabs).ToHaveTextAsync(["third", "first", "second"]);
            await Assertions.Expect(Tab(page, "first")).ToHaveAttributeAsync("aria-selected", "true");
            await Tab(page, "third").FocusAsync();
            await page.Keyboard.PressAsync("Alt+Shift+ArrowRight");
            await Assertions.Expect(tabs).ToHaveTextAsync(["first", "third", "second"]);
            await Assertions.Expect(Tab(page, "third")).ToBeFocusedAsync();
            foreach (var viewer in viewers)
            {
                Assert.True(await viewer.EvaluateAsync<bool>("element => element.isConnected"));
            }

            for (var i = 0; i < 12; i++)
            {
                await updates.Writer.WriteAsync(Change(TerminalChangeType.Added, $"terminal-{i:00}"));
            }
            await page.SetViewportSizeAsync(700, 800);
            await Assertions.Expect(tabs).ToHaveCountAsync(15);
            var left = page.GetByRole(AriaRole.Button, new() { Name = "Scroll terminal tabs left", Exact = true });
            var right = page.GetByRole(AriaRole.Button, new() { Name = "Scroll terminal tabs right", Exact = true });
            await Assertions.Expect(right).ToBeEnabledAsync();
            await right.ClickAsync();
            await Assertions.Expect(left).ToBeEnabledAsync();
            await left.ClickAsync();
            await Assertions.Expect(left).ToBeDisabledAsync();
            Assert.True(await tabs.EvaluateAllAsync<bool>("tabs => tabs.every(t => Math.abs(t.getBoundingClientRect().top - tabs[0].getBoundingClientRect().top) < 1)"));

            await Tab(page, "first").FocusAsync();
            await page.Keyboard.PressAsync("End");
            await Assertions.Expect(Tab(page, "terminal-11")).ToBeFocusedAsync();
            await Assertions.Expect(right).ToBeDisabledAsync();
            await page.Keyboard.PressAsync("Home");
            await Assertions.Expect(Tab(page, "first")).ToBeFocusedAsync();
            await Assertions.Expect(left).ToBeDisabledAsync();
            await updates.Writer.WriteAsync(Change(TerminalChangeType.Activated, "terminal-11"));
            await Assertions.Expect(Tab(page, "terminal-11")).ToHaveAttributeAsync("aria-selected", "true");
            await Assertions.Expect(right).ToBeDisabledAsync();

            for (var i = 0; i < 12; i++)
            {
                await updates.Writer.WriteAsync(Change(TerminalChangeType.Removed, $"terminal-{i:00}"));
            }
            await page.SetViewportSizeAsync(1280, 900);
            await Assertions.Expect(tabs).ToHaveCountAsync(3);
            await Assertions.Expect(left).ToBeVisibleAsync();
            await Assertions.Expect(right).ToBeVisibleAsync();
            await Assertions.Expect(left).ToBeDisabledAsync();
            await Assertions.Expect(right).ToBeDisabledAsync();
            Assert.True(await page.Locator(".terminal-dock-tab-scroll").EvaluateAsync<bool>(
                "element => element.nextElementSibling.classList.contains('terminal-dock-detach')"));
            foreach (var id in new[] { "first", "second", "third" })
            {
                await updates.Writer.WriteAsync(Change(TerminalChangeType.Removed, id));
            }
            await Assertions.Expect(tabs).ToHaveCountAsync(0);
            await Assertions.Expect(left).ToBeVisibleAsync();
            await Assertions.Expect(right).ToBeVisibleAsync();
            await Assertions.Expect(left).ToBeDisabledAsync();
            await Assertions.Expect(right).ToBeDisabledAsync();
            await page.SetViewportSizeAsync(900, 800);
            for (var i = 0; i < 5; i++)
            {
                await updates.Writer.WriteAsync(Change(TerminalChangeType.Added, $"terminal-{i:00}"));
            }
            await Assertions.Expect(tabs).ToHaveCountAsync(5);
            await Assertions.Expect(left).ToBeDisabledAsync();
            await Assertions.Expect(right).ToBeDisabledAsync();
            Assert.Empty(fixture.Client.ClosedTerminals);
        });
    }

    [Fact]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task EmptyDock_ResizingPreservesContentInPriorityOrder()
    {
        await RunTestAsync(async page =>
        {
            var (updates, _) = await fixture.StartSessionAsync();
            await page.SetViewportSizeAsync(1280, 900);
            await page.GotoAsync("/").DefaultTimeout();
            await updates.Writer.WriteAsync(new WatchTerminalsUpdate
            {
                Snapshot = new TerminalDescriptorList { ActivatedTerminalId = "missing" }
            });
            var panel = page.Locator(".terminal-dock-panel");
            var heading = panel.GetByRole(AriaRole.Heading, new() { Name = "No docked terminals", Exact = true });
            var hint = panel.Locator(".terminal-dock-panel-hint");
            var moreInformation = panel.GetByRole(AriaRole.Link, new() { Name = "More information", Exact = true, IncludeHidden = true });
            var handle = page.GetByRole(AriaRole.Separator, new() { Name = "Terminals", Exact = true });
            await Assertions.Expect(panel).ToBeVisibleAsync();

            foreach (var width in new[] { 1280, 360 })
            {
                await page.SetViewportSizeAsync(width, 900);
                await Assertions.Expect(handle).ToHaveAttributeAsync("aria-valuemax", "900");
                foreach (var height in new[] { 420, 320, 160, 120, 160, 420 })
                {
                    await handle.FocusAsync();
                    await page.Keyboard.PressAsync("Home");
                    var remaining = height - 120;
                    while (remaining >= 50)
                    {
                        await page.Keyboard.PressAsync("Shift+ArrowUp");
                        remaining -= 50;
                    }
                    while (remaining > 0)
                    {
                        await page.Keyboard.PressAsync("ArrowUp");
                        remaining -= 10;
                    }
                    await Assertions.Expect(handle).ToHaveAttributeAsync("aria-valuenow", height.ToString());

                    var panelBox = await panel.BoundingBoxAsync();
                    Assert.NotNull(panelBox);
                    foreach (var (element, visible) in new[]
                    {
                        (heading, true),
                        (hint, true),
                        (moreInformation, height > 120)
                    })
                    {
                        if (visible)
                        {
                            await Assertions.Expect(element).ToBeVisibleAsync();
                            var box = await element.BoundingBoxAsync();
                            Assert.NotNull(box);
                            Assert.InRange(box.Y, panelBox.Y - 1, panelBox.Y + panelBox.Height - box.Height + 1);
                        }
                        else
                        {
                            await Assertions.Expect(element).ToBeHiddenAsync();
                        }
                    }
                }

                await page.SetViewportSizeAsync(width, 80);
                await Assertions.Expect(handle).ToHaveAttributeAsync("aria-valuemax", "80");
                await handle.FocusAsync();
                await page.Keyboard.PressAsync("End");
                await Assertions.Expect(handle).ToHaveAttributeAsync("aria-valuenow", "80");
                await Assertions.Expect(heading).ToBeVisibleAsync();
                await Assertions.Expect(hint).ToBeHiddenAsync();
                await Assertions.Expect(moreInformation).ToBeHiddenAsync();
                var tinyPanelBox = await panel.BoundingBoxAsync();
                var headingBox = await heading.BoundingBoxAsync();
                Assert.NotNull(tinyPanelBox);
                Assert.NotNull(headingBox);
                Assert.InRange(headingBox.Y, tinyPanelBox.Y, tinyPanelBox.Y + tinyPanelBox.Height - headingBox.Height);
            }
        });
    }

    [Fact]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task ResizeHandle_KeyboardAndPointerResizingRespectFocusAndBounds()
    {
        await RunTestAsync(async page =>
        {
            await OpenDockAsync(page);
            var terminals = await page.Locator(".terminal-dock textarea").ElementHandlesAsync();
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
            var draggedHeight = (int)Math.Round(viewportHeight - (y - 60));
            await Assertions.Expect(page.Locator(".terminal-dock")).ToHaveCSSAsync("height", $"{draggedHeight}px");
            await Assertions.Expect(handle).ToHaveAttributeAsync("aria-valuenow", draggedHeight.ToString());
            await page.Mouse.UpAsync();
            await Assertions.Expect(handle).ToHaveAttributeAsync("aria-valuenow", draggedHeight.ToString());
            await Assertions.Expect(handle).ToBeFocusedAsync();
            await page.Keyboard.PressAsync("ArrowUp");
            await Assertions.Expect(handle).ToHaveAttributeAsync("aria-valuenow", (draggedHeight + 10).ToString());

            var input = page.Locator(".terminal-dock-pane.active").GetByRole(AriaRole.Textbox);
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
                Assert.InRange(box.Y + box.Height / 2, 0, viewportHeight);
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
            var terminals = await page.Locator(".terminal-dock textarea").ElementHandlesAsync();
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

            var input = page.Locator(".terminal-dock-pane.active").GetByRole(AriaRole.Textbox);
            await input.FocusAsync();
            foreach (var key in new[] { "ArrowLeft", "ArrowRight", "Home", "End", "Delete" })
            {
                await page.Keyboard.PressAsync(key);
                await Assertions.Expect(input).ToBeFocusedAsync();
                await Assertions.Expect(Tab(page, "second")).ToHaveAttributeAsync("aria-selected", "true");
            }

            await page.Keyboard.PressAsync("F6");
            await Assertions.Expect(page.Locator(".terminal-dock-pane.active")
                .GetByRole(AriaRole.Button, new() { Name = "Decrease font size", Exact = true })).ToBeFocusedAsync();
            await input.FocusAsync();
            await page.Keyboard.PressAsync("Shift+F6");
            Assert.True(await page.EvaluateAsync<bool>("!!document.activeElement.closest('.terminal-dock-tabstrip')"));

            Assert.Empty(fixture.Client.ClosedTerminals);
            foreach (var terminal in terminals)
            {
                Assert.True(await terminal.EvaluateAsync<bool>("element => element.isConnected"));
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
                    await Assertions.Expect(page.Locator(".terminal-dock-panel-heading")).ToHaveTextAsync("No docked terminals");
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
                focusTarget = page.Locator("#dashboard-settings-button");
            }
            else
            {
                await Tab(page, "second").ClickAsync();
                focusTarget = Tab(page, "second");
            }

            await focusTarget.FocusAsync();
            await updates.Writer.WriteAsync(Change(TerminalChangeType.Removed, "first"));
            await Assertions.Expect(page.Locator(".terminal-dock-tab-select")).ToHaveCountAsync(2);
            await Assertions.Expect(focusTarget).ToBeFocusedAsync();
        });
    }

    private async Task<(Channel<WatchTerminalsUpdate> Updates, Channel<string> Closes)> OpenDockAsync(IPage page)
    {
        var channels = await fixture.StartSessionAsync();
        await page.GotoAsync("/").DefaultTimeout();
        foreach (var id in new[] { "first", "second", "third" })
        {
            await channels.Updates.Writer.WriteAsync(Change(TerminalChangeType.Added, id));
        }
        await channels.Updates.Writer.WriteAsync(Change(TerminalChangeType.Activated, "first"));
        await Assertions.Expect(Tab(page, "first")).ToBeVisibleAsync();
        // Hidden views mount when first shown. Visit each pane so subsequent keyboard
        // navigation proves an existing WebTerminal is retained rather than recreated.
        foreach (var id in new[] { "first", "second", "third" })
        {
            await Tab(page, id).ClickAsync();
            await Assertions.Expect(page.Locator(".terminal-dock-pane.active")
                .GetByRole(AriaRole.Button, new() { Name = "Decrease font size", Exact = true })).ToBeEnabledAsync();
        }
        await Tab(page, "first").ClickAsync();
        await Assertions.Expect(page.Locator(".terminal-dock textarea")).ToHaveCountAsync(3);
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
        internal TestTerminalConnectionResolver TerminalResolver { get; } = new();

        public TestDashboardClient Client { get; }

        public TerminalDockDashboardServerFixture()
        {
            Client = new TestDashboardClient(
                isEnabled: true,
                terminalChannelProvider: () => Volatile.Read(ref _updates),
                attachTerminal: async (id, token) => (await TerminalResolver.ConnectAsync(id, token))!,
                closeTerminal: (id, token) => Volatile.Read(ref _closes).Writer.WriteAsync(id, token).AsTask());
        }

        public async Task<(Channel<WatchTerminalsUpdate> Updates, Channel<string> Closes)> StartSessionAsync()
        {
            await TerminalResolver.DiscardPendingConnectionsAsync();
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
            services.AddSingleton<ITerminalConnectionResolver>(_ => TerminalResolver);
        }
    }
}
