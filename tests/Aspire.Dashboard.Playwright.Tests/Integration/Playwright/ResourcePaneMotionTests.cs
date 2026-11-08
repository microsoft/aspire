// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Layout;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Tests.Integration.Playwright.Infrastructure;
using Aspire.Dashboard.Utils;
using Aspire.TestUtilities;
using Microsoft.Playwright;
using Xunit;

namespace Aspire.Dashboard.Tests.Integration.Playwright;

[RequiresFeature(TestFeature.Playwright)]
public sealed class ResourcePaneMotionTests(ResourcePaneResponsiveTests.MetadataResourcesFixture fixture)
    : PlaywrightTestsBase<ResourcePaneResponsiveTests.MetadataResourcesFixture>(fixture)
{
    [Theory]
    [InlineData(420, false)]
    [InlineData(560, false)]
    [InlineData(420, true)]
    [InlineData(560, true)]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task CollapseRestore_AnimatesClippedTrackAndRetainsSavedWidth(int width, bool reducedMotion)
    {
        await RunTestAsync(async page =>
        {
            await page.SetViewportSizeAsync(1440, 900);
            await page.EmulateMediaAsync(new() { ReducedMotion = reducedMotion ? ReducedMotion.Reduce : ReducedMotion.NoPreference });
            await page.AddInitScriptAsync($"localStorage.setItem('{BrowserStorageKeys.ResourcePaneWidth}', '{width}')");
            await page.GotoAsync("/resources/cache?pane=tags");
            var separator = page.GetByRole(AriaRole.Separator, new() { Name = Resources.Layout.ResourcePaneResize });
            await Assertions.Expect(separator).ToHaveAttributeAsync("aria-disabled", "false");
            await Assertions.Expect(separator).ToHaveAttributeAsync("aria-valuenow", width.ToString(System.Globalization.CultureInfo.InvariantCulture));
            var toggle = page.Locator(".resource-header-row .resource-pane-toggle-button");
            var pane = page.Locator(".resource-pane");
            var originalRow = await page.Locator(".resource-row[data-resource-name]").First.BoundingBoxAsync();
            Assert.NotNull(originalRow);
            for (var iteration = 0; iteration < 3; iteration++)
            {
                foreach (var collapse in new[] { true, false })
                {
                    await page.EvaluateAsync("""
                        () => {
                            const layout=document.querySelector('.resources-layout');
                            window.collapseFrames=new Promise(resolve => {
                                const observer=new MutationObserver(() => {
                                    observer.disconnect();
                                    const frames=[];
                                    const sample=() => {
                                        const pane=layout.querySelector(':scope > .resource-pane'), row=pane?.querySelector('.resource-row[data-resource-name]');
                                        const b=pane?.getBoundingClientRect(),r=row?.getBoundingClientRect(),
                                            content=layout.querySelector('.resource-content'),c=content.getBoundingClientRect(),
                                            workspace=layout.getBoundingClientRect();
                                        // Resolved tracks look like "420px 1020px". The first track still
                                        // animates after its pane is unmounted.
                                        const trackWidth=parseFloat(getComputedStyle(layout).gridTemplateColumns);
                                        frames.push([trackWidth,r?.width??0,r?.height??0,document.documentElement.scrollWidth,
                                            document.documentElement.clientWidth,c.width,c.left,c.right,workspace.left,
                                            workspace.right,b?.right??workspace.left+trackWidth,getComputedStyle(content).transform==='none'?0:1]);
                                        if(layout.getAnimations().some(a => a.playState==='running')) requestAnimationFrame(sample);
                                        else resolve(frames);
                                    };
                                    requestAnimationFrame(sample);
                                });
                                observer.observe(layout,{attributes:true,attributeFilter:['class']});
                            });
                        }
                        """);
                    await toggle.FocusAsync();
                    await toggle.PressAsync("Enter");
                    var frames = await page.EvaluateAsync<double[][]>("() => window.collapseFrames");
                    Assert.NotEmpty(frames);
                    Assert.Equal(collapse ? 0 : width, frames[^1][0]);
                    Assert.Equal(!reducedMotion, frames.Any(f => f[0] > .1 && f[0] < width - .1));
                    for (var i = 1; i < frames.Length; i++)
                    {
                        Assert.True(collapse ? frames[i][0] <= frames[i - 1][0] : frames[i][0] >= frames[i - 1][0]);
                        Assert.True(collapse ? frames[i][5] >= frames[i - 1][5] : frames[i][5] <= frames[i - 1][5]);
                    }
                    Assert.All(frames, frame =>
                    {
                        if (frame[1] > 0)
                        {
                            Assert.Equal(originalRow.Width, frame[1]);
                            Assert.Equal(originalRow.Height, frame[2]);
                        }
                        else
                        {
                            Assert.True(collapse);
                            Assert.Equal(0, frame[2]);
                        }
                        Assert.True(frame[3] <= frame[4]);
                        Assert.InRange(Math.Abs(frame[7] - frame[9]), 0, .5);
                        Assert.InRange(Math.Abs(frame[6] - frame[10]), 0, .5);
                        Assert.InRange(Math.Abs(frame[5] - (frame[9] - frame[8] - frame[0])), 0, .5);
                        Assert.Equal(0, frame[11]);
                    });
                    await Assertions.Expect(toggle).ToBeFocusedAsync();
                    await Assertions.Expect(toggle).ToHaveAttributeAsync("aria-expanded", collapse ? "false" : "true");
                    if (collapse)
                    {
                        await Assertions.Expect(pane).ToHaveCountAsync(0);
                        await Assertions.Expect(separator).ToHaveCountAsync(0);
                        var content = await page.Locator(".resource-content").BoundingBoxAsync();
                        var layout = await page.Locator(".resources-layout").BoundingBoxAsync();
                        Assert.NotNull(content);
                        Assert.NotNull(layout);
                        Assert.Equal(layout.Width, content.Width);
                    }
                    else
                    {
                        await Assertions.Expect(pane).ToBeVisibleAsync();
                        await Assertions.Expect(pane).Not.ToHaveAttributeAsync("inert", "");
                        await Assertions.Expect(separator).ToHaveAttributeAsync("aria-valuenow", width.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    }
                    Assert.Equal(0, await page.Locator(".pane-full-canvas,.pane-selection-reveal").CountAsync());
                }
            }
        });
    }

    [Theory]
    [InlineData(1440, false, false)]
    [InlineData(1440, false, true)]
    [InlineData(2000, false, false)]
    [InlineData(2000, false, true)]
    [InlineData(2000, true, false)]
    [InlineData(2000, true, true)]
    [InlineData(769, false, false)]
    [InlineData(769, true, true)]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task SelectionReveal_PreservesRigidDetailsAndFocusesAfterSettling(int width, bool reducedMotion, bool keyboard)
    {
        await RunTestAsync(async page =>
        {
            await page.SetViewportSizeAsync(width, 1000);
            await page.EmulateMediaAsync(new() { ReducedMotion = reducedMotion ? ReducedMotion.Reduce : ReducedMotion.NoPreference });
            await page.GotoAsync("/resources/cache?pane=tags");
            var pane = page.Locator(".resource-pane");
            if (width <= 768)
            {
                await page.Locator(".resource-picker").ClickAsync();
                await Assertions.Expect(pane).ToBeVisibleAsync();
            }
            else
            {
                // The separator handles keys only once its script is registered. Before the minimum width was lowered
                // below the default, a Home press that came too early went unnoticed.
                var resizer = page.GetByRole(AriaRole.Separator, new() { Name = Resources.Layout.ResourcePaneResize });
                await Assertions.Expect(resizer).ToHaveAttributeAsync("aria-disabled", "false");
                await resizer.PressAsync("Home");
                await Assertions.Expect(resizer).ToHaveAttributeAsync("aria-valuenow", ResourcesLayout.MinimumPaneWidthPx.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            await ResourcePaneNavigation.ToggleResourceListAsync(page);
            await page.WaitForFunctionAsync("""
                () => Math.abs(document.querySelector('.resource-pane').getBoundingClientRect().width -
                    document.querySelector('.resources-layout').getBoundingClientRect().width) < .1
                """);
            var navBox = await page.Locator(".resource-tabs-nav").BoundingBoxAsync();
            var toolbarBox = await pane.Locator(".resource-pane-head").BoundingBoxAsync();
            Assert.NotNull(navBox);
            Assert.NotNull(toolbarBox);
            await page.EvaluateAsync("""
                () => {
                    window.selectionRevealFrames = new Promise(resolve => {
                        const layout = document.querySelector('.resources-layout');
                        const observer = new MutationObserver(() => {
                            if (!layout.classList.contains('pane-selection-reveal')) return;
                            observer.disconnect();
                            const frames = [], start = performance.now();
                            window.selectionRevealTitles = [];
                            window.selectionRevealResources = [];
                            window.selectionRevealSubscriptions = [];
                            const sample = () => {
                                const pane = layout.querySelector('.resource-pane').getBoundingClientRect();
                                const content = layout.querySelector('.resource-content');
                                const details = content.getBoundingClientRect();
                                const nav = document.querySelector('.resource-tabs-nav').getBoundingClientRect();
                                const toolbar = layout.querySelector('.resource-pane-head').getBoundingClientRect();
                                frames.push([pane.width, details.width, details.x, content.inert ? 1 : 0,
                                    nav.x, nav.y, nav.width, nav.height, toolbar.y, toolbar.height]);
                                window.selectionRevealTitles.push(layout.querySelector('h1.resource-title')?.textContent.trim());
                                window.selectionRevealResources.push(layout.querySelector('.resource-overview')?.dataset.resourceName);
                                window.selectionRevealSubscriptions.push(layout.querySelector('.resource-overview')?.dataset.resourceSubscription);
                                if (layout.classList.contains('pane-selection-reveal') && performance.now() - start < 2000)
                                    requestAnimationFrame(sample);
                                else resolve(frames);
                            };
                            requestAnimationFrame(sample);
                        });
                        observer.observe(layout, {attributes:true, attributeFilter:['class']});
                    });
                }
                """);
            var row = pane.Locator("[data-resource-name='api'] .resource-row-link").First;
            if (keyboard)
            {
                await row.FocusAsync();
                await row.PressAsync("Space");
            }
            else
            {
                await row.ClickAsync();
            }
            var frames = await page.EvaluateAsync<double[][]>("() => window.selectionRevealFrames");
            await Assertions.Expect(page.Locator(".pane-selection-reveal")).ToHaveCountAsync(0);
            await Assertions.Expect(page.Locator("h1.resource-title")).ToHaveTextAsync("api");
            await Assertions.Expect(page.Locator("h1.resource-title")).ToBeFocusedAsync();
            await Assertions.Expect(page.Locator(".resource-content")).Not.ToHaveAttributeAsync("inert", "");
            var layoutBox = await page.Locator(".resources-layout").BoundingBoxAsync();
            Assert.NotNull(layoutBox);
            var expectedDetailsWidth = layoutBox.Width - (width > 768 ? ResourcesLayout.MinimumPaneWidthPx : 0);
            Assert.NotEmpty(frames);
            Assert.All(await page.EvaluateAsync<string[]>("() => window.selectionRevealTitles"),
                title => Assert.Equal("api", title));
            Assert.All(await page.EvaluateAsync<string[]>("() => window.selectionRevealResources"),
                resource => Assert.Equal("api", resource));
            Assert.All(await page.EvaluateAsync<string[]>("() => window.selectionRevealSubscriptions"),
                resource => Assert.Equal("api", resource));
            Assert.All(frames, frame =>
            {
                Assert.InRange(Math.Abs(frame[1] - expectedDetailsWidth), 0, .1);
                Assert.Equal(navBox.X, frame[4]);
                Assert.Equal(navBox.Y, frame[5]);
                Assert.Equal(navBox.Width, frame[6]);
                Assert.Equal(navBox.Height, frame[7]);
                if (frame[0] > 0)
                {
                    Assert.Equal(toolbarBox.Y, frame[8]);
                    Assert.Equal(toolbarBox.Height, frame[9]);
                }
            });
            if (width > 768)
            {
                var intermediate = frames.Where(frame => frame[0] > 420.1 && frame[0] < layoutBox.Width - .1).ToList();
                Assert.Equal(!reducedMotion, intermediate.Count > 0);
                Assert.All(intermediate, frame =>
                {
                    Assert.Equal(1, frame[3]);
                    Assert.InRange(Math.Abs(frame[2] - frame[0]), 0, .1);
                });
            }
            else
            {
                await Assertions.Expect(pane).Not.ToBeVisibleAsync();
            }
        });
    }

    [Theory]
    [InlineData("selection")]
    [InlineData("reverse")]
    [InlineData("back")]
    [InlineData("reload")]
    [InlineData("focus")]
    [InlineData("early-focus")]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task SelectionReveal_InterruptionDoesNotRestoreStaleContentOrStealFocus(string interruption)
    {
        await RunTestAsync(async page =>
        {
            await page.SetViewportSizeAsync(2000, 1000);
            await page.GotoAsync("/resources/cache?pane=tags");
            var pane = page.Locator(".resource-pane");
            await ResourcePaneNavigation.ToggleResourceListAsync(page);
            await page.WaitForFunctionAsync("() => document.querySelector('.resource-pane').getBoundingClientRect().width === 2000");
            await page.EvaluateAsync("""
                () => {
                    window.interruptedSelectionFrames = [];
                    const start = performance.now();
                    window.recordInterruptedSelectionFrame = () => {
                        const layout = document.querySelector('.resources-layout');
                        if (!layout.classList.contains('pane-full-canvas')) {
                            const selected = layout.querySelector('.resource-row:has(.resource-row-link[aria-current="page"])');
                            if (selected) window.interruptedSelectionFrames.push([
                                selected?.dataset.resourceName,
                                layout.querySelector('.resource-overview')?.dataset.resourceName,
                                selected?.querySelector('.resource-row-name')?.textContent.trim(),
                                layout.querySelector('h1.resource-title')?.textContent.trim(),
                                layout.querySelector('.resource-overview')?.dataset.resourceSubscription
                            ]);
                        }
                    };
                    const sample = () => {
                        window.recordInterruptedSelectionFrame();
                        if (performance.now()-start < 2000) requestAnimationFrame(sample);
                    };
                    requestAnimationFrame(sample);
                }
                """);
            if (interruption == "early-focus")
            {
                // Move focus before the server can start its reveal interop, not only
                // after the reveal class has reached the browser.
                await pane.GetByRole(AriaRole.Searchbox).EvaluateAsync("""
                    field => document.querySelector('.resources-layout').addEventListener('click',
                        () => field.focus(), { once:true })
                    """);
            }
            // Interrupt in the browser when the reveal begins. A test-host round trip can
            // miss the entire transition, especially with shorter motion or a busy runner.
            await pane.GetByRole(AriaRole.Searchbox).EvaluateAsync("""
                (field, interruption) => {
                    const layout = document.querySelector('.resources-layout');
                    window.revealInterruptionObserved = false;
                    const observer = new MutationObserver(() => {
                        if (!layout.classList.contains('pane-selection-reveal')) return;
                        observer.disconnect();
                        window.revealInterruptionObserved = true;
                        window.recordInterruptedSelectionFrame();
                        switch (interruption) {
                            case 'selection':
                                layout.querySelector('[data-resource-name="frontend"] .resource-row-link').click();
                                break;
                            case 'reverse':
                                layout.querySelector('.detail-page-close').click();
                                break;
                            case 'back':
                                history.back();
                                break;
                            case 'reload':
                                location.reload();
                                break;
                            case 'focus':
                                field.focus();
                                break;
                        }
                    });
                    observer.observe(layout, { attributes:true, attributeFilter:['class'] });
                }
                """, interruption);
            await pane.Locator("[data-resource-name='api'] .resource-row-link").First.ClickAsync();
            if (interruption == "reload")
            {
                await page.WaitForFunctionAsync("() => performance.getEntriesByType('navigation')[0]?.type === 'reload'");
            }
            else
            {
                await page.WaitForFunctionAsync("() => window.revealInterruptionObserved");
            }
            await Assertions.Expect(page.Locator(".pane-selection-reveal")).ToHaveCountAsync(0);
            var expected = interruption == "selection" ? "frontend" : interruption == "back" ? Resources.Layout.ResourceHeaderAllResources : "api";
            if (interruption == "reverse")
            {
                await Assertions.Expect(page.Locator(".resource-content")).ToHaveAttributeAsync("inert", "");
                await Assertions.Expect(pane.Locator(".resource-select-all-toggle")).ToHaveAttributeAsync("aria-description", Resources.Layout.ResourcePaneAllSelected);
                await pane.Locator("[data-resource-name='api'] .resource-row-link").First.ClickAsync();
            }
            else
            {
                await Assertions.Expect(page.Locator(".resource-content")).Not.ToHaveAttributeAsync("inert", "");
            }
            await Assertions.Expect(page.Locator("h1.resource-title")).ToHaveTextAsync(expected);
            var interruptionFrames = await page.EvaluateAsync<string[][]>("() => window.interruptedSelectionFrames ?? []");
            // An immediate reversal can restore full canvas before a selected row is rendered.
            if (interruption is not ("reload" or "reverse"))
            {
                Assert.NotEmpty(interruptionFrames);
            }
            Assert.All(interruptionFrames, frame =>
            {
                Assert.Equal(frame[0], frame[1]);
                Assert.Equal(frame[2], frame[3]);
                Assert.Equal(frame[0], frame[4]);
            });
            if (interruption is "focus" or "early-focus")
            {
                await Assertions.Expect(pane.GetByRole(AriaRole.Searchbox)).ToBeFocusedAsync();
            }
            if (interruption is "back" or "reload")
            {
                Assert.Empty(await page.Locator(".resources-layout").EvaluateAsync<string[]>(
                    """
                    el => {
                        const transition = getComputedStyle(el).transitionProperty;
                        return el.getAnimations().map(animation =>
                            `${animation.transitionProperty}: ${animation.playState}; transition=${transition}; class=${el.className}`);
                    }
                    """));
            }
        });
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task CanvasReturn_AfterKindChangeRestoresLogicalWidth(bool reducedMotion, bool keyboard)
    {
        await RunTestAsync(async page =>
        {
            await page.SetViewportSizeAsync(2000, 1000);
            await page.EmulateMediaAsync(new() { ReducedMotion = reducedMotion ? ReducedMotion.Reduce : ReducedMotion.NoPreference });
            await page.GotoAsync("/resources?pane=tags");
            var pane = page.Locator(".resource-pane");
            var parameter = pane.Locator("[data-resource-name='service-key']");
            await Assertions.Expect(parameter).ToBeVisibleAsync();
            var separator = page.GetByRole(AriaRole.Separator, new() { Name = Resources.Layout.ResourcePaneResize });
            await separator.PressAsync("End");
            var expectedWidth = await separator.EvaluateAsync<double>("e => Number(e.getAttribute('aria-valuenow'))");
            var toggle = page.Locator(".detail-page-close, .pane-full-canvas .resource-select-all-toggle");
            await ToggleAsync();
            await Assertions.Expect(page.Locator(".pane-full-canvas")).ToBeVisibleAsync();

            // Filtering must not change the saved width while the resizer is unmounted.
            await page.Locator("#resourcePaneFilterButton").ClickAsync();
            var parameterType = page.GetByRole(AriaRole.Group, new() { Name = Resources.ControlsStrings.ChartContainerFiltersHeader, Exact = true })
                .GetByRole(AriaRole.Checkbox, new() { Name = KnownResourceTypes.Parameter, Exact = true });
            await parameterType.ClickAsync();
            await Assertions.Expect(parameter).ToHaveCountAsync(0);
            await parameterType.ClickAsync();
            await Assertions.Expect(parameter).ToHaveCountAsync(1);
            await page.Keyboard.PressAsync("Escape");
            await ToggleAsync();
            await AssertReturnedAsync();

            await ToggleAsync();
            await Assertions.Expect(page.Locator(".pane-full-canvas")).ToBeVisibleAsync();
            await ToggleAsync();
            await AssertReturnedAsync();
            await page.ReloadAsync();
            await AssertReturnedAsync();

            async Task ToggleAsync()
            {
                if (keyboard)
                {
                    await toggle.FocusAsync();
                    await toggle.PressAsync("Space");
                }
                else
                {
                    await toggle.ClickAsync();
                }
            }

            async Task AssertReturnedAsync()
            {
                await Assertions.Expect(page.Locator(".pane-full-canvas")).ToHaveCountAsync(0);
                await Assertions.Expect(page.Locator(".resource-content")).Not.ToHaveAttributeAsync("inert", "");
                await page.WaitForFunctionAsync("""
                    width => {
                        const pane = document.querySelector('.resource-pane').getBoundingClientRect();
                        const content = document.querySelector('.resource-content').getBoundingClientRect();
                        return Math.abs(pane.width - width) < 1 && Math.abs(content.x - pane.right) < 1;
                    }
                    """, expectedWidth);
                await Assertions.Expect(page.GetByRole(AriaRole.Separator, new() { Name = Resources.Layout.ResourcePaneResize }))
                    .ToHaveAttributeAsync("aria-valuenow", expectedWidth.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        });
    }

    [Theory]
    [InlineData(769, 1024, false)]
    [InlineData(1440, 900, false)]
    [InlineData(769, 1024, true)]
    [InlineData(1440, 900, true)]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task CanvasMotion_MovesOnlyPaneAndCanReverseOrNavigate(int width, int height, bool reducedMotion)
    {
        await RunTestAsync(async page =>
        {
            await page.SetViewportSizeAsync(width, height);
            await page.EmulateMediaAsync(new() { ColorScheme = ColorScheme.Dark, ReducedMotion = reducedMotion ? ReducedMotion.Reduce : ReducedMotion.NoPreference });
            await page.GotoAsync("/resources?pane=tags");
            await Assertions.Expect(page.Locator(".resource-header-actions").GetByRole(AriaRole.Button).First).ToBeVisibleAsync();
            if (width <= 768)
            {
                await page.Locator(".resource-picker").ClickAsync();
            }
            var layout = page.Locator(".resources-layout");
            var pane = page.Locator(".resource-pane");
            var toggle = page.Locator(".detail-page-close, .pane-full-canvas .resource-select-all-toggle");
            var header = page.Locator(".resource-tabs-nav");
            var headerBox = await header.BoundingBoxAsync();
            var toolbarBox = await pane.Locator(".resource-pane-head").BoundingBoxAsync();
            Assert.NotNull(headerBox);
            Assert.NotNull(toolbarBox);
            await page.Context.Tracing.StartAsync(new() { Screenshots = true, Snapshots = true });
            try
            {
                await SampleToggleAsync(expanding: true);
                await SampleToggleAsync(expanding: false);
                await toggle.DispatchEventAsync("click");
                await Assertions.Expect(layout).ToHaveClassAsync(new System.Text.RegularExpressions.Regex("pane-full-canvas"));
                await toggle.DispatchEventAsync("click");
                await Assertions.Expect(layout).Not.ToHaveClassAsync(new System.Text.RegularExpressions.Regex("pane-full-canvas"));
                await toggle.DispatchEventAsync("click");
                await Assertions.Expect(layout).ToHaveClassAsync(new System.Text.RegularExpressions.Regex("pane-full-canvas"));
                await pane.Locator("[data-resource-name='api'] .resource-row-link").First.ClickAsync(new() { Force = true });
                await Assertions.Expect(layout).Not.ToHaveClassAsync(new System.Text.RegularExpressions.Regex("pane-full-canvas|pane-motion-enabled"));
                await page.GoBackAsync();
                await Assertions.Expect(layout).Not.ToHaveClassAsync(new System.Text.RegularExpressions.Regex("pane-full-canvas|pane-motion-enabled"));
                await page.GoForwardAsync();
                await Assertions.Expect(layout).Not.ToHaveClassAsync(new System.Text.RegularExpressions.Regex("pane-full-canvas|pane-motion-enabled"));
            }
            finally
            {
                await page.Context.Tracing.StopAsync(new() { Path = Path.Combine(AppContext.BaseDirectory, $"resource-pane-motion-{width}-{reducedMotion}.zip") });
            }

            async Task SampleToggleAsync(bool expanding)
            {
                var start = await pane.BoundingBoxAsync();
                Assert.NotNull(start);
                await page.EvaluateAsync("""
                    () => {
                        const pane = document.querySelector('.resource-pane');
                        const header = document.querySelector('.resource-context-header');
                        const nav = document.querySelector('.resource-tabs-nav');
                        const layout = document.querySelector('.resources-layout');
                        const before = layout.classList.contains('pane-full-canvas');
                        window.paneMotionFrames = new Promise(resolve => {
                            const frames = [];
                            const observer = new MutationObserver(() => {
                                if (layout.classList.contains('pane-full-canvas') === before) return;
                                observer.disconnect();
                                const start = performance.now();
                                function sample() {
                                    const p = pane.getBoundingClientRect(), h = header.getBoundingClientRect(), n = nav.getBoundingClientRect();
                                    const toolbar = pane.querySelector('.resource-pane-head').getBoundingClientRect();
                                    const controls = [...pane.querySelector('.resource-pane-head').children].map(el => el.getBoundingClientRect()).filter(r => r.width > 0);
                                    const ordered = controls.every((r, i) => i === 0 || r.x >= controls[i - 1].right - 1);
                                    frames.push([p.width, h.x, h.y, h.width, h.height, n.x, n.y, n.width, n.height, toolbar.y, toolbar.height, ordered ? 1 : 0]);
                                    if (performance.now() - start < 500) requestAnimationFrame(sample);
                                    else resolve(frames);
                                }
                                requestAnimationFrame(sample);
                            });
                            observer.observe(layout, {attributes:true, attributeFilter:['class']});
                        });
                    }
                    """);
                await toggle.ClickAsync(new() { Force = true });
                var frames = await page.EvaluateAsync<double[][]>("() => window.paneMotionFrames");
                var end = await pane.BoundingBoxAsync();
                Assert.NotNull(end);
                Assert.NotEqual(start.Width, end.Width);
                Assert.All(frames, frame =>
                {
                    Assert.Equal(headerBox.X, frame[5]);
                    Assert.Equal(headerBox.Y, frame[6]);
                    Assert.Equal(headerBox.Width, frame[7]);
                    Assert.Equal(headerBox.Height, frame[8]);
                    Assert.Equal(toolbarBox.Y, frame[9]);
                    Assert.Equal(toolbarBox.Height, frame[10]);
                    Assert.Equal(1, frame[11]);
                });
                // The full-page list is the overview's List layout, so the Layout toggle shows in both presentations,
                // with List pressed while the list fills the page.
                var layoutToggle = page.GetByRole(AriaRole.Group, new() { Name = "Layout", Exact = true });
                await Assertions.Expect(layoutToggle).ToHaveCountAsync(width > 768 ? 1 : 0);
                if (width > 768)
                {
                    await Assertions.Expect(layoutToggle.GetByRole(AriaRole.Button, new() { Name = Resources.Resources.ResourceOverviewLayoutList, Exact = true }))
                        .ToHaveAttributeAsync("aria-pressed", expanding ? "true" : "false");
                }
                var intermediate = frames.Any(frame => frame[0] > Math.Min(start.Width, end.Width) + 0.1 && frame[0] < Math.Max(start.Width, end.Width) - 0.1);
                Assert.Equal(!reducedMotion, intermediate);
                await page.ScreenshotAsync(new() { Path = Path.Combine(AppContext.BaseDirectory, $"resource-pane-motion-{width}-{reducedMotion}-{expanding}.png") });
            }
        });
    }
}
