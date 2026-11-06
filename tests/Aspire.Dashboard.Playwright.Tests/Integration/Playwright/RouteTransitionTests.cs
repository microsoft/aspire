// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Tests.Integration.Playwright.Infrastructure;
using Aspire.Dashboard.Otlp.Storage;
using Aspire.Dashboard.Model;
using Aspire.Tests.Shared.DashboardModel;
using Aspire.TestUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Playwright;
using OpenTelemetry.Proto.Metrics.V1;
using Xunit;
using static Aspire.Tests.Shared.Telemetry.TelemetryTestHelpers;

namespace Aspire.Dashboard.Tests.Integration.Playwright;

[RequiresFeature(TestFeature.Playwright)]
public sealed class RouteTransitionTests(RouteTransitionTests.RouteTransitionDashboardServerFixture fixture)
    : PlaywrightTestsBase<RouteTransitionTests.RouteTransitionDashboardServerFixture>(fixture)
{
    [Fact]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task PrimaryRoutes_AnimateOnlyContentAndRapidNavigationSettles()
    {
        await RunTestAsync(async page =>
        {
            await page.GotoAsync("/resources");
            await Assertions.Expect(page.Locator(".resource-tab-body")).ToHaveAttributeAsync("data-resource-tab", "Overview");
            await Assertions.Expect(page.GetByRole(AriaRole.Tablist, new() { Name = Resources.Layout.ResourceTabsLandmark, Exact = true })).ToBeVisibleAsync();
            // Warm first-use interop so motion assertions don't race the intentional slow-render cutoff.
            await page.Locator("#tab-Console").ClickAsync();
            await Assertions.Expect(page.Locator(".resource-tab-body")).ToHaveAttributeAsync("data-resource-tab", "Console");
            await Assertions.Expect(page.Locator(".resources-layout[data-tab-navigation]")).ToHaveCountAsync(0);
            await page.Locator("#tab-Overview").ClickAsync();
            await Assertions.Expect(page.Locator(".resource-tab-body")).ToHaveAttributeAsync("data-resource-tab", "Overview");
            await Assertions.Expect(page.Locator(".resources-layout[data-tab-navigation]")).ToHaveCountAsync(0);
            await page.EvaluateAsync("""
                () => {
                    window.routeMotion = [];
                    window.routeNavigations = [];
                    window.navigation.addEventListener('navigate', event => window.routeNavigations.push(new URL(event.destination.url).pathname));
                    const start = document.startViewTransition.bind(document);
                    document.startViewTransition = update => {
                        const result = { direction: document.documentElement.dataset.routeTransition };
                        window.routeMotion.push(result);
                        const transition = start(update);
                        transition.ready.then(() => {
                            result.names = document.getAnimations()
                                .filter(a => a.animationName?.startsWith('route-content-'))
                                .map(a => a.effect.pseudoElement).sort();
                            const link = document.querySelector('.resource-pane .resource-row-link');
                            const rect = link.getBoundingClientRect();
                            const hit = document.elementFromPoint(rect.x + rect.width / 2, rect.y + rect.height / 2);
                            result.shellHit = { x: rect.x, y: rect.y, width: rect.width, height: rect.height, element: hit?.outerHTML };
                            result.shellIsInteractive = hit?.closest('.resource-row-link') === link;
                        }, error => { result.skipped = error.name; });
                        return transition;
                    };
                }
                """);
            var console = page.GetByRole(AriaRole.Tab, new() { Name = Resources.Layout.NavMenuConsoleLogsTab, Exact = true });
            // The documented 200ms snapshot cutoff can skip a loaded server's render.
            // Exercise several paced round trips; require real effects in both directions
            // while allowing only that expected cancellation for the other snapshots.
            for (var round = 0; round < 3; round++)
            {
                await console.FocusAsync();
                await page.Keyboard.PressAsync("Enter");
                await Assertions.Expect(page.Locator(".resource-tab-body")).ToHaveAttributeAsync("data-resource-tab", "Console");
                await Assertions.Expect(page.Locator(".resources-layout[data-tab-navigation]")).ToHaveCountAsync(0);
                await page.GoBackAsync();
                await Assertions.Expect(page.Locator(".resource-tab-body")).ToHaveAttributeAsync("data-resource-tab", "Overview");
                await Assertions.Expect(page.Locator("html[data-route-transition]")).ToHaveCountAsync(0);
            }
            Assert.True(await page.EvaluateAsync<bool>("['forward', 'backward'].every(direction => window.routeMotion.some(m => m.direction === direction && m.shellIsInteractive))"),
                await page.EvaluateAsync<string>("JSON.stringify({ motions: window.routeMotion, navigations: window.routeNavigations })"));
            Assert.True(await page.EvaluateAsync<bool>("window.routeMotion.every(m => !m.skipped || m.skipped === 'AbortError')"));
            Assert.Equal(
                ["::view-transition-new(resource-route)", "::view-transition-old(resource-route)"],
                await page.EvaluateAsync<string[]>("window.routeMotion.find(m => m.shellIsInteractive).names"));
            await page.GoForwardAsync();
            await Assertions.Expect(page.Locator(".resource-tab-body")).ToHaveAttributeAsync("data-resource-tab", "Console");
            await page.EvaluateAsync("""
                Blazor.navigateTo('/structuredlogs');
                Blazor.navigateTo('/metrics');
                """);
            await Assertions.Expect(page.Locator(".resource-tab-body")).ToHaveAttributeAsync("data-resource-tab", "Metrics");
            await Assertions.Expect(page.Locator("html[data-route-transition]")).ToHaveCountAsync(0);
            Assert.Empty(await page.EvaluateAsync<string[]>("document.getAnimations().filter(a => a.animationName?.startsWith('route-content-')).map(a => a.animationName)"));
        });
    }

    [Theory]
    [InlineData("reduced-motion")]
    [InlineData("view-transitions")]
    [InlineData("navigation")]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task ReducedMotionOrUnsupportedBrowser_NavigatesWithoutTransitions(string disabledCapability)
    {
        await RunTestAsync(async page =>
        {
            if (disabledCapability == "reduced-motion")
            {
                await page.EmulateMediaAsync(new() { ReducedMotion = ReducedMotion.Reduce });
            }
            else if (disabledCapability == "view-transitions")
            {
                await page.Context.AddInitScriptAsync("document.startViewTransition = undefined;");
            }
            else
            {
                await page.Context.AddInitScriptAsync("Object.defineProperty(window, 'navigation', { value: undefined });");
            }
            var errors = new List<string>();
            page.PageError += (_, error) => errors.Add(error);
            await page.GotoAsync("/consolelogs");
            await Assertions.Expect(page.Locator(".resource-tab-body")).ToHaveAttributeAsync("data-resource-tab", "Console");
            await page.GetByRole(AriaRole.Tab, new() { Name = Resources.Layout.NavMenuStructuredLogsTab, Exact = true }).ClickAsync();
            await Assertions.Expect(page.Locator(".resource-tab-body")).ToHaveAttributeAsync("data-resource-tab", "StructuredLogs");
            await Assertions.Expect(page.Locator("html[data-route-transition]")).ToHaveCountAsync(0);
            Assert.Empty(await page.EvaluateAsync<string[]>("document.getAnimations().filter(a => a.animationName?.startsWith('route-content-')).map(a => a.animationName)"));
            Assert.Empty(errors);
        });
    }

    [Fact]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task RepeatedPrimaryNavigation_DisposalAndHistoryRemainSafe()
    {
        var sink = new TestSink(write => write.LogLevel >= LogLevel.Warning &&
            (write.LoggerName?.EndsWith(".CircuitHost", StringComparison.Ordinal) == true ||
             write.LoggerName?.EndsWith(".RemoteRenderer", StringComparison.Ordinal) == true));
        DashboardServerFixture.DashboardApp.Services.GetRequiredService<ILoggerFactory>().AddProvider(new TestLoggerProvider(sink));
        // The app-model resource has no replica suffix, so use its singleton telemetry key.
        var metricResource = CreateResource("TestResource");
        metricResource.Attributes.Remove(metricResource.Attributes.Single(attribute => attribute.Key == "service.instance.id"));
        await DashboardServerFixture.DashboardApp.Services.GetRequiredService<ITelemetryRepositoryWriter>().AddMetricsAsync(new(),
            [new ResourceMetrics
            {
                Resource = metricResource,
                ScopeMetrics =
                {
                    new ScopeMetrics
                    {
                        Scope = CreateScope(name: "navigation-meter"),
                        Metrics = { CreateSumMetric(metricName: "navigation.count", startTime: DateTime.UtcNow.AddSeconds(-1)) }
                    }
                }
            }]);
        await RunTestAsync(async page =>
        {
            var errors = new System.Collections.Concurrent.ConcurrentQueue<string>();
            await page.Context.AddInitScriptAsync("localStorage.setItem('Aspire_DashboardFeedback_PromptDismissed', 'true');");
            await page.Context.AddInitScriptAsync("""
                (() => {
                    window.concurrentRouteTransitions = 0;
                    window.routeMeterObservers = new Map();
                    const MutationObserverBase = window.MutationObserver;
                    window.MutationObserver = class extends MutationObserverBase {
                        observe(target, options) {
                            super.observe(target, options);
                            if (target.matches?.('.meter-overview')) window.routeMeterObservers.set(this, target);
                        }
                        disconnect() {
                            window.routeMeterObservers.delete(this);
                            super.disconnect();
                        }
                    };
                    if (!document.startViewTransition) return;
                    let running = 0;
                    const start = document.startViewTransition.bind(document);
                    document.startViewTransition = update => {
                        if (running) window.concurrentRouteTransitions++;
                        running++;
                        const transition = start(update);
                        transition.finished.then(() => running--, () => running--);
                        return transition;
                    };
                })();
                """);
            page.PageError += (_, error) => errors.Enqueue(error);
            page.Console += (_, message) =>
            {
                if (message.Type == "error")
                {
                    errors.Enqueue(message.Text);
                }
            };
            // Seeded telemetry can arrive before the first AppHost resource snapshot.
            // Establish the app-model resource before requesting its Overview route.
            await page.GotoAsync("/consolelogs");
            await Assertions.Expect(page.Locator(".resource-pane [data-resource-name='TestResource']")).ToBeVisibleAsync();
            await page.GotoAsync("/resources/TestResource");
            await Assertions.Expect(page.GetByRole(AriaRole.Tablist, new() { Name = Resources.Layout.ResourceTabsLandmark, Exact = true })).ToBeVisibleAsync();
            await Assertions.Expect(page.Locator(".property-grid").First).ToBeVisibleAsync();
            await page.GetByRole(AriaRole.Tab, new() { Name = Resources.Layout.NavMenuMetricsTab, Exact = true }).ClickAsync();
            await Assertions.Expect(page.Locator(".meter-overview .meter-card").First).ToBeVisibleAsync();
            await page.GetByRole(AriaRole.Tab, new() { Name = Resources.Layout.ResourceTabOverview, Exact = true }).ClickAsync();
            await Assertions.Expect(page.Locator(".property-grid").First).ToBeVisibleAsync();
            var sidebar = await page.Locator(".resource-pane").BoundingBoxAsync();
            foreach (var mode in new[] { "animated", "reduced", "unsupported" })
            {
                await page.EmulateMediaAsync(new() { ReducedMotion = mode == "reduced" ? ReducedMotion.Reduce : ReducedMotion.NoPreference });
                if (mode == "unsupported")
                {
                    await page.Context.AddInitScriptAsync("document.startViewTransition = undefined;");
                    await page.ReloadAsync();
                    await Assertions.Expect(page.Locator(".resource-tab-body")).ToHaveAttributeAsync("data-resource-tab", "Metrics");
                }
                // Rapid navigation replaces pages while their first-render JS imports and
                // observers are still pending. Final navigation must win without a dead circuit.
                await page.EvaluateAsync("""
                    async () => {
                        const tabs = ['Overview', 'Console', 'StructuredLogs', 'Traces', 'Metrics'];
                        for (let cycle = 0; cycle < 12; cycle++) {
                            for (const tab of tabs) {
                                let target;
                                const deadline = Date.now() + 5000;
                                while (!(target = document.getElementById('tab-' + tab))) {
                                    if (Date.now() > deadline) throw new Error('Missing primary tab ' + tab);
                                    await new Promise(resolve => setTimeout(resolve, 1));
                                }
                                target.click();
                                await new Promise(resolve => setTimeout(resolve, 15));
                            }
                        }
                    }
                    """);
                await page.GetByRole(AriaRole.Tab, new() { Name = Resources.Layout.NavMenuMetricsTab, Exact = true }).ClickAsync();
                await Assertions.Expect(page.Locator(".resource-tab-body")).ToHaveAttributeAsync("data-resource-tab", "Metrics");
                Assert.EndsWith("/metrics/resource/TestResource", page.Url);
                await Assertions.Expect(page.Locator("#tab-Metrics")).ToHaveAttributeAsync("aria-selected", "true");
                await Assertions.Expect(page.Locator("html[data-route-transition]")).ToHaveCountAsync(0);
                await page.GetByRole(AriaRole.Tab, new() { Name = Resources.Layout.NavMenuTracesTab, Exact = true }).FocusAsync();
                await page.Keyboard.PressAsync("Enter");
                await Assertions.Expect(page.Locator(".resource-tab-body")).ToHaveAttributeAsync("data-resource-tab", "Traces");
                await page.GetByRole(AriaRole.Tab, new() { Name = Resources.Layout.NavMenuMetricsTab, Exact = true }).ClickAsync();
                await Assertions.Expect(page.Locator(".resource-tab-body")).ToHaveAttributeAsync("data-resource-tab", "Metrics");
                await page.GoBackAsync();
                await Assertions.Expect(page.Locator(".resource-tab-body")).ToHaveAttributeAsync("data-resource-tab", "Traces");
                await page.GoForwardAsync();
                await Assertions.Expect(page.Locator(".resource-tab-body")).ToHaveAttributeAsync("data-resource-tab", "Metrics");
                await Assertions.Expect(page.Locator("html[data-route-transition]")).ToHaveCountAsync(0);
                await Assertions.Expect(page.Locator("#blazor-error-ui")).Not.ToBeVisibleAsync();
                Assert.Empty(await page.EvaluateAsync<string[]>("document.getAnimations().filter(a => a.animationName?.startsWith('route-content-')).map(a => a.animationName)"));
                var currentSidebar = await page.Locator(".resource-pane").BoundingBoxAsync();
                Assert.NotNull(sidebar);
                Assert.NotNull(currentSidebar);
                Assert.Equal((sidebar.X, sidebar.Y, sidebar.Width, sidebar.Height),
                    (currentSidebar.X, currentSidebar.Y, currentSidebar.Width, currentSidebar.Height));
                Assert.Empty(errors);
                Assert.Equal(0, await page.EvaluateAsync<int>("window.concurrentRouteTransitions"));
                Assert.Equal(0, await page.EvaluateAsync<int>("Array.from(window.routeMeterObservers.values()).filter(target => !target.isConnected).length"));
            }
            await page.GetByRole(AriaRole.Tab, new() { Name = Resources.Layout.NavMenuConsoleLogsTab, Exact = true }).ClickAsync();
            await Assertions.Expect(page.Locator(".resource-tab-body")).ToHaveAttributeAsync("data-resource-tab", "Console");
            Assert.Equal(0, await page.EvaluateAsync<int>("window.routeMeterObservers.size"));
            Assert.Equal(0, await page.EvaluateAsync<int>("window.concurrentRouteTransitions"));
            Assert.True(await page.EvaluateAsync<bool>("""
                async () => {
                    const module = await import('/Components/Controls/Chart/MeterOverview.razor.js');
                    const metrics = await import('/Components/Pages/Metrics.razor.js');
                    return module.observeCards(null, {}) === null &&
                        module.observeCards(document.createElement('div'), {}) === null &&
                        metrics.registerMetricsSplitter(null, {}) === false &&
                        metrics.registerMetricsSplitter(document.createElement('div'), {}) === false;
                }
                """));
        });
        Assert.Empty(sink.Writes);
    }

    [Fact]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task UncommittedNavigation_SkipsSnapshotWithoutHoldingRendering()
    {
        await RunTestAsync(async page =>
        {
            await page.GotoAsync("/resources");
            await Assertions.Expect(page.Locator(".resource-tab-body")).ToHaveAttributeAsync("data-resource-tab", "Overview");
            await Assertions.Expect(page.GetByRole(AriaRole.Tablist, new() { Name = Resources.Layout.ResourceTabsLandmark, Exact = true })).ToBeVisibleAsync();
            // An interrupted navigation can notify the browser without a matching server render.
            Assert.Equal("forward", await page.EvaluateAsync<string>("""
                () => {
                    window.navigation.dispatchEvent(Object.assign(new Event('navigate'), {
                        destination: { url: new URL('/metrics', location.origin).href, sameDocument: true }
                    }));
                    return document.documentElement.dataset.routeTransition;
                }
                """));
            await Assertions.Expect(page.Locator("html[data-route-transition]")).ToHaveCountAsync(0);
            await Assertions.Expect(page.Locator(".resource-tab-body")).ToHaveAttributeAsync("data-resource-tab", "Overview");
            await page.GetByRole(AriaRole.Tab, new() { Name = Resources.Layout.NavMenuConsoleLogsTab, Exact = true }).ClickAsync();
            await Assertions.Expect(page.Locator(".resource-tab-body")).ToHaveAttributeAsync("data-resource-tab", "Console");
        });
    }

    [Theory]
    [InlineData("animated")]
    [InlineData("reduced")]
    [InlineData("unsupported")]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task PrimaryTabBurst_CreatesOnlyFirstAndLatestPages(string mode)
    {
        await RunTestAsync(async page =>
        {
            var errors = new System.Collections.Concurrent.ConcurrentQueue<string>();
            page.PageError += (_, error) => errors.Enqueue(error);
            page.Console += (_, message) =>
            {
                if (message.Type == "error")
                {
                    errors.Enqueue(message.Text);
                }
            };
            if (mode == "reduced")
            {
                await page.EmulateMediaAsync(new() { ReducedMotion = ReducedMotion.Reduce });
            }
            else if (mode == "unsupported")
            {
                await page.Context.AddInitScriptAsync("document.startViewTransition = undefined;");
            }
            await page.GotoAsync("/resources/TestResource");
            await Assertions.Expect(page.Locator(".property-grid").First).ToBeVisibleAsync();
            await page.EvaluateAsync("""
                () => {
                    window.coalescedRoutes = [];
                    window.coalescedPages = [];
                    window.navigation.addEventListener('navigate', event => {
                        if (event.destination.sameDocument) window.coalescedRoutes.push(new URL(event.destination.url).pathname);
                    });
                    const body = document.querySelector('.resource-tab-body');
                    new MutationObserver(() => window.coalescedPages.push(body.dataset.resourceTab))
                        .observe(body, { attributes: true, attributeFilter: ['data-resource-tab'] });
                }
                """);
            for (var burst = 0; burst < 3; burst++)
            {
                await Assertions.Expect(page.Locator(".resources-layout[data-tab-navigation]")).ToHaveCountAsync(0);
                // One browser task sends all activations before a render can be acknowledged.
                // The router must instantiate only Console and the final Metrics destination.
                await page.EvaluateAsync("""
                    () => {
                        window.coalescedRoutes = [];
                        window.coalescedPages = [];
                        for (let cycle = 0; cycle < 12; cycle++) {
                            for (const tab of ['Overview', 'Console', 'StructuredLogs', 'Traces', 'Metrics']) {
                                document.getElementById('tab-' + tab).click();
                            }
                        }
                    }
                    """);
                await Assertions.Expect(page.Locator(".resource-tab-body")).ToHaveAttributeAsync("data-resource-tab", "Metrics");
                await Assertions.Expect(page.Locator(".resources-layout[data-tab-navigation]")).ToHaveCountAsync(0);
                await Assertions.Expect(page.Locator("#tab-Metrics")).ToHaveAttributeAsync("aria-selected", "true");
                Assert.EndsWith("/metrics/resource/TestResource", page.Url);
                Assert.Equal(["/consolelogs/resource/TestResource", "/metrics/resource/TestResource"],
                    await page.EvaluateAsync<string[]>("window.coalescedRoutes"));
                Assert.Equal(["Console", "Metrics"], await page.EvaluateAsync<string[]>("window.coalescedPages"));
                await Assertions.Expect(page.Locator("#blazor-error-ui")).Not.ToBeVisibleAsync();
                Assert.Empty(errors);
                await page.GoBackAsync();
                await Assertions.Expect(page.Locator(".resource-tab-body")).ToHaveAttributeAsync("data-resource-tab", "Console");
                await page.GoForwardAsync();
                await Assertions.Expect(page.Locator(".resource-tab-body")).ToHaveAttributeAsync("data-resource-tab", "Metrics");
                await page.Locator("#tab-Overview").ClickAsync();
                await Assertions.Expect(page.Locator(".resource-tab-body")).ToHaveAttributeAsync("data-resource-tab", "Overview");
            }
        });
    }

    [Theory]
    [InlineData("hidden", "animated")]
    [InlineData("timeout", "animated")]
    [InlineData("disposed", "animated")]
    [InlineData("hidden", "reduced")]
    [InlineData("hidden", "unsupported")]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task SuspendedRenderFrames_AcknowledgementSettlesAndNavigationRecovers(string interruption, string mode)
    {
        await RunTestAsync(async page =>
        {
            if (mode == "reduced")
            {
                await page.EmulateMediaAsync(new() { ReducedMotion = ReducedMotion.Reduce });
            }
            else if (mode == "unsupported")
            {
                await page.Context.AddInitScriptAsync("document.startViewTransition = undefined;");
            }
            await page.GotoAsync("/resources/TestResource");
            await Assertions.Expect(page.Locator(".property-grid").First).ToBeVisibleAsync();
            await page.Locator("#tab-Console").ClickAsync();
            await Assertions.Expect(page.Locator(".resource-tab-body")).ToHaveAttributeAsync("data-resource-tab", "Console");
            await Assertions.Expect(page.Locator(".resources-layout[data-tab-navigation]")).ToHaveCountAsync(0);
            await page.Locator("#tab-Overview").ClickAsync();
            await Assertions.Expect(page.Locator(".resource-tab-body")).ToHaveAttributeAsync("data-resource-tab", "Overview");
            await Assertions.Expect(page.Locator(".resources-layout[data-tab-navigation]")).ToHaveCountAsync(0);
            Assert.True(await page.EvaluateAsync<bool>("""
                async interruption => {
                    const module = await import('/Components/Layout/ResourcesLayout.razor.js');
                    const layout = document.querySelector('.resources-layout');
                    const requestFrame = window.requestAnimationFrame;
                    const cancelFrame = window.cancelAnimationFrame;
                    const visibility = Object.getOwnPropertyDescriptor(document, 'visibilityState');
                    let frameCanceled = false;
                    try {
                        // Deterministically suspend the acknowledgement's first frame without
                        // depending on headless Chromium's OS-dependent background throttling.
                        window.requestAnimationFrame = () => 2147483647;
                        window.cancelAnimationFrame = id => {
                            if (id === 2147483647) frameCanceled = true;
                            else cancelFrame.call(window, id);
                        };
                        let settled = false;
                        const acknowledgement = module.waitForResourceTabRender(layout).then(() => { settled = true; });
                        window.requestAnimationFrame = requestFrame;
                        if (interruption === 'hidden') {
                            Object.defineProperty(document, 'visibilityState', { configurable: true, value: 'hidden' });
                            document.dispatchEvent(new Event('visibilitychange'));
                        } else if (interruption === 'disposed') {
                            module.unregisterRouteTransitions(layout);
                        }
                        await Promise.race([acknowledgement, new Promise(resolve => setTimeout(resolve, 1000))]);
                        return settled && frameCanceled;
                    } finally {
                        window.requestAnimationFrame = requestFrame;
                        window.cancelAnimationFrame = cancelFrame;
                        if (visibility) Object.defineProperty(document, 'visibilityState', visibility);
                        else delete document.visibilityState;
                    }
                }
                """, interruption));
            await page.EvaluateAsync("""
                () => {
                    document.getElementById('tab-Console').click();
                    document.getElementById('tab-Metrics').click();
                }
                """);
            await Assertions.Expect(page.Locator(".resource-tab-body")).ToHaveAttributeAsync("data-resource-tab", "Metrics");
            await Assertions.Expect(page.Locator(".resources-layout[data-tab-navigation]")).ToHaveCountAsync(0);
            await Assertions.Expect(page.Locator("#tab-Metrics")).ToHaveAttributeAsync("aria-selected", "true");
            Assert.EndsWith("/metrics/resource/TestResource", page.Url);
            await Assertions.Expect(page.Locator("#blazor-error-ui")).Not.ToBeVisibleAsync();
        });
    }

    [Theory]
    [InlineData("true")]
    [InlineData("TRUE")]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task TerminalFocusRoute_DoesNotStartPrimaryTransition(string terminalFocus)
    {
        await RunTestAsync(async page =>
        {
            await page.GotoAsync("/consolelogs/resource/TestResource");
            await page.Locator("#tab-Traces").ClickAsync();
            await Assertions.Expect(page.Locator(".resource-tab-body")).ToHaveAttributeAsync("data-resource-tab", "Traces");
            await Assertions.Expect(page.Locator(".resources-layout[data-tab-navigation]")).ToHaveCountAsync(0);
            await page.EvaluateAsync("""
                () => {
                    window.terminalTransitions = 0;
                    const start = document.startViewTransition.bind(document);
                    document.startViewTransition = update => {
                        window.terminalTransitions++;
                        return start(update);
                    };
                }
                """);
            await page.EvaluateAsync("url => Blazor.navigateTo(url)", $"/resources/TestResource?terminal={terminalFocus}");
            await Assertions.Expect(page.Locator(".resource-tab-body")).ToHaveAttributeAsync("data-resource-tab", "Terminal");
            Assert.Equal(0, await page.EvaluateAsync<int>("window.terminalTransitions"));
            await Assertions.Expect(page.Locator("html[data-route-transition]")).ToHaveCountAsync(0);
            await Assertions.Expect(page.Locator("#blazor-error-ui")).Not.ToBeVisibleAsync();
        });
    }

    public sealed class RouteTransitionDashboardServerFixture : DashboardServerFixture
    {
        protected override IReadOnlyList<ResourceViewModel> Resources =>
        [
            ModelTestHelpers.CreateResource(resourceName: "TestResource", resourceType: KnownResourceTypes.Project,
                state: KnownResourceState.Running,
                environment: [new EnvironmentVariableViewModel("NAVIGATION_TEST", "test", fromSpec: true)])
        ];
    }
}
