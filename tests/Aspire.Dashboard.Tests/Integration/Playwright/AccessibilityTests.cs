// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using System.Text.Json;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Resources;
using Aspire.Dashboard.Tests.Integration.Playwright.Infrastructure;
using Aspire.TestUtilities;
using Aspire.Tests.Shared.DashboardModel;
using Deque.AxeCore.Commons;
using Deque.AxeCore.Playwright;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Playwright;
using Xunit;

namespace Aspire.Dashboard.Tests.Integration.Playwright;

/// <summary>
/// Automated WCAG 2.0 / 2.1 (levels A and AA) accessibility checks for the dashboard's primary
/// pages, driven by the axe-core engine (via the MIT-licensed <c>Deque.AxeCore.Playwright</c>
/// wrapper) against a real dashboard server. Both the light and dark themes are scanned because
/// color-contrast outcomes differ between them, the primary page is additionally scanned across
/// mobile, tablet and desktop viewports, the key dialogs/flyout panels are opened and scanned (a
/// surface the resting-state page matrix never reaches), and code block syntax colors are checked
/// for AA contrast in both themes (a surface axe can't reach on its own).
/// </summary>
[RequiresFeature(TestFeature.Playwright)]
public sealed class AccessibilityTests : PlaywrightTestsBase<AccessibilityTests.AccessibilityDashboardServerFixture>
{
    // WCAG 2.0 and 2.1, levels A and AA - the conformance target the dashboard commits to. axe's
    // "best-practice" and experimental rule sets are intentionally excluded so the gate tracks an
    // actual accessibility standard rather than axe's opinionated extras.
    // See https://github.com/dequelabs/axe-core/blob/develop/doc/API.md#axe-core-tags.
    private static readonly string[] s_wcagTags = ["wcag2a", "wcag2aa", "wcag21a", "wcag21aa"];

    // axe impact levels that fail the build. Serious and critical issues are unambiguous barriers
    // for assistive-technology users. Moderate/minor findings are still reported in the failure
    // message (when a serious/critical issue trips the gate) but don't fail on their own, which
    // keeps the gate stable against third-party (Fluent UI) shadow-DOM churn we don't control.
    private static readonly HashSet<string> s_failingImpacts = new(StringComparer.OrdinalIgnoreCase) { "serious", "critical" };

    // Rule IDs intentionally excluded from the gate. Keep this empty unless a violation is a
    // confirmed false positive or lives entirely in third-party (Fluent UI) shadow DOM that
    // cannot be fixed from this repo; document each entry with a tracking issue link when added.
    private static readonly HashSet<string> s_allowedRuleIds = new(StringComparer.OrdinalIgnoreCase);

    // WCAG 2.0 SC 1.4.3 (AA) minimum contrast ratio for normal-size text.
    private const double WcagAaContrastMinimum = 4.5;

    // Mirrors ViewportInformation.MobileCutoffPixelWidth: at or below this width the dashboard renders
    // its mobile chrome (the page title is not teleported into the top bar). Kept as a local literal so
    // the test doesn't depend on dashboard internals.
    private const int MobileCutoffPixelWidth = 768;

    // The default desktop viewport used by the per-theme matrix and the code block contrast checks.
    private static readonly ViewportSize s_desktopViewport = new() { Width = 1280, Height = 900 };

    public AccessibilityTests(AccessibilityDashboardServerFixture dashboardServerFixture)
        : base(dashboardServerFixture)
    {
    }

    [Theory]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    [InlineData("/", "Light")]
    [InlineData("/", "Dark")]
    [InlineData("/consolelogs", "Light")]
    [InlineData("/consolelogs", "Dark")]
    [InlineData("/structuredlogs", "Light")]
    [InlineData("/structuredlogs", "Dark")]
    [InlineData("/traces", "Light")]
    [InlineData("/traces", "Dark")]
    [InlineData("/metrics", "Light")]
    [InlineData("/metrics", "Dark")]
    public Task DashboardPage_HasNoSeriousOrCriticalWcagViolations(string relativeUrl, string theme)
        => AssertNoBlockingWcagViolationsAsync(relativeUrl, theme, s_desktopViewport);

    // Responsive coverage: re-run the same WCAG gate on the primary page across mobile, tablet and
    // desktop widths so layout-sensitive barriers - reflow, focus order, and content that overlaps or
    // is clipped once the app switches to its mobile chrome (at/below MobileCutoffPixelWidth) - are
    // caught in addition to the per-theme desktop matrix above. Light theme only; color-contrast, the
    // theme-sensitive axis, is already exercised in both themes by the matrix above.
    [Theory]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    [InlineData("Mobile", 375, 812)]
    [InlineData("Tablet", 768, 1024)]
    [InlineData("Desktop", 1280, 900)]
    public Task DashboardHomePage_IsAccessibleAcrossViewports(string viewportName, int width, int height)
        => AssertNoBlockingWcagViolationsAsync("/", "Light", new ViewportSize { Width = width, Height = height }, viewportName);

    // Dialogs and flyout panels are only reachable behind a click, so the resting-state page matrix
    // above never audits them. The dashboard's dialog chrome - panel background, input wells, and the
    // primary/secondary action buttons that read very differently in dark theme - is exactly the kind
    // of surface where contrast regressions hide. Open each key surface and run the same
    // serious/critical WCAG gate against it in both themes (color-contrast is the theme-sensitive axis).
    [Theory]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    [InlineData("Settings", "Light")]
    [InlineData("Settings", "Dark")]
    [InlineData("Filter", "Light")]
    [InlineData("Filter", "Dark")]
    public Task DashboardDialog_HasNoSeriousOrCriticalWcagViolations(string surface, string theme)
    {
        var (startUrl, label, openSurfaceAsync) = s_dialogSurfaces[surface];
        return AssertNoBlockingWcagViolationsAsync(startUrl, theme, s_desktopViewport, openSurfaceAsync: openSurfaceAsync, surfaceLabel: label);
    }

    [Theory]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    [InlineData("Light", "fluent-text-field", "root")]
    [InlineData("Light", "fluent-search", "root")]
    [InlineData("Light", "fluent-number-field", "root")]
    [InlineData("Light", "fluent-text-area", "control")]
    [InlineData("Dark", "fluent-text-field", "root")]
    [InlineData("Dark", "fluent-search", "root")]
    [InlineData("Dark", "fluent-number-field", "root")]
    [InlineData("Dark", "fluent-text-area", "control")]
    public async Task FluentDelegatedInput_ShowsVisibleFocusIndicator(string theme, string controlName, string partName)
    {
        var baseUrl = DashboardServerFixture.DashboardApp.FrontendSingleEndPointAccessor().GetResolvedAddress();

        await using var context = await PlaywrightFixture.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            IgnoreHTTPSErrors = true,
            BaseURL = baseUrl,
            ViewportSize = s_desktopViewport
        });

        await context.AddCookiesAsync([new Cookie { Name = "currentTheme", Value = theme, Url = baseUrl }]);

        var page = await context.NewPageAsync();
        await page.GotoAsync("/").DefaultTimeout();
        await page.WaitForSelectorAsync("body:not(.before-upgrade)").DefaultTimeout();
        await page.WaitForSelectorAsync(
            $"html[data-theme='{theme.ToLowerInvariant()}']",
            new PageWaitForSelectorOptions { State = WaitForSelectorState.Attached }).DefaultTimeout();
        await Assertions.Expect(page.GetByText("frontend", new PageGetByTextOptions { Exact = true }).First).ToBeVisibleAsync();
        await WaitForComponentsAndFontsAsync(page);

        ILocator control;
        if (string.Equals(controlName, "fluent-search", StringComparison.Ordinal))
        {
            control = page.Locator("fluent-search[name='resources-search']");
        }
        else
        {
            await page.EvaluateAsync(
                """
                async controlName => {
                    await customElements.whenDefined(controlName);
                    const control = document.createElement(controlName);
                    control.id = 'delegated-focus-probe';
                    control.style.cssText = 'position:fixed;left:-99999px;top:0;';
                    document.body.appendChild(control);
                    await new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)));
                }
                """,
                controlName).DefaultTimeout();
            control = page.Locator("#delegated-focus-probe");
        }

        await AssertVisibleFocusIndicatorAsync(control, partName);
    }

    [Fact]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task DarkAccentButtonInteractionStates_MeetWcagAaContrast()
    {
        var baseUrl = DashboardServerFixture.DashboardApp.FrontendSingleEndPointAccessor().GetResolvedAddress();

        await using var context = await PlaywrightFixture.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            IgnoreHTTPSErrors = true,
            BaseURL = baseUrl,
            ViewportSize = s_desktopViewport
        });

        await context.AddCookiesAsync([new Cookie { Name = "currentTheme", Value = "Dark", Url = baseUrl }]);

        var page = await context.NewPageAsync();
        await page.GotoAsync("/").DefaultTimeout();
        await page.WaitForSelectorAsync("body:not(.before-upgrade)").DefaultTimeout();
        await page.WaitForSelectorAsync(
            "html[data-theme='dark']",
            new PageWaitForSelectorOptions { State = WaitForSelectorState.Attached }).DefaultTimeout();
        await WaitForComponentsAndFontsAsync(page);

        await page.EvaluateAsync("""
            async () => {
                await customElements.whenDefined('fluent-button');
                const button = document.createElement('fluent-button');
                button.id = 'accent-contrast-probe';
                button.setAttribute('appearance', 'accent');
                button.textContent = 'Primary action';
                button.style.cssText = 'position:fixed;left:16px;top:16px;z-index:10000;';
                document.body.appendChild(button);
                await new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)));
            }
            """).DefaultTimeout();

        var button = page.Locator("#accent-contrast-probe");
        var states = new List<(string Name, (int R, int G, int B) Foreground, (int R, int G, int B) Background)>();
        var rest = await ReadFluentControlColorsAsync(button);
        states.Add(("rest", rest.Foreground, rest.Background));

        await button.HoverAsync();
        var hover = await ReadFluentControlColorsAsync(button);
        states.Add(("hover", hover.Foreground, hover.Background));

        await page.Mouse.DownAsync();
        var active = await ReadFluentControlColorsAsync(button);
        states.Add(("active", active.Foreground, active.Background));
        await page.Mouse.UpAsync();

        var failures = states
            .Select(state => (state.Name, state.Foreground, state.Background, Ratio: ContrastRatio(state.Foreground, state.Background)))
            .Where(state => state.Ratio < WcagAaContrastMinimum)
            .Select(state =>
                $"  {state.Name}: {state.Ratio:F2}:1 " +
                $"(text rgb({state.Foreground.R},{state.Foreground.G},{state.Foreground.B}) on background rgb({state.Background.R},{state.Background.G},{state.Background.B}))")
            .ToList();

        Assert.True(
            failures.Count == 0,
            $"Dark accent button state contrast falls below the WCAG AA {WcagAaContrastMinimum:F1}:1 minimum:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");
    }

    // Maps each dialog surface (the InlineData key) to the page it's opened from, a human-readable
    // label used in failure messages, and the interaction that opens it and waits for it to render.
    // Settings is a right-aligned flyout panel reachable from every page's header; Filter is the
    // FilterDialog panel opened from the structured logs toolbar.
    private static readonly IReadOnlyDictionary<string, (string StartUrl, string Label, Func<IPage, Task> OpenSurfaceAsync)> s_dialogSurfaces =
        new Dictionary<string, (string, string, Func<IPage, Task>)>(StringComparer.Ordinal)
        {
            ["Settings"] = ("/", "Settings flyout", OpenSettingsFlyoutAsync),
            ["Filter"] = ("/structuredlogs", "Add filter dialog", OpenAddFilterDialogAsync),
        };

    private static async Task OpenSettingsFlyoutAsync(IPage page)
    {
        // The settings button lives in the top header on every page (MainLayout.SettingsButtonId).
        await page.Locator("#dashboard-settings-button").ClickAsync();

        // The settings panel is a right-aligned fluent-dialog with a fixed id (MainLayout.SettingsDialogId).
        // Wait on a light-DOM descendant rather than the <fluent-dialog> host: Fluent projects the dialog
        // body through a slot, so the custom-element host carries no layout box and never satisfies
        // Playwright's visibility check even once the panel is fully open. .input-container wraps each
        // settings group in SettingsDialog.razor and is a reliable "content has rendered" signal.
        await Assertions.Expect(page.Locator("fluent-dialog#SettingsDialog .input-container").First).ToBeVisibleAsync();
    }

    private static async Task OpenAddFilterDialogAsync(IPage page)
    {
        // The structured logs toolbar's "Add filter" button carries the localized aria-label from the
        // StructuredFiltering resource; reading it back from the same resource keeps the selector correct
        // regardless of test culture rather than hard-coding the English text.
        await page.Locator($"fluent-button[aria-label='{StructuredFiltering.AddFilter}']").ClickAsync();

        // FilterDialog opens as a right-aligned panel with no id, so wait for its distinctive
        // .filter-button-container (see FilterDialog.razor) to confirm the dialog body has rendered.
        await Assertions.Expect(page.Locator("fluent-dialog .filter-button-container")).ToBeVisibleAsync();
    }

    /// <summary>
    /// Verifies that every highlight.js syntax-token color used in code blocks (the text visualizer and
    /// rendered markdown) meets the WCAG 2.0 AA 4.5:1 minimum contrast against the code block
    /// background, in both themes. axe's own color-contrast rule can't cover these because a
    /// syntax-highlighted block isn't present on the scanned pages, so this renders an off-screen block
    /// that mirrors the dialog's DOM, reads the computed colors, and asserts the ratio here.
    /// </summary>
    [Theory]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    [InlineData("Light")]
    [InlineData("Dark")]
    public async Task CodeblockSyntaxColors_MeetWcagAaContrast(string theme)
    {
        var baseUrl = DashboardServerFixture.DashboardApp.FrontendSingleEndPointAccessor().GetResolvedAddress();

        await using var context = await PlaywrightFixture.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            IgnoreHTTPSErrors = true,
            BaseURL = baseUrl,
            ViewportSize = s_desktopViewport
        });

        await context.AddCookiesAsync([new Cookie { Name = "currentTheme", Value = theme, Url = baseUrl }]);

        var page = await context.NewPageAsync();
        await page.GotoAsync("/").DefaultTimeout();
        await page.WaitForSelectorAsync("body:not(.before-upgrade)").DefaultTimeout();
        await page.WaitForSelectorAsync(
            $"html[data-theme='{theme.ToLowerInvariant()}']",
            new PageWaitForSelectorOptions { State = WaitForSelectorState.Attached }).DefaultTimeout();

        // Settle component hydration + fonts before probing so the code block's computed colors are read
        // against fully applied theme styles (see WaitForComponentsAndFontsAsync).
        await WaitForComponentsAndFontsAsync(page);

        var probeJson = await page.EvaluateAsync<string>(CodeblockColorProbeScript);
        using var probe = JsonDocument.Parse(probeJson);

        var failures = new List<string>();
        foreach (var surface in probe.RootElement.GetProperty("surfaces").EnumerateArray())
        {
            var surfaceName = surface.GetProperty("name").GetString();
            var background = ReadRgb(surface.GetProperty("bg"));

            foreach (var token in surface.GetProperty("tokens").EnumerateArray())
            {
                var foreground = ReadRgb(token);
                var ratio = ContrastRatio(foreground, background);
                if (ratio < WcagAaContrastMinimum)
                {
                    failures.Add(
                        $"  {surfaceName} / {token.GetProperty("name").GetString()}: {ratio:F2}:1 " +
                        $"(text rgb({foreground.R},{foreground.G},{foreground.B}) on background rgb({background.R},{background.G},{background.B}))");
                }
            }
        }

        Assert.True(
            failures.Count == 0,
            $"{failures.Count} code block syntax color(s) fall below the WCAG AA {WcagAaContrastMinimum:F1}:1 minimum " +
            $"in the {theme} theme:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");
    }

    private async Task AssertNoBlockingWcagViolationsAsync(string relativeUrl, string theme, ViewportSize viewport, string? viewportLabel = null, Func<IPage, Task>? openSurfaceAsync = null, string? surfaceLabel = null)
    {
        var baseUrl = DashboardServerFixture.DashboardApp.FrontendSingleEndPointAccessor().GetResolvedAddress();

        await using var context = await PlaywrightFixture.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            IgnoreHTTPSErrors = true,
            BaseURL = baseUrl,
            ViewportSize = viewport
        });

        // The dashboard resolves its theme from the `currentTheme` cookie on boot (see
        // wwwroot/js/app-theme.js). Seeding it before the first navigation makes the scanned
        // theme deterministic instead of depending on the OS/browser "System" preference.
        await context.AddCookiesAsync([new Cookie { Name = "currentTheme", Value = theme, Url = baseUrl }]);

        var page = await context.NewPageAsync();
        await page.GotoAsync(relativeUrl).DefaultTimeout();

        // Wait until Blazor has upgraded the Fluent web components - app.js removes the
        // `before-upgrade` class from <body> once the components are ready (before then <body> is
        // visibility:hidden). Scanning earlier would audit the pre-hydration shell.
        await page.WaitForSelectorAsync("body:not(.before-upgrade)").DefaultTimeout();

        // Confirm the requested theme actually applied (app-theme.js sets data-theme on <html>),
        // otherwise a contrast scan could silently run against the wrong palette.
        await page.WaitForSelectorAsync(
            $"html[data-theme='{theme.ToLowerInvariant()}']",
            new PageWaitForSelectorOptions { State = WaitForSelectorState.Attached }).DefaultTimeout();

        // Readiness signal that content has rendered. On desktop the page title teleports into the top
        // bar as <h1 class="page-header"> (AspirePageContentLayout); at/below MobileCutoffPixelWidth
        // that teleport doesn't happen, so fall back to the always-present <main> region.
        if (viewport.Width > MobileCutoffPixelWidth)
        {
            await Assertions.Expect(page.Locator("h1.page-header")).ToBeVisibleAsync();
        }
        else
        {
            await Assertions.Expect(page.Locator("main")).ToBeVisibleAsync();
        }

        await WaitForPageContentAsync(page, relativeUrl);

        // Ensure every Fluent web component has finished upgrading and fonts have loaded before the
        // page is treated as scannable (see WaitForComponentsAndFontsAsync). This settles the base
        // page's comboboxes/buttons so the open click below and the scan don't race hydration.
        await WaitForComponentsAndFontsAsync(page);

        // Open an interactive surface (dialog, flyout panel) when requested. The caller's delegate both
        // triggers the surface and waits for it to render, so the scan below audits the page with that
        // surface open. Done before freezing animations so the surface's own open/fade animation is
        // pinned to its end state too (see below).
        if (openSurfaceAsync is not null)
        {
            await openSurfaceAsync(page);

            // The just-opened dialog mounts its own custom elements (e.g. the Settings language
            // <fluent-select>); wait for those to upgrade too so the scan doesn't sample a
            // half-hydrated combobox inside the panel.
            await WaitForComponentsAndFontsAsync(page);
        }

        // Collapse all CSS animations/transitions to their end state before scanning. axe's
        // color-contrast check multiplies an element's foreground by its (and its ancestors')
        // computed opacity, so if it samples while something is still animating in - e.g. the
        // FluentMessageBar fades in via a 1.5s `fadein` opacity animation - it reads a washed-out
        // effective color (settled text blended toward the background) and reports a false failure.
        // Forcing animation/transition durations to 0 pins every element at its resting opacity/color.
        await page.AddStyleTagAsync(new PageAddStyleTagOptions
        {
            Content = "*, *::before, *::after { animation-duration: 0s !important; animation-delay: 0s !important; transition-duration: 0s !important; transition-delay: 0s !important; }"
        });

        // Yield two animation frames so the freeze stylesheet above is applied and painted before axe
        // samples computed styles; otherwise axe can read a value still interpolating from the frame the
        // stylesheet was injected on.
        await page.EvaluateAsync("() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)))").DefaultTimeout();

        var axeResults = await page.RunAxe(new AxeRunOptions
        {
            RunOnly = new RunOnlyOptions { Type = "tag", Values = [.. s_wcagTags] }
        });

        var blockingViolations = axeResults.Violations
            .Where(v => v.Impact is not null
                && s_failingImpacts.Contains(v.Impact)
                && !s_allowedRuleIds.Contains(v.Id))
            .ToList();

        Assert.True(
            blockingViolations.Count == 0,
            BuildFailureMessage(axeResults, blockingViolations, relativeUrl, theme, viewportLabel, surfaceLabel));
    }

    // Blazor + Fluent web components hydrate asynchronously and independently: app.js clears the
    // <body> `before-upgrade` class as soon as the *first* custom element upgrades, but other component
    // types on the page (notably the Settings language <fluent-select> and the filter comboboxes) can
    // still be mid-upgrade at that moment. axe scanning a half-upgraded combobox reads a transient
    // role/accessible-name and yields non-deterministic pass/fail results. Wait for every custom element
    // currently in the DOM to finish upgrading, then for web fonts to finish loading, so the scan (and
    // any preceding interaction) samples a fully settled, stable page.
    private static async Task WaitForComponentsAndFontsAsync(IPage page)
    {
        await page.EvaluateAsync("""
            async () => {
                const undefinedNames = new Set();
                // :not(:defined) matches only not-yet-upgraded custom elements - built-in elements are
                // always :defined - so this collects exactly the component types still hydrating.
                for (const el of document.querySelectorAll(':not(:defined)')) {
                    undefinedNames.add(el.localName);
                }
                await Promise.all([...undefinedNames].map(name => customElements.whenDefined(name)));
                // document.fonts.ready resolves once the font loads triggered so far settle, so text
                // metrics/rendering are stable for the scan (the dashboard ships a custom body font).
                await document.fonts.ready;
            }
            """).DefaultTimeout();
    }

    private static Task WaitForPageContentAsync(IPage page, string relativeUrl)
    {
        var path = relativeUrl.Split('?', 2)[0].TrimEnd('/');
        var content = path switch
        {
            "" => page.GetByText("frontend", new PageGetByTextOptions { Exact = true }).First,
            "/consolelogs" => page.GetByText(ConsoleLogs.ConsoleLogsNoLogsFound, new PageGetByTextOptions { Exact = true }).First,
            "/structuredlogs" => page.GetByText(StructuredLogs.StructuredLogsNoLogsFound, new PageGetByTextOptions { Exact = true }).First,
            "/traces" => page.GetByText(Traces.TracesNoTraces, new PageGetByTextOptions { Exact = true }).First,
            "/metrics" => page.GetByText(Metrics.MetricsSelectAResource, new PageGetByTextOptions { Exact = true }).First,
            _ => throw new InvalidOperationException($"No accessibility readiness signal is configured for '{relativeUrl}'.")
        };

        // The shell can hydrate before each page's asynchronous data load finishes. Wait for content
        // that is specific to the requested route so axe never scans a transient empty page.
        return Assertions.Expect(content).ToBeVisibleAsync();
    }

    private static string BuildFailureMessage(AxeResult results, IReadOnlyList<AxeResultItem> blocking, string relativeUrl, string theme, string? viewportLabel = null, string? surfaceLabel = null)
    {
        var contextParts = new List<string> { $"{theme} theme" };
        if (viewportLabel is not null)
        {
            contextParts.Add($"{viewportLabel} viewport");
        }
        if (surfaceLabel is not null)
        {
            contextParts.Add(surfaceLabel);
        }

        var scanContext = string.Join(", ", contextParts);
        var sb = new StringBuilder();
        sb.AppendLine($"Found {blocking.Count} serious/critical WCAG 2.x A/AA accessibility violation(s) on '{relativeUrl}' ({scanContext}):");
        sb.AppendLine();

        foreach (var violation in blocking)
        {
            sb.AppendLine($"  [{violation.Impact}] {violation.Id}: {violation.Help}");
            sb.AppendLine($"    {violation.HelpUrl}");

            // Show a few offending nodes (selector + markup) to make the failure actionable without
            // re-running axe locally. Cap the count/length so the message stays readable. Use the
            // AxeSelector's string form rather than .Selector because Fluent UI renders into shadow
            // DOM, and .Selector throws for shadow-nested nodes (it can't be a single CSS selector).
            foreach (var node in violation.Nodes.Take(5))
            {
                sb.AppendLine($"      target: {node.Target}");
                sb.AppendLine($"      html:   {Truncate(node.Html, 200)}");

                // For color-contrast (and similar) rules, axe records the measured values - e.g.
                // foreground/background color, ratio and required ratio - in the per-node "any"
                // checks. Surfacing those messages makes the fix precise (which color, which ratio).
                foreach (var check in node.Any)
                {
                    sb.AppendLine($"      why:    {check.Message}");
                }
            }

            sb.AppendLine();
        }

        // Surface non-blocking (moderate/minor) findings and any allowlisted rules too, so the
        // failure captures the full accessibility picture for the page in one place.
        var other = results.Violations.Where(v => !blocking.Contains(v)).ToList();
        if (other.Count > 0)
        {
            sb.AppendLine($"Additional non-blocking violations ({other.Count}): "
                + string.Join(", ", other.Select(v => $"{v.Id}({v.Impact ?? "n/a"})")));
        }

        return sb.ToString();
    }

    private static string Truncate(string? value, int maxLength)
        => string.IsNullOrEmpty(value)
            ? string.Empty
            : value.Length <= maxLength ? value : string.Concat(value.AsSpan(0, maxLength), "…");

    private static (int R, int G, int B) ReadRgb(JsonElement element)
        => (element.GetProperty("r").GetInt32(), element.GetProperty("g").GetInt32(), element.GetProperty("b").GetInt32());

    private static async Task AssertVisibleFocusIndicatorAsync(ILocator host, string partName)
    {
        var focusJson = await host.EvaluateAsync<string>(
            """
            (element, partName) => {
                const focusTarget = element.shadowRoot?.querySelector(
                    'input, textarea, [role="combobox"], [tabindex]:not([tabindex="-1"])');
                if (!focusTarget) {
                    throw new Error(`No delegated focus target found for ${element.localName}.`);
                }

                focusTarget.focus();

                const part = element.shadowRoot.querySelector(`[part~="${partName}"]`);
                if (!part) {
                    const availableParts = [...element.shadowRoot.querySelectorAll('[part]')]
                        .map(node => node.getAttribute('part'))
                        .join(', ');
                    throw new Error(
                        `No ${partName} part found for ${element.localName}. Available parts: ${availableParts}.`);
                }

                const style = getComputedStyle(part);
                return JSON.stringify({
                    focusWithin: element.matches(':focus-within'),
                    focusVisible: element.matches(':focus-visible'),
                    outlineStyle: style.outlineStyle,
                    outlineWidth: Number.parseFloat(style.outlineWidth),
                    outlineColor: style.outlineColor
                });
            }
            """,
            partName);

        using var focus = JsonDocument.Parse(focusJson);
        var root = focus.RootElement;
        var outlineStyle = root.GetProperty("outlineStyle").GetString();
        var outlineWidth = root.GetProperty("outlineWidth").GetDouble();
        var outlineColor = root.GetProperty("outlineColor").GetString();
        var hasOpaqueOutline = !string.Equals(outlineColor, "transparent", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(outlineColor, "rgba(0, 0, 0, 0)", StringComparison.OrdinalIgnoreCase);

        Assert.True(root.GetProperty("focusWithin").GetBoolean(), $"Expected {await host.EvaluateAsync<string>("element => element.localName")} to contain delegated focus.");
        Assert.True(
            !string.Equals(outlineStyle, "none", StringComparison.Ordinal) && outlineWidth >= 2 && hasOpaqueOutline,
            $"Expected a visible focus outline on the {partName} part, but got style '{outlineStyle}', width {outlineWidth}px, color {outlineColor}, host :focus-visible={root.GetProperty("focusVisible").GetBoolean()}.");
    }

    private static async Task<((int R, int G, int B) Foreground, (int R, int G, int B) Background)> ReadFluentControlColorsAsync(ILocator host)
    {
        var colorsJson = await host.EvaluateAsync<string>(
            """
            element => {
                function parseRgb(s) {
                    s = String(s).trim();
                    let m = s.match(/rgba?\(([^)]+)\)/);
                    if (m) {
                        const p = m[1].split(/[ ,\/]+/).filter(Boolean).map(parseFloat);
                        return [p[0], p[1], p[2]];
                    }
                    m = s.match(/color\(srgb\s+([^)]+)\)/);
                    if (m) {
                        const p = m[1].split(/[ \/]+/).filter(Boolean).map(parseFloat);
                        return [Math.round(p[0] * 255), Math.round(p[1] * 255), Math.round(p[2] * 255)];
                    }
                    throw new Error('unparseable color: ' + s);
                }

                const control = element.shadowRoot?.querySelector('[part~="control"]');
                if (!control) {
                    throw new Error(`No control part found for ${element.localName}.`);
                }

                const style = getComputedStyle(control);
                const foreground = parseRgb(style.color);
                const background = parseRgb(style.backgroundColor);
                return JSON.stringify({
                    fg: { r: foreground[0], g: foreground[1], b: foreground[2] },
                    bg: { r: background[0], g: background[1], b: background[2] }
                });
            }
            """);

        using var colors = JsonDocument.Parse(colorsJson);
        var root = colors.RootElement;

        return (ReadRgb(root.GetProperty("fg")), ReadRgb(root.GetProperty("bg")));
    }

    // WCAG 2.x relative luminance and contrast ratio.
    // See https://www.w3.org/TR/WCAG21/#dfn-relative-luminance and #dfn-contrast-ratio.
    private static double ContrastRatio((int R, int G, int B) foreground, (int R, int G, int B) background)
    {
        var l1 = RelativeLuminance(foreground.R, foreground.G, foreground.B);
        var l2 = RelativeLuminance(background.R, background.G, background.B);
        var (lighter, darker) = l1 >= l2 ? (l1, l2) : (l2, l1);
        return (lighter + 0.05) / (darker + 0.05);
    }

    private static double RelativeLuminance(int r, int g, int b)
    {
        static double Channel(int component)
        {
            var c = component / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(r)) + (0.7152 * Channel(g)) + (0.0722 * Channel(b));
    }

    // Render the two production syntax-highlight surfaces off-screen: Text Visualizer inherits the
    // dialog surface, while rendered markdown paints its own code-block surface. The active theme is
    // whatever the page booted with (data-theme on <html>). WCAG math is done in C# in the test.
    private const string CodeblockColorProbeScript = """
        async () => {
            function parseRgb(s) {
                s = String(s).trim();
                let m = s.match(/rgba?\(([^)]+)\)/);
                if (m) { const p = m[1].split(/[ ,\/]+/).filter(Boolean).map(parseFloat); return [p[0], p[1], p[2]]; }
                m = s.match(/color\(srgb\s+([^)]+)\)/);
                if (m) { const p = m[1].split(/[ \/]+/).filter(Boolean).map(parseFloat); return [Math.round(p[0] * 255), Math.round(p[1] * 255), Math.round(p[2] * 255)]; }
                throw new Error('unparseable color: ' + s);
            }

            const groups = { default: 'hljs', comment: 'hljs-comment', variable: 'hljs-variable', literal: 'hljs-literal', attribute: 'hljs-attribute', string: 'hljs-string', section: 'hljs-section', keyword: 'hljs-keyword' };
            const theme = document.documentElement.getAttribute('data-theme');

            async function readSurface(name, container, getBackgroundElement, line) {
                document.body.appendChild(container);
                await new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)));

                const backgroundElement = getBackgroundElement();
                if (!backgroundElement) {
                    throw new Error(`No production background element found for ${name}.`);
                }

                const bg = parseRgb(getComputedStyle(backgroundElement).backgroundColor);
                const tokens = [];

                for (const tokenName of Object.keys(groups)) {
                    const cls = groups[tokenName];
                    let element;
                    if (cls === 'hljs') {
                        element = line;
                    } else {
                        element = document.createElement('span');
                        element.className = cls;
                        element.textContent = 'x';
                        line.appendChild(element);
                    }

                    const fg = parseRgb(getComputedStyle(element).color);
                    tokens.push({ name: tokenName, r: fg[0], g: fg[1], b: fg[2] });
                }

                container.remove();
                return { name: name, bg: { r: bg[0], g: bg[1], b: bg[2] }, tokens: tokens };
            }

            await customElements.whenDefined('fluent-dialog');
            const dialog = document.createElement('fluent-dialog');
            dialog.style.cssText = 'position:fixed;left:-99999px;top:0;';
            const textVisualizer = document.createElement('div');
            textVisualizer.className = 'text-visualizer-container';
            const overflow = document.createElement('div');
            overflow.className = 'log-overflow';
            const visualizerLine = document.createElement('span');
            visualizerLine.className = 'log-content highlight-line hljs theme-a11y-' + theme + '-min';
            visualizerLine.textContent = 'sample';
            overflow.appendChild(visualizerLine);
            textVisualizer.appendChild(overflow);
            dialog.appendChild(textVisualizer);

            const markdown = document.createElement('div');
            markdown.className = 'markdown-container';
            markdown.style.cssText = 'position:fixed;left:-99999px;top:0;';
            const codeBlock = document.createElement('div');
            codeBlock.className = 'code-block';
            const markdownCode = document.createElement('code');
            markdownCode.className = 'hljs theme-a11y-' + theme + '-min';
            markdownCode.textContent = 'sample';
            codeBlock.appendChild(markdownCode);
            markdown.appendChild(codeBlock);

            return JSON.stringify({
                surfaces: [
                    await readSurface(
                        'text visualizer',
                        dialog,
                        () => dialog.shadowRoot?.querySelector('[part~="control"]'),
                        visualizerLine),
                    await readSurface('rendered markdown', markdown, () => codeBlock, markdownCode)
                ]
            });
        }
        """;

    public sealed class AccessibilityDashboardServerFixture : DashboardServerFixture
    {
        // A small but representative resource set so the resource grid, filters and pickers render
        // real content for the scan rather than an empty state.
        protected override IReadOnlyList<ResourceViewModel> Resources =>
        [
            ModelTestHelpers.CreateResource(
                resourceName: "frontend",
                resourceType: KnownResourceTypes.Project,
                state: KnownResourceState.Running),
            ModelTestHelpers.CreateResource(
                resourceName: "cache",
                resourceType: KnownResourceTypes.Container,
                state: KnownResourceState.Running),
        ];
    }
}
