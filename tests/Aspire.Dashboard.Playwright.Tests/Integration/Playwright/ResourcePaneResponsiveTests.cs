// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Layout;
using System.Collections.Immutable;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Extensions;
using Aspire.Dashboard.Model.Otlp;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Otlp.Storage;
using Aspire.Dashboard.Tests.Integration.Playwright.Infrastructure;
using Aspire.Dashboard.Utils;
using Aspire.Tests.Shared.DashboardModel;
using Aspire.TestUtilities;
using Deque.AxeCore.Commons;
using Deque.AxeCore.Playwright;
using Google.Protobuf.WellKnownTypes;
using Google.Protobuf.Collections;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using OpenTelemetry.Proto.Logs.V1;
using OpenTelemetry.Proto.Trace.V1;
using Xunit;
using static Aspire.Tests.Shared.Telemetry.TelemetryTestHelpers;

namespace Aspire.Dashboard.Tests.Integration.Playwright;

[RequiresFeature(TestFeature.Playwright)]
public sealed class ResourcePaneResponsiveTests(ResourcePaneResponsiveTests.MetadataResourcesFixture fixture)
    : PlaywrightTestsBase<ResourcePaneResponsiveTests.MetadataResourcesFixture>(fixture)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task CollapsedPicker_ControlsRemainReachableAtPopupWidth(bool savedWidth)
    {
        await RunTestAsync(async page =>
        {
            await page.SetViewportSizeAsync(1440, 900);
            await page.AddInitScriptAsync($"localStorage.setItem('{BrowserStorageKeys.ResourcePaneCollapsed}', 'true')");
            if (savedWidth)
            {
                await page.AddInitScriptAsync($"localStorage.setItem('{BrowserStorageKeys.ResourcePaneWidth}', '560')");
            }
            await page.GotoAsync("/resources/cache?pane=tags");
            var picker = page.Locator("#resourcePanePickerButton");
            await picker.ClickAsync();
            var popup = page.Locator(".resource-picker-popover:popover-open");
            var selectAll = popup.Locator(".resource-select-all-toggle");
            await Assertions.Expect(selectAll).ToBeInViewportAsync(new() { Ratio = 1 });
            await Assertions.Expect(popup.Locator(".resource-row[data-resource-name='cache'] .resource-row-actions").First)
                .ToBeInViewportAsync(new() { Ratio = 1 });
            await selectAll.ClickAsync();
            await Assertions.Expect(picker).ToHaveAttributeAsync("aria-expanded", "false");
            await Assertions.Expect(page).ToHaveURLAsync(new Regex(@"/resources(?:\?.*)?$"));
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task Activity_ResumesAfterAnimatedExpansion(bool remountPane)
    {
        await RunTestAsync(async page =>
        {
            await fixture.SeedActivityAsync();
            await page.SetViewportSizeAsync(2000, 1000);
            await page.EmulateMediaAsync(new() { ReducedMotion = ReducedMotion.NoPreference });
            await page.GotoAsync("/resources/cache?pane=tags");
            await Assertions.Expect(page.Locator(".resource-row[data-resource-name='cache']").First).ToBeVisibleAsync();
            if (remountPane)
            {
                var toggle = page.Locator(".resource-header-row .resource-pane-toggle-button");
                await toggle.ClickAsync();
                await Assertions.Expect(page.Locator(".resources-layout > .resource-pane")).ToHaveCountAsync(0);
                var picker = page.Locator("#resourcePanePickerButton");
                await picker.ClickAsync();
                await Assertions.Expect(page.Locator(".resource-picker-popover .resource-pane")).ToBeVisibleAsync();
                await page.Keyboard.PressAsync("Escape");
                await Assertions.Expect(picker).ToHaveAttributeAsync("aria-expanded", "false");
                await toggle.ClickAsync();
                await Assertions.Expect(page.Locator(".resources-layout > .resource-pane")).ToBeVisibleAsync();
            }
            await ResourcePaneNavigation.ToggleResourceListAsync(page);
            await Assertions.Expect(page.Locator(".resource-row[data-resource-name='api'] .resource-row-insight-requests").First)
                .ToHaveTextAsync("20 incoming");
            await ResourcePaneNavigation.ToggleResourceListAsync(page);
            await Assertions.Expect(page.Locator(".resource-row-insight")).ToHaveCountAsync(0);
        });
    }

    [Theory]
    [InlineData(390, false)]
    [InlineData(390, true)]
    [InlineData(768, false)]
    [InlineData(768, true)]
    [InlineData(769, false)]
    [InlineData(769, true)]
    [InlineData(1440, false)]
    [InlineData(1440, true)]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task Accessibility_PaneAndPopoversHaveNoBlockingWcagViolations(int width, bool dark)
    {
        await RunTestAsync(async page =>
        {
            await page.SetViewportSizeAsync(1440, 900);
            await page.EmulateMediaAsync(new() { ReducedMotion = ReducedMotion.Reduce });
            await page.GotoAsync("/resources/cache?pane=tags");
            await page.GetByRole(AriaRole.Button, new() { Name = "Settings", Exact = true }).ClickAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = dark ? "Dark" : "Light", Exact = true }).ClickAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "Close", Exact = true }).Last.ClickAsync();
            await page.SetViewportSizeAsync(width, width == 768 ? 1024 : width == 390 ? 844 : 900);
            if (width <= 768)
            {
                await page.Locator(".resource-picker").ClickAsync();
            }
            await Assertions.Expect(page.Locator(".resource-row[data-resource-name='cache']").First).ToBeVisibleAsync();
            foreach (var canvas in width <= 768 ? new[] { false } : new[] { false, true })
            {
                if (canvas)
                {
                    await ResourcePaneNavigation.ToggleResourceListAsync(page);
                    await Assertions.Expect(page.Locator(".pane-full-canvas")).ToBeVisibleAsync();
                }
                foreach (var surface in new[] { "pane", "filter", "grouping", "context" })
                {
                    if (surface != "pane")
                    {
                        if (surface == "context")
                        {
                            await page.Locator(".resource-row[data-resource-name='cache'] .resource-row-link").First.ClickAsync(new() { Button = MouseButton.Right });
                        }
                        else
                        {
                            await page.Locator(surface == "filter" ? "#resourcePaneFilterButton" : "#resourcePaneGroupingButton").ClickAsync();
                        }
                        await Assertions.Expect(surface == "filter" ? page.Locator(".resource-filter") :
                            page.Locator("fluent-menu-list:popover-open")).ToBeVisibleAsync();
                    }
                    var result = await page.RunAxe(new AxeRunOptions
                    {
                        RunOnly = new RunOnlyOptions { Type = "tag", Values = ["wcag2a", "wcag2aa", "wcag21a", "wcag21aa"] }
                    });
                    var blocking = result.Violations.Where(v => v.Impact is "serious" or "critical").ToArray();
                    // AxeSelector.Selector throws for shadow-DOM targets. Report the actual
                    // failing HTML instead of serializing that convenience property.
                    var report = new
                    {
                        Passes = result.Passes.Length,
                        Violations = result.Violations.Select(v => new
                        {
                            v.Id,
                            v.Impact,
                            v.Help,
                            Nodes = v.Nodes.Select(n => n.Html)
                        }),
                        Incomplete = result.Incomplete.Select(v => v.Id)
                    };
                    await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory, $"axe-pane-{width}-{dark}-{canvas}-{surface}.json"),
                        JsonSerializer.Serialize(report));
                    Assert.True(blocking.Length == 0, JsonSerializer.Serialize(report.Violations));
                    if (surface != "pane")
                    {
                        await page.Keyboard.PressAsync("Escape");
                    }
                }
            }
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task EndpointTypography_UsesSecondaryDensityAndKeepsRowRhythm(bool dark)
    {
        await RunTestAsync(async page =>
        {
            await page.SetViewportSizeAsync(2000, 1000);
            await page.EmulateMediaAsync(new() { ReducedMotion = ReducedMotion.Reduce });
            await page.GotoAsync("/resources/cache?pane=tags");
            // These are expanded-list rows: a compact row is a single 32px line, and a spacious one keeps two lines
            // because its third-line details have their own columns here.
            foreach (var (density, fontSize, lineHeight, rowHeight) in new[]
                { ("Compact", 11d, 16d, 32d), ("Comfortable", 12d, 18d, 52d), ("Spacious", 14d, 20d, 60d) })
            {
                await page.GetByRole(AriaRole.Button, new() { Name = "Settings", Exact = true }).ClickAsync();
                await page.GetByRole(AriaRole.Button, new() { Name = density, Exact = true }).ClickAsync();
                await page.GetByRole(AriaRole.Button, new() { Name = dark ? "Dark" : "Light", Exact = true }).ClickAsync();
                await page.GetByRole(AriaRole.Button, new() { Name = "Close", Exact = true }).Last.ClickAsync();
                await ResourcePaneNavigation.ToggleResourceListAsync(page);
                // The URLs column is the resources grid's: a link per URL, named by its display name.
                var link = page.Locator(".resource-row[data-resource-name='api'] .resource-row-endpoints a").First;
                await Assertions.Expect(link).ToBeVisibleAsync();
                await Assertions.Expect(link).ToHaveTextAsync("Friendly API");
                await Assertions.Expect(link).ToHaveAttributeAsync("href", MetadataResourcesFixture.Endpoint);
                await Assertions.Expect(link).ToHaveAttributeAsync("target", "_blank");
                var metrics = await link.EvaluateAsync<double[]>("""
                    link => {
                        const row=link.closest('.resource-row'),source=row.querySelector('.resource-row-source-value'),
                            time=row.querySelector('.resource-row-start-time time'),a=getComputedStyle(link),
                            s=getComputedStyle(source),t=getComputedStyle(time),b=link.getBoundingClientRect(),
                            sb=source.getBoundingClientRect(),tb=time.getBoundingClientRect();
                        return [parseFloat(a.fontSize),parseFloat(a.lineHeight),parseFloat(a.fontWeight),
                            parseFloat(s.fontSize),parseFloat(t.fontSize),b.y+b.height/2-sb.y-sb.height/2,
                            b.y+b.height/2-tb.y-tb.height/2,row.getBoundingClientRect().height,
                            a.textOverflow==='ellipsis'?1:0];
                    }
                    """);
                Assert.Equal([fontSize, lineHeight, 400d, fontSize, fontSize], metrics[..5]);
                Assert.All(metrics[5..7], value => Assert.InRange(Math.Abs(value), 0, .5));
                Assert.Equal(rowHeight, metrics[7]);
                Assert.Equal(1d, metrics[8]);
                await ResourcePaneNavigation.ToggleResourceListAsync(page);
            }
        });
    }

    [Fact]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task PaneWidth_ChangesOnlyAfterExplicitResizeAndSurvivesCacheBypassReload()
    {
        await RunTestAsync(async page =>
        {
            fixture.StartDynamicSession();
            await page.AddInitScriptAsync($$$"""
                (() => {
                    const widthKey={{{JsonSerializer.Serialize(BrowserStorageKeys.ResourcePaneWidth)}}};
                    if(localStorage.getItem(widthKey)===null)localStorage.setItem(widthKey,'507');
                    window.paneWidthWrites=[];
                    const setItem=Storage.prototype.setItem;
                    Storage.prototype.setItem=function(key,value) {
                        if(key===widthKey)window.paneWidthWrites.push(value);
                        return setItem.call(this,key,value);
                    };
                })();
                """);
            await page.SetViewportSizeAsync(1440, 900);
            await page.EmulateMediaAsync(new() { ReducedMotion = ReducedMotion.Reduce });
            await page.GotoAsync("/resources/cache?pane=tags");
            var resizer = page.Locator(".resource-pane-resizer");
            await AssertSavedWidthAsync(507);
            await fixture.PublishAsync([
                new(ResourceViewModelChangeType.Upsert, MetadataResourcesFixture.CreateDynamicChild(91)),
                new(ResourceViewModelChangeType.Upsert, ModelTestHelpers.CreateResource("api",
                    commands: [new("inspect", CommandViewModelState.Enabled, "Inspect", "", "", [],
                        isHighlighted: true, iconName: "Search", iconVariant: Microsoft.FluentUI.AspNetCore.Components.IconVariant.Regular)]))
            ]);
            await Assertions.Expect(page.Locator(".resource-row[data-resource-name='rebuilder-91']")).ToBeVisibleAsync();
            await AssertSavedWidthAsync(507);

            await page.Locator("#resourcePaneGroupingButton").ClickAsync();
            await page.GetByRole(AriaRole.Menuitemradio, new() { Name = "Resource type", Exact = true }).ClickAsync();
            await AssertSavedWidthAsync(507);
            await page.Locator("#resourcePaneFilterButton").ClickAsync();
            await page.Keyboard.PressAsync("Escape");
            await ResourcePaneNavigation.ToggleResourceListAsync(page);
            await ResourcePaneNavigation.ToggleResourceListAsync(page);
            await AssertSavedWidthAsync(507);
            var collapse = page.Locator(".resource-header-row .resource-pane-toggle-button");
            await collapse.ClickAsync();
            await collapse.ClickAsync();
            await AssertSavedWidthAsync(507);
            await page.Locator(".resource-row[data-resource-name='api'] .resource-row-link").ClickAsync();
            await AssertSavedWidthAsync(507);
            Assert.Empty(await page.EvaluateAsync<string[]>("window.paneWidthWrites"));

            var session = await page.Context.NewCDPSessionAsync(page);
            await session.SendAsync("Network.setCacheDisabled", new Dictionary<string, object> { ["cacheDisabled"] = true });
            await page.ReloadAsync();
            await AssertSavedWidthAsync(507);
            Assert.Empty(await page.EvaluateAsync<string[]>("window.paneWidthWrites"));
            await session.SendAsync("Network.setCacheDisabled", new Dictionary<string, object> { ["cacheDisabled"] = false });
            await page.ReloadAsync();
            await AssertSavedWidthAsync(507);
            Assert.Empty(await page.EvaluateAsync<string[]>("window.paneWidthWrites"));
            await session.DetachAsync();

            await resizer.DblClickAsync();
            await AssertSavedWidthAsync(420);
            await page.ReloadAsync();
            await AssertSavedWidthAsync(420);
            Assert.Empty(await page.EvaluateAsync<string[]>("window.paneWidthWrites"));

            async Task AssertSavedWidthAsync(int width)
            {
                await Assertions.Expect(resizer).ToHaveAttributeAsync("aria-disabled", "false");
                await Assertions.Expect(resizer).ToHaveAttributeAsync("aria-valuenow", width.ToString(CultureInfo.InvariantCulture));
                Assert.Equal(width, await page.Locator(".resource-pane").EvaluateAsync<double>("e=>e.getBoundingClientRect().width"));
                await AsyncTestHelpers.AssertIsTrueRetryAsync(
                    async () => await page.EvaluateAsync<string?>(
                        $"localStorage.getItem({JsonSerializer.Serialize(BrowserStorageKeys.ResourcePaneWidth)})") == width.ToString(CultureInfo.InvariantCulture),
                    "Explicit resource pane width wasn't retained.");
            }
        });
    }

    [Fact]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task PopulatedActivity_BoundsIdleAndRefreshRenderingAndResumesWithoutAnimationFrames()
    {
        await RunTestAsync(async page =>
        {
            fixture.StartDynamicSession();
            await page.AddInitScriptAsync("""
                (() => {
                    window.activitySocketStats={frames:0,bytes:0,renderFrames:0};
                    const Socket=window.WebSocket;
                    window.WebSocket=class extends Socket {
                        constructor(...args) {
                            super(...args);
                            this.addEventListener('message',event=>{
                                const stats=window.activitySocketStats;
                                const bytes=typeof event.data==='string' ? new TextEncoder().encode(event.data) :
                                    event.data instanceof ArrayBuffer ? new Uint8Array(event.data) : null;
                                if(!bytes)return;
                                stats.frames++;stats.bytes+=bytes.length;
                                if(new TextDecoder().decode(bytes).includes('JS.RenderBatch'))stats.renderFrames++;
                            });
                        }
                    };
                })();
                """);
            await page.SetViewportSizeAsync(2000, 1000);
            await page.GotoAsync("/resources?pane=tags");
            await Assertions.Expect(page.Locator(".resource-row[data-resource-name]")).ToHaveCountAsync(8);
            var resources = Enumerable.Range(0, 28).Select(MetadataResourcesFixture.CreatePerformanceResource).ToArray();
            await fixture.PublishAsync([
                .. MetadataResourcesFixture.CreateResources().Select(resource => new ResourceViewModelChange(ResourceViewModelChangeType.Delete, resource)),
                .. resources.Select(resource => new ResourceViewModelChange(ResourceViewModelChangeType.Upsert, resource))
            ]);
            await Assertions.Expect(page.Locator(".resource-row[data-resource-name]")).ToHaveCountAsync(56);
            await fixture.SeedActivityAsync(resources.Select(resource => (resource.Name, 20)).ToArray());
            await ResourcePaneNavigation.ToggleResourceListAsync(page);
            await Assertions.Expect(page.Locator(".resource-row-insight-requests")).ToHaveCountAsync(56);
            await Assertions.Expect(page.Locator(".resource-row-insight-latency")).ToHaveCountAsync(56);
            await page.EmulateMediaAsync(new() { ReducedMotion = ReducedMotion.Reduce });
            await page.WaitForFunctionAsync("""
                () => !document.querySelector('.resources-layout').getAnimations({subtree:true})
                    .some(animation=>animation.playState==='running')
                """);
            await page.EvaluateAsync("""
                () => {
                    window.activityMeasure={mutations:0,rows:[],longTasks:[],shifts:[]};
                    const rows=new Set();
                    window.activityMutationObserver=new MutationObserver(records=>{
                        window.activityMeasure.mutations+=records.length;
                        for(const record of records) {
                            const element=record.target.nodeType===1 ? record.target : record.target.parentElement;
                            const row=element.closest('.resource-row[data-resource-name]');
                            if(row)rows.add(row.dataset.resourceName);
                        }
                        window.activityMeasure.rows=[...rows];
                    });
                    window.activityMutationObserver.observe(document.querySelector('.resource-pane-list'),
                        {subtree:true,childList:true,attributes:true,characterData:true});
                    new PerformanceObserver(list=>window.activityMeasure.longTasks.push(...list.getEntries().map(e=>e.duration)))
                        .observe({type:'longtask'});
                    new PerformanceObserver(list=>window.activityMeasure.shifts.push(...list.getEntries().map(e=>e.value)))
                        .observe({type:'layout-shift'});
                    window.activitySocketStats={frames:0,bytes:0,renderFrames:0};
                    window.resetActivityMeasure=()=>{
                        rows.clear();
                        window.activityMeasure={mutations:0,rows:[],longTasks:[],shifts:[]};
                        window.activitySocketStats={frames:0,bytes:0,renderFrames:0};
                    };
                }
                """);
            await page.EvaluateAsync("() => new Promise(resolve=>setTimeout(resolve,10000))");
            var idle = await MeasureAsync();
            await page.EvaluateAsync("() => window.resetActivityMeasure()");
            await fixture.SeedActivityAsync(resources.Select(resource => (resource.Name, 21)).ToArray());
            await Assertions.Expect(page.Locator(".resource-row-insight-requests > span")).ToHaveTextAsync(Enumerable.Repeat("21 incoming", 56));
            await page.EvaluateAsync("() => new Promise(resolve=>setTimeout(resolve,500))");
            var refresh = await MeasureAsync();
            await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory, "activity-performance.json"),
                JsonSerializer.Serialize(new { idle, refresh, resources = 28, memberships = 56 }, new JsonSerializerOptions { WriteIndented = true }));
            Assert.InRange(idle.GetProperty("mutations").GetInt32(), 0, 2);
            Assert.InRange(refresh.GetProperty("mutations").GetInt32(), 1, 56 * 16);
            Assert.Equal(28, refresh.GetProperty("rows").GetArrayLength());
            foreach (var sample in new[] { idle, refresh })
            {
                Assert.Empty(sample.GetProperty("longTasks").EnumerateArray());
                Assert.Equal(0, sample.GetProperty("shifts").EnumerateArray().Sum(value => value.GetDouble()));
            }

            var href = await page.Locator(".resource-row-insight-requests").First.GetAttributeAsync("href");
            await page.EvaluateAsync("""
                () => {
                    window.savedActivityAnimationFrame=window.requestAnimationFrame;
                    window.requestAnimationFrame=()=>0;
                    Object.defineProperty(document,'visibilityState',{configurable:true,get:()=> 'hidden'});
                    document.dispatchEvent(new Event('visibilitychange'));
                }
                """);
            await Assertions.Expect(page.Locator(".resource-row-insight")).ToHaveCountAsync(0);
            await page.EvaluateAsync("""
                () => {
                    window.requestAnimationFrame=window.savedActivityAnimationFrame;
                    delete document.visibilityState;
                    document.dispatchEvent(new Event('visibilitychange'));
                }
                """);
            await Assertions.Expect(page.Locator(".resource-row-insight-requests")).ToHaveCountAsync(56);
            Assert.NotEqual(href, await page.Locator(".resource-row-insight-requests").First.GetAttributeAsync("href"));
            await page.ScreenshotAsync(new() { Path = Path.Combine(AppContext.BaseDirectory, "activity-populated-28-resources.png") });

            Task<JsonElement> MeasureAsync() => page.EvaluateAsync<JsonElement>(
                "() => ({...window.activityMeasure,socket:{...window.activitySocketStats}})");
        });
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    [InlineData("fr-FR")]
    [InlineData("ja-JP")]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task StartTime_RemainsCompleteAndAccessibleAcrossCulturesAndWidths(string culture)
    {
        await RunTestAsync(async page =>
        {
            await page.SetViewportSizeAsync(2000, 1000);
            await page.GotoAsync("/resources/api?pane=tags");
            var origin = new Uri(page.Url).GetLeftPart(UriPartial.Authority);
            foreach (var density in new[] { "Compact", "Comfortable", "Spacious" })
            {
                await SetCultureAsync("en-US");
                await page.GetByRole(AriaRole.Button, new() { Name = "Settings", Exact = true }).ClickAsync();
                await page.GetByRole(AriaRole.Button, new() { Name = density, Exact = true }).ClickAsync();
                await page.GetByRole(AriaRole.Button, new() { Name = "Close", Exact = true }).Last.ClickAsync();
                await SetCultureAsync(culture);
                await Assertions.Expect(page.Locator("html")).ToHaveAttributeAsync("lang", culture);
                await fixture.PublishAsync([new(ResourceViewModelChangeType.Upsert, MetadataResourcesFixture.CreateDynamicChild(0))]);
                await Assertions.Expect(page.Locator("[data-resource-name='rebuilder-0']")).ToBeVisibleAsync();
                foreach (var width in new[] { ResourcesLayout.MinimumPaneWidthPx, 560, 769, 1279, 1280, 1440, 2000 })
                {
                    if (await page.Locator(".pane-full-canvas").CountAsync() > 0)
                    {
                        await ResourcePaneNavigation.ToggleResourceListAsync(page);
                    }
                    await page.SetViewportSizeAsync(width <= 560 ? 2000 : width, 1000);
                    if (width <= 560)
                    {
                        // The separator handles keys only once its script is registered.
                        await Assertions.Expect(page.Locator(".resource-pane-resizer")).ToHaveAttributeAsync("aria-disabled", "false");
                        await page.Locator(".resource-pane-resizer").PressAsync(width == ResourcesLayout.MinimumPaneWidthPx ? "Home" : "End");
                        await Assertions.Expect(page.Locator(".resource-pane-resizer"))
                            .ToHaveAttributeAsync("aria-valuenow", width.ToString(CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        await ResourcePaneNavigation.ToggleResourceListAsync(page);
                        var chrome = await page.EvaluateAsync<double>("() => innerWidth - document.querySelector('.resources-layout').getBoundingClientRect().width");
                        await page.SetViewportSizeAsync(width + (int)Math.Round(chrome), 1000);
                    }
                    await page.WaitForFunctionAsync("""
                        width => Math.abs(document.querySelector('.resource-pane').getBoundingClientRect().width - width) < .5
                        """, width);
                    var problems = await page.Locator(".resource-pane").EvaluateAsync<string[]>("""
                        pane => [...pane.querySelectorAll('time')].flatMap(time => {
                            const b=time.getBoundingClientRect();
                            if(!b.width)return [];
                            const visible=time.innerText.replace(/\s+/g,' ').trim(), container=time.parentElement.getBoundingClientRect();
                            return time.scrollWidth>time.clientWidth || b.right>container.right+.5 ||
                                time.tabIndex!==0 || !time.getAttribute('aria-label')?.includes(visible) ||
                                !time.getAttribute('data-tooltip')?.includes(visible) ||
                                !time.getAttribute('aria-label')?.includes('2026')
                                ? [`${time.closest('.resource-row').dataset.resourceName}: ${visible}; width=${b.width}; scroll=${time.scrollWidth}; parent=${container.width}`] : [];
                        })
                        """);
                    Assert.True(problems.Length == 0, $"{culture}/{density}/{width}: {string.Join("; ", problems)}");
                }
            }

            async Task SetCultureAsync(string name)
            {
                await page.Context.AddCookiesAsync([new() { Name = ".AspNetCore.Culture", Value = $"c={name}|uic={name}", Url = origin }]);
                fixture.StartDynamicSession();
                await page.GotoAsync("/resources/api?pane=tags");
                await Assertions.Expect(page.Locator(".resource-row[data-resource-name='api']").First).ToBeVisibleAsync();
            }
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task Resizer_DoubleClickRestoresDefaultAcrossPresentationAndNavigation(bool reducedMotion)
    {
        await RunTestAsync(async page =>
        {
            fixture.StartDynamicSession();
            await page.EmulateMediaAsync(new() { ReducedMotion = reducedMotion ? ReducedMotion.Reduce : ReducedMotion.NoPreference });
            await page.SetViewportSizeAsync(1440, 1000);
            await page.GotoAsync("/resources/cache?pane=tags");
            var separator = page.Locator(".resource-pane-resizer");
            await Assertions.Expect(separator).ToBeVisibleAsync();
            await Assertions.Expect(separator).ToHaveAttributeAsync("aria-disabled", "false");
            foreach (var stage in new[] { "drag", "density", "filter-group", "canvas", "traces", "overview" })
            {
                if (stage == "density")
                {
                    await page.GetByRole(AriaRole.Button, new() { Name = "Settings", Exact = true }).ClickAsync();
                    await page.GetByRole(AriaRole.Button, new() { Name = "Spacious", Exact = true }).ClickAsync();
                    await page.GetByRole(AriaRole.Button, new() { Name = "Close", Exact = true }).Last.ClickAsync();
                    await Assertions.Expect(page.GetByRole(AriaRole.Dialog)).ToHaveCountAsync(0);
                    await Assertions.Expect(page.Locator("#dashboard-settings-button")).ToBeFocusedAsync();
                }
                else if (stage == "filter-group")
                {
                    await page.Locator(".resource-pane").GetByRole(AriaRole.Searchbox).FillAsync("cache");
                    await page.Locator(".resource-pane").GetByRole(AriaRole.Searchbox).FillAsync("");
                    await page.Locator("#resourcePaneGroupingButton").ClickAsync();
                    await page.GetByRole(AriaRole.Menuitemradio, new() { Name = "None", Exact = true }).ClickAsync();
                    await Assertions.Expect(page.Locator("#resourcePaneGroupingButton")).ToBeFocusedAsync();
                }
                else if (stage == "canvas")
                {
                    await ResourcePaneNavigation.ToggleResourceListAsync(page);
                    await Assertions.Expect(page.Locator(".pane-full-canvas")).ToBeVisibleAsync();
                    await page.Locator(".resource-row[data-resource-name='cache'] .resource-row-link").First.ClickAsync();
                }
                else if (stage is "traces" or "overview")
                {
                    await page.GetByRole(AriaRole.Tab, new() { Name = stage == "traces" ? "Traces" : "Overview", Exact = true }).ClickAsync();
                    if (stage == "traces")
                    {
                        await Assertions.Expect(page.Locator("#tracesScrollContainer")).ToBeFocusedAsync();
                    }
                }

                await Assertions.Expect(separator).ToHaveAttributeAsync("aria-disabled", "false");
                await separator.FocusAsync();
                await separator.PressAsync("End");
                await Assertions.Expect(separator).ToHaveAttributeAsync("aria-valuenow", "560");
                await page.WaitForFunctionAsync("() => Math.abs(document.querySelector('.resource-pane').getBoundingClientRect().width - 560) < .5");
                if (stage == "drag")
                {
                    var box = await separator.BoundingBoxAsync();
                    Assert.NotNull(box);
                    await page.Mouse.MoveAsync(box.X + box.Width / 2, box.Y + box.Height / 2);
                    await page.Mouse.DownAsync();
                    await page.Mouse.MoveAsync(box.X + box.Width / 2 - 60, box.Y + box.Height / 2, new() { Steps = 5 });
                    await page.Mouse.UpAsync();
                    await Assertions.Expect(separator).ToHaveAttributeAsync("aria-valuenow", "500");
                }
                var url = page.Url;
                await separator.FocusAsync();
                await separator.DblClickAsync(new() { Delay = 80 });
                await Assertions.Expect(separator).ToHaveAttributeAsync("aria-valuenow", "420");
                await Assertions.Expect(separator).ToBeFocusedAsync();
                await Assertions.Expect(page.Locator(".resource-row[data-resource-name='cache'] .resource-row-link").First)
                    .ToHaveAttributeAsync("aria-current", "page");
                await page.WaitForFunctionAsync("""
                    () => Math.abs(document.querySelector('.resource-pane').getBoundingClientRect().width - 420) < .5 &&
                        document.querySelector('.resources-layout').style.getPropertyValue('--resource-pane-expanded-width') === '420px' &&
                        localStorage.getItem('Aspire_ResourcePane_Width') === '420'
                    """);
                Assert.Equal(url, page.Url);
            }
            await page.ReloadAsync();
            await Assertions.Expect(separator).ToHaveAttributeAsync("aria-valuenow", "420");
            await page.WaitForFunctionAsync("() => Math.abs(document.querySelector('.resource-pane').getBoundingClientRect().width - 420) < .5");
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task SelectionAndResize_PreserveAvailableTracesTab(bool reducedMotion)
    {
        await RunTestAsync(async page =>
        {
            await fixture.SeedActivityAsync([("cache", 20), ("api", 20), ("frontend", 20)]);
            await page.SetViewportSizeAsync(1440, 900);
            await page.EmulateMediaAsync(new() { ReducedMotion = reducedMotion ? ReducedMotion.Reduce : ReducedMotion.NoPreference });
            await page.GotoAsync("/resources/cache?pane=tags");
            var tracesTab = page.GetByRole(AriaRole.Tab, new() { Name = "Traces", Exact = true });
            if (reducedMotion)
            {
                await tracesTab.FocusAsync();
                await tracesTab.PressAsync("Enter");
            }
            else
            {
                await tracesTab.ClickAsync();
            }
            await Assertions.Expect(page.Locator("#tracesScrollContainer")).ToBeFocusedAsync();
            foreach (var name in new[] { "api", "frontend", "cache" })
            {
                await ResourcePaneNavigation.ToggleResourceListAsync(page);
                await Assertions.Expect(page.Locator(".pane-full-canvas")).ToBeVisibleAsync();
                await page.Locator($".resource-row[data-resource-name='{name}'] .resource-row-link").First.ClickAsync();
                await page.WaitForURLAsync($"**/traces/resource/{name}");
                await Assertions.Expect(page.Locator(".pane-full-canvas,.pane-selection-reveal")).ToHaveCountAsync(0);
                var separator = page.Locator(".resource-pane-resizer");
                await Assertions.Expect(separator).ToHaveAttributeAsync("aria-disabled", "false");
                await separator.PressAsync("End");
                await Assertions.Expect(separator).ToHaveAttributeAsync("aria-valuenow", "560");
                await separator.DblClickAsync(new() { Delay = 80 });
                await Assertions.Expect(separator).ToHaveAttributeAsync("aria-valuenow", "420");
                await Assertions.Expect(separator).ToBeFocusedAsync();
                Assert.EndsWith($"/traces/resource/{name}", page.Url, StringComparison.Ordinal);
                await Assertions.Expect(page.GetByRole(AriaRole.Tab, new() { Name = "Traces", Exact = true }))
                    .ToHaveAttributeAsync("aria-selected", "true");
            }
        });
    }

    [Theory]
    [InlineData(390, 844, false)]
    [InlineData(768, 1024, false)]
    [InlineData(1440, 900, false)]
    [InlineData(1440, 900, true)]
    [InlineData(2000, 1000, false)]
    [InlineData(2000, 1000, true)]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task FilterAndGrouping_StayInsideVisualViewportAcrossPaneModesAndResize(int width, int height, bool fullCanvas)
    {
        await RunTestAsync(async page =>
        {
            await page.SetViewportSizeAsync(width, height);
            await page.EmulateMediaAsync(new() { ReducedMotion = ReducedMotion.Reduce });
            // The expanded list is opened by closing a resource's console logs rather than its overview. The overview's
            // data grids initialize in the browser after they render, and leaving the page in the middle of that, as
            // this test would right after the page loads, makes FluentDataGrid use a disposed JS module and end the circuit.
            await page.GotoAsync(fullCanvas ? "/consolelogs/resource/cache?pane=tags" : "/resources/cache?pane=tags");
            if (width <= 768)
            {
                await page.Locator(".resource-picker").ClickAsync();
            }
            if (fullCanvas)
            {
                await ResourcePaneNavigation.ToggleResourceListAsync(page);
                await Assertions.Expect(page.Locator(".pane-full-canvas")).ToBeVisibleAsync();
            }
            foreach (var filter in new[] { true, false })
            {
                var button = page.Locator(filter ? "#resourcePaneFilterButton" : "#resourcePaneGroupingButton");
                await button.HoverAsync();
                var tooltip = page.Locator(".dashboard-tooltip").Last;
                await Assertions.Expect(tooltip).ToBeVisibleAsync();
                await Assertions.Expect(tooltip).ToHaveAttributeAsync("positioning", "above");
                await AssertContainedAsync(tooltip);
                await button.ClickAsync();
                var surface = filter ? page.Locator(".resource-filter-popover[part='dialog']") :
                    page.Locator("fluent-menu-list:popover-open");
                await Assertions.Expect(surface).ToBeVisibleAsync();
                if (filter)
                {
                    // Expanding a group re-renders the popover. Wait for each to expand before looking for the next, or
                    // a header that's already been clicked can still be counted as collapsed.
                    var collapsed = page.Locator(".resource-filter-group-header[aria-expanded='false']");
                    for (var remaining = await collapsed.CountAsync(); remaining > 0; remaining = await collapsed.CountAsync())
                    {
                        await collapsed.First.ClickAsync();
                        await Assertions.Expect(collapsed).ToHaveCountAsync(remaining - 1);
                    }
                }
                await AssertContainedAsync(surface);
                await page.SetViewportSizeAsync(width - 40, 260);
                await AssertContainedAsync(surface);
                if (filter)
                {
                    await AssertContainedAsync(page.Locator(".resource-filter-footer"));
                    Assert.True(await page.Locator(".resource-filter-body").EvaluateAsync<bool>(
                        "el => el.scrollHeight>el.clientHeight&&getComputedStyle(el).overflowY==='auto'"));
                    await page.Locator(".resource-filter-body").EvaluateAsync("el=>el.scrollTop=el.scrollHeight");
                    await AssertContainedAsync(page.Locator(".resource-filter-done"));
                    // Measure native dismissal in the same JavaScript turn, before Blazor can handle the close event.
                    var dismissal = await surface.EvaluateAsync<int[]>("""
                        dialog => {
                            const wasOpen = dialog.matches(':popover-open');
                            dialog.hidePopover();
                            return [wasOpen ? 1 : 0, dialog.matches(':popover-open') ? 1 : 0, dialog.getClientRects().length];
                        }
                        """);
                    Assert.Equal([1, 0, 0], dismissal);
                    await Assertions.Expect(button).ToHaveAttributeAsync("aria-expanded", "false");
                    await button.ClickAsync();
                    await Assertions.Expect(surface).ToBeVisibleAsync();
                }
                else
                {
                    Assert.Equal("auto", await surface.EvaluateAsync<string>("el=>getComputedStyle(el).overflowY"));
                    var item = surface.Locator("fluent-menu-item").Last;
                    await item.ScrollIntoViewIfNeededAsync();
                    await AssertContainedAsync(item);
                }
                await page.Keyboard.PressAsync("Escape");
                await Assertions.Expect(surface).Not.ToBeVisibleAsync();
                await Assertions.Expect(button).ToBeFocusedAsync();
                await page.SetViewportSizeAsync(width, height);
            }

            static async Task AssertContainedAsync(ILocator surface)
            {
                var geometry = await surface.EvaluateAsync<double[]>("""
                    el => {
                        const b=el.getBoundingClientRect(),v=window.visualViewport;
                        return [b.left-v.offsetLeft,b.top-v.offsetTop,v.offsetLeft+v.width-b.right,
                            v.offsetTop+v.height-b.bottom,b.width,b.height,
                            document.documentElement.scrollWidth-document.documentElement.clientWidth];
                    }
                    """);
                Assert.All(geometry[..4], value => Assert.True(value >= -.5, string.Join(", ", geometry)));
                Assert.True(geometry[4] > 0 && geometry[5] > 0);
                Assert.InRange(geometry[6], 0, 1);
            }
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task SelectionHint_RotatesWithoutLayoutChangesAndPausesWhenInactive(bool reducedMotion)
    {
        await RunTestAsync(async page =>
        {
            await page.SetViewportSizeAsync(1440, 900);
            await page.EmulateMediaAsync(new() { ReducedMotion = reducedMotion ? ReducedMotion.Reduce : ReducedMotion.NoPreference });
            await page.GotoAsync("/resources/cache?pane=tags");
            var hint = page.Locator(".resource-selection-hint");
            await Assertions.Expect(hint).ToBeVisibleAsync();
            var before = await hint.BoundingBoxAsync();
            Assert.NotNull(before);
            var phases = await hint.EvaluateAsync<double[][]>("""
                hint => [1000,6000].map(time => {
                    const groups=[...hint.querySelectorAll('.resource-selection-instruction')];
                    groups.flatMap(group=>group.getAnimations()).forEach(animation=>animation.currentTime=time);
                    const b=hint.getBoundingClientRect();
                    return [...groups.map(group=>Number(getComputedStyle(group).opacity)),b.x,b.y,b.width,b.height];
                })
                """);
            Assert.Equal(1, phases[0][0]);
            Assert.Equal(0, phases[0][1]);
            Assert.Equal(0, phases[1][0]);
            Assert.Equal(1, phases[1][1]);
            Assert.Equal(phases[0][2..], phases[1][2..]);
            Assert.InRange(before.Height, 0, 18);
            await hint.HoverAsync();
            Assert.True(await hint.EvaluateAsync<bool>("el => [...el.querySelectorAll('.resource-selection-instruction')].every(g=>g.getAnimations().every(a=>a.playState==='paused'))"));
            await hint.FocusAsync();
            await page.Mouse.MoveAsync(1, 1);
            Assert.True(await hint.EvaluateAsync<bool>("el => [...el.querySelectorAll('.resource-selection-instruction')].every(g=>getComputedStyle(g).animationPlayState==='paused')"));
            await hint.PressAsync("Tab");
            await page.EvaluateAsync("""
                () => {
                    Object.defineProperty(document,'visibilityState',{configurable:true,value:'hidden'});
                    document.dispatchEvent(new Event('visibilitychange'));
                }
                """);
            await Assertions.Expect(page.Locator(".resources-layout")).ToHaveAttributeAsync("data-document-hidden", "");
            Assert.True(await hint.EvaluateAsync<bool>("el => [...el.querySelectorAll('.resource-selection-instruction')].every(g=>getComputedStyle(g).animationPlayState==='paused')"));
            await page.EvaluateAsync("""
                () => {
                    delete document.visibilityState;
                    document.dispatchEvent(new Event('visibilitychange'));
                }
                """);
            await ResourcePaneNavigation.ToggleResourceListAsync(page);
            await page.WaitForFunctionAsync("() => document.querySelector('.resource-pane').getBoundingClientRect().width===1440");
            Assert.True(await hint.EvaluateAsync<bool>("el => [...el.querySelectorAll('.resource-selection-instruction')].every(g=>g.getAnimations().length===0&&getComputedStyle(g).opacity==='1')"));
            var after = await hint.BoundingBoxAsync();
            Assert.NotNull(after);
            Assert.InRange(after.Height, 0, 18);
            Assert.Equal(2, await hint.Locator("[aria-hidden='true']").CountAsync());
        });
    }

    [Theory]
    [InlineData("Windows", "MacIntel", 0, false)]
    [InlineData("macOS", "Win32", 0, true)]
    [InlineData(null, "MacIntel", 5, true)]
    [InlineData(null, "iPad", 0, true)]
    [InlineData(null, "iPhone", 0, true)]
    [InlineData(null, "Win32", 0, false)]
    [InlineData(null, "Linux x86_64", 0, false)]
    [InlineData("", "MacIntel", 0, true)]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task SelectionHint_ResolvesPlatformBeforeDisplayingModifier(string? userAgentPlatform, string platform, int touchPoints, bool command)
    {
        await RunTestAsync(async page =>
        {
            await page.AddInitScriptAsync($$$"""
                (() => {
                    const data={{{JsonSerializer.Serialize(new { userAgentPlatform, platform, touchPoints })}}};
                    Object.defineProperty(navigator,'userAgentData',{value:data.userAgentPlatform===null ? undefined : {platform:data.userAgentPlatform}});
                    Object.defineProperty(navigator,'platform',{value:data.platform});
                    Object.defineProperty(navigator,'maxTouchPoints',{value:data.touchPoints});
                    window.selectionHintHistory=[];
                    new MutationObserver(() => {
                        const text=document.querySelector('.resource-selection-hint kbd')?.textContent;
                        if(text && window.selectionHintHistory.at(-1)!==text) window.selectionHintHistory.push(text);
                    }).observe(document,{subtree:true,childList:true,characterData:true});
                })();
                """);
            await page.SetViewportSizeAsync(1440, 900);
            await page.GotoAsync("/resources/cache?pane=tags");
            var hint = page.Locator(".resource-selection-hint");
            await Assertions.Expect(hint).ToBeVisibleAsync();
            var expected = command ? "⌘ + click" : "Ctrl + click";
            await Assertions.Expect(hint.Locator("kbd").First).ToHaveTextAsync(expected);
            await Assertions.Expect(hint).ToHaveAttributeAsync("aria-label", command
                ? Resources.Layout.ResourcePaneSelectionCommandDescription
                : Resources.Layout.ResourcePaneSelectionControlDescription);
            Assert.Equal([expected], await page.EvaluateAsync<string[]>("() => window.selectionHintHistory"));
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task IconTile_PreservesStylesAcrossPaneModesAndDensities(bool dark)
    {
        await RunTestAsync(async page =>
        {
            await page.SetViewportSizeAsync(1440, 900);
            await page.EmulateMediaAsync(new() { ReducedMotion = ReducedMotion.Reduce });
            await page.GotoAsync("/resources/cache?pane=tags");
            await Assertions.Expect(page.Locator(".resource-row.selected[data-resource-name='cache']").First).ToBeVisibleAsync();
            await Assertions.Expect(page.Locator(".resource-row:not(.selected)[data-resource-name]").First).ToBeVisibleAsync();
            await page.Locator(".resource-select-all-toggle").ClickAsync();
            await Assertions.Expect(page).ToHaveURLAsync(new Regex("/resources$"));
            await Assertions.Expect(page.Locator(".resource-select-all-toggle.on")).ToHaveCountAsync(1);
            await Assertions.Expect(page.Locator(".resource-row.selected[data-resource-name]")).ToHaveCountAsync(0);
            foreach (var density in new[] { "Compact", "Comfortable", "Spacious" })
            {
                await page.GetByRole(AriaRole.Button, new() { Name = "Settings", Exact = true }).ClickAsync();
                await page.GetByRole(AriaRole.Button, new() { Name = density, Exact = true }).ClickAsync();
                await page.GetByRole(AriaRole.Button, new() { Name = dark ? "Dark" : "Light", Exact = true }).ClickAsync();
                await page.GetByRole(AriaRole.Button, new() { Name = "Close", Exact = true }).Last.ClickAsync();
                var before = await ReadTilesAsync();
                await ResourcePaneNavigation.ToggleResourceListAsync(page);
                await Assertions.Expect(page.Locator(".pane-full-canvas")).ToBeVisibleAsync();
                Assert.Equal(before, await ReadTilesAsync());
                await Assertions.Expect(page.Locator(".resource-select-all-toggle")).ToHaveAttributeAsync("aria-description", Resources.Layout.ResourcePaneAllSelected);
                await ResourcePaneNavigation.ToggleResourceListAsync(page);
                await Assertions.Expect(page.Locator(".pane-full-canvas")).ToHaveCountAsync(0);
                Assert.Equal(before, await ReadTilesAsync());
            }

            Task<string> ReadTilesAsync() => page.Locator(".resource-pane-list").EvaluateAsync<string>("""
                list => JSON.stringify([...list.querySelectorAll('.resource-row[data-resource-name]')].map(row => {
                    const icon=row.querySelector('.resource-row-icon'), glyph=icon.querySelector('.resource-row-glyph svg');
                    const b=icon.getBoundingClientRect(), r=row.getBoundingClientRect(), g=glyph.getBoundingClientRect();
                    const badge=icon.querySelector('.persistent-container-badge')?.getBoundingClientRect(), style=getComputedStyle(icon);
                    return {name:row.dataset.resourceName,
                        width:b.width,height:b.height,radius:style.borderRadius,background:style.backgroundColor,
                        border:style.border,glyphWidth:g.width,glyphHeight:g.height,
                        color:getComputedStyle(glyph).color,fill:getComputedStyle(glyph).fill,
                        center:b.y+b.height/2-r.y-r.height/2,
                        badge:badge ? {x:badge.x-b.x,y:badge.y-b.y,width:badge.width,height:badge.height} : null};
                }))
                """);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task SelectionSurfaces_UseDashboardNeutralTokensAcrossThemes(bool dark)
    {
        await RunTestAsync(async page =>
        {
            fixture.StartDynamicSession();
            await page.SetViewportSizeAsync(1440, 1000);
            await page.GotoAsync("/resources/api?pane=tags");
            await Assertions.Expect(page.Locator(".resource-row[data-resource-name]")).ToHaveCountAsync(8);
            await page.GetByRole(AriaRole.Button, new() { Name = "Settings", Exact = true }).ClickAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = dark ? "Dark" : "Light", Exact = true }).ClickAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "Close", Exact = true }).Last.ClickAsync();
            await page.Mouse.MoveAsync(1, 1);
            var selected = page.Locator(".resource-row[data-resource-name='api']").First;
            var rest = page.Locator(".resource-row[data-resource-name='cache']").First;
            var bounds = await selected.BoundingBoxAsync();
            Assert.NotNull(bounds);
            await AssertSurfaceAsync(rest, null);
            await AssertSurfaceAsync(selected, "--colorNeutralBackground1");
            await rest.HoverAsync();
            await AssertSurfaceAsync(rest, "--colorNeutralBackground3");
            await selected.HoverAsync();
            await AssertSurfaceAsync(selected, "--colorNeutralBackground3");
            await page.Mouse.DownAsync();
            var pressed = await selected.EvaluateAsync<string[]>("""
            row => {
                const ctx=document.createElement('canvas').getContext('2d'),style=getComputedStyle(row);
                ctx.fillStyle=style.getPropertyValue('--colorNeutralBackground1Pressed');
                const expected=ctx.fillStyle;ctx.fillStyle=style.backgroundColor;
                return [expected,ctx.fillStyle];
            }
            """);
            Assert.Equal(pressed[0], pressed[1]);
            await page.Mouse.MoveAsync(1, 1);
            await page.Mouse.UpAsync();
            await selected.Locator(".resource-row-link").FocusAsync();
            await page.Keyboard.PressAsync("Tab");
            await page.Keyboard.PressAsync("Shift+Tab");
            await Assertions.Expect(selected.Locator(".resource-row-link")).ToBeFocusedAsync();
            Assert.True(await selected.EvaluateAsync<bool>("row => row.matches(':has(> .resource-row-link:focus-visible)')"));
            Assert.Equal("2px", await selected.EvaluateAsync<string>("row => getComputedStyle(row).outlineWidth"));
            await AssertSurfaceAsync(selected, "--colorNeutralBackground1");

            var selectAll = page.GetByRole(AriaRole.Button, new() { Name = Resources.Layout.ResourceHeaderAllResources, Exact = true });
            await selectAll.FocusAsync();
            await selectAll.PressAsync("Space");
            await Assertions.Expect(selectAll).ToHaveAttributeAsync("aria-description", Resources.Layout.ResourcePaneAllSelected);
            await Assertions.Expect(page.Locator(".resource-select-all-toggle.on")).ToHaveCountAsync(1);
            await Assertions.Expect(page.Locator(".resource-row.selected[data-resource-name]")).ToHaveCountAsync(0);
            foreach (var row in await page.Locator(".resource-row[data-resource-name]").AllAsync())
            {
                await AssertSurfaceAsync(row, null);
            }
            var selectedBounds = await selected.BoundingBoxAsync();
            Assert.NotNull(selectedBounds);
            Assert.Equal(new[] { bounds.X, bounds.Y, bounds.Width, bounds.Height },
                new[] { selectedBounds.X, selectedBounds.Y, selectedBounds.Width, selectedBounds.Height });

            await rest.Locator(".resource-row-link").ClickAsync();
            await Assertions.Expect(selectAll).Not.ToHaveAttributeAsync("aria-description", Resources.Layout.ResourcePaneAllSelected);
            Assert.Null(await selectAll.GetAttributeAsync("aria-description"));
            await Assertions.Expect(page.Locator(".resource-row.selected[data-resource-name='cache']")).ToHaveCountAsync(2);
            await Assertions.Expect(page.Locator(".resource-row.selected[data-resource-name]")).ToHaveCountAsync(2);
            await page.Mouse.MoveAsync(1, 1);
            await AssertSurfaceAsync(rest, "--colorNeutralBackground1");
            await AssertSurfaceAsync(selected, null);
            await page.EmulateMediaAsync(new() { ForcedColors = ForcedColors.Active });
            Assert.True(await rest.EvaluateAsync<bool>("""
            row => {
                const probe=document.createElement('span');
                probe.style.background='Highlight'; row.append(probe);
                const matches=getComputedStyle(probe).backgroundColor===getComputedStyle(row).backgroundColor;
                probe.remove(); return matches;
            }
            """));
            await page.EmulateMediaAsync(new() { ForcedColors = ForcedColors.None });
            await ResourcePaneNavigation.ToggleResourceListAsync(page);
            await Assertions.Expect(page.Locator(".pane-full-canvas")).ToBeVisibleAsync();
            await Assertions.Expect(page.Locator(".resource-select-all-toggle.on")).ToHaveCountAsync(1);
            await Assertions.Expect(page.Locator(".resource-row.selected")).ToHaveCountAsync(0);
            await page.Mouse.MoveAsync(1, 1);
            await AssertSurfaceAsync(rest, null);
            await AssertSurfaceAsync(selected, null);

            static async Task AssertSurfaceAsync(ILocator row, string? token)
            {
                var sample = await row.EvaluateAsync<JsonElement>("""
                (row, token) => {
                    const canvas=document.createElement('canvas'),ctx=canvas.getContext('2d',{willReadFrequently:true});
                    canvas.width=canvas.height=1;
                    const style=getComputedStyle(row), ancestors=[];
                    for(let e=row.parentElement;e;e=e.parentElement)ancestors.unshift(e);
                    ctx.fillStyle='white';ctx.fillRect(0,0,1,1);
                    for(const e of ancestors){ctx.fillStyle=getComputedStyle(e).backgroundColor;ctx.fillRect(0,0,1,1);}
                    const surface=ctx.getImageData(0,0,1,1);
                    ctx.fillStyle=style.backgroundColor;ctx.fillRect(0,0,1,1);
                    const actual=[...ctx.getImageData(0,0,1,1).data].slice(0,3);
                    ctx.putImageData(surface,0,0);
                    if(token){ctx.fillStyle=style.getPropertyValue(token);ctx.fillRect(0,0,1,1);}
                    const expected=[...ctx.getImageData(0,0,1,1).data].slice(0,3);
                    return {actual,expected,background:style.backgroundColor,border:style.borderTopWidth,
                        classes:row.className,hover:row.matches(':hover'),
                        rail:getComputedStyle(row,'::before').width,
                        railColor:getComputedStyle(row,'::before').backgroundColor,
                        gap:row.querySelector('.resource-row-icon').getBoundingClientRect().x-
                            row.getBoundingClientRect().x-parseFloat(getComputedStyle(row,'::before').width),
                        selected:row.classList.contains('selected')};
                }
                """, token);
                var actual = sample.GetProperty("actual").EnumerateArray().Select(v => v.GetInt32()).ToArray();
                var expected = sample.GetProperty("expected").EnumerateArray().Select(v => v.GetInt32()).ToArray();
                Assert.All(actual.Zip(expected), pair => Assert.True(Math.Abs(pair.First - pair.Second) <= 1,
                    $"Unexpected selection surface for {token ?? "the pane background"}: {sample}"));
                Assert.Equal("0px", sample.GetProperty("border").GetString());
                Assert.Equal(sample.GetProperty("selected").GetBoolean() ? "3px" : "2px", sample.GetProperty("rail").GetString());
                Assert.Equal(!sample.GetProperty("selected").GetBoolean(),
                    sample.GetProperty("railColor").GetString() == "rgba(0, 0, 0, 0)");
                Assert.True(sample.GetProperty("gap").GetDouble() >= 7);
                if (token is null)
                {
                    Assert.Equal("rgba(0, 0, 0, 0)", sample.GetProperty("background").GetString());
                }
            }
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task DynamicRows_PreserveTokenGeometryAfterModelAndPresentationChanges(bool dark)
    {
        await RunTestAsync(async page =>
        {
            var snapshots = new List<JsonElement>();
            var stage = "initial-additions";
            fixture.StartDynamicSession();
            await fixture.SeedActivityAsync();
            await page.SetViewportSizeAsync(2000, 1000);
            await page.GotoAsync("/resources/api?pane=tags");
            await page.Locator("#resourcePaneGroupingButton").ClickAsync();
            await page.GetByRole(AriaRole.Menuitemradio, new() { Name = "None", Exact = true }).ClickAsync();
            await Assertions.Expect(page.Locator(".resource-row[data-resource-name]")).ToHaveCountAsync(6);
            var additions = Enumerable.Range(0, 22).Select(MetadataResourcesFixture.CreateDynamicChild).ToArray();
            await fixture.PublishAsync(additions.Select(resource => new ResourceViewModelChange(ResourceViewModelChangeType.Upsert, resource)).ToArray());
            await Assertions.Expect(page.Locator(".resource-row[data-resource-name]")).ToHaveCountAsync(28);
            foreach (var density in new[] { "Compact", "Comfortable", "Spacious" })
            {
                await page.GetByRole(AriaRole.Button, new() { Name = "Settings", Exact = true }).ClickAsync();
                await page.GetByRole(AriaRole.Button, new() { Name = density, Exact = true }).ClickAsync();
                await page.GetByRole(AriaRole.Button, new() { Name = dark ? "Dark" : "Light", Exact = true }).ClickAsync();
                await page.GetByRole(AriaRole.Button, new() { Name = "Close", Exact = true }).Last.ClickAsync();
                await AssertGeometryAsync(density);
                var pane = page.Locator(".resource-pane");
                foreach (var width in new[] { ResourcesLayout.MinimumPaneWidthPx, 560, 769, 960, 990, 1279, 1280, 1281, 1304, 1439, 1440, 1441, 1599, 1600, 1601, 1799, 1800, 1801, 2000 })
                {
                    stage = $"{density}-{width}-expanded";
                    if (await page.Locator(".pane-full-canvas").CountAsync() > 0)
                    {
                        await ResourcePaneNavigation.ToggleResourceListAsync(page);
                    }
                    await page.SetViewportSizeAsync(width <= 560 ? 1440 : width, 1000);
                    if (width <= 560)
                    {
                        var separator = page.Locator(".resource-pane-resizer");
                        // The separator handles keys only once its script is registered.
                        await Assertions.Expect(separator).ToHaveAttributeAsync("aria-disabled", "false");
                        await separator.FocusAsync();
                        await separator.PressAsync(width == ResourcesLayout.MinimumPaneWidthPx ? "Home" : "End");
                        await Assertions.Expect(separator).ToHaveAttributeAsync("aria-valuenow", width.ToString(CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        await ResourcePaneNavigation.ToggleResourceListAsync(page);
                        var chromeWidth = await page.EvaluateAsync<double>("() => innerWidth - document.querySelector('.resources-layout').getBoundingClientRect().width");
                        await page.SetViewportSizeAsync(width + (int)Math.Round(chromeWidth), 1000);
                        await page.WaitForFunctionAsync("""
                            width => Math.abs(document.querySelector('.resource-pane').getBoundingClientRect().width - width) <= .5
                            """, width);
                    }
                    if (width >= 1440)
                    {
                        await Assertions.Expect(page.Locator(".resource-row-insight-requests")).ToHaveCountAsync(3);
                        await Assertions.Expect(page.Locator(".resource-row-insight-latency")).ToHaveCountAsync(2);
                        await Assertions.Expect(page.Locator(".resource-row-insight-failed")).ToHaveCountAsync(1);
                        await Assertions.Expect(page.Locator(".resource-row-insight-requests").First).ToBeVisibleAsync();
                    }
                    else if (width >= 960)
                    {
                        await Assertions.Expect(page.Locator(".resource-row-insight-requests").First).Not.ToBeVisibleAsync();
                    }
                    await AssertGeometryAsync(density);
                    await page.ScreenshotAsync(new()
                    {
                        Path = Path.Combine(AppContext.BaseDirectory, $"ux29-final-{(dark ? "dark" : "light")}-{density}-pane{width}.png")
                    });
                    var parent = page.Locator(".resource-row[data-resource-name='api'] .resource-row-link");
                    await parent.FocusAsync();
                    await parent.PressAsync("ArrowLeft");
                    await Assertions.Expect(page.Locator("[data-resource-name='rebuilder-0']")).ToHaveCountAsync(0);
                    stage = $"{density}-{width}-collapsed";
                    await AssertGeometryAsync(density);
                    await parent.PressAsync("ArrowRight");
                    await Assertions.Expect(page.Locator("[data-resource-name='rebuilder-0']")).ToBeVisibleAsync();
                    var filter = pane.GetByRole(AriaRole.Searchbox);
                    await filter.FillAsync("rebuilder");
                    await Assertions.Expect(page.Locator("[data-resource-name='worker']")).ToHaveCountAsync(0);
                    stage = $"{density}-{width}-filtered";
                    await AssertGeometryAsync(density);
                    await filter.FillAsync("");
                    await Assertions.Expect(page.Locator(".resource-row[data-resource-name]")).ToHaveCountAsync(28);
                    stage = $"{density}-{width}-filter-cleared";
                    await AssertGeometryAsync(density);
                }
            }
            await fixture.PublishAsync(additions.Take(11).Select(resource => new ResourceViewModelChange(ResourceViewModelChangeType.Delete, resource)).ToArray());
            await Assertions.Expect(page.Locator(".resource-row[data-resource-name]")).ToHaveCountAsync(17);
            stage = "eleven-removed";
            await AssertGeometryAsync("Spacious");
            await fixture.PublishAsync(additions.Reverse().Select(resource => new ResourceViewModelChange(ResourceViewModelChangeType.Upsert, resource)).ToArray());
            await Assertions.Expect(page.Locator(".resource-row[data-resource-name]")).ToHaveCountAsync(28);
            stage = "reinserted-reverse-order";
            await AssertGeometryAsync("Spacious");
            // OrderStore survives browser contexts; choose a movable child in the actual order.
            var siblings = page.Locator(".resource-row[data-resource-parent='api']");
            var expectedOrder = await siblings.EvaluateAllAsync<string[]>("rows => rows.map(row => row.dataset.resourceName)");
            Assert.True(expectedOrder.Length >= 2);
            var reorder = siblings.Nth(1).Locator(".resource-row-link");
            (expectedOrder[0], expectedOrder[1]) = (expectedOrder[1], expectedOrder[0]);
            var console = new ConcurrentQueue<string>();
            page.Console += (_, message) => console.Enqueue($"{message.Type}: {message.Text}");
            await reorder.FocusAsync();
            await reorder.PressAsync("Alt+ArrowUp");
            try
            {
                await page.WaitForFunctionAsync("""
                    expected => JSON.stringify([...document.querySelectorAll('.resource-row[data-resource-parent="api"]')].map(row => row.dataset.resourceName)) === JSON.stringify(expected)
                    """, expectedOrder);
            }
            catch (TimeoutException)
            {
                var prefix = Path.Combine(AppContext.BaseDirectory, $"dynamic-reorder-{(dark ? "dark" : "light")}");
                await page.ScreenshotAsync(new() { Path = $"{prefix}.png", FullPage = true });
                var state = await page.Locator(".resource-pane").EvaluateAsync<string>("""
                    (pane, expected) => JSON.stringify({
                        expected,
                        rows: [...pane.querySelectorAll('.resource-row[data-resource-name]')].map(row => ({
                            name: row.dataset.resourceName, parent: row.dataset.resourceParent,
                            height: row.getBoundingClientRect().height,
                            shortcuts: row.querySelector('.resource-row-link').getAttribute('aria-keyshortcuts')
                        })),
                        background: getComputedStyle(pane).backgroundColor
                    }, null, 2)
                    """, expectedOrder);
                await File.WriteAllTextAsync($"{prefix}.json", state);
                await File.WriteAllLinesAsync($"{prefix}.console.log", console);
                throw;
            }
            stage = "reordered";
            await AssertGeometryAsync("Spacious");
            var selectAll = page.GetByRole(AriaRole.Button, new() { Name = Resources.Layout.ResourceHeaderAllResources, Exact = true });
            await selectAll.ClickAsync();
            await Assertions.Expect(page.Locator(".resource-select-all-toggle.on")).ToHaveCountAsync(1);
            await Assertions.Expect(page.Locator(".resource-row.selected[data-resource-name]")).ToHaveCountAsync(0);
            stage = "all-nested-selected";
            await AssertGeometryAsync("Spacious");
            await Assertions.Expect(page.Locator(".pane-full-canvas")).ToHaveCountAsync(0);
            stage = "all-nested-selected-sidebar";
            await AssertGeometryAsync("Spacious");
            await page.Locator("#resourcePaneGroupingButton").ClickAsync();
            await page.GetByRole(AriaRole.Menuitemradio, new() { Name = "Tags", Exact = true }).ClickAsync();
            await Assertions.Expect(page.Locator(".resource-tag-group").First).ToBeVisibleAsync();
            stage = "grouped-tags";
            await AssertGeometryAsync("Spacious");
            await page.ReloadAsync();
            await Assertions.Expect(page.Locator(".resource-row[data-resource-name]")).ToHaveCountAsync(8);
            stage = "reloaded";
            await AssertGeometryAsync("Spacious");
            await page.Locator("#resourcePaneGroupingButton").ClickAsync();
            await page.GetByRole(AriaRole.Menuitemradio, new() { Name = "None", Exact = true }).ClickAsync();
            await Assertions.Expect(page.Locator(".resource-row[data-resource-name]")).ToHaveCountAsync(6);
            stage = "ungrouped-after-reload";
            await AssertGeometryAsync("Spacious");
            await File.WriteAllTextAsync(
                Path.Combine(AppContext.BaseDirectory, $"dynamic-geometry-{(dark ? "dark" : "light")}.json"),
                JsonSerializer.Serialize(snapshots, new JsonSerializerOptions { WriteIndented = true }));

            async Task AssertGeometryAsync(string density)
            {
                var result = await page.Locator(".resource-pane-list").EvaluateAsync<JsonElement>("""
                    (list, {density, stage}) => {
                        const errors = [], rows = [...list.querySelectorAll('.resource-row[data-resource-name]')];
                        const compact = density === 'Compact', comfortable = density === 'Comfortable';
                        const fullCanvas = !!list.closest('.pane-full-canvas');
                        // A compact row is a single line. A spacious sidebar row adds a third line for its health,
                        // endpoint or source; the expanded list has columns for those, so its rows keep two lines.
                        const height = compact ? 32 : comfortable ? 52 : fullCanvas ? 60 : 76;
                        const iconBox = compact ? 22 : comfortable ? 32 : 40, glyphSize = density === 'Spacious' ? 20 : 16;
                        const nameOffsets = rows.map(row =>
                            row.querySelector('.resource-row-name').getBoundingClientRect().x -
                            row.querySelector('.resource-row-icon').getBoundingClientRect().right);
                        if (Math.max(...nameOffsets) - Math.min(...nameOffsets) > .5)
                            errors.push('persistent/ordinary name offset variance');
                        for (const [i,row] of rows.entries()) {
                            const b = row.getBoundingClientRect(), icon = row.querySelector('.resource-row-icon').getBoundingClientRect();
                            const rail = getComputedStyle(row,'::before');
                            if ((rail.backgroundColor === 'rgba(0, 0, 0, 0)') === row.classList.contains('selected') ||
                                icon.x - b.x - parseFloat(rail.width) < 7)
                                errors.push(`${row.dataset.resourceName}: selection rail/spacing`);
                            if (b.height !== height) errors.push(`${row.dataset.resourceName}: height ${b.height}`);
                            if (icon.width !== iconBox || icon.height !== iconBox) errors.push(`${row.dataset.resourceName}: icon box`);
                            const mainGlyph = row.querySelector('.resource-row-glyph svg').getBoundingClientRect();
                            if (mainGlyph.width !== glyphSize || mainGlyph.height !== glyphSize) errors.push(`${row.dataset.resourceName}: glyph`);
                            const expectedFont = compact ? 12 : comfortable ? 14 : 16;
                            if (parseFloat(getComputedStyle(row.querySelector('.resource-row-name')).fontSize) !== expectedFont)
                                errors.push(`${row.dataset.resourceName}: density font`);
                            for (const chip of row.querySelectorAll('.resource-row-insight')) {
                                const chipBox = chip.getBoundingClientRect();
                                if (!chipBox.width) continue;
                                const style = getComputedStyle(chip), glyph = chip.querySelector('svg').getBoundingClientRect();
                                const font = compact ? 11 : comfortable ? 12 : 14;
                                const line = compact ? 16 : comfortable ? 18 : 20;
                                const target = compact ? 24 : comfortable ? 28 : 32;
                                const iconSize = compact ? 12 : comfortable ? 14 : 16;
                                if (parseFloat(style.fontSize) !== font || parseFloat(style.lineHeight) !== line ||
                                    chipBox.height !== target || chipBox.width < target ||
                                    glyph.width !== iconSize || glyph.height !== iconSize ||
                                    style.fontVariantNumeric !== 'tabular-nums')
                                    errors.push(`${row.dataset.resourceName}: chip density geometry; font=${style.fontSize}; line=${style.lineHeight}; target=${chipBox.width}x${chipBox.height}; icon=${glyph.width}x${glyph.height}`);
                            }
                            if (i && b.y < rows[i-1].getBoundingClientRect().bottom + 1) errors.push(`${i}: overlap/gap`);
                            if (i && rows[i-1].nextElementSibling === row &&
                                b.y - rows[i-1].getBoundingClientRect().bottom !== 1)
                                errors.push(`${row.dataset.resourceName}: nonuniform sibling gap`);
                            if (icon.y < b.y || icon.bottom > b.bottom) errors.push(`${i}: icon outside row`);
                            const guide = row.querySelector('.resource-tree-guide.branch');
                            if (guide) {
                                const g = guide.getBoundingClientRect(), elbow = getComputedStyle(guide,'::after');
                                if (getComputedStyle(guide).borderInlineStartStyle !== 'dotted' || elbow.borderTopStyle !== 'dotted')
                                    errors.push(`${i}: tree stroke`);
                                if (g.x + parseFloat(elbow.left) + parseFloat(elbow.width) !== icon.x ||
                                    g.y + parseFloat(elbow.top) !== icon.y + icon.height/2 ||
                                    g.x !== icon.x + icon.width/2 - parseFloat(getComputedStyle(row).getPropertyValue('--resource-row-depth-step')))
                                    errors.push(`${i}: connector`);
                                if (!guide.classList.contains('continuing') && g.bottom !== b.y + height/2)
                                    errors.push(`${i}: last-child termination`);
                            }
                            const badge = row.querySelector('.persistent-container-badge');
                            if (badge) {
                                const bb = badge.getBoundingClientRect(), style = getComputedStyle(badge);
                                // A compact row's 22px icon box leaves no room under its 16px glyph for the 10px badge
                                // inside a 32px row, so there the badge overlaps the glyph's lower corner, as it did
                                // before #21727. It must still stay inside the row and clear of the name.
                                if (style.position !== 'absolute' || style.pointerEvents !== 'none' ||
                                    bb.right !== icon.right + 4 || bb.bottom !== icon.bottom + 3 ||
                                    (!compact && bb.y < mainGlyph.bottom) || bb.bottom > b.bottom ||
                                    bb.right > row.querySelector('.resource-row-name').getBoundingClientRect().x)
                                    errors.push(`${i}: badge overlay/clipping`);
                            }
                            if (Math.abs(mainGlyph.x + mainGlyph.width/2 - icon.x - icon.width/2) > .5 ||
                                Math.abs(mainGlyph.y + mainGlyph.height/2 - icon.y - icon.height/2) > .5)
                                errors.push(`${i}: displaced base glyph`);
                            for (const time of row.querySelectorAll('time')) {
                                if (time.getBoundingClientRect().width &&
                                    (time.scrollWidth > time.clientWidth || time.tabIndex !== 0 ||
                                        !time.getAttribute('aria-label')?.includes(time.textContent.trim()) ||
                                        !time.getAttribute('data-tooltip')?.includes(time.textContent.trim())))
                                    errors.push(`${row.dataset.resourceName}: incomplete start time; client=${time.clientWidth}; scroll=${time.scrollWidth}; text=${time.textContent}`);
                            }
                        }
                        const paneWidth = list.closest('.resource-pane').getBoundingClientRect().width;
                        // The row tracks' minimums must fit the pane at every width, or the actions scroll out of view.
                        if (list.scrollWidth > list.clientWidth + 1)
                            errors.push(`horizontal overflow; client=${list.clientWidth}; scroll=${list.scrollWidth}`);
                        const columns = {};
                        if (fullCanvas && paneWidth >= 960) {
                            const templates = new Set(rows.map(row => getComputedStyle(row).gridTemplateColumns));
                            if (templates.size !== 1) errors.push('content-dependent grid tracks');
                            for (const selector of ['.resource-row-health','.resource-row-endpoints .url-container',
                                '.resource-row-source','.resource-row-insights',
                                '.resource-row-insight-requests','.resource-row-insight-latency','.resource-row-insight-failed',
                                '.resource-row-actions']) {
                                const values = rows.map(row => row.querySelector(selector)?.getBoundingClientRect())
                                    .filter(box => box?.width).map(box => box.x);
                                columns[selector] = values;
                                if (values.length && Math.max(...values) - Math.min(...values) > 1)
                                    errors.push(`${selector}: column variance`);
                            }
                            for (const row of rows) {
                                const box = selector => row.querySelector(selector)?.getBoundingClientRect();
                                const identity = box('.resource-row-link'), health = box('.resource-row-health');
                                const details = box('.resource-row-endpoints'), insights = box('.resource-row-insights');
                                const actions = box('.resource-row-actions'), bounds = row.getBoundingClientRect();
                                if (identity.right > health.x + .5 || health.right > details.x + .5 ||
                                    details.right > (insights?.width ? insights.x : actions.x) + .5 ||
                                    (insights?.width && insights.right > actions.x + .5) ||
                                    actions.right > bounds.right + .5)
                                    errors.push(`${row.dataset.resourceName}: intersecting tracks`);
                                if (insights?.width) {
                                    const gap = parseFloat(getComputedStyle(row).columnGap);
                                    const latency = box('.resource-row-insight-latency');
                                    if (Math.abs(actions.x - insights.right - gap) > .5)
                                        errors.push(`${row.dataset.resourceName}: activity/action gap; activityRight=${insights.right}; actionX=${actions.x}; expected=${gap}`);
                                    if (latency?.width && Math.abs(latency.right - insights.right) > .5)
                                        errors.push(`${row.dataset.resourceName}: latency not trailing; latencyRight=${latency.right}; trackRight=${insights.right}`);
                                }
                                for (const label of row.querySelectorAll('.resource-relationship-label')) {
                                    if (label.getBoundingClientRect().width && label.scrollWidth > label.clientWidth)
                                        errors.push(`${row.dataset.resourceName}: relationship label cutoff; client=${label.clientWidth}; scroll=${label.scrollWidth}`);
                                }
                                for (const label of row.querySelectorAll('.resource-row-health-status,.resource-row-start-time time,.resource-relationship-count,.resource-row-insight > span')) {
                                    if (label.getBoundingClientRect().width && label.scrollWidth > label.clientWidth)
                                        errors.push(`${row.dataset.resourceName}: non-truncatable ${label.className}; client=${label.clientWidth}; scroll=${label.scrollWidth}; text=${label.textContent}`);
                                }
                            }
                        }
                        return {stage, height, errors, columns,
                            paneWidth: list.getBoundingClientRect().width,
                            rows: rows.map(row => ({name:row.dataset.resourceName,
                                parent:row.dataset.resourceParent,
                                bounds:row.getBoundingClientRect().toJSON(),
                                icon:row.querySelector('.resource-row-icon').getBoundingClientRect().toJSON(),
                                badge:row.querySelector('.persistent-container-badge')?.getBoundingClientRect().toJSON()}))};
                    }
                    """, new { density, stage });
                var errors = result.GetProperty("errors").EnumerateArray().Select(error => error.GetString()).ToArray();
                Assert.True(errors.Length == 0, $"{stage}: {string.Join("; ", errors)}");
                snapshots.Add(result);
            }
        });
    }

    [Theory]
    [InlineData(769)]
    [InlineData(1440)]
    [InlineData(2000)]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task ContextMenu_PreservesNativeEndpointActivityAndControlMenus(int width)
    {
        await RunTestAsync(async page =>
        {
            fixture.StartDynamicSession();
            await fixture.SeedActivityAsync();
            await page.SetViewportSizeAsync(width, 900);
            await page.GotoAsync("/resources/cache?pane=tags");
            await Assertions.Expect(page.Locator(".resource-row[data-resource-name]")).ToHaveCountAsync(8);
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            await ResourcePaneNavigation.ToggleResourceListAsync(page);
            var original = page.Url;
            var row = page.Locator(".resource-row[data-resource-name='api']").First;
            var menu = page.Locator("fluent-menu[aria-labelledby='resource-pane-context-menu-header'] fluent-menu-list");
            var targets = new List<ILocator>
            {
                row.Locator(".resource-row-primary-endpoint svg").First,
                row.GetByRole(AriaRole.Button, new() { Name = "Actions for api", Exact = true })
            };
            if (width >= 1440)
            {
                // Activity is intentionally undisclosed, and its queries suspended, below 1440px.
                await Assertions.Expect(row.Locator(".resource-row-insight-latency")).ToBeVisibleAsync();
                targets.Add(row.Locator(".resource-row-insight-requests span").First);
                targets.Add(row.Locator(".resource-row-insight-latency span").First);
            }
            else
            {
                await Assertions.Expect(row.Locator(".resource-row-insight")).ToHaveCountAsync(0);
            }
            var selected = page.Locator(".resource-row[data-resource-name] > .resource-row-link[aria-current]");
            var selection = await selected.EvaluateAllAsync<string[]>("links => links.map(link => link.parentElement.dataset.resourceName)");
            await page.EvaluateAsync("""
                () => document.addEventListener('contextmenu', event => {
                    window.resourceContextMenuEvent = event;
                }, true)
                """);
            foreach (var target in targets)
            {
                await target.ClickAsync(new() { Button = MouseButton.Right });
                Assert.False(await page.EvaluateAsync<bool>("() => window.resourceContextMenuEvent.defaultPrevented"));
                await Assertions.Expect(menu).Not.ToBeVisibleAsync();
                Assert.Equal(original, page.Url);
                Assert.Equal(selection, await selected.EvaluateAllAsync<string[]>("links => links.map(link => link.parentElement.dataset.resourceName)"));
                await Assertions.Expect(page.Locator(".resource-select-all-toggle"))
                    .ToHaveAttributeAsync("aria-description", Resources.Layout.ResourcePaneAllSelected);
                await page.Keyboard.PressAsync("Escape");
            }
            foreach (var target in new[] { row.Locator(".resource-row-icon"), row.Locator(".resource-row-wide-metadata") })
            {
                await target.ClickAsync(new() { Button = MouseButton.Right });
                await Assertions.Expect(menu).ToBeVisibleAsync();
                await Assertions.Expect(menu.GetByText("api", new() { Exact = true })).ToBeVisibleAsync();
                await page.Keyboard.PressAsync("Escape");
                await Assertions.Expect(row.Locator(".resource-row-link")).ToBeFocusedAsync();
            }
        });
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task ActivityLinks_OpenExactResourceWindowAndRestoreSelection(bool latency, bool keyboard)
    {
        await RunTestAsync(async page =>
        {
            fixture.StartDynamicSession();
            var seededTime = await fixture.SeedActivityAsync();
            await page.SetViewportSizeAsync(2000, 1000);
            await page.GotoAsync("/resources/cache?pane=tags");
            await Assertions.Expect(page.Locator(".resource-row[data-resource-name]")).ToHaveCountAsync(8);
            await ResourcePaneNavigation.ToggleResourceListAsync(page);
            var row = page.Locator(".resource-row[data-resource-name='api']").First;
            var link = row.Locator(latency ? ".resource-row-insight-latency" : ".resource-row-insight-requests");
            await Assertions.Expect(link).ToBeVisibleAsync();
            var href = await link.GetAttributeAsync("href");
            Assert.NotNull(href);
            var expected = new Uri(new Uri(page.Url), href);
            Assert.Equal("/traces/resource/api", expected.AbsolutePath);
            var filters = TelemetryFilterFormatter.DeserializeFiltersFromString(QueryHelpers.ParseQuery(expected.Query)["filters"].ToString());
            Assert.Equal(2, filters.Count);
            Assert.Equal(KnownTraceFields.TimestampField, filters[0].Field);
            Assert.Equal(KnownTraceFields.TimestampField, filters[1].Field);
            Assert.Equal(FilterCondition.GreaterThanOrEqual, filters[0].Condition);
            Assert.Equal(FilterCondition.LessThan, filters[1].Condition);
            var start = DateTime.ParseExact(filters[0].Value, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            var end = DateTime.ParseExact(filters[1].Value, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            Assert.Equal(DateTimeKind.Utc, start.Kind);
            Assert.Equal(DateTimeKind.Utc, end.Kind);
            Assert.Equal(TimeSpan.FromMinutes(5), end - start);
            Assert.InRange(seededTime, start, end);
            await page.EvaluateAsync("""
                () => {
                    window.activityRowActivations = 0;
                    document.addEventListener('click', event => {
                        if (event.target.closest('.resource-row[data-resource-name="api"] > .resource-row-link'))
                            window.activityRowActivations++;
                    });
                }
                """);
            if (keyboard)
            {
                await link.FocusAsync();
                await Assertions.Expect(link).ToBeFocusedAsync();
                await link.PressAsync("Enter");
            }
            else
            {
                await link.ClickAsync();
            }
            await Assertions.Expect(page).ToHaveURLAsync(new Regex($"^{Regex.Escape(expected.AbsoluteUri)}$"));
            Assert.Equal(expected.AbsoluteUri, page.Url);
            await Assertions.Expect(page.Locator(".resource-row[data-resource-name='api'] .resource-row-link").First)
                .ToHaveAttributeAsync("aria-current", "page");
            await Assertions.Expect(page.Locator(".trace-name-content")).ToHaveCountAsync(20);
            await Assertions.Expect(page.Locator(".trace-name-content")
                .Filter(new() { HasTextRegex = new Regex(@"api: Test span\. Id: api-1\b") })).ToBeVisibleAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Toolbar, new() { Name = "Page toolbar" })
                .GetByRole(AriaRole.Button, new() { Name = "Filters", Exact = true })).ToBeVisibleAsync();
            Assert.Equal(0, await page.EvaluateAsync<int>("() => window.activityRowActivations"));
            await page.EvaluateAsync("() => history.back()");
            await Assertions.Expect(page).ToHaveURLAsync(new Regex("/resources/api$"));
            await Assertions.Expect(page.Locator("h1.resource-title")).ToHaveTextAsync("api");
            await Assertions.Expect(page.Locator(".resource-row[data-resource-name='api'] .resource-row-link").First)
                .ToHaveAttributeAsync("aria-current", "page");
            await Assertions.Expect(page.Locator(".resource-row[data-resource-name]")).ToHaveCountAsync(8);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task RowComposition_AlignsStatusAndSingleLineDetailsWithAccessibleContrast(bool dark)
    {
        await RunTestAsync(async page =>
        {
            await fixture.SeedActivityAsync();
            await page.SetViewportSizeAsync(2000, 1000);
            await page.GotoAsync("/resources/api?pane=tags");
            await page.GetByRole(AriaRole.Button, new() { Name = "Settings", Exact = true }).ClickAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = dark ? "Dark" : "Light", Exact = true }).ClickAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "Close", Exact = true }).Last.ClickAsync();
            await ResourcePaneNavigation.ToggleResourceListAsync(page);
            await page.WaitForFunctionAsync("""
                () => document.querySelector('.resource-pane').getBoundingClientRect().width ===
                    document.querySelector('.resources-layout').getBoundingClientRect().width
                """);
            var row = page.Locator(".resource-row[data-resource-name='api']").First;
            foreach (var state in new[] { "selected", "hover", "focus" })
            {
                if (state == "hover")
                {
                    await row.HoverAsync();
                }
                if (state == "focus")
                {
                    await page.Mouse.MoveAsync(1, 1);
                    await row.Locator(".resource-row-link").FocusAsync();
                }
                var geometry = await row.EvaluateAsync<double[]>("""
                    row => {
                        const box = selector => row.querySelector(selector).getBoundingClientRect();
                        const time = box('.resource-row-start-time');
                        const detail = box('.resource-row-endpoints'), endpoint = box('.resource-row-endpoints a');
                        const source = box('.resource-row-source'), rail = box('.resource-row-actions');
                        const center = b => b.y + b.height / 2;
                        return [center(time) - center(row.getBoundingClientRect()), center(endpoint) - center(detail),
                            center(source) - center(detail), center(rail) - center(row.getBoundingClientRect())];
                    }
                    """);
                Assert.Equal(0, geometry[0]);
                foreach (var (selector, delta) in new[]
                {
                    ("endpoint/detail center", geometry[1]),
                    ("source/detail center", geometry[2]),
                    ("actions/row center", geometry[3])
                })
                {
                    Assert.True(Math.Abs(delta) <= 1, $"{selector}: delta={delta}; state={state}; dark={dark}");
                }
                var contrast = await row.EvaluateAsync<JsonElement>("""
                    row => {
                        const canvas = document.createElement('canvas'), ctx = canvas.getContext('2d', {willReadFrequently:true});
                        canvas.width = canvas.height = 1;
                        // getImageData's pixels are a Uint8ClampedArray, whose map would round each linear value to 0 or 1.
                        const luminance = rgb => Array.from(rgb).slice(0,3).map(v => {
                            v /= 255; return v <= .04045 ? v / 12.92 : ((v + .055) / 1.055) ** 2.4;
                        }).reduce((sum,v,i) => sum + v * [.2126,.7152,.0722][i], 0);
                        return [...row.querySelectorAll('.resource-row-name,.resource-row-sub,.resource-row-start-time,.resource-row-endpoints a,.resource-row-source-value,.resource-row-insight')].map(e => {
                            const ancestors = []; for (let a = e; a; a = a.parentElement) ancestors.unshift(a);
                            ctx.fillStyle = 'white'; ctx.fillRect(0,0,1,1);
                            for (const a of ancestors) { ctx.fillStyle = getComputedStyle(a).backgroundColor; ctx.fillRect(0,0,1,1); }
                            const background = luminance(ctx.getImageData(0,0,1,1).data);
                            ctx.fillStyle = getComputedStyle(e).color; ctx.fillRect(0,0,1,1);
                            const foreground = luminance(ctx.getImageData(0,0,1,1).data);
                            return {ratio:(Math.max(background,foreground)+.05)/(Math.min(background,foreground)+.05),
                                minimum:e.textContent.trim() ? 4.5 : 3};
                        });
                    }
                    """);
                Assert.NotEmpty(contrast.EnumerateArray());
                Assert.All(contrast.EnumerateArray(), sample => Assert.True(sample.GetProperty("ratio").GetDouble() >= sample.GetProperty("minimum").GetDouble(),
                    $"{state}; dark={dark}; contrast={sample}"));
            }
            await Assertions.Expect(row.Locator(".resource-relationship-summary")).ToHaveCountAsync(0);
            var selectAll = page.GetByRole(AriaRole.Button, new() { Name = Resources.Layout.ResourceHeaderAllResources, Exact = true });
            await Assertions.Expect(selectAll).ToHaveAttributeAsync("aria-description", Resources.Layout.ResourcePaneAllSelected);
            await selectAll.FocusAsync();
            await selectAll.PressAsync("Space");
            await Assertions.Expect(selectAll).ToHaveAttributeAsync("aria-description", Resources.Layout.ResourcePaneAllSelected);
            await Assertions.Expect(page.Locator(".resource-row.selected[data-resource-name]")).ToHaveCountAsync(0);
            foreach (var density in new[] { "Compact", "Comfortable", "Spacious" })
            {
                await page.GetByRole(AriaRole.Button, new() { Name = "Settings", Exact = true }).ClickAsync();
                await page.GetByRole(AriaRole.Button, new() { Name = density, Exact = true }).ClickAsync();
                await page.GetByRole(AriaRole.Button, new() { Name = "Close", Exact = true }).Last.ClickAsync();
                await Assertions.Expect(selectAll).ToHaveAttributeAsync("aria-description", Resources.Layout.ResourcePaneAllSelected);
                await Assertions.Expect(page.Locator(".resource-select-all-toggle.on")).ToHaveCountAsync(1);
                await Assertions.Expect(page.Locator(".resource-row.selected[data-resource-name]")).ToHaveCountAsync(0);
            }
            await row.Locator(".resource-row-link").ClickAsync();
            await Assertions.Expect(selectAll).Not.ToHaveAttributeAsync("aria-description", Resources.Layout.ResourcePaneAllSelected);
            Assert.Null(await selectAll.GetAttributeAsync("aria-description"));
            var cache = page.Locator(".resource-row[data-resource-name='cache']").First;
            await page.Mouse.MoveAsync(1, 1);
            await selectAll.FocusAsync();
            Assert.Equal("rgba(0, 0, 0, 0)", await cache.EvaluateAsync<string>("row => getComputedStyle(row).backgroundColor"));
            await cache.Locator(".resource-row-link").ClickAsync(new() { Modifiers = [KeyboardModifier.Control] });
            await Assertions.Expect(page.Locator(".resource-row.selected[data-resource-name]")).ToHaveCountAsync(4);
            await selectAll.ClickAsync();
            await Assertions.Expect(selectAll).ToHaveAttributeAsync("aria-description", Resources.Layout.ResourcePaneAllSelected);
            await Assertions.Expect(page.Locator(".resource-select-all-toggle.on")).ToHaveCountAsync(1);
            await Assertions.Expect(page.Locator(".resource-row.selected[data-resource-name]")).ToHaveCountAsync(0);
        });
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task HierarchyAndSource_PreserveTreePathsAndCompleteDisclosure(bool dark, bool forcedColors)
    {
        await RunTestAsync(async page =>
        {
            await page.SetViewportSizeAsync(1440, 1000);
            await page.GotoAsync("/resources/api?pane=tags");
            await page.GetByRole(AriaRole.Button, new() { Name = "Settings", Exact = true }).ClickAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = dark ? "Dark" : "Light", Exact = true }).ClickAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "Close", Exact = true }).Last.ClickAsync();
            await page.EmulateMediaAsync(new() { ForcedColors = forcedColors ? ForcedColors.Active : ForcedColors.None });
            await page.Locator("#resourcePaneGroupingButton").ClickAsync();
            await page.GetByRole(AriaRole.Menuitemradio, new() { Name = "None", Exact = true }).ClickAsync();
            var nested = page.Locator(".resource-row[data-resource-name='frontend']");
            await Assertions.Expect(nested.Locator(".resource-row-link")).ToHaveAttributeAsync("data-resource-depth", "2");
            Assert.Equal(2, await nested.Locator(".resource-tree-guide").CountAsync());
            Assert.Equal(1, await page.Locator(".resource-row[data-resource-name='queue'] .resource-tree-guide.continuing").CountAsync());
            Assert.Equal(0, await page.Locator(".resource-row[data-resource-name='worker'] .resource-tree-guide.continuing").CountAsync());
            var parent = page.Locator(".resource-row[data-resource-name='cache'] .resource-row-link").First;
            await parent.FocusAsync();
            await parent.PressAsync("ArrowLeft");
            await Assertions.Expect(nested).ToHaveCountAsync(0);
            await parent.PressAsync("ArrowRight");
            await Assertions.Expect(nested).ToBeVisibleAsync();
            await parent.PressAsync("ArrowRight");
            await Assertions.Expect(page.Locator(".resource-row[data-resource-name='queue'] .resource-row-link")).ToBeFocusedAsync();
            await page.Keyboard.PressAsync("ArrowLeft");
            await Assertions.Expect(page.Locator(".resource-row[data-resource-name='frontend']")).ToHaveCountAsync(0);
            await page.Keyboard.PressAsync("ArrowRight");
            await Assertions.Expect(nested).ToBeVisibleAsync();
            var pane = page.Locator(".resource-pane");
            var theme = forcedColors ? "forced-colors" : dark ? "dark" : "light";
            foreach (var width in new[] { 420, 560 })
            {
                var separator = page.GetByRole(AriaRole.Separator, new() { Name = Resources.Layout.ResourcePaneResize });
                var box = await separator.BoundingBoxAsync();
                var initial = await pane.BoundingBoxAsync();
                Assert.NotNull(box);
                Assert.NotNull(initial);
                await page.Mouse.MoveAsync(box.X + box.Width / 2, box.Y + box.Height / 2);
                await page.Mouse.DownAsync();
                await page.Mouse.MoveAsync(box.X + box.Width / 2 + width - initial.Width, box.Y + box.Height / 2);
                await page.Mouse.UpAsync();
                await page.WaitForFunctionAsync("width => Math.abs(document.querySelector('.resource-pane').getBoundingClientRect().width - width) < 1", width);
                await page.ScreenshotAsync(new() { Path = Path.Combine(AppContext.BaseDirectory, $"resource-tree-{theme}-{width}.png") });
                Assert.False(await pane.EvaluateAsync<bool>("el => el.querySelector('.resource-pane-list').scrollWidth > el.querySelector('.resource-pane-list').clientWidth"));
            }
            await ResourcePaneNavigation.ToggleResourceListAsync(page);
            try
            {
                await Assertions.Expect(page.Locator(".pane-full-canvas")).ToBeVisibleAsync();
            }
            catch (PlaywrightException ex)
            {
                await page.ScreenshotAsync(new() { Path = Path.Combine(AppContext.BaseDirectory, $"canvas-failure-{theme}.png") });
                throw new InvalidOperationException(await page.Locator(".resources-layout").EvaluateAsync<string>("""
                    e => {
                        const chain=[];
                        for(let p=e;p;p=p.parentElement){const s=getComputedStyle(p);chain.push({tag:p.tagName,cls:p.className,
                            box:p.getBoundingClientRect().toJSON(),display:s.display,visibility:s.visibility,height:s.height});}
                        return JSON.stringify(chain);
                    }
                    """), ex);
            }
            foreach (var width in new[] { 1304, 1440, 2000 })
            {
                await page.SetViewportSizeAsync(width, 1000);
                await page.WaitForFunctionAsync("""
                    () => Math.abs(document.querySelector('.resource-pane').getBoundingClientRect().width -
                        document.querySelector('.resources-layout').getBoundingClientRect().width) < 1
                    """);
                var source = page.Locator(".resource-row[data-resource-name='api'] .resource-row-source-value");
                var before = page.Url;
                await Assertions.Expect(source).ToHaveTextAsync("MyFrontend.csproj");
                await source.FocusAsync();
                await Assertions.Expect(source).ToBeFocusedAsync();
                await Assertions.Expect(source).ToHaveAttributeAsync("data-tooltip", Path.Combine("workspace", "MyFrontend.csproj"));
                await Assertions.Expect(source).ToHaveAttributeAsync("aria-label", $"Source: {Path.Combine("workspace", "MyFrontend.csproj")}");
                await Assertions.Expect(page.Locator(".resource-relationship-summary, .resource-row-dependencies, .resource-row-references")).ToHaveCountAsync(0);
                var columns = await page.Locator(".resource-row-source").EvaluateAllAsync<double[]>(
                    "sources => sources.map(source => source.getBoundingClientRect().x)");
                Assert.InRange(columns.Max() - columns.Min(), 0, 1);
                var image = page.Locator(".resource-row[data-resource-name='cache'] .resource-row-source-value");
                await Assertions.Expect(image).ToHaveAttributeAsync("data-tooltip", MetadataResourcesFixture.LongImage);
                Assert.True(await image.EvaluateAsync<bool>("source => source.scrollWidth > source.clientWidth"));
                Assert.Equal(before, page.Url);
                await page.ScreenshotAsync(new() { Path = Path.Combine(AppContext.BaseDirectory, $"resource-tree-{theme}-canvas-{width}.png") });
            }
        });
    }

    [Theory]
    [InlineData("/resources/cache?pane=tags", false)]
    [InlineData("/structuredlogs/resource/cache", true)]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task ExtremeName_RetainsFullAccessibleAndHoverKeyboardDisclosure(string url, bool unavailable)
    {
        await RunTestAsync(async page =>
        {
            await page.SetViewportSizeAsync(1440, 900);
            await page.GotoAsync(url);
            var expectedTooltip = unavailable
                ? $"{MetadataResourcesFixture.LongResourceName}\n{Resources.Layout.ResourcePaneNoStructuredLogs}"
                : MetadataResourcesFixture.LongResourceName;
            var link = page.Locator(".resource-row[data-resource-name='worker'] .resource-row-link").First;
            await Assertions.Expect(link).ToHaveAttributeAsync("aria-label", MetadataResourcesFixture.LongResourceName);
            Assert.True(await link.Locator(".resource-row-name").EvaluateAsync<bool>("el => el.scrollWidth > el.clientWidth"));
            await link.Locator(".resource-row-name").HoverAsync();
            await Assertions.Expect(link).ToHaveAttributeAsync("data-tooltip", expectedTooltip);
            await Assertions.Expect(page.Locator("aspire-tooltip-provider fluent-tooltip[aria-hidden='false']")).ToHaveTextAsync(expectedTooltip);
            await page.Mouse.MoveAsync(1, 1);
            await page.Keyboard.PressAsync("Tab");
            await link.FocusAsync();
            await Assertions.Expect(page.Locator("aspire-tooltip-provider fluent-tooltip[aria-hidden='false']")).ToHaveTextAsync(expectedTooltip);
            await link.PressAsync("Shift+F10");
            await Assertions.Expect(page.Locator("#resource-pane-context-menu-header")).ToHaveTextAsync(MetadataResourcesFixture.LongResourceName);
            await page.Keyboard.PressAsync("Escape");
            await Assertions.Expect(link).ToBeFocusedAsync();
        });
    }

    [Theory]
    [InlineData(390, false)]
    [InlineData(420, false)]
    [InlineData(560, false)]
    [InlineData(420, true)]
    [InlineData(560, true)]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task Rows_KeepDensityRhythmAndProtectedSidebarActions(int width, bool dark)
    {
        await RunTestAsync(async page =>
        {
            await page.SetViewportSizeAsync(1440, 900);
            await page.GotoAsync("/resources/cache?pane=tags");
            // A spacious sidebar row has a third line; the expanded list keeps two because it has columns for those details.
            foreach (var (density, sidebarHeight, fullCanvasHeight) in new[] { ("Compact", 32d, 32d), ("Comfortable", 52d, 52d), ("Spacious", 76d, 60d) })
            {
                await page.SetViewportSizeAsync(1440, 900);
                if (await page.Locator(".resources-layout.pane-full-canvas").CountAsync() > 0)
                {
                    await ResourcePaneNavigation.ToggleResourceListAsync(page);
                    await Assertions.Expect(page.Locator(".resources-layout.pane-full-canvas")).ToHaveCountAsync(0);
                }
                await page.GetByRole(AriaRole.Button, new() { Name = "Settings", Exact = true }).ClickAsync();
                await page.GetByRole(AriaRole.Button, new() { Name = density, Exact = true }).ClickAsync();
                await page.GetByRole(AriaRole.Button, new() { Name = dark ? "Dark" : "Light", Exact = true }).ClickAsync();
                await page.GetByRole(AriaRole.Button, new() { Name = "Close", Exact = true }).Last.ClickAsync();
                var pane = page.Locator(".resource-pane");
                if (width == 390)
                {
                    await page.SetViewportSizeAsync(390, 844);
                    await Assertions.Expect(page.Locator(".resource-pane-resizer")).ToHaveCountAsync(0);
                    if (!await pane.IsVisibleAsync())
                    {
                        await page.Locator(".resource-picker").ClickAsync();
                    }
                }
                else
                {
                    var separator = page.GetByRole(AriaRole.Separator, new() { Name = Resources.Layout.ResourcePaneResize });
                    var handle = await separator.BoundingBoxAsync();
                    var initial = await pane.BoundingBoxAsync();
                    Assert.NotNull(handle);
                    Assert.NotNull(initial);
                    await page.Mouse.MoveAsync(handle.X + handle.Width / 2, handle.Y + handle.Height / 2);
                    await page.Mouse.DownAsync();
                    await page.Mouse.MoveAsync(handle.X + handle.Width / 2 + width - initial.Width, handle.Y + handle.Height / 2);
                    await page.Mouse.UpAsync();
                    await Assertions.Expect(separator).ToHaveAttributeAsync("aria-valuenow", width.ToString(CultureInfo.InvariantCulture));
                }

                foreach (var fullCanvas in width == 390 ? new[] { false } : new[] { false, true })
                {
                    if (fullCanvas)
                    {
                        await ResourcePaneNavigation.ToggleResourceListAsync(page);
                        await Assertions.Expect(page.Locator(".pane-full-canvas")).ToBeVisibleAsync();
                        await page.WaitForFunctionAsync("""
                            () => Math.abs(document.querySelector('.resource-pane').getBoundingClientRect().width -
                                document.querySelector('.resources-layout').getBoundingClientRect().width) < 1
                            """);
                    }
                    await Assertions.Expect(pane).ToBeVisibleAsync();
                    // Compact sidebar rows reveal their actions on hover, so measure them with the pointer away first.
                    await page.Mouse.MoveAsync(1, 1);
                    const string measureRows = """
                        rows => rows.map(row => {
                            const rect = row.getBoundingClientRect();
                            const next = row.nextElementSibling;
                            const rail = row.querySelector('.resource-row-actions');
                            const shown = el => el.getBoundingClientRect().width > 0 && el.getBoundingClientRect().height > 0;
                            const overflow = rail.querySelector('.aspire-menu-button');
                            const essential = [...rail.querySelectorAll('.resource-row-highlighted, .resource-row-primary-endpoint')];
                            return [rect.height,
                                next?.matches('.resource-row[data-resource-name]') ? next.getBoundingClientRect().top - rect.bottom : 1,
                                rail.getBoundingClientRect().width, shown(overflow) ? 1 : 0,
                                essential.every(shown) ? 1 : 0,
                                [...rail.querySelectorAll('.resource-row-action')].some(shown) ? 1 : 0,
                                row.matches(':hover, :focus-within') ? 1 : 0];
                        })
                        """;
                    var resourceRows = pane.Locator(".resource-row[data-resource-name]");
                    var rows = await resourceRows.EvaluateAllAsync<double[][]>(measureRows);
                    Assert.NotEmpty(rows);
                    var compactSidebar = density == "Compact" && !fullCanvas;
                    Assert.All(rows, values =>
                    {
                        Assert.Equal(fullCanvas ? fullCanvasHeight : sidebarHeight, values[0]);
                        Assert.Equal(1, values[1]);
                        if (compactSidebar && values[6] == 0)
                        {
                            // At rest, a compact row's rail narrows to the expand toggle and hides every action, so the
                            // name gets the room. Hovering or focusing it brings the actions back (checked below).
                            Assert.Equal(20, values[2]);
                            Assert.Equal(0, values[5]);
                        }
                        else
                        {
                            Assert.Equal(1, values[3]);
                            if (!fullCanvas)
                            {
                                Assert.Equal(112, values[2]);
                                Assert.Equal(1, values[4]);
                            }
                        }
                    });
                    if (compactSidebar)
                    {
                        // At most one row can still have focus from the controls used above; the others are at rest.
                        Assert.True(rows.Count(values => values[6] == 0) >= rows.Length - 1);
                        // Hover reveals the full rail, and so does keyboard focus, so the actions stay reachable
                        // without a pointer.
                        foreach (var reveal in new Func<ILocator, Task>[] { row => row.HoverAsync(), row => row.Locator(".resource-row-link").FocusAsync() })
                        {
                            var revealed = resourceRows.First;
                            await reveal(revealed);
                            var values = (await revealed.EvaluateAsync<double[][]>($"row => ({measureRows})([row])"))[0];
                            Assert.Equal(new double[] { 112, 1, 1, 1 }, values[2..6]);
                            await page.Mouse.MoveAsync(1, 1);
                            await revealed.Locator(".resource-row-link").BlurAsync();
                        }
                    }
                    var list = pane.Locator(".resource-pane-list");
                    Assert.True(await list.EvaluateAsync<bool>("el => el.scrollWidth <= el.clientWidth + 1"),
                        $"{density}; full canvas={fullCanvas}; " + await list.EvaluateAsync<string>("""
                            el => JSON.stringify({
                                clientWidth: el.clientWidth, scrollWidth: el.scrollWidth,
                                overflowing: [...el.querySelectorAll('*')].filter(child =>
                                    child.getBoundingClientRect().right > el.getBoundingClientRect().right + 1)
                                    .map(child => ({ className: child.className, width: child.getBoundingClientRect().width }))
                            })
                            """));
                }
            }
        });
    }

    [Theory]
    [InlineData(769, true)]
    [InlineData(1440, false)]
    [InlineData(2000, false)]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task FullCanvas_RowWhitespaceNavigatesOnceAndPreservesNestedActions(int width, bool reducedMotion)
    {
        await RunTestAsync(async page =>
        {
            await page.SetViewportSizeAsync(width, 900);
            await page.EmulateMediaAsync(new() { ReducedMotion = reducedMotion ? ReducedMotion.Reduce : ReducedMotion.NoPreference });
            await page.GotoAsync("/resources/cache?pane=tags");
            if (width <= 768)
            {
                await page.Locator(".resource-picker").ClickAsync();
            }
            await ResourcePaneNavigation.ToggleResourceListAsync(page);
            await page.WaitForFunctionAsync("""
                () => Math.abs(document.querySelector('.resource-pane').getBoundingClientRect().width -
                    document.querySelector('.resources-layout').getBoundingClientRect().width) < 1
                """);
            var row = page.Locator(".resource-row[data-resource-name='api']").First;
            var original = page.Url;
            await row.Locator(".resource-row-name").ClickAsync(new() { Button = MouseButton.Right });
            await Assertions.Expect(page.Locator("#resource-pane-context-menu-header")).ToBeVisibleAsync();
            await page.Keyboard.PressAsync("Escape");
            await Assertions.Expect(page.Locator("#resource-pane-context-menu-header")).ToBeHiddenAsync();
            Assert.Equal(original, page.Url);
            await Assertions.Expect(page.Locator(".resources-layout.pane-full-canvas")).ToBeVisibleAsync();
            await row.Locator(".resource-row-actions .aspire-menu-button").Last.ClickAsync();
            var menuItem = page.GetByRole(AriaRole.Menuitem, new() { Name = "inspect", Exact = true });
            await Assertions.Expect(menuItem).ToBeVisibleAsync();
            await page.Keyboard.PressAsync("Escape");
            await Assertions.Expect(menuItem).ToBeHiddenAsync();
            Assert.Equal(original, page.Url);

            await page.EvaluateAsync("""
                () => {
                    window.rowLinkActivations = 0;
                    document.addEventListener('click', event => {
                        if (event.target.closest('.resource-row[data-resource-name="api"] > .resource-row-link')) {
                            window.rowLinkActivations++;
                        }
                    });
                }
                """);
            var target = await row.Locator(".resource-row-link").GetAttributeAsync("href");
            Assert.NotNull(target);
            var box = await row.BoundingBoxAsync();
            Assert.NotNull(box);
            await row.ClickAsync(new() { Position = new() { X = box.Width - 4, Y = box.Height / 2 } });
            await Assertions.Expect(page).ToHaveURLAsync(new Regex($"{Regex.Escape(target)}$"));
            await Assertions.Expect(row.Locator(".resource-row-link")).ToHaveAttributeAsync("aria-current", "page");
            await Assertions.Expect(page.Locator("h1.resource-title")).ToHaveTextAsync("api");
            await Assertions.Expect(page.Locator(".resources-layout.pane-full-canvas")).ToHaveCountAsync(0);
            Assert.Equal(1, await page.EvaluateAsync<int>("() => window.rowLinkActivations"));

            if (width <= 768)
            {
                await page.Locator(".resource-picker").ClickAsync();
            }
            await ResourcePaneNavigation.ToggleResourceListAsync(page);
            await Assertions.Expect(page.Locator(".resources-layout.pane-full-canvas")).ToBeVisibleAsync();
            await page.WaitForFunctionAsync("""
                () => Math.abs(document.querySelector('.resource-pane').getBoundingClientRect().width -
                    document.querySelector('.resources-layout').getBoundingClientRect().width) < 1
                """);
            var next = page.Locator(".resource-row[data-resource-name='frontend'] .resource-row-link");
            var nextTarget = await next.GetAttributeAsync("href");
            Assert.NotNull(nextTarget);
            await next.FocusAsync();
            await Assertions.Expect(next).ToBeFocusedAsync();
            await next.PressAsync("Space");
            await Assertions.Expect(page).ToHaveURLAsync(new Regex($"{Regex.Escape(nextTarget)}$"));
            await Assertions.Expect(page.Locator(".resources-layout.pane-full-canvas")).ToHaveCountAsync(0);
        });
    }

    [Theory]
    [InlineData(420)]
    [InlineData(540)]
    [InlineData(560)]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task Sidebar_PrioritizesNameAndOneIndependentPrimaryEndpoint(int paneWidth)
    {
        await RunTestAsync(async page =>
        {
            await page.SetViewportSizeAsync(1440, 900);
            await page.EmulateMediaAsync(new() { ColorScheme = ColorScheme.Light });
            await page.GotoAsync("/resources/cache?pane=tags");
            await page.GetByRole(AriaRole.Button, new() { Name = "Settings", Exact = true }).ClickAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "Compact", Exact = true }).ClickAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "Close", Exact = true }).Last.ClickAsync();
            var pane = page.Locator(".resource-pane");
            var separator = page.GetByRole(AriaRole.Separator, new() { Name = Resources.Layout.ResourcePaneResize });
            // The settings drawer stays over the page while it animates closed, and would take the drag's pointer
            // down. A drag is also handled only once the separator's script is registered.
            await Assertions.Expect(page.GetByRole(AriaRole.Dialog)).ToHaveCountAsync(0);
            await Assertions.Expect(separator).ToHaveAttributeAsync("aria-disabled", "false");
            var handle = await separator.BoundingBoxAsync();
            var initialPane = await pane.BoundingBoxAsync();
            Assert.NotNull(handle);
            Assert.NotNull(initialPane);
            await page.Mouse.MoveAsync(handle.X + handle.Width / 2, handle.Y + handle.Height / 2);
            await page.Mouse.DownAsync();
            await page.Mouse.MoveAsync(handle.X + handle.Width / 2 + paneWidth - initialPane.Width, handle.Y + handle.Height / 2);
            await page.Mouse.UpAsync();
            await Assertions.Expect(separator).ToHaveAttributeAsync("aria-valuenow", paneWidth.ToString(CultureInfo.InvariantCulture));

            var row = pane.Locator("[data-resource-name='api']").First;
            var endpoint = row.Locator(".resource-row-primary-endpoint");
            // A compact row is one line: the name, then the type and state columns. The state is a dot that names
            // itself in its tooltip. At rest the actions are hidden, so the name gets the action rail's room.
            await page.Mouse.MoveAsync(1, 1);
            await Assertions.Expect(row.Locator(".resource-row-meta .resource-row-type")).ToBeVisibleAsync();
            await Assertions.Expect(row.Locator(".resource-row-sub")).ToHaveCountAsync(0);
            await Assertions.Expect(row.Locator(".resource-row-endpoints")).ToHaveCountAsync(0);
            await Assertions.Expect(row.Locator(".resource-row-meta-state")).ToHaveAttributeAsync("data-tooltip", "Running (Unhealthy)");
            await Assertions.Expect(row.Locator(".resource-row-link")).ToHaveAttributeAsync("aria-description", new System.Text.RegularExpressions.Regex("Unhealthy"));
            await Assertions.Expect(row.Locator(".resource-row-link .state-dot")).ToHaveCountAsync(1);
            await Assertions.Expect(endpoint).Not.ToBeVisibleAsync();
            Assert.True(await row.Locator(".resource-row-text").EvaluateAsync<double>("el => el.clientWidth") >= 160);
            // Hover and focus swap the type and state columns for the actions, which keeps the name's room.
            await row.HoverAsync();
            await row.Locator(".resource-row-link").FocusAsync();
            await Assertions.Expect(row.Locator(".resource-row-meta")).Not.ToBeVisibleAsync();
            Assert.True(await row.Locator(".resource-row-text").EvaluateAsync<double>("el => el.clientWidth") >= 160);

            await Assertions.Expect(endpoint).ToBeVisibleAsync();
            await Assertions.Expect(endpoint).ToHaveAttributeAsync("href", MetadataResourcesFixture.Endpoint);
            await Assertions.Expect(endpoint).ToHaveAttributeAsync("target", "_blank");
            await Assertions.Expect(endpoint).ToHaveAttributeAsync("rel", "noreferrer noopener");
            Assert.Equal(1, await row.Locator(".resource-row-actions a:visible").CountAsync());
            await Assertions.Expect(pane.Locator("[data-resource-name='service-key'] .resource-row-primary-endpoint")).ToHaveCountAsync(0);
            var before = page.Url;
            await page.Context.RouteAsync("https://example.test/**", route => route.FulfillAsync(new() { Body = "Local endpoint navigation proof." }));
            await endpoint.FocusAsync();
            var popup = await page.RunAndWaitForPopupAsync(() => page.Keyboard.PressAsync("Enter"));
            await Assertions.Expect(popup).ToHaveURLAsync(MetadataResourcesFixture.Endpoint);
            await popup.CloseAsync();
            Assert.Equal(before, page.Url);
            Assert.True(await pane.Locator(".resource-pane-list").EvaluateAsync<bool>("el => el.scrollWidth <= el.clientWidth + 1"));
            Assert.True(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= document.documentElement.clientWidth + 1"));
        });
    }

    [Theory]
    [InlineData(420)]
    [InlineData(560)]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task CompactSidebar_ErrorBadgeKeepsItsPlaceWhileActionsShow(int paneWidth)
    {
        // The fixture's persistent "cache" container widens every row's compact type column. That is when the badge
        // used to move: at rest it sat before the wide columns, and while the actions showed it went to the end of the
        // narrower name track, out from under a pointer that was on it.
        await AddErrorLogsAsync("api", "cache");

        try
        {
            await RunTestAsync(async page =>
            {
                await page.SetViewportSizeAsync(1440, 900);
                await page.EmulateMediaAsync(new() { ColorScheme = ColorScheme.Light });
                await page.GotoAsync("/resources/cache?pane=tags");
                await UseCompactSidebarAsync(page, paneWidth);
                var pane = page.Locator(".resource-pane");

                // Per row: the badge's box; whether it is the topmost element at its center; the hidden spacer's box,
                // which stops the name before the badge; the end of the name's text; whether the badge overlaps a
                // shown column or action; and whether any action is shown.
                const string measure = """
                    row => {
                        const box = el => el.getBoundingClientRect();
                        const shown = el => box(el).width > 0 && box(el).height > 0 && getComputedStyle(el).visibility !== 'hidden';
                        const badge = row.querySelector(':scope > .resource-row-error-link');
                        const b = box(badge);
                        const top = document.elementFromPoint(b.left + b.width / 2, b.top + b.height / 2);
                        const spacer = box(row.querySelector('.error-badge-spacer'));
                        const others = [...row.querySelectorAll('.resource-row-meta > *, .resource-row-actions .resource-row-action, .resource-row-actions .resource-row-toggle')]
                            .filter(shown);
                        const overlaps = others.some(el => { const o = box(el); return o.left < b.right - 0.5 && o.right > b.left + 0.5; });
                        return [b.left, b.top, b.right, b.bottom, top?.closest('.resource-row-error-link') === badge ? 1 : 0,
                            spacer.left, spacer.right, box(row.querySelector('.resource-row-text')).right, overlaps ? 1 : 0,
                            [...row.querySelectorAll('.resource-row-actions .resource-row-action')].some(shown) ? 1 : 0];
                    }
                    """;

                // With the persistent type in the list, the wide columns decide the badge's place. Filtered down to
                // "api", the columns are 64px and the actions decide it instead.
                await AssertRowAsync("api", "persistent types", typeWidth: 128);
                await AssertRowAsync("cache", "persistent types", typeWidth: 128);
                await pane.GetByRole(AriaRole.Searchbox).FillAsync("api");
                await Assertions.Expect(pane.Locator("[data-resource-name='cache']")).ToHaveCountAsync(0);
                await AssertRowAsync("api", "no persistent types", typeWidth: 64);

                async Task AssertRowAsync(string name, string stage, double typeWidth)
                {
                    var row = pane.Locator($"[data-resource-name='{name}']").First;
                    var badge = row.Locator(":scope > .resource-row-error-link");
                    var context = $"{name} ({stage})";
                    await row.ScrollIntoViewIfNeededAsync();
                    await page.Mouse.MoveAsync(1, 1);
                    await Assertions.Expect(badge).ToHaveTextAsync("1");
                    var type = await row.Locator(".resource-row-meta .resource-row-type").BoundingBoxAsync();
                    Assert.NotNull(type);
                    Assert.Equal(typeWidth, type.Width, 0.5);
                    var rest = await row.EvaluateAsync<double[]>(measure);
                    AssertBadge(rest, rest, context, "rest", actionsShown: false);

                    // Pointing at the row or at the badge itself, focusing the row, and opening its menu all show the
                    // actions in place of the columns. None of them may move or cover the badge.
                    await row.Locator(".resource-row-name").HoverAsync();
                    AssertBadge(rest, await row.EvaluateAsync<double[]>(measure), context, "row hover", actionsShown: true);

                    await page.Mouse.MoveAsync(1, 1);
                    await page.Mouse.MoveAsync((float)((rest[0] + rest[2]) / 2), (float)((rest[1] + rest[3]) / 2));
                    AssertBadge(rest, await row.EvaluateAsync<double[]>(measure), context, "badge hover", actionsShown: true);

                    await page.Mouse.MoveAsync(1, 1);
                    await row.Locator(".resource-row-link").FocusAsync();
                    AssertBadge(rest, await row.EvaluateAsync<double[]>(measure), context, "focus", actionsShown: true);

                    await row.HoverAsync();
                    await row.Locator(".resource-row-actions .aspire-menu-button").Last.ClickAsync();
                    await page.Mouse.MoveAsync(1, 1);
                    AssertBadge(rest, await row.EvaluateAsync<double[]>(measure), context, "menu open", actionsShown: true);
                    await page.Keyboard.PressAsync("Escape");
                }

                static void AssertBadge(double[] rest, double[] current, string row, string state, bool actionsShown)
                {
                    var context = $"{row}, {state}";
                    Assert.True(Math.Abs(current[0] - rest[0]) < 0.5 && Math.Abs(current[1] - rest[1]) < 0.5,
                        $"{context}: the badge moved from ({rest[0]}, {rest[1]}) to ({current[0]}, {current[1]}).");
                    Assert.True(current[4] == 1, $"{context}: something covers the badge.");
                    Assert.True(Math.Abs(current[5] - current[0]) < 0.5 && Math.Abs(current[6] - current[2]) < 0.5,
                        $"{context}: the spacer ({current[5]}-{current[6]}) is not under the badge ({current[0]}-{current[2]}).");
                    Assert.True(current[7] <= current[0] + 0.5, $"{context}: the name ends at {current[7]}, under the badge at {current[0]}.");
                    Assert.True(current[8] == 0, $"{context}: the badge overlaps a column or an action.");
                    Assert.Equal(actionsShown ? 1d : 0d, current[9]);
                }
            });
        }
        finally
        {
            await ClearErrorLogsAsync();
        }
    }

    [Theory]
    [InlineData("Comfortable")]
    [InlineData("Spacious")]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task TwoLineRows_EndTheSecondLineWithOneEllipsisBeforeTheErrorBadge(string density)
    {
        await AddErrorLogsAsync("api");
        try
        {
            await RunTestAsync(async page =>
            {
                await page.SetViewportSizeAsync(1440, 900);
                await page.GotoAsync("/resources/cache?pane=tags");
                await page.GetByRole(AriaRole.Button, new() { Name = "Settings", Exact = true }).ClickAsync();
                await page.GetByRole(AriaRole.Button, new() { Name = density, Exact = true }).ClickAsync();
                await page.GetByRole(AriaRole.Button, new() { Name = "Close", Exact = true }).Last.ClickAsync();
                var row = page.Locator(".resource-pane [data-resource-name='api']").First;
                await Assertions.Expect(row.Locator(":scope > .resource-row-error-link")).ToHaveTextAsync("1");
                await page.Mouse.MoveAsync(1, 1);

                // At the default pane width, "Running (Unhealthy)" and the age don't both fit before the badge. The line
                // flows as text, so it's cut once, at its end, rather than each part getting an ellipsis or being cut
                // off; the state comes first, so it stays whole.
                await AssertSecondLineAsync(row, stateIsWhole: true);

                // The full-page list's second line, the state and type, ends the same way before the badge. Its identity
                // column is narrower than the sidebar, so at the spacious density the state is cut too.
                await ResourcePaneNavigation.ToggleResourceListAsync(page);
                await Assertions.Expect(page.Locator(".pane-full-canvas")).ToBeVisibleAsync();
                await page.Mouse.MoveAsync(1, 1);
                await AssertSecondLineAsync(row, stateIsWhole: density == "Comfortable");
            });

            static async Task AssertSecondLineAsync(ILocator row, bool stateIsWhole)
            {
                var line = await row.EvaluateAsync<double[]>("""
                    row => {
                        const sub = row.querySelector('.resource-row-sub');
                        const state = sub.querySelector('.state-dot + span');
                        const badge = row.querySelector(':scope > .resource-row-error-link').getBoundingClientRect();
                        const box = sub.getBoundingClientRect();
                        const style = getComputedStyle(sub);
                        return [style.display === 'block' ? 1 : 0, style.textOverflow === 'ellipsis' ? 1 : 0,
                            sub.scrollWidth > sub.clientWidth ? 1 : 0, box.right, badge.left, state.getBoundingClientRect().right];
                    }
                    """);
                Assert.Equal([1d, 1d, 1d], line[..3]);
                Assert.True(line[3] <= line[4] + 0.5, $"The second line ends at {line[3]}, under the badge at {line[4]}.");
                if (stateIsWhole)
                {
                    Assert.True(line[5] <= line[3] + 0.5, $"The state ends at {line[5]}, past the end of the line at {line[3]}.");
                }
            }
        }
        finally
        {
            await ClearErrorLogsAsync();
        }
    }

    // Unviewed error logs give the resources' rows an error badge.
    private async Task AddErrorLogsAsync(params string[] resourceNames)
    {
        await fixture.DashboardApp.Services.GetRequiredService<ITelemetryRepositoryWriter>().AddLogsAsync(new AddContext(), new RepeatedField<ResourceLogs>
        {
            resourceNames.Select(name =>
            {
                var resource = CreateResource(name);
                resource.Attributes.Remove(resource.Attributes.Single(attribute => attribute.Key == OtlpResource.SERVICE_INSTANCE_ID));
                return new ResourceLogs
                {
                    Resource = resource,
                    ScopeLogs = { new ScopeLogs { Scope = CreateScope(), LogRecords = { CreateLogRecord(message: $"{name} failed", severity: SeverityNumber.Error) } } }
                };
            })
        });
    }

    // The fixture is shared by the class, so later tests must not see the badges.
    private async Task ClearErrorLogsAsync()
    {
        var services = fixture.DashboardApp.Services;
        await services.GetRequiredService<ITelemetryRepositoryWriter>().ClearStructuredLogsAsync();
        services.GetRequiredService<ITelemetryRepository>().MarkViewedErrorLogs(null);
    }

    [Theory]
    [InlineData(420)]
    [InlineData(560)]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task CompactSidebar_ExpandToggleKeepsItsPlaceAndTabOrderWhileActionsShow(int paneWidth)
    {
        await RunTestAsync(async page =>
        {
            fixture.StartDynamicSession();
            await page.SetViewportSizeAsync(1440, 900);
            await page.EmulateMediaAsync(new() { ColorScheme = ColorScheme.Light });
            await page.GotoAsync("/resources/cache");
            await page.Locator("#resourcePaneGroupingButton").ClickAsync();
            await page.GetByRole(AriaRole.Menuitemradio, new() { Name = "None", Exact = true }).ClickAsync();
            await UseCompactSidebarAsync(page, paneWidth);
            // A child makes "api" a parent too. With an endpoint and two highlighted commands, it needs the rail's
            // tightest layout; "cache" shows only its menu beside the toggle.
            await fixture.PublishAsync([new(ResourceViewModelChangeType.Upsert, MetadataResourcesFixture.CreateDynamicChild(0))]);
            var pane = page.Locator(".resource-pane");
            await Assertions.Expect(pane.Locator("[data-resource-name='rebuilder-0']")).ToBeVisibleAsync();

            // Per row: the toggle's position; whether it is the topmost element at its center; whether the shown
            // controls, in DOM (tab) order, run left to right without overlapping; whether the toggle is the last of
            // them; how many are shown; and whether the name ends before the first of them.
            const string measure = """
                row => {
                    const box = el => el.getBoundingClientRect();
                    const toggle = row.querySelector('.resource-row-actions > .resource-row-toggle');
                    const t = box(toggle);
                    const top = document.elementFromPoint(t.left + t.width / 2, t.top + t.height / 2);
                    const controls = [...row.querySelectorAll('.resource-row-actions .resource-row-action, .resource-row-actions > .resource-row-toggle')]
                        .filter(el => box(el).width > 0 && box(el).height > 0);
                    const ordered = controls.every((el, i) => i === 0 || box(controls[i - 1]).right <= box(el).left + 0.5);
                    return [t.left, t.top, top?.closest('.resource-row-toggle') === toggle ? 1 : 0, ordered ? 1 : 0,
                        controls.at(-1) === toggle ? 1 : 0, controls.length,
                        box(row.querySelector('.resource-row-text')).right <= box(controls[0]).left + 0.5 ? 1 : 0];
                }
                """;

            foreach (var (name, shownControls) in new[] { ("cache", 2d), ("api", 5d) })
            {
                var row = pane.Locator($"[data-resource-name='{name}']").First;
                await row.ScrollIntoViewIfNeededAsync();
                await page.Mouse.MoveAsync(1, 1);
                await Assertions.Expect(row.Locator(".resource-row-actions > .resource-row-toggle")).ToBeVisibleAsync();
                var rest = await row.EvaluateAsync<double[]>(measure);
                AssertToggle(rest, rest, name, "rest", 1, checkTopmost: true);

                await row.Locator(".resource-row-name").HoverAsync();
                AssertToggle(rest, await row.EvaluateAsync<double[]>(measure), name, "row hover", shownControls, checkTopmost: true);

                await page.Mouse.MoveAsync(1, 1);
                await row.Locator(".resource-row-link").FocusAsync();
                AssertToggle(rest, await row.EvaluateAsync<double[]>(measure), name, "focus", shownControls, checkTopmost: true);

                // The open menu's popup may lie over the toggle, so only its place and order are checked.
                await row.HoverAsync();
                await row.Locator(".resource-row-actions .aspire-menu-button").Last.ClickAsync();
                await page.Mouse.MoveAsync(1, 1);
                AssertToggle(rest, await row.EvaluateAsync<double[]>(measure), name, "menu open", shownControls, checkTopmost: false);
                await page.Keyboard.PressAsync("Escape");
            }

            static void AssertToggle(double[] rest, double[] current, string row, string state, double shownControls, bool checkTopmost)
            {
                var context = $"{row}, {state}";
                Assert.True(Math.Abs(current[0] - rest[0]) < 0.5 && Math.Abs(current[1] - rest[1]) < 0.5,
                    $"{context}: the toggle moved from ({rest[0]}, {rest[1]}) to ({current[0]}, {current[1]}).");
                Assert.True(!checkTopmost || current[2] == 1, $"{context}: something covers the toggle.");
                Assert.True(current[3] == 1, $"{context}: the shown controls overlap or don't run left to right in tab order.");
                Assert.True(current[4] == 1, $"{context}: the toggle isn't the last control.");
                Assert.Equal(shownControls, current[5]);
                Assert.True(current[6] == 1, $"{context}: the name runs into the actions.");
            }
        });
    }

    [Fact]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task CompactSidebar_RowTooltipNamesTypeAndStateWhileActionsShow()
    {
        await RunTestAsync(async page =>
        {
            await page.SetViewportSizeAsync(1440, 900);
            await page.GotoAsync("/resources/cache?pane=tags");
            await UseCompactSidebarAsync(page, 420);
            var pane = page.Locator(".resource-pane");
            var tooltip = page.Locator("aspire-tooltip-provider fluent-tooltip[aria-hidden='false']");

            // While the actions show in place of the type and state columns, the row's tooltip names them, for the
            // pointer and for the keyboard.
            foreach (var (name, expected) in new[] { ("api", "Project · Running (Unhealthy)"), ("cache", "Container (persistent) · Running") })
            {
                var link = pane.Locator($"[data-resource-name='{name}'] .resource-row-link").First;
                await page.Mouse.MoveAsync(1, 1);
                await link.Locator(".resource-row-name").HoverAsync();
                await Assertions.Expect(link.Locator(".resource-row-meta")).Not.ToBeVisibleAsync();
                await Assertions.Expect(link).ToHaveAttributeAsync("data-tooltip", expected);
                await Assertions.Expect(tooltip).ToHaveTextAsync(expected);
                await page.Mouse.MoveAsync(1, 1);
                await page.Keyboard.PressAsync("Tab");
                await link.FocusAsync();
                await Assertions.Expect(tooltip).ToHaveTextAsync(expected);
            }

            // The expanded list keeps its columns showing, so its rows stay quiet.
            await ResourcePaneNavigation.ToggleResourceListAsync(page);
            await Assertions.Expect(page.Locator(".pane-full-canvas")).ToBeVisibleAsync();
            var expandedLink = pane.Locator("[data-resource-name='api'] .resource-row-link").First;
            await page.Mouse.MoveAsync(1, 1);
            await expandedLink.Locator(".resource-row-name").HoverAsync();
            await Assertions.Expect(expandedLink.Locator(".resource-row-type")).ToBeVisibleAsync();
            Assert.Null(await expandedLink.GetAttributeAsync("data-tooltip"));
        });
    }

    // Switches the list to Compact and drags the resource pane's resizer to the given width.
    private static async Task UseCompactSidebarAsync(IPage page, int paneWidth)
    {
        await page.GetByRole(AriaRole.Button, new() { Name = "Settings", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Compact", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Close", Exact = true }).Last.ClickAsync();
        var pane = page.Locator(".resource-pane");
        var separator = page.GetByRole(AriaRole.Separator, new() { Name = Resources.Layout.ResourcePaneResize });
        // The settings drawer stays over the page while it animates closed, and would take the drag's pointer down.
        // A drag is also handled only once the separator's script is registered.
        await Assertions.Expect(page.GetByRole(AriaRole.Dialog)).ToHaveCountAsync(0);
        await Assertions.Expect(separator).ToHaveAttributeAsync("aria-disabled", "false");
        var handle = await separator.BoundingBoxAsync();
        var initialPane = await pane.BoundingBoxAsync();
        Assert.NotNull(handle);
        Assert.NotNull(initialPane);
        await page.Mouse.MoveAsync(handle.X + handle.Width / 2, handle.Y + handle.Height / 2);
        await page.Mouse.DownAsync();
        await page.Mouse.MoveAsync(handle.X + handle.Width / 2 + paneWidth - initialPane.Width, handle.Y + handle.Height / 2);
        await page.Mouse.UpAsync();
        await Assertions.Expect(separator).ToHaveAttributeAsync("aria-valuenow", paneWidth.ToString(CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData(769, false)]
    [InlineData(1440, false)]
    [InlineData(769, true)]
    [InlineData(1440, true)]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task CompactRow_RetainsPersistentTypeDuringHoverFocusAndMenu(int width, bool dark)
    {
        await RunTestAsync(async page =>
        {
            await page.SetViewportSizeAsync(1440, 900);
            await page.EmulateMediaAsync(new() { ColorScheme = dark ? ColorScheme.Dark : ColorScheme.Light });
            await page.GotoAsync("/resources/cache?pane=tags");
            await page.GetByRole(AriaRole.Button, new() { Name = "Settings", Exact = true }).ClickAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "Compact", Exact = true }).ClickAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "Close", Exact = true }).Last.ClickAsync();
            await page.SetViewportSizeAsync(width, 900);
            if (width <= 768)
            {
                await page.Locator(".resource-picker").ClickAsync();
            }
            await page.Locator(".detail-page-close").ClickAsync();

            var pane = page.Locator(".resource-pane");
            foreach (var name in new[] { "cache", "frontend", "service-key" })
            {
                var row = pane.Locator($"[data-resource-name='{name}']").First;
                var type = row.Locator(".resource-row-type");
                var link = row.Locator(".resource-row-link");
                await row.ScrollIntoViewIfNeededAsync();
                await page.Mouse.MoveAsync(1, 1);
                await Assertions.Expect(type).ToBeVisibleAsync();
                var typeText = await type.TextContentAsync();
                Assert.NotNull(typeText);
                var nameBox = await row.Locator(".resource-row-name").BoundingBoxAsync();
                Assert.NotNull(nameBox);
                await row.HoverAsync();
                await Assertions.Expect(type).ToBeVisibleAsync();
                await Assertions.Expect(type).ToHaveTextAsync(typeText);
                var hoveredNameBox = await row.Locator(".resource-row-name").BoundingBoxAsync();
                Assert.NotNull(hoveredNameBox);
                Assert.Equal(nameBox.Width, hoveredNameBox.Width);
                await link.FocusAsync();
                await page.Mouse.MoveAsync(1, 1);
                await Assertions.Expect(type).ToBeVisibleAsync();
                Assert.Equal("pointer", await link.EvaluateAsync<string>("el => getComputedStyle(el).cursor"));

                await row.Locator(".resource-row-actions .aspire-menu-button").Last.ClickAsync();
                await Assertions.Expect(type).ToBeVisibleAsync();
                await page.Keyboard.PressAsync("Escape");
                await Assertions.Expect(type).ToBeVisibleAsync();
            }
            await Assertions.Expect(pane.Locator("[data-resource-name='cache'] .resource-row-type").First)
                .ToHaveTextAsync("Container (persistent)");
            Assert.True(await pane.EvaluateAsync<bool>("el => el.scrollWidth <= el.clientWidth + 1"));
        });
    }

    [Theory]
    [InlineData(769, 1024)]
    [InlineData(1440, 900)]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task FullCanvas_BoundsLongFriendlyNameAndPreservesAddress(int width, int height)
    {
        await RunTestAsync(async page =>
        {
            await page.SetViewportSizeAsync(width, height);
            await page.EmulateMediaAsync(new() { ColorScheme = ColorScheme.Dark });
            await page.GotoAsync("/resources?pane=tags");
            if (width <= 768)
            {
                await page.Locator(".resource-picker").ClickAsync();
            }
            await page.Locator(".detail-page-close").ClickAsync();
            await Assertions.Expect(page.Locator(".resources-layout.pane-full-canvas")).ToBeVisibleAsync();
            var pane = page.Locator(".resource-pane");
            var row = pane.Locator("[data-resource-name='frontend']");
            await Assertions.Expect(row.Locator(".resource-row-endpoints a")).ToHaveTextAsync(MetadataResourcesFixture.LongFriendlyName);
            await Assertions.Expect(row.Locator(".resource-row-endpoints a")).ToHaveAttributeAsync("href", "https://example.test/frontend");
            await Assertions.Expect(row.Locator(".resource-row-primary-endpoint")).ToHaveAttributeAsync("href", "https://example.test/frontend");
            await Assertions.Expect(row.Locator(".resource-row-primary-endpoint")).ToHaveAttributeAsync("aria-label",
                $"{MetadataResourcesFixture.LongFriendlyName}: https://example.test/frontend");
            if (width >= 420)
            {
                await AssertBoundedEndpointAsync(pane);
                Assert.True(await pane.EvaluateAsync<bool>("el => el.scrollWidth <= el.clientWidth + 1"));
                var nameBox = await row.Locator(".resource-row-endpoints a").BoundingBoxAsync();
                var paneBox = await pane.BoundingBoxAsync();
                Assert.NotNull(paneBox);
                if (nameBox is not null)
                {
                    Assert.True(nameBox.X + nameBox.Width <= paneBox.X + paneBox.Width);
                }
            }
            else
            {
                await Assertions.Expect(row.Locator(".resource-row-endpoints")).Not.ToBeVisibleAsync();
                await Assertions.Expect(row.Locator(".resource-row-link")).ToBeVisibleAsync();
            }
            await page.ScreenshotAsync(new() { Path = Path.Combine(AppContext.BaseDirectory, $"resource-pane-label-canvas-{width}.png") });
        });
    }

    [Fact]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task FullCanvasUrls_ListTheOtherUrlsInAPopoverWithoutSelectingTheResource()
    {
        await RunTestAsync(async page =>
        {
            fixture.StartDynamicSession();
            await page.SetViewportSizeAsync(1440, 900);
            await page.GotoAsync("/resources?pane=tags");
            await ResourcePaneNavigation.ToggleResourceListAsync(page);
            await Assertions.Expect(page.Locator(".pane-full-canvas")).ToBeVisibleAsync();
            const string first = "https://example.test/a-long-address-that-fills-the-urls-column-on-its-own-and-more";
            const string second = "https://example.test/second";
            await fixture.PublishAsync([new(ResourceViewModelChangeType.Upsert, ModelTestHelpers.CreateResource("api",
                state: KnownResourceState.Running, resourceType: KnownResourceTypes.Project, urls:
                [
                    new("https", new Uri(first), false, false, UrlDisplayPropertiesViewModel.Empty),
                    new("http", new Uri(second), false, false, UrlDisplayPropertiesViewModel.Empty)
                ]))]);
            var pane = page.Locator(".resource-pane");
            var api = pane.Locator("[data-resource-name='api']").First;

            // Like the resources grid, the URLs that don't fit are behind a +N button, which lists them.
            var more = api.Locator(".resource-row-endpoints .url-button");
            await Assertions.Expect(more).ToHaveTextAsync("+1");
            await more.ClickAsync();
            var list = api.Locator(".url-overflow-popover");
            var listed = list.Locator("a");
            await Assertions.Expect(listed).ToBeVisibleAsync();
            Assert.Contains(await listed.GetAttributeAsync("href"), new[] { first, second });

            // Neither the button nor the list selects the resource, though they're in its row.
            await list.Locator("strong").ClickAsync();
            await Assertions.Expect(listed).ToBeVisibleAsync();
            await Assertions.Expect(pane.Locator(".resource-row.selected[data-resource-name]")).ToHaveCountAsync(0);
            await Assertions.Expect(page.Locator(".pane-full-canvas")).ToBeVisibleAsync();

            // Escape closes the list.
            await page.Keyboard.PressAsync("Escape");
            await Assertions.Expect(listed).Not.ToBeVisibleAsync();
            await Assertions.Expect(page.Locator(".pane-full-canvas")).ToBeVisibleAsync();
        });
    }

    [Fact]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task PaneWidth_DisclosesMetadataWithoutChangingSelectionAndKeepsEndpointBehavior()
    {
        await RunTestAsync(async page =>
        {
            await page.SetViewportSizeAsync(1440, 900);
            await page.EmulateMediaAsync(new() { ColorScheme = ColorScheme.Dark });
            await page.GotoAsync("/resources?pane=tags");
            var pane = page.Locator(".resource-pane");
            var api = pane.Locator("[data-resource-name='api']").First;
            var metadata = api.Locator(".resource-row-wide-metadata");
            await Assertions.Expect(api).ToBeVisibleAsync();
            var layoutActions = page.GetByRole(AriaRole.Group, new() { Name = "Layout", Exact = true });
            await Assertions.Expect(layoutActions).ToBeVisibleAsync();
            var actionNames = await layoutActions.GetByRole(AriaRole.Button).AllTextContentsAsync();
            await Assertions.Expect(metadata.Locator(".resource-row-health")).Not.ToBeVisibleAsync();
            var url = page.Url;
            var rowOrder = await pane.Locator("[data-resource-name]").EvaluateAllAsync<string[]>("rows => rows.map(row => row.dataset.resourceName)");
            var separator = page.GetByRole(AriaRole.Separator, new() { Name = Resources.Layout.ResourcePaneResize });
            await DragPaneToAsync(460);
            await Assertions.Expect(metadata).ToHaveCountAsync(0);
            await Assertions.Expect(api.Locator(".resource-row-link .state-dot + span")).ToHaveTextAsync("Running (Unhealthy)");
            await Assertions.Expect(api.Locator(".resource-row-link")).ToHaveAttributeAsync("aria-description", new System.Text.RegularExpressions.Regex("Unhealthy"));
            await page.ScreenshotAsync(new() { Path = Path.Combine(AppContext.BaseDirectory, "resource-pane-label-resize-460.png") });
            await DragPaneToAsync(560);
            await Assertions.Expect(api.Locator("time")).ToBeVisibleAsync();
            await page.ScreenshotAsync(new() { Path = Path.Combine(AppContext.BaseDirectory, "resource-pane-label-resize-560.png") });
            await Assertions.Expect(api.Locator("time")).ToHaveAttributeAsync("datetime", MetadataResourcesFixture.StartTime.ToString("O", CultureInfo.InvariantCulture));
            await Assertions.Expect(pane.Locator("[data-resource-name='cache'] time")).ToHaveCountAsync(0);
            await Assertions.Expect(pane.Locator("[data-resource-name='cache'] .url-container")).ToHaveCountAsync(0);
            Assert.Equal(url, page.Url);
            Assert.Equal(rowOrder, await pane.Locator("[data-resource-name]").EvaluateAllAsync<string[]>("rows => rows.map(row => row.dataset.resourceName)"));

            await ResourcePaneNavigation.ToggleResourceListAsync(page);
            url = page.Url;
            await Assertions.Expect(metadata.Locator(".resource-row-health-status")).ToHaveCountAsync(0);
            await Assertions.Expect(metadata.Locator("time")).ToBeVisibleAsync();
            await AssertBoundedEndpointAsync(pane);
            var openEndpoint = api.Locator(".resource-row-primary-endpoint");
            await Assertions.Expect(openEndpoint).ToHaveAttributeAsync("href", MetadataResourcesFixture.Endpoint);
            await Assertions.Expect(openEndpoint).ToHaveAttributeAsync("target", "_blank");
            await Assertions.Expect(openEndpoint).ToHaveAttributeAsync("rel", "noreferrer noopener");
            await page.Context.RouteAsync("https://example.test/**", route => route.FulfillAsync(new() { Body = "Synthetic endpoint; no external request." }));
            // The URLs column's link and the open action both open the URL without selecting the resource.
            foreach (var opener in new[] { api.Locator(".resource-row-endpoints a"), openEndpoint })
            {
                var popup = await page.RunAndWaitForPopupAsync(() => opener.ClickAsync());
                try
                {
                    await Assertions.Expect(popup).ToHaveURLAsync(MetadataResourcesFixture.Endpoint);
                    Assert.Equal(url, page.Url);
                    await Assertions.Expect(pane.Locator(".resource-select-all-toggle.on")).ToHaveCountAsync(1);
                    await Assertions.Expect(pane.Locator(".resource-row.selected[data-resource-name]")).ToHaveCountAsync(0);
                    await Assertions.Expect(pane.Locator(".resource-row[data-resource-name]")).ToHaveCountAsync(8);
                }
                finally
                {
                    await popup.CloseAsync();
                }
            }
            // A tcp address can't be opened, so it isn't a link.
            var queueUrls = pane.Locator("[data-resource-name='queue'] .url-container");
            await Assertions.Expect(queueUrls.Locator("span")).ToBeVisibleAsync();
            await Assertions.Expect(queueUrls.Locator("a")).ToHaveCountAsync(0);
            await page.Locator(".resource-select-all-toggle").ClickAsync();
            await page.WaitForFunctionAsync("() => Math.abs(document.querySelector('.resource-pane').getBoundingClientRect().width - 560) < 0.05");

            var target = api.Locator(".resource-row-link");
            var targetBox = await target.BoundingBoxAsync();
            Assert.NotNull(targetBox);
            await page.Locator(".detail-page-close").ClickAsync();
            await Assertions.Expect(page.Locator(".resources-layout.pane-full-canvas")).ToBeVisibleAsync();
            // The full-page list is the overview's List layout and shows the same toggle, with List pressed.
            await Assertions.Expect(layoutActions).ToBeVisibleAsync();
            await Assertions.Expect(layoutActions.GetByRole(AriaRole.Button, new() { Name = Resources.Resources.ResourceOverviewLayoutList, Exact = true }))
                .ToHaveAttributeAsync("aria-pressed", "true");
            await AssertRowGapsAsync();
            await pane.Locator(".resource-select-all-toggle").ClickAsync();
            await Assertions.Expect(page.Locator(".resources-layout.pane-full-canvas")).ToHaveCountAsync(0);
            await page.WaitForFunctionAsync("() => Math.abs(document.querySelector('.resource-pane').getBoundingClientRect().width - 560) < 0.05");
            await Assertions.Expect(layoutActions).ToBeVisibleAsync();
            Assert.Equal(actionNames, await layoutActions.GetByRole(AriaRole.Button).AllTextContentsAsync());
            var restoredTargetBox = await target.BoundingBoxAsync();
            Assert.NotNull(restoredTargetBox);
            Assert.Equal(targetBox.Height, restoredTargetBox.Height);
            Assert.Equal(targetBox.Width, restoredTargetBox.Width);
            await pane.GetByRole(AriaRole.Group, new() { Name = "Backend", Exact = true }).Locator(".resource-tag-link").ClickAsync();
            await Assertions.Expect(pane.Locator("[data-resource-name='frontend'].selected")).ToHaveCountAsync(0);
            await Assertions.Expect(pane.Locator("[data-resource-name='api'].selected")).ToHaveCountAsync(2);
            await Assertions.Expect(pane.Locator("[data-resource-name='cache'].selected")).ToHaveCountAsync(2);
            await AssertRowGapsAsync();
            var selectedTargetBox = await target.BoundingBoxAsync();
            Assert.NotNull(selectedTargetBox);
            Assert.Equal(targetBox.Height, selectedTargetBox.Height);
            Assert.Equal(targetBox.Width, selectedTargetBox.Width);
            await separator.PressAsync("Home");
            await Assertions.Expect(metadata.Locator(".resource-row-health")).Not.ToBeVisibleAsync();
            await Assertions.Expect(target).ToBeVisibleAsync();

            async Task DragPaneToAsync(int width)
            {
                var handle = await separator.BoundingBoxAsync();
                var paneBox = await pane.BoundingBoxAsync();
                Assert.NotNull(handle);
                Assert.NotNull(paneBox);
                await page.Mouse.MoveAsync(handle.X + handle.Width / 2, handle.Y + handle.Height / 2);
                await page.Mouse.DownAsync();
                await page.Mouse.MoveAsync(handle.X + handle.Width / 2 + width - paneBox.Width, handle.Y + handle.Height / 2, new() { Steps = 5 });
                await page.Mouse.UpAsync();
                await Assertions.Expect(separator).ToHaveAttributeAsync("aria-valuenow", width.ToString(CultureInfo.InvariantCulture));
            }

            async Task AssertRowGapsAsync()
            {
                var gaps = await pane.Locator(".resource-row").EvaluateAllAsync<double[]>(
                    "rows => rows.filter(row => row.nextElementSibling?.matches('.resource-row')).map(row => row.nextElementSibling.getBoundingClientRect().top - row.getBoundingClientRect().bottom)");
                Assert.NotEmpty(gaps);
                Assert.All(gaps, gap => Assert.InRange(gap, 0.95, 1.05));
            }
        });
    }

    // A long URL name ends with an ellipsis in the URLs column rather than widening the column or the row.
    private static async Task AssertBoundedEndpointAsync(ILocator pane)
    {
        var row = pane.Locator("[data-resource-name='frontend']");
        var link = row.Locator(".resource-row-endpoints a");
        await Assertions.Expect(link).ToHaveTextAsync(MetadataResourcesFixture.LongFriendlyName);
        // The column shows once the pane is wide enough, which can take a moment after the list fills the page.
        await Assertions.Expect(link).ToBeVisibleAsync();
        var bounds = await row.Locator(".resource-row-endpoints").EvaluateAsync<double[]>("""
            endpoints => {
                const link=endpoints.querySelector('a'),box=endpoints.getBoundingClientRect(),linkBox=link.getBoundingClientRect();
                return [link.scrollWidth > link.clientWidth ? 1 : 0, getComputedStyle(link).textOverflow === 'ellipsis' ? 1 : 0,
                    linkBox.right - box.right];
            }
            """);
        Assert.Equal([1d, 1d], bounds[..2]);
        Assert.True(bounds[2] <= .5, $"The link ends {bounds[2]}px past the URLs column.");
        var rowBox = await row.BoundingBoxAsync();
        var normalLabelRowBox = await pane.Locator("[data-resource-name='api']").First.BoundingBoxAsync();
        Assert.NotNull(rowBox);
        Assert.NotNull(normalLabelRowBox);
        Assert.Equal(normalLabelRowBox.Height, rowBox.Height);
    }

    public sealed class MetadataResourcesFixture : DashboardServerFixture
    {
        private ConcurrentBag<Channel<IReadOnlyList<ResourceViewModelChange>>> _updates = [];

        public void StartDynamicSession()
        {
            Volatile.Write(ref _updates, []);
        }

        public Task PublishAsync(IReadOnlyList<ResourceViewModelChange> changes) =>
            Task.WhenAll(_updates.Select(channel => channel.Writer.WriteAsync(changes).AsTask()));

        public Task<DateTime> SeedActivityAsync() => SeedActivityAsync([("api", 20), ("frontend", 20), ("queue", 2)]);

        public async Task<DateTime> SeedActivityAsync(IReadOnlyList<(string Name, int Count)> resources)
        {
            var now = DateTime.UtcNow.AddSeconds(-5);
            var writer = DashboardApp.Services.GetRequiredService<ITelemetryRepositoryWriter>();
            await writer.ClearTracesAsync();
            await writer.AddTracesAsync(new AddContext(), new RepeatedField<ResourceSpans>
            {
                resources.Select(item =>
                {
                    var resource = CreateResource(item.Name);
                    resource.Attributes.RemoveAt(1);
                    return new ResourceSpans
                    {
                        Resource = resource,
                        ScopeSpans =
                        {
                            new ScopeSpans
                            {
                                Scope = CreateScope(),
                                Spans =
                                {
                                    Enumerable.Range(1, item.Count).Select(i =>
                                        CreateSpan($"{item.Name}-{i}", $"{item.Name}-{i}", now, now.AddMilliseconds(i),
                                            kind: Span.Types.SpanKind.Server,
                                            status: new Status { Code = item.Name == "api" && i == 1
                                                ? Status.Types.StatusCode.Error : Status.Types.StatusCode.Ok }))
                                }
                            }
                        }
                    };
                })
            });
            return now;
        }

        protected override void ConfigureServices(IServiceCollection services)
        {
            var client = new MockDashboardClient(CreateResources(), () =>
            {
                // Layout and detail subscribers each need the whole update, not competing reads.
                var channel = Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>();
                _updates.Add(channel);
                return channel;
            });
            services.AddSingleton<IDashboardClient>(client);
            services.AddSingleton<IRepositoryFactory>(services => new MockRepositoryFactory(services, client));
        }

        public static ResourceViewModel CreateDynamicChild(int index)
        {
            return ModelTestHelpers.CreateResource($"rebuilder-{index}", resourceType: KnownResourceTypes.Executable,
                state: KnownResourceState.NotStarted, properties: new()
                {
                    [KnownProperties.Resource.ParentName] =
                    new(KnownProperties.Resource.ParentName, Value.ForString(index % 2 == 0 ? "api" : "cache"), false, null, 0, null, false)
                });
        }

        public static ResourceViewModel CreatePerformanceResource(int index) =>
            Create($"perf-{index}", KnownResourceTypes.Project, KnownResourceState.Running, HealthStatus.Healthy,
                StartTime, [], ["Backend", "Shared"]);

        public const string Endpoint = "https://example.test/api/a-deliberately-long-endpoint-address-for-the-resource-pane?token=synthetic-secret&mode=preview";
        public const string TcpEndpoint = "tcp://example.test:5432?password=synthetic-secret";
        public static readonly string LongFriendlyName = "SyntheticFrontend" + new string('A', 150);
        public static readonly string LongImage = "registry.example.test/testshop/" + new string('i', 120) + ":latest";
        public static readonly string LongResourceName = "worker-" + new string('x', 160);
        public static readonly DateTime StartTime = new(2026, 11, 2, 18, 20, 30, DateTimeKind.Utc);

        public static IReadOnlyList<ResourceViewModel> CreateResources() =>
        [
            Create("api", KnownResourceTypes.Project, KnownResourceState.Running, HealthStatus.Unhealthy, StartTime,
                [new("https", new Uri(Endpoint), false, false, new("Friendly API", 0))], ["Backend", "Public"]),
            Create("frontend", KnownResourceTypes.Project, KnownResourceState.Running, HealthStatus.Healthy, StartTime,
                [new("https", new Uri("https://example.test/frontend"), false, false, new(LongFriendlyName, 0))], ["Public"]),
            Create("cache", KnownResourceTypes.Container, KnownResourceState.Running, HealthStatus.Healthy, null, [], ["Backend", "Infrastructure"]),
            Create("queue", KnownResourceTypes.Container, KnownResourceState.Running, HealthStatus.Degraded, null,
                [new("tcp", new Uri(TcpEndpoint), false, false, UrlDisplayPropertiesViewModel.Empty)], ["Infrastructure"]),
            Create("worker", KnownResourceTypes.Executable, KnownResourceState.Exited, null, StartTime, [], ["Backend"]),
            Create("service-key", KnownResourceTypes.Parameter, KnownResourceState.Running, null, null, [], ["Config"])
        ];

        private static ResourceViewModel Create(string name, string type, KnownResourceState state, HealthStatus? health,
            DateTime? startTime, ImmutableArray<UrlViewModel> urls, string[] tags)
        {
            var tagKey = KnownProperties.Resource.TraitPrefix + KnownResourceTraits.Tag;
            var properties = ImmutableDictionary<string, ResourcePropertyViewModel>.Empty.Add(tagKey,
                new(tagKey, Value.ForList([.. tags.Select(Value.ForString)]), false, null, 0, null, false));
            if (type != KnownResourceTypes.Parameter)
            {
                var key = type == KnownResourceTypes.Project ? KnownProperties.Project.Path :
                    type == KnownResourceTypes.Container ? KnownProperties.Container.Image : KnownProperties.Executable.Path;
                var value = type == KnownResourceTypes.Project ? Path.Combine("workspace", name == "api" ? "MyFrontend.csproj" : $"{name}.csproj") :
                    type == KnownResourceTypes.Container ? name == "cache" ? LongImage : "rabbitmq:4" : Path.Combine("tools", name);
                properties = properties.Add(key, new(key, Value.ForString(value), false, null, 0, null, false));
            }
            if (name == "cache")
            {
                properties = properties.Add(KnownProperties.Container.Lifetime,
                    new(KnownProperties.Container.Lifetime, Value.ForString("Persistent"), false, null, 0, null, false));
            }
            if (name is "queue" or "worker" or "frontend")
            {
                properties = properties.Add(KnownProperties.Resource.ParentName,
                    new(KnownProperties.Resource.ParentName, Value.ForString(name == "frontend" ? "queue" : "cache"), false, null, 0, null, false));
            }
            return new ResourceViewModel
            {
                Name = name,
                DisplayName = name == "worker" ? LongResourceName : name,
                ResourceType = type,
                Uid = $"synthetic-{name}",
                ReplicaIndex = 0,
                State = state.ToString(),
                KnownState = state,
                StateStyle = null,
                CreationTimeStamp = null,
                StartTimeStamp = startTime,
                StopTimeStamp = null,
                Environment = [],
                Urls = urls,
                Volumes = [],
                Relationships = name switch
                {
                    "api" => [new("cache", KnownRelationshipTypes.WaitFor), new("queue", KnownRelationshipTypes.WaitFor),
                        new(LongResourceName, KnownRelationshipTypes.WaitFor), new("queue", KnownRelationshipTypes.Reference)],
                    "queue" => [new("cache", KnownRelationshipTypes.WaitFor)],
                    "frontend" => [new("queue", KnownRelationshipTypes.Reference)],
                    _ => []
                },
                Properties = properties,
                Commands = name == "api" ?
                    [Command("inspect"), Command("diagnose")] :
                    name == "service-key" ? [Command(CommandViewModel.SetParameterCommand)] : [],
                HealthReports = health is { } status ? [new("Synthetic fixture check", status, null, null)] : []
            };
        }

        private static CommandViewModel Command(string name) =>
            new(name, CommandViewModelState.Enabled, name, "", confirmationMessage: "", argumentInputs: [],
                isHighlighted: true, iconName: "Search", iconVariant: Microsoft.FluentUI.AspNetCore.Components.IconVariant.Regular);
    }
}
