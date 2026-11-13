// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.RegularExpressions;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Tests.Integration.Playwright.Infrastructure;
using Aspire.TestUtilities;
using Aspire.Tests.Shared.DashboardModel;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Playwright;
using Xunit;

namespace Aspire.Dashboard.Tests.Integration.Playwright;

// Browser coverage for reconnect dialogs, terminal keyboard shortcuts, grid auto-fit, and the <aspire-scroll-to-bottom> custom element.
[RequiresFeature(TestFeature.Playwright)]
public class DashboardInteractionsTests : PlaywrightTestsBase<DashboardInteractionsTests.InteractionsDashboardServerFixture>
{
    public DashboardInteractionsTests(InteractionsDashboardServerFixture dashboardServerFixture)
        : base(dashboardServerFixture)
    {
    }

    [Theory]
    [InlineData("a")]
    [InlineData("g")]
    [InlineData("path")]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task Tooltips_SvgAnchorsPositionAndCleanUp(string tagName)
    {
        await RunTestAsync(async page =>
        {
            await page.SetViewportSizeAsync(1280, 900);
            await GoToResourcesAndWaitAsync(page);
            await page.EvaluateAsync("""
                tagName => {
                    const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
                    svg.id = 'svg-tooltip-graph';
                    svg.style.cssText = 'position:fixed;left:600px;top:250px;width:200px;height:80px;z-index:1000;';
                    svg.setAttribute('viewBox', '0 0 200 80');
                    const group = document.createElementNS(svg.namespaceURI, 'g');
                    const anchor = document.createElementNS(svg.namespaceURI, tagName);
                    anchor.id = 'svg-tooltip-probe';
                    anchor.setAttribute('tabindex', '0');
                    anchor.setAttribute('data-tooltip', 'Graph resource');
                    if (tagName === 'path') {
                        anchor.setAttribute('d', 'M0 0 H120 V40 H0 Z');
                    } else {
                        const rect = document.createElementNS(svg.namespaceURI, 'rect');
                        rect.setAttribute('width', '120');
                        rect.setAttribute('height', '40');
                        anchor.appendChild(rect);
                    }
                    group.appendChild(anchor);
                    svg.appendChild(group);
                    document.body.appendChild(svg);
                    const button = document.createElement('button');
                    button.id = 'html-tooltip-probe';
                    button.textContent = 'HTML control';
                    button.setAttribute('data-tooltip', 'HTML control');
                    button.style.cssText = 'position:fixed;left:900px;top:250px;z-index:1000;';
                    document.body.appendChild(button);
                }
                """, tagName);

            var anchor = page.Locator("#svg-tooltip-probe");
            var tooltip = page.Locator(".dashboard-tooltip");
            var positioningAnchor = page.Locator(".dashboard-tooltip-anchor");
            await anchor.HoverAsync();
            await Assertions.Expect(tooltip).ToHaveTextAsync("Graph resource");
            await AssertPositionedAboveAsync(anchor);
            await Assertions.Expect(tooltip).ToHaveAttributeAsync("anchor", "svg-tooltip-probe");
            await Assertions.Expect(anchor).ToHaveAttributeAsync("aria-describedby", (await tooltip.GetAttributeAsync("id"))!);

            await page.Keyboard.PressAsync("Escape");
            await Assertions.Expect(tooltip).ToHaveCountAsync(0);
            await Assertions.Expect(positioningAnchor).ToHaveCountAsync(0);

            foreach (var mutation in new[]
            {
                "anchor => anchor.setAttribute('transform', 'translate(15,0)')",
                "anchor => anchor.parentElement.setAttribute('transform', 'translate(30,0)')",
                "anchor => anchor.ownerSVGElement.setAttribute('viewBox', '0 0 240 96')",
                "anchor => anchor.ownerSVGElement.style.transform = 'translateX(10px)'",
                """
                anchor => {
                    const style = document.createElement('style');
                    style.textContent = '.svg-tooltip-shifted { margin-left: 15px; }';
                    anchor.ownerSVGElement.appendChild(style);
                    anchor.ownerSVGElement.classList.add('svg-tooltip-shifted');
                }
                """
            })
            {
                await anchor.BlurAsync();
                await anchor.FocusAsync();
                await AssertPositionedAboveAsync(anchor);
                var before = await anchor.BoundingBoxAsync();
                Assert.NotNull(before);

                await anchor.EvaluateAsync(mutation);

                await Assertions.Expect(tooltip).ToHaveCountAsync(0);
                await Assertions.Expect(positioningAnchor).ToHaveCountAsync(0);
                await Assertions.Expect(anchor).ToBeFocusedAsync();
                await Assertions.Expect(anchor).ToBeVisibleAsync();
                var after = await anchor.BoundingBoxAsync();
                Assert.NotNull(after);
                Assert.True(before.X != after.X || before.Y != after.Y || before.Width != after.Width || before.Height != after.Height);
            }

            await anchor.BlurAsync();
            await anchor.FocusAsync();
            await Assertions.Expect(tooltip).ToHaveTextAsync("Graph resource");
            await AssertPositionedAboveAsync(anchor);
            await page.SetViewportSizeAsync(1100, 800);
            await Assertions.Expect(tooltip).ToHaveCountAsync(0);
            await Assertions.Expect(positioningAnchor).ToHaveCountAsync(0);

            await page.Mouse.MoveAsync(0, 0);
            await anchor.HoverAsync();
            await AssertPositionedAboveAsync(anchor);
            await anchor.EvaluateAsync("anchor => anchor.remove()");
            await Assertions.Expect(tooltip).ToHaveCountAsync(0);
            await Assertions.Expect(positioningAnchor).ToHaveCountAsync(0);

            var button = page.Locator("#html-tooltip-probe");
            await button.HoverAsync();
            await Assertions.Expect(tooltip).ToHaveTextAsync("HTML control");
            await AssertPositionedAboveAsync(button);
            await Assertions.Expect(positioningAnchor).ToHaveCountAsync(0);
            await page.Locator("#svg-tooltip-graph").EvaluateAsync("svg => svg.setAttribute('viewBox', '0 0 200 80')");
            await Assertions.Expect(tooltip).ToHaveTextAsync("HTML control");
            await AssertPositionedAboveAsync(button);

            async Task AssertPositionedAboveAsync(ILocator target)
            {
                await Assertions.Expect(tooltip).ToBeVisibleAsync();
                await page.WaitForFunctionAsync("""
                    anchor => {
                        const tooltip = document.querySelector('.dashboard-tooltip:popover-open');
                        if (!tooltip) {
                            return false;
                        }
                        const a = anchor.getBoundingClientRect();
                        const t = tooltip.getBoundingClientRect();
                        return Math.abs((a.left + a.width / 2) - (t.left + t.width / 2)) <= 2 &&
                            Math.abs(a.top - t.bottom) <= 8;
                    }
                    """, await target.ElementHandleAsync()).DefaultTimeout();
            }
        });
    }

    [Fact]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task Tooltips_HiddenContentIsClonedAndUpdates()
    {
        await RunTestAsync(async page =>
        {
            await GoToResourcesAndWaitAsync(page);
            await page.EvaluateAsync("""
                () => {
                    const button = document.createElement('button');
                    button.id = 'table-tooltip-probe';
                    button.textContent = 'Log entry';
                    button.style.cssText = 'position:fixed;left:600px;top:250px;z-index:1000;';
                    button.dataset.tooltipContent = 'hidden-tooltip-probe';
                    const source = document.createElement('div');
                    source.id = 'hidden-tooltip-probe';
                    source.className = 'log-tooltip';
                    source.hidden = true;
                    source.innerHTML = `
                        <div class="log-tooltip-title-container">
                            <span class="log-tooltip-title"></span> <span class="log-tooltip-subtitle"></span><br>
                        </div>
                        <table class="log-tooltip-table"><tbody>
                            <tr><td class="label-column">Level</td><td><strong>Information</strong></td></tr>
                            <tr><td class="label-column">Timestamp</td><td>2026-01-01 10:00:00</td></tr>
                            <tr><td class="label-column">Message</td><td></td></tr>
                        </tbody></table>`;
                    source.querySelector('.log-tooltip-title').textContent = 'Event <name>';
                    source.querySelector('.log-tooltip-subtitle').textContent = 'Scope "name"';
                    source.querySelector('tr:last-child td:last-child').textContent = 'Test <message>\nwith details';
                    document.body.append(button, source);
                }
                """);

            var anchor = page.Locator("#table-tooltip-probe");
            var source = page.Locator("#hidden-tooltip-probe");
            var tooltip = page.Locator(".dashboard-tooltip");
            await anchor.HoverAsync();
            await Assertions.Expect(tooltip).ToBeVisibleAsync();
            await Assertions.Expect(page.Locator(".dashboard-tooltip.log-tooltip")).ToBeVisibleAsync();
            await Assertions.Expect(tooltip.Locator(".log-tooltip-title")).ToHaveTextAsync("Event <name>");
            await Assertions.Expect(tooltip.Locator(".log-tooltip-subtitle")).ToHaveTextAsync("Scope \"name\"");
            await Assertions.Expect(tooltip.Locator("strong")).ToHaveTextAsync("Information");
            await Assertions.Expect(source).ToBeHiddenAsync();
            await Assertions.Expect(source.Locator(".log-tooltip-title")).ToHaveTextAsync("Event <name>");
            await Assertions.Expect(tooltip.Locator("td")).ToHaveTextAsync(
                ["Level", "Information", "Timestamp", "2026-01-01 10:00:00", "Message", "Test <message>\nwith details"]);

            await source.EvaluateAsync("""
                source => {
                    source.querySelector('.log-tooltip-title').firstChild.data = 'Updated event';
                    source.querySelector('tr:last-child td:last-child').textContent = 'Updated message';
                }
                """);
            await Assertions.Expect(tooltip).ToHaveCountAsync(1);
            await Assertions.Expect(tooltip.Locator(".log-tooltip-title")).ToHaveTextAsync("Updated event");
            await Assertions.Expect(tooltip.Locator("td")).ToHaveTextAsync(
                ["Level", "Information", "Timestamp", "2026-01-01 10:00:00", "Message", "Updated message"]);

            await page.Keyboard.PressAsync("Escape");
            await Assertions.Expect(tooltip).ToHaveCountAsync(0);
            await anchor.FocusAsync();
            await Assertions.Expect(tooltip.Locator(".log-tooltip-title")).ToHaveTextAsync("Updated event");
            // Fluent gives a new tooltip its id after it's connected, so read the id once it has one.
            await Assertions.Expect(tooltip).ToHaveAttributeAsync("id", new Regex(".+"));
            await Assertions.Expect(anchor).ToHaveAttributeAsync("aria-describedby", (await tooltip.GetAttributeAsync("id"))!);

            await anchor.EvaluateAsync("""
                anchor => {
                    const source = document.createElement('div');
                    source.id = 'replacement-tooltip-probe';
                    source.hidden = true;
                    const paragraph = document.createElement('p');
                    paragraph.textContent = 'Another preview';
                    source.appendChild(paragraph);
                    document.body.appendChild(source);
                    anchor.dataset.tooltipContent = source.id;
                }
                """);
            await Assertions.Expect(tooltip.Locator("p")).ToHaveTextAsync("Another preview");
            await Assertions.Expect(page.Locator(".dashboard-tooltip.log-tooltip")).ToHaveCountAsync(0);
            await page.Locator("#replacement-tooltip-probe p").EvaluateAsync("paragraph => paragraph.firstChild.data = 'Updated preview'");
            await Assertions.Expect(tooltip.Locator("p")).ToHaveTextAsync("Updated preview");

            await anchor.EvaluateAsync("""
                anchor => {
                    delete anchor.dataset.tooltipContent;
                    anchor.dataset.tooltip = 'Plain text';
                }
                """);
            await Assertions.Expect(tooltip).ToHaveTextAsync("Plain text");
            await Assertions.Expect(tooltip.Locator(":scope > span")).ToHaveTextAsync("Plain text");
            await anchor.EvaluateAsync("anchor => anchor.remove()");
            await Assertions.Expect(tooltip).ToHaveCountAsync(0);
            await Assertions.Expect(source.Locator(".log-tooltip-title")).ToHaveTextAsync("Updated event");
            await Assertions.Expect(source).ToBeHiddenAsync();
        });
    }

    [Fact]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task ReconnectModal_RemainsAboveInteractionDialog()
    {
        await RunTestAsync(async page =>
        {
            await GoToResourcesAndWaitAsync(page);
            await page.WaitForFunctionAsync("() => customElements.get('fluent-dialog') !== undefined").DefaultTimeout();
            await page.EvaluateAsync("""
                async () => {
                    const reconnect = document.getElementById('components-reconnect-modal');
                    const retry = document.createElement('button');
                    retry.id = 'reconnect-layer-probe';
                    retry.textContent = 'Reconnect action';
                    retry.addEventListener('click', () => retry.dataset.clicked = 'true');
                    reconnect.appendChild(retry);

                    const interaction = document.createElement('fluent-dialog');
                    interaction.id = 'interaction-layer-probe';
                    interaction.setAttribute('type', 'alert');
                    const action = document.createElement('button');
                    action.id = 'interaction-layer-action';
                    action.textContent = 'Interaction action';
                    action.addEventListener('click', () => action.dataset.clicked = 'true');
                    interaction.appendChild(action);
                    document.body.appendChild(interaction);

                    reconnect.dispatchEvent(new CustomEvent('components-reconnect-state-changed', {
                        detail: { state: 'show' }
                    }));
                    await new Promise(resolve => {
                        interaction.addEventListener('toggle', resolve, { once: true });
                        interaction.show();
                    });
                }
                """);

            await page.WaitForFunctionAsync("""
                () => {
                    const interaction = document.getElementById('interaction-layer-probe');
                    return interaction.shadowRoot?.querySelector('dialog')?.open;
                }
                """).DefaultTimeout();

            var reconnectAction = page.Locator("#reconnect-layer-probe");
            await reconnectAction.ClickAsync();
            await Assertions.Expect(reconnectAction).ToHaveAttributeAsync("data-clicked", "true");

            await page.EvaluateAsync("""
                () => {
                    document.getElementById('components-reconnect-modal').dispatchEvent(
                        new CustomEvent('components-reconnect-state-changed', { detail: { state: 'hide' } }));
                }
                """);

            await Assertions.Expect(page.Locator("#components-reconnect-modal")).Not.ToHaveAttributeAsync("open", "");
            var interactionAction = page.Locator("#interaction-layer-action");
            await interactionAction.ClickAsync();
            await Assertions.Expect(interactionAction).ToHaveAttributeAsync("data-clicked", "true");
        });
    }

    [Fact]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task TerminalDockShortcut_UsesUnmodifiedPhysicalKeyAndPreservesInputGuard()
    {
        await RunTestAsync(async page =>
        {
            await page.SetContentAsync("""
                <button id="control">Control</button>
                <input id="input">
                <textarea id="textarea"></textarea>
                <div id="terminal"></div>
                <fluent-text-field id="fluent"></fluent-text-field>
                """);
            await page.AddScriptTagAsync(new() { Path = Path.Combine(AppContext.BaseDirectory, "wwwroot", "js", "app.js") });
            await page.EvaluateAsync("""
                () => {
                    const host = document.getElementById('fluent');
                    host.attachShadow({ mode: 'open' }).appendChild(document.createElement('input'));
                    const terminal = document.getElementById('terminal');
                    const view = terminal.attachShadow({ mode: 'open' }).appendChild(document.createElement('div'));
                    view.attachShadow({ mode: 'open' }).appendChild(document.createElement('textarea'));
                }
                """);

            var cases = new (string Key, string Code, bool Shift, bool Alt, bool Ctrl, bool Meta, string Target, int? Expected)[]
            {
                ("`", "Backquote", false, false, false, false, "control", 400),
                ("^", "Backquote", false, false, false, false, "control", 400),
                ("Dead", "Backquote", false, false, false, false, "control", 400),
                ("`", "BracketRight", false, false, false, false, "control", null),
                ("~", "Backquote", true, false, false, false, "control", null),
                ("`", "Backquote", false, true, false, false, "control", null),
                ("`", "Backquote", false, false, true, false, "control", null),
                ("`", "Backquote", false, false, false, true, "control", null),
                ("`", "Backquote", false, false, false, false, "input", null),
                ("^", "Backquote", false, false, false, false, "textarea", null),
                ("`", "Backquote", false, false, false, false, "terminal", null),
                ("^", "Backquote", false, false, false, false, "fluent", null),
                ("S", "KeyS", true, false, false, false, "control", 110),
                ("r", "KeyR", false, false, false, false, "control", 200)
            };

            foreach (var (key, code, shiftKey, altKey, ctrlKey, metaKey, target, expected) in cases)
            {
                var shortcuts = await page.EvaluateAsync<int[]>("""
                    ({ key, code, shiftKey, altKey, ctrlKey, metaKey, target }) => {
                        const calls = [];
                        const registration = window.registerGlobalKeydownListener({
                            invokeMethodAsync: (_, shortcut) => {
                                calls.push(shortcut);
                                return Promise.resolve();
                            }
                        });
                        try {
                            const host = document.getElementById(target);
                            let input = host;
                            while (input.shadowRoot?.firstElementChild) {
                                input = input.shadowRoot.firstElementChild;
                            }
                            input.focus();
                            input.dispatchEvent(new KeyboardEvent('keydown', {
                                key, code, shiftKey, altKey, ctrlKey, metaKey,
                                bubbles: true, composed: true
                            }));
                            return calls;
                        } finally {
                            window.unregisterGlobalKeydownListener(registration);
                        }
                    }
                    """, new { key, code, shiftKey, altKey, ctrlKey, metaKey, target });
                Assert.Equal(expected is { } shortcut ? [shortcut] : Array.Empty<int>(), shortcuts);
            }
        });
    }

    [Fact]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task GridColumn_DoubleClickResizeHandle_AutoFitsColumnWidth()
    {
        await RunTestAsync(async page =>
        {
            var baseUrl = DashboardServerFixture.DashboardApp.FrontendSingleEndPointAccessor().GetResolvedAddress();
            await page.Context.AddCookiesAsync([new Cookie { Name = "Aspire_Dashboard_Experience", Value = "legacy", Url = baseUrl }]);
            await page.GotoAsync("/legacy/resources");
            await page.GetByRole(AriaRole.Tab, new() { Name = Resources.Layout.NavMenuParametersTab, Exact = true }).ClickAsync();

            var grid = page.Locator(".main-grid").First;
            await Assertions.Expect(grid.Locator(".resource-row").First).ToBeVisibleAsync();

            // Guard against a Fluent UI Blazor rename of the resize handle marker: the auto-fit
            // double-click handler keys off exactly these selectors, so if none is present the
            // feature is silently dead and this count assertion surfaces it.
            var handles = page.Locator(".main-grid [actual-resize-handle], .main-grid .resize-handle, .main-grid .col-width-draghandle");
            Assert.True(await handles.CountAsync() > 0, "Expected at least one grid resize handle to be rendered.");

            // Fluent writes the resolved template from GetGridTemplateColumns() to the table's *inline*
            // grid-template-columns, so it is already populated at rest (typically with fr/auto tracks).
            // auto-fit resolves every track to concrete px and rewrites the inline template with the
            // fitted column, so the reliable end-to-end proof is that the inline value *changes* and is
            // now an explicit px template.
            var inlineBefore = await grid.EvaluateAsync<string>("el => el.style.gridTemplateColumns");

            // The auto-fit behavior is a delegated document-level "dblclick" listener that keys off
            // e.target.closest(".resize-handle"). Dispatch the dblclick straight onto the handle element
            // rather than a pixel-precise click: the handle is a thin edge bar whose pointer-events are
            // gated, so a coordinate double-click retargets to the header behind it and never reaches the
            // handler. DispatchEvent fires a real bubbling MouseEvent on the exact element, which bubbles
            // to the document listener exactly as a user double-click on the handle would.
            await handles.First.DispatchEventAsync("dblclick");

            await page.WaitForFunctionAsync(
                @"before => {
                    const g = document.querySelector('.main-grid');
                    if (!g) { return false; }
                    const now = g.style.gridTemplateColumns;
                    return now.length > 0 && now !== before && /px/.test(now);
                }",
                inlineBefore)
                .DefaultTimeout();
        });
    }

    [Fact]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task ScrollButtons_DelaysRevealAndLayout_AndCancelsRevealWhenScrolledToBottom()
    {
        await RunTestAsync(async page =>
        {
            await GoToResourcesAndWaitAsync(page);
            await page.Clock.InstallAsync();
            await page.Clock.PauseAtAsync(DateTime.UtcNow.AddMinutes(1));
            await AddScrollRegionAsync(page);
            // Count geometry reads to distinguish deferred layout from merely hiding the button.
            await page.EvaluateAsync("""
                () => {
                    const region = document.getElementById('scroll-region');
                    const getBounds = region.getBoundingClientRect.bind(region);
                    window.__scrollRegionLayoutReads = 0;
                    region.getBoundingClientRect = () => {
                        window.__scrollRegionLayoutReads++;
                        return getBounds();
                    };
                }
                """);
            var bottomButton = page.Locator(".scroll-to-bottom");
            await Assertions.Expect(bottomButton).ToHaveCountAsync(1);

            await page.Clock.RunForAsync(100);
            Assert.Equal("scroll-button scroll-to-bottom", await bottomButton.GetAttributeAsync("class"));
            Assert.Equal(0, await page.EvaluateAsync<int>("() => window.__scrollRegionLayoutReads"));

            // Restore the initial bottom position while the reveal timer is still pending.
            await page.EvaluateAsync("""
                () => {
                    const region = document.getElementById('scroll-region');
                    region.scrollTop = region.scrollHeight;
                    region.dispatchEvent(new Event('scroll'));
                }
                """);
            await page.Clock.RunForAsync(300);
            Assert.Equal("scroll-button scroll-to-bottom", await bottomButton.GetAttributeAsync("class"));
            Assert.Equal(0, await page.EvaluateAsync<int>("() => window.__scrollRegionLayoutReads"));

            await page.EvaluateAsync("""
                () => {
                    document.getElementById('scroll-region').style.top = '160px';
                    window.dispatchEvent(new Event('resize'));
                }
                """);
            await page.Clock.RunForAsync(300);
            Assert.Equal(0, await page.EvaluateAsync<int>("() => window.__scrollRegionLayoutReads"));

            await page.EvaluateAsync("""
                () => {
                    const region = document.getElementById('scroll-region');
                    region.scrollTop = 0;
                    region.dispatchEvent(new Event('scroll'));
                }
                """);
            await page.Clock.RunForAsync(199);
            Assert.Equal("scroll-button scroll-to-bottom", await bottomButton.GetAttributeAsync("class"));
            Assert.Equal(0, await page.EvaluateAsync<int>("() => window.__scrollRegionLayoutReads"));
            await page.Clock.RunForAsync(50);
            Assert.Equal("scroll-button scroll-to-bottom is-visible", await bottomButton.GetAttributeAsync("class"));
            Assert.True(await page.EvaluateAsync<int>("() => window.__scrollRegionLayoutReads") > 0);
            Assert.Equal(172, (await page.Locator(".scroll-buttons").BoundingBoxAsync())!.Y);
            await page.Clock.ResumeAsync();
            await Assertions.Expect(bottomButton).ToBeVisibleAsync();
        });
    }

    [Fact]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task ScrollButtons_ContentResizeUpdatesVisibility()
    {
        await RunTestAsync(async page =>
        {
            await GoToResourcesAndWaitAsync(page);
            await AddScrollRegionAsync(page);
            var bottomButton = page.Locator(".scroll-to-bottom");
            await Assertions.Expect(bottomButton).ToBeVisibleAsync();
            var clientHeight = await page.Locator("#scroll-region").EvaluateAsync<int>("region => region.clientHeight");

            // Change only the content height: no scroll or resize event tells the button to update.
            await page.Locator("#scroll-region .scroll-content").EvaluateAsync("content => content.style.height = '500px'");
            await Assertions.Expect(bottomButton).ToBeHiddenAsync();
            Assert.Equal(clientHeight, await page.Locator("#scroll-region").EvaluateAsync<int>("region => region.clientHeight"));

            await page.Locator("#scroll-region .scroll-content").EvaluateAsync("content => content.style.height = '2000px'");
            await Assertions.Expect(bottomButton).ToBeVisibleAsync();
        });
    }

    [Fact]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task ScrollButtons_SmoothScrollHidesImmediately_AndReachesNewBottom()
    {
        await RunTestAsync(async page =>
        {
            await GoToResourcesAndWaitAsync(page);
            await page.EmulateMediaAsync(new() { ReducedMotion = ReducedMotion.NoPreference });
            await AddScrollRegionAsync(page);
            var bottomButton = page.Locator(".scroll-to-bottom");
            await Assertions.Expect(bottomButton).ToBeVisibleAsync();

            // Headless browsers can skip every intermediate compositor frame under load. Capture the native
            // smooth-scroll request, then dispatch scrollend explicitly so the content-growth behavior is deterministic.
            await page.EvaluateAsync("""
                () => {
                    const region = document.getElementById('scroll-region');
                    region.scrollTo = options => {
                        window.__scrollToBottomRequest = options;
                    };
                    document.querySelector('.scroll-to-bottom').addEventListener('click', event => {
                        window.__hiddenOnClick = event.currentTarget.hidden;
                    }, { once: true });
                }
                """);

            await bottomButton.ClickAsync();
            Assert.True(await page.EvaluateAsync<bool>("() => window.__hiddenOnClick"));
            Assert.True(await page.EvaluateAsync<bool>("""
                () => {
                    const region = document.getElementById('scroll-region');
                    return window.__scrollToBottomRequest.behavior === 'smooth' &&
                        window.__scrollToBottomRequest.top === region.scrollHeight;
                }
                """));
            await page.EvaluateAsync("""
                () => {
                    const region = document.getElementById('scroll-region');
                    region.querySelector('.scroll-content').style.height = '4000px';
                    region.dispatchEvent(new Event('scrollend'));
                }
                """);
            await page.WaitForFunctionAsync("""
                () => {
                    const region = document.getElementById('scroll-region');
                    return region.scrollHeight >= 4000 &&
                        Math.abs(region.scrollHeight - region.clientHeight - region.scrollTop) < 1;
                }
                """).DefaultTimeout();
            await Assertions.Expect(bottomButton).ToBeHiddenAsync();
        });
    }

    [Fact]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task ScrollButtons_ReducedMotionScrollsImmediately()
    {
        await RunTestAsync(async page =>
        {
            await GoToResourcesAndWaitAsync(page);
            await page.EmulateMediaAsync(new() { ReducedMotion = ReducedMotion.Reduce });
            await AddScrollRegionAsync(page);
            var bottomButton = page.Locator(".scroll-to-bottom");
            await Assertions.Expect(bottomButton).ToBeVisibleAsync();

            // Observe the destination in the same click dispatch, before any animation can run.
            await page.EvaluateAsync("""
                () => document.querySelector('.scroll-to-bottom').addEventListener('click', event => {
                    const region = document.getElementById('scroll-region');
                    window.__remainingOnClick = region.scrollHeight - region.clientHeight - region.scrollTop;
                    window.__hiddenOnClick = event.currentTarget.hidden;
                }, { once: true })
                """);
            await bottomButton.ClickAsync();
            Assert.Equal(0, await page.EvaluateAsync<int>("() => window.__remainingOnClick"));
            Assert.True(await page.EvaluateAsync<bool>("() => window.__hiddenOnClick"));
        });
    }

    [Fact]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task ScrollButtons_ModalBackdropBlocksClicks()
    {
        await RunTestAsync(async page =>
        {
            await GoToResourcesAndWaitAsync(page);
            await page.EmulateMediaAsync(new() { ReducedMotion = ReducedMotion.Reduce });
            await AddScrollRegionAsync(page);
            var bottomButton = page.Locator(".scroll-to-bottom");
            await Assertions.Expect(bottomButton).ToBeVisibleAsync();
            var buttonBounds = (await bottomButton.BoundingBoxAsync())!;
            await page.EvaluateAsync("""
                () => {
                    const dialog = document.createElement('fluent-dialog');
                    dialog.id = 'scroll-overlay-dialog';
                    dialog.setAttribute('type', 'modal');
                    dialog.innerHTML = '<p>Modal content</p>';
                    document.body.appendChild(dialog);
                    dialog.show();
                }
                """);
            await page.WaitForFunctionAsync("() => document.getElementById('scroll-overlay-dialog').shadowRoot.querySelector('dialog').open").DefaultTimeout();
            await Assertions.Expect(bottomButton).ToBeVisibleAsync();
            await page.Mouse.ClickAsync(buttonBounds.X + buttonBounds.Width / 2, buttonBounds.Y + buttonBounds.Height / 2);
            Assert.Equal(0, await page.Locator("#scroll-region").EvaluateAsync<int>("region => region.scrollTop"));
            Assert.Null(await bottomButton.GetAttributeAsync("hidden"));
            await page.EvaluateAsync("""
                () => {
                    const dialog = document.getElementById('scroll-overlay-dialog');
                    dialog.hide();
                    dialog.remove();
                }
                """);

            await bottomButton.ClickAsync();
            Assert.Equal(0, await page.Locator("#scroll-region").EvaluateAsync<int>(
                "region => region.scrollHeight - region.clientHeight - region.scrollTop"));
        });
    }

    [Fact]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task ScrollButtons_CustomElementReplacesRegistration_AndCleansUpOnDisconnect()
    {
        await RunTestAsync(async page =>
        {
            await GoToResourcesAndWaitAsync(page);
            await page.EmulateMediaAsync(new() { ReducedMotion = ReducedMotion.Reduce });
            await AddScrollRegionAsync(page);
            await Assertions.Expect(page.Locator(".scroll-buttons")).ToHaveCountAsync(1);

            await page.EvaluateAsync("""
                () => {
                    window.__previousScrollButton = document.querySelector('.scroll-to-bottom');
                    const parent = document.getElementById('scroll-owner').cloneNode(true);
                    parent.id = 'next-scroll-owner';
                    parent.firstElementChild.id = 'next-scroll-region';
                    document.body.appendChild(parent);
                    window.__nextScrollRoot = document.querySelector('.scroll-buttons');
                }
                """);
            await Assertions.Expect(page.Locator(".scroll-buttons")).ToHaveCountAsync(1);
            Assert.False(await page.EvaluateAsync<bool>("() => window.__previousScrollButton.isConnected"));

            // Click the replaced button while its container is still connected. A detached
            // container reports scrollTop=0 even if cleanup failed to remove the click listener.
            Assert.Equal(new[] { 0, 0 }, await page.EvaluateAsync<int[]>("""
                () => {
                    window.__previousScrollButton.click();
                    return [document.getElementById('scroll-region').scrollTop,
                        document.getElementById('next-scroll-region').scrollTop];
                }
                """));

            await page.EvaluateAsync("() => document.getElementById('scroll-owner').remove()");
            Assert.True(await page.EvaluateAsync<bool>("() => window.__nextScrollRoot.isConnected"));
            await page.Locator(".scroll-to-bottom").ClickAsync();
            Assert.Equal(0, await page.Locator("#next-scroll-region").EvaluateAsync<int>(
                "region => region.scrollHeight - region.clientHeight - region.scrollTop"));

            await page.EvaluateAsync("""
                () => {
                    window.__removedScrollOwner = document.getElementById('next-scroll-owner');
                    window.__removedScrollOwner.remove();
                }
                """);
            await Assertions.Expect(page.Locator(".scroll-buttons")).ToHaveCountAsync(0);

            await page.EvaluateAsync("() => document.body.appendChild(window.__removedScrollOwner)");
            await Assertions.Expect(page.Locator(".scroll-buttons")).ToHaveCountAsync(1);

            Assert.Equal(0, await page.EvaluateAsync<int>("""
                () => {
                    const region = document.getElementById('next-scroll-region');
                    region.scrollTop = 0;
                    const button = document.querySelector('.scroll-to-bottom');
                    region.querySelector('aspire-scroll-to-bottom').remove();
                    button.click();
                    return region.scrollTop;
                }
                """));
            await Assertions.Expect(page.Locator(".scroll-buttons")).ToHaveCountAsync(0);
        });
    }

    [Fact]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task ScrollButtons_RemainInactiveWhenRegionHasNoRoomForControl()
    {
        await RunTestAsync(async page =>
        {
            await GoToResourcesAndWaitAsync(page);
            await AddScrollRegionAsync(page);
            await Assertions.Expect(page.Locator(".scroll-to-bottom")).ToBeVisibleAsync();
            await page.Locator("#scroll-region").EvaluateAsync("region => region.style.top = '-280px'");
            var buttons = page.Locator(".scroll-buttons");
            await Assertions.Expect(buttons).ToHaveCountAsync(1);
            await page.EvaluateAsync("() => window.dispatchEvent(new Event('resize'))");
            await Assertions.Expect(buttons).ToHaveClassAsync("scroll-buttons");
            await Assertions.Expect(page.Locator(".scroll-to-bottom")).ToBeHiddenAsync();

            await page.EvaluateAsync("""
                () => {
                    document.getElementById('scroll-region').style.top = '0';
                    window.dispatchEvent(new Event('resize'));
                }
                """);
            await Assertions.Expect(page.Locator(".scroll-to-bottom")).ToBeVisibleAsync();
        });
    }

    [Fact]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task ScrollButtons_AncestorScrollRepositionsControl()
    {
        await RunTestAsync(async page =>
        {
            await GoToResourcesAndWaitAsync(page);
            await AddScrollRegionAsync(page);
            await page.EvaluateAsync("""
                () => {
                    const owner = document.getElementById('scroll-owner');
                    owner.style.cssText = 'position:fixed;left:0;top:100px;width:450px;height:400px;overflow:auto;';
                    document.getElementById('scroll-region').style.position = 'static';
                    const spacer = document.createElement('div');
                    spacer.style.height = '1000px';
                    owner.appendChild(spacer);
                }
                """);

            var bottomButton = page.Locator(".scroll-to-bottom");
            await Assertions.Expect(bottomButton).ToBeVisibleAsync();
            var originalBounds = (await page.Locator(".scroll-buttons").BoundingBoxAsync())!;
            var region = page.Locator("#scroll-region");
            var originalRegionBounds = (await region.BoundingBoxAsync())!;

            // Scroll the ancestor without synthesizing resize or bubbling scroll events. The
            // window capture listener must follow the container while its contents stay still.
            await page.Locator("#scroll-owner").EvaluateAsync("owner => owner.scrollTop = 40");
            await page.WaitForFunctionAsync("""
                expectedBottom => Math.abs(document.querySelector('.scroll-buttons').getBoundingClientRect().bottom - expectedBottom) < 1
                """, (double)(originalBounds.Y + originalBounds.Height - 40)).DefaultTimeout();
            Assert.Equal(originalBounds.Y, (await page.Locator(".scroll-buttons").BoundingBoxAsync())!.Y);
            Assert.Equal(originalRegionBounds.Y - 40, (await region.BoundingBoxAsync())!.Y);
            Assert.Equal(0, await region.EvaluateAsync<int>("element => element.scrollTop"));
            await Assertions.Expect(bottomButton).ToBeVisibleAsync();

            // The region is still inside the viewport, but fully above its ancestor's scrollport.
            await page.Locator("#scroll-owner").EvaluateAsync("owner => owner.scrollTop = 300");
            await Assertions.Expect(page.Locator(".scroll-buttons")).ToHaveClassAsync("scroll-buttons");
            await Assertions.Expect(bottomButton).ToBeHiddenAsync();

            await page.Locator("#scroll-owner").EvaluateAsync("owner => owner.scrollTop = 0");
            await Assertions.Expect(bottomButton).ToBeVisibleAsync();
            await page.WaitForFunctionAsync("""
                expectedBottom => Math.abs(document.querySelector('.scroll-buttons').getBoundingClientRect().bottom - expectedBottom) < 1
                """, (double)(originalBounds.Y + originalBounds.Height)).DefaultTimeout();
            Assert.Equal(originalRegionBounds.Y, (await region.BoundingBoxAsync())!.Y);
            Assert.Equal(0, await region.EvaluateAsync<int>("element => element.scrollTop"));
        });
    }

    [Theory]
    [InlineData("hidden")]
    [InlineData("clip")]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task ScrollButtons_NestedClippingAncestorConstrainsControl(string overflow)
    {
        await RunTestAsync(async page =>
        {
            await GoToResourcesAndWaitAsync(page);
            await AddScrollRegionAsync(page);
            await page.EvaluateAsync("""
                overflow => {
                    const owner = document.getElementById('scroll-owner');
                    owner.style.cssText = 'position:fixed;left:0;top:100px;width:200px;height:200px;border:10px solid;';
                    owner.style.overflow = overflow;
                    const region = document.getElementById('scroll-region');
                    region.style.position = 'static';
                    const wrapper = document.createElement('div');
                    wrapper.style.cssText = 'width:400px;height:300px;overflow:visible;';
                    owner.appendChild(wrapper);
                    wrapper.appendChild(region);
                    window.dispatchEvent(new Event('resize'));
                }
                """, overflow);

            var bottomButton = page.Locator(".scroll-to-bottom");
            await Assertions.Expect(bottomButton).ToBeVisibleAsync();
            var root = page.Locator(".scroll-buttons");
            await page.WaitForFunctionAsync("""
                () => document.querySelector('.scroll-buttons').style.left === '100px'
                """).DefaultTimeout();
            var bounds = (await root.BoundingBoxAsync())!;
            Assert.Equal(100, bounds.X + bounds.Width / 2);
            Assert.Equal(122, bounds.Y);
            Assert.Equal(156, bounds.Height);

            // Keep the region in the viewport, but move it past the horizontal clip edge.
            await page.EvaluateAsync("""
                () => {
                    document.getElementById('scroll-region').style.marginLeft = '220px';
                    window.dispatchEvent(new Event('resize'));
                }
                """);
            await Assertions.Expect(root).ToHaveClassAsync("scroll-buttons");
            await Assertions.Expect(bottomButton).ToBeHiddenAsync();
        });
    }

    [Fact]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task ScrollButtons_UsesLabelFromCustomElement()
    {
        await RunTestAsync(async page =>
        {
            await GoToResourcesAndWaitAsync(page);
            Assert.Null(await page.Locator("body").GetAttributeAsync("data-scroll-to-bottom-label"));
            await AddScrollRegionAsync(page);

            var bottomButton = page.Locator(".scroll-to-bottom");
            await Assertions.Expect(bottomButton).ToHaveAttributeAsync("aria-label", "Jump to latest");
            await Assertions.Expect(bottomButton).ToHaveAttributeAsync("data-tooltip", "Jump to latest");
        });
    }

    private static Task AddScrollRegionAsync(IPage page)
    {
        // Connect the marker before adding the content, as can happen while Blazor applies a render
        // batch. The Resources page has no marker of its own, so registration comes from this one.
        return page.EvaluateAsync("""
            () => {
                const owner = document.createElement('div');
                owner.id = 'scroll-owner';
                owner.innerHTML = `
                    <div id="scroll-region" style="position:fixed;left:0;top:100px;width:400px;height:300px;overflow:auto;">
                        <aspire-scroll-to-bottom hidden data-scroll-to-bottom-label="Jump to latest"></aspire-scroll-to-bottom>
                    </div>`;
                document.body.appendChild(owner);
                const content = document.createElement('div');
                content.className = 'scroll-content';
                content.style.height = '2000px';
                document.getElementById('scroll-region').appendChild(content);
            }
            """);
    }

    private static async Task GoToResourcesAndWaitAsync(IPage page)
    {
        await page.GotoAsync("/");
        await Assertions
            .Expect(page.GetByText(InteractionsDashboardServerFixture.ParentResourceName).First)
            .ToBeVisibleAsync();
    }

    public sealed class InteractionsDashboardServerFixture : DashboardServerFixture
    {
        public const string ParentResourceName = "parentapp";

        protected override IReadOnlyList<ResourceViewModel> Resources =>
        [
            ModelTestHelpers.CreateResource(
                resourceName: ParentResourceName,
                resourceType: KnownResourceTypes.Project,
                state: KnownResourceState.Running,
                urls:
                [
                    new UrlViewModel("http", new Uri("about:blank#parent-url"), isInternal: false, isInactive: false, UrlDisplayPropertiesViewModel.Empty)
                ]),
            ModelTestHelpers.CreateResource(
                resourceName: "connection-string",
                resourceType: KnownResourceTypes.Parameter,
                state: KnownResourceState.Running),
        ];
    }
}
