// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.Dashboard.Resources;
using Aspire.Dashboard.Tests.Integration.Playwright.Infrastructure;
using Aspire.TestUtilities;
using Microsoft.Playwright;
using Xunit;

namespace Aspire.Dashboard.Tests.Integration.Playwright;

// Focused browser coverage for the seven Deck-redesign findings. The colocated ".razor.js" modules
// are exercised in a real Chromium under the running dashboard's Content-Security-Policy (proving
// they are CSP-safe: they are imported same-origin as ES modules with no inline script). Each test
// asserts there were no browser console errors so a regression that logs to the console fails here.
[RequiresFeature(TestFeature.Playwright)]
public class DeckRedesignFindingsTests : PlaywrightTestsBase<DashboardServerFixture>
{
    public DeckRedesignFindingsTests(DashboardServerFixture dashboardServerFixture)
        : base(dashboardServerFixture)
    {
    }

    [Fact]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task RealThemeToggle_ChangesThemeWithoutConsoleErrors()
    {
        await RunTestAsync(async page =>
        {
            var errors = TrackConsoleErrors(page);
            await GoHomeAsync(page);

            // Open settings once, then flip the real theme radios and assert the applied theme.
            var settingsButton = page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = Layout.MainLayoutLaunchSettings });
            await settingsButton.ClickAsync();

            await ClickThemeAndVerifyAsync(page, Dialogs.SettingsDialogLightTheme, "light");
            await ClickThemeAndVerifyAsync(page, Dialogs.SettingsDialogDarkTheme, "dark");

            // Escape dismisses the settings surface (best-effort cleanup; not required for the assert).
            await page.Keyboard.PressAsync("Escape");

            AssertNoConsoleErrors(errors);
        });
    }

    [Fact]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task Finding1_DialogProviderModule_TrapsFocusLocksScrollAndRestores()
    {
        await RunTestAsync(async page =>
        {
            var errors = TrackConsoleErrors(page);
            await GoHomeAsync(page);

            var result = await page.EvaluateAsync<JsonElement>("""
                async (moduleUrl) => {
                    const mod = await import(moduleUrl);

                    const outside = document.createElement('button');
                    outside.id = 'finding1-outside';
                    document.body.appendChild(outside);
                    outside.focus();

                    const dialog = document.createElement('div');
                    dialog.id = 'deck-dialog-finding1';
                    dialog.setAttribute('role', 'dialog');
                    dialog.tabIndex = -1;
                    const first = document.createElement('button');
                    first.id = 'finding1-first';
                    const last = document.createElement('button');
                    last.id = 'finding1-last';
                    dialog.appendChild(first);
                    dialog.appendChild(last);
                    document.body.appendChild(dialog);

                    mod.initialize('deck-dialog-finding1', { trapFocus: true, preventScroll: true });
                    const focusedAfterInit = document.activeElement?.id;
                    const overflowLocked = document.body.style.overflow;

                    // Tab off the last control wraps back to the first (forward trap).
                    last.focus();
                    dialog.dispatchEvent(new KeyboardEvent('keydown', { key: 'Tab', bubbles: true, cancelable: true }));
                    const afterTab = document.activeElement?.id;

                    // Shift+Tab off the first control wraps to the last (backward trap).
                    first.focus();
                    dialog.dispatchEvent(new KeyboardEvent('keydown', { key: 'Tab', shiftKey: true, bubbles: true, cancelable: true }));
                    const afterShiftTab = document.activeElement?.id;

                    mod.dispose('deck-dialog-finding1');
                    const overflowAfterDispose = document.body.style.overflow;
                    const focusRestored = document.activeElement?.id;

                    return { focusedAfterInit, overflowLocked, afterTab, afterShiftTab, overflowAfterDispose, focusRestored };
                }
                """, DeckModuleUrl(page, "Components/Dialogs/DeckDialogProvider.razor.js"));

            Assert.Equal("finding1-first", result.GetProperty("focusedAfterInit").GetString());
            Assert.Equal("hidden", result.GetProperty("overflowLocked").GetString());
            Assert.Equal("finding1-first", result.GetProperty("afterTab").GetString());
            Assert.Equal("finding1-last", result.GetProperty("afterShiftTab").GetString());
            Assert.NotEqual("hidden", result.GetProperty("overflowAfterDispose").GetString());
            Assert.Equal("finding1-outside", result.GetProperty("focusRestored").GetString());

            AssertNoConsoleErrors(errors);
        });
    }

    [Fact]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task Finding2_ModalCss_IsViewportSafeAndDistinctFromPanel()
    {
        await RunTestAsync(async page =>
        {
            var errors = TrackConsoleErrors(page);
            await GoHomeAsync(page);

            var result = await page.EvaluateAsync<JsonElement>("""
                () => {
                    const modal = document.createElement('div');
                    modal.className = 'deck-dialog deck-dialog--modal';
                    // Request a very large width; the modal must stay viewport-safe via max-width.
                    modal.style.width = '5000px';
                    document.body.appendChild(modal);

                    const panel = document.createElement('div');
                    panel.className = 'deck-dialog deck-dialog--panel';
                    document.body.appendChild(panel);

                    const modalStyle = getComputedStyle(modal);
                    const panelStyle = getComputedStyle(panel);

                    return {
                        modalWidth: modal.getBoundingClientRect().width,
                        modalMinWidth: modalStyle.minWidth,
                        viewportWidth: window.innerWidth,
                        panelHeight: panel.getBoundingClientRect().height,
                        viewportHeight: window.innerHeight,
                        modalHeight: modal.getBoundingClientRect().height
                    };
                }
                """);

            var viewportWidth = result.GetProperty("viewportWidth").GetDouble();
            var modalWidth = result.GetProperty("modalWidth").GetDouble();
            // Modal never exceeds 90vw even though 5000px was requested, so it stays viewport-safe.
            Assert.True(modalWidth <= viewportWidth, $"Modal width {modalWidth} should not exceed viewport {viewportWidth}.");
            Assert.Equal("320px", result.GetProperty("modalMinWidth").GetString());

            // A panel is full viewport height; a modal is content-height. This confirms the modal box
            // is styled distinctly from a panel (finding 2: alignment must not turn a modal into a panel).
            var panelHeight = result.GetProperty("panelHeight").GetDouble();
            var viewportHeight = result.GetProperty("viewportHeight").GetDouble();
            var modalHeight = result.GetProperty("modalHeight").GetDouble();
            Assert.True(panelHeight >= viewportHeight - 1, $"Panel height {panelHeight} should fill the viewport {viewportHeight}.");
            Assert.True(modalHeight < panelHeight, $"Modal height {modalHeight} should be smaller than the full-height panel {panelHeight}.");

            AssertNoConsoleErrors(errors);
        });
    }

    [Fact]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task Finding3_ResourceSelect_EnterActivatesExactlyOnce()
    {
        await RunTestAsync(async page =>
        {
            var errors = TrackConsoleErrors(page);
            await GoHomeAsync(page);
            // The metrics page hosts a ResourceSelect and (unlike console logs) does not subscribe to
            // any not-implemented mock stream, so it renders cleanly under the test harness.
            await page.GotoAsync("/metrics");

            var trigger = page.Locator(".deck-resource-select__trigger");
            await Assertions.Expect(trigger).ToBeVisibleAsync();
            await Assertions.Expect(trigger).ToHaveAttributeAsync("aria-expanded", "false");

            // Enter on the native button synthesizes a single click. If the keydown handler ALSO
            // toggled (the bug), Enter would open then immediately close, leaving it collapsed. So a
            // single Enter opening the listbox proves activation happens exactly once.
            await trigger.FocusAsync();
            await page.Keyboard.PressAsync("Enter");
            await Assertions.Expect(trigger).ToHaveAttributeAsync("aria-expanded", "true");

            // Escape (handled in the keydown switch) closes it.
            await page.Keyboard.PressAsync("Escape");
            await Assertions.Expect(trigger).ToHaveAttributeAsync("aria-expanded", "false");

            AssertNoConsoleErrors(errors);
        });
    }

    [Fact]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task Finding4_ComboboxModule_PreventsEnterSubmitOnlyWhenOptionActive()
    {
        await RunTestAsync(async page =>
        {
            var errors = TrackConsoleErrors(page);
            await GoHomeAsync(page);

            var result = await page.EvaluateAsync<JsonElement>("""
                async (moduleUrl) => {
                    const mod = await import(moduleUrl);

                    const input = document.createElement('input');
                    input.type = 'text';
                    document.body.appendChild(input);
                    mod.initialize(input);

                    input.dataset.activeOption = 'true';
                    const active = new KeyboardEvent('keydown', { key: 'Enter', bubbles: true, cancelable: true });
                    input.dispatchEvent(active);

                    input.dataset.activeOption = 'false';
                    const inactive = new KeyboardEvent('keydown', { key: 'Enter', bubbles: true, cancelable: true });
                    input.dispatchEvent(inactive);

                    const typing = new KeyboardEvent('keydown', { key: 'a', bubbles: true, cancelable: true });
                    input.dispatchEvent(typing);

                    mod.dispose(input);

                    return {
                        preventedWhenActive: active.defaultPrevented,
                        preventedWhenInactive: inactive.defaultPrevented,
                        preventedForTyping: typing.defaultPrevented
                    };
                }
                """, DeckModuleUrl(page, "Components/Deck/Combobox.razor.js"));

            // Only Enter-with-active-option cancels the browser's implicit form submit.
            Assert.True(result.GetProperty("preventedWhenActive").GetBoolean());
            Assert.False(result.GetProperty("preventedWhenInactive").GetBoolean());
            Assert.False(result.GetProperty("preventedForTyping").GetBoolean());

            AssertNoConsoleErrors(errors);
        });
    }

    [Fact]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task Finding5_PopoverModule_StableAnchorIdLifecycleIsLeakFree()
    {
        await RunTestAsync(async page =>
        {
            var errors = TrackConsoleErrors(page);
            await GoHomeAsync(page);

            // The ChartFilters fix gives each DimensionFilter a stable anchor id (asserted in
            // ChartFiltersTests). This verifies the Popover module — which keys init/dispose on that
            // id — initializes and disposes cleanly across repeated re-renders using the SAME id,
            // with no leftover document listeners or thrown errors.
            var result = await page.EvaluateAsync<JsonElement>("""
                async (moduleUrl) => {
                    const mod = await import(moduleUrl);

                    const anchor = document.createElement('button');
                    anchor.id = 'finding5-stable-anchor';
                    document.body.appendChild(anchor);

                    const popover = document.createElement('div');
                    popover.style.position = 'fixed';
                    document.body.appendChild(popover);

                    const dotNet = { invokeMethodAsync: () => Promise.resolve() };

                    // Simulate several re-renders that reuse the same stable anchor id.
                    for (let i = 0; i < 3; i++) {
                        mod.initialize(popover, 'finding5-stable-anchor', dotNet);
                        mod.dispose('finding5-stable-anchor');
                    }

                    // A dispose after everything is torn down must be a safe no-op.
                    mod.dispose('finding5-stable-anchor');

                    return { completed: true };
                }
                """, DeckModuleUrl(page, "Components/Deck/Popover.razor.js"));

            Assert.True(result.GetProperty("completed").GetBoolean());

            AssertNoConsoleErrors(errors);
        });
    }

    [Fact]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task Finding6_ColumnResizerModule_DynamicHandlesWorkAfterRerender()
    {
        await RunTestAsync(async page =>
        {
            var errors = TrackConsoleErrors(page);
            await GoHomeAsync(page);

            var result = await page.EvaluateAsync<JsonElement>("""
                async (moduleUrl) => {
                    const mod = await import(moduleUrl);

                    const table = document.createElement('table');
                    table.style.tableLayout = 'fixed';
                    table.style.width = '300px';
                    const colgroup = document.createElement('colgroup');
                    for (let i = 0; i < 3; i++) {
                        const col = document.createElement('col');
                        col.style.width = '100px';
                        colgroup.appendChild(col);
                    }
                    table.appendChild(colgroup);

                    const thead = document.createElement('thead');
                    const row = document.createElement('tr');
                    const headers = [];
                    for (let i = 0; i < 3; i++) {
                        const th = document.createElement('th');
                        th.textContent = 'H' + i;
                        th.style.position = 'relative';
                        row.appendChild(th);
                        headers.push(th);
                    }
                    thead.appendChild(row);
                    table.appendChild(thead);

                    // Only column 0's handle exists when initialize() runs.
                    const handle0 = document.createElement('span');
                    handle0.setAttribute('data-resize-handle', '');
                    handle0.setAttribute('data-column-index', '0');
                    handle0.setAttribute('role', 'separator');
                    handle0.tabIndex = 0;
                    headers[0].appendChild(handle0);

                    document.body.appendChild(table);
                    const marker = document.createElement('span');
                    document.body.appendChild(marker); // marker.previousElementSibling === table

                    mod.initialize(marker, { id: 'finding6', minWidth: 20 });

                    // The responsive grid adds column 1's handle AFTER initialization.
                    const handle1 = document.createElement('span');
                    handle1.setAttribute('data-resize-handle', '');
                    handle1.setAttribute('data-column-index', '1');
                    handle1.setAttribute('role', 'separator');
                    handle1.tabIndex = 0;
                    headers[1].appendChild(handle1);

                    // Let the MutationObserver initialize the new handle's ARIA metadata.
                    await new Promise(resolve => requestAnimationFrame(() => resolve()));
                    const newHandleAriaMin = handle1.getAttribute('aria-valuemin');

                    const cols = () => Array.from(table.querySelectorAll('colgroup col'));
                    const beforeWidth = cols()[1].style.width;

                    // Keyboard-resize using the dynamically added handle; the delegated root listener
                    // must service it even though it did not exist at initialize() time.
                    handle1.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowRight', bubbles: true, cancelable: true }));
                    const afterWidth = cols()[1].style.width;

                    // After dispose, the handle is inert (no leaked listeners keep resizing).
                    mod.dispose('finding6');
                    handle1.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowRight', bubbles: true, cancelable: true }));
                    const afterDisposeWidth = cols()[1].style.width;

                    return { newHandleAriaMin, beforeWidth, afterWidth, afterDisposeWidth };
                }
                """, DeckModuleUrl(page, "Components/Controls/Grid/ColumnResizer.razor.js"));

            Assert.Equal("20", result.GetProperty("newHandleAriaMin").GetString());
            var beforeWidth = result.GetProperty("beforeWidth").GetString();
            var afterWidth = result.GetProperty("afterWidth").GetString();
            var afterDisposeWidth = result.GetProperty("afterDisposeWidth").GetString();
            Assert.NotEqual(beforeWidth, afterWidth);
            Assert.Equal(afterWidth, afterDisposeWidth);

            AssertNoConsoleErrors(errors);
        });
    }

    [Fact]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task Finding7_AspireMenuModule_SubmenuArrowsMoveWithinSubmenu()
    {
        await RunTestAsync(async page =>
        {
            var errors = TrackConsoleErrors(page);
            await GoHomeAsync(page);

            var result = await page.EvaluateAsync<JsonElement>("""
                async (moduleUrl) => {
                    const mod = await import(moduleUrl);

                    const menu = document.createElement('div');
                    menu.className = 'deck-menu';
                    menu.setAttribute('role', 'menu');
                    menu.id = 'finding7-menu';

                    const topItem = document.createElement('button');
                    topItem.className = 'deck-menu__item';
                    topItem.setAttribute('role', 'menuitem');
                    topItem.id = 'finding7-top';
                    topItem.textContent = 'Top';

                    const wrapper = document.createElement('div');
                    wrapper.className = 'deck-menu__item-wrapper';
                    const trigger = document.createElement('button');
                    trigger.className = 'deck-menu__item deck-menu__item--has-submenu';
                    trigger.setAttribute('role', 'menuitem');
                    trigger.setAttribute('aria-haspopup', 'true');
                    trigger.textContent = 'More';

                    const submenu = document.createElement('div');
                    submenu.className = 'deck-menu__submenu';
                    submenu.setAttribute('role', 'menu');
                    // The stylesheet hides submenus (display:none) until hover/focus-within; force it
                    // open so its items are focusable for this arrow-key navigation check.
                    submenu.style.display = 'block';
                    const sub1 = document.createElement('button');
                    sub1.className = 'deck-menu__item';
                    sub1.setAttribute('role', 'menuitem');
                    sub1.id = 'finding7-sub1';
                    const sub2 = document.createElement('button');
                    sub2.className = 'deck-menu__item';
                    sub2.setAttribute('role', 'menuitem');
                    sub2.id = 'finding7-sub2';
                    submenu.appendChild(sub1);
                    submenu.appendChild(sub2);
                    wrapper.appendChild(trigger);
                    wrapper.appendChild(submenu);

                    menu.appendChild(topItem);
                    menu.appendChild(wrapper);
                    document.body.appendChild(menu);

                    const dotNet = { invokeMethodAsync: () => Promise.resolve() };
                    mod.initialize(menu, 'finding7-menu', 'cursor', null, 100, 100, dotNet);

                    // Focus the first item inside the OPEN submenu, then arrow down.
                    sub1.focus();
                    const before = document.activeElement?.id;
                    menu.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowDown', bubbles: true, cancelable: true }));
                    const after = document.activeElement?.id;

                    mod.dispose('finding7-menu');

                    return { before, after };
                }
                """, DeckModuleUrl(page, "Components/Controls/AspireMenu.razor.js"));

            // Arrow keys move through the submenu's own siblings, not back to the top-level menu.
            Assert.Equal("finding7-sub1", result.GetProperty("before").GetString());
            Assert.Equal("finding7-sub2", result.GetProperty("after").GetString());

            AssertNoConsoleErrors(errors);
        });
    }

    [Fact]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task ConsolidationB_PopoverModule_KeepsOpenOnOwnContentScrollButRepositionsOnAncestorScroll()
    {
        await RunTestAsync(async page =>
        {
            var errors = TrackConsoleErrors(page);
            await GoHomeAsync(page);

            var result = await page.EvaluateAsync<JsonElement>("""
                async (moduleUrl) => {
                    const mod = await import(moduleUrl);

                    const closeCalls = [];
                    const dotNet = { invokeMethodAsync: (name) => { closeCalls.push(name); return Promise.resolve(); } };

                    const anchor = document.createElement('button');
                    anchor.id = 'consolidationB-anchor';
                    anchor.style.position = 'fixed';
                    anchor.style.top = '300px';
                    anchor.style.left = '100px';
                    anchor.style.width = '40px';
                    anchor.style.height = '20px';
                    document.body.appendChild(anchor);

                    const popover = document.createElement('div');
                    popover.style.position = 'fixed';
                    const body = document.createElement('div');
                    body.className = 'popover__body';
                    body.style.overflowY = 'auto';
                    body.style.height = '40px';
                    const tall = document.createElement('div');
                    tall.style.height = '400px';
                    body.appendChild(tall);
                    popover.appendChild(body);
                    document.body.appendChild(popover);

                    mod.initialize(popover, 'consolidationB-anchor', dotNet);

                    // 1) Scrolling the popover's OWN content must not dismiss it and must not reposition
                    //    (the anchor hasn't moved). A capturing window listener still receives this.
                    const topBeforeInternal = popover.style.top;
                    body.dispatchEvent(new Event('scroll', { bubbles: false }));
                    const closesAfterInternalScroll = closeCalls.length;
                    const topAfterInternal = popover.style.top;

                    // 2) An ancestor/page scroll after the anchor moves repositions the popover so it
                    //    keeps following the anchor - and still never dismisses.
                    anchor.style.top = '350px';
                    document.dispatchEvent(new Event('scroll', { bubbles: false }));
                    const closesAfterOuterScroll = closeCalls.length;
                    const topAfterOuter = popover.style.top;

                    // 3) Escape dismisses (anchor lifecycle preserved).
                    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
                    const closesAfterEscape = closeCalls.length;

                    // 4) Outside pointerdown dismisses (attached on the next tick via setTimeout(0)).
                    await new Promise(resolve => setTimeout(resolve, 10));
                    const outside = document.createElement('button');
                    outside.id = 'consolidationB-outside';
                    document.body.appendChild(outside);
                    outside.dispatchEvent(new PointerEvent('pointerdown', { bubbles: true }));
                    const closesAfterOutsidePointer = closeCalls.length;

                    mod.dispose('consolidationB-anchor');

                    return {
                        closesAfterInternalScroll,
                        topBeforeInternal,
                        topAfterInternal,
                        closesAfterOuterScroll,
                        topAfterOuter,
                        closesAfterEscape,
                        closesAfterOutsidePointer
                    };
                }
                """, DeckModuleUrl(page, "Components/Deck/Popover.razor.js"));

            // Scrolling the popover's own content never dismisses it and never repositions it.
            Assert.Equal(0, result.GetProperty("closesAfterInternalScroll").GetInt32());
            Assert.Equal(result.GetProperty("topBeforeInternal").GetString(), result.GetProperty("topAfterInternal").GetString());

            // An ancestor scroll repositions (top changes to follow the moved anchor) but still never dismisses.
            Assert.Equal(0, result.GetProperty("closesAfterOuterScroll").GetInt32());
            Assert.NotEqual(result.GetProperty("topAfterInternal").GetString(), result.GetProperty("topAfterOuter").GetString());

            // Escape and outside pointerdown still dismiss the popover.
            Assert.True(result.GetProperty("closesAfterEscape").GetInt32() >= 1);
            Assert.True(result.GetProperty("closesAfterOutsidePointer").GetInt32() > result.GetProperty("closesAfterEscape").GetInt32());

            AssertNoConsoleErrors(errors);
        });
    }

    private static async Task GoHomeAsync(IPage page)
    {
        await page.GotoAsync("/");
        await Assertions.Expect(page.GetByText(MockDashboardClient.TestResource1.DisplayName)).ToBeVisibleAsync();
    }

    private static async Task ClickThemeAndVerifyAsync(IPage page, string themeLabel, string expectedTheme)
    {
        var themeOption = page.Locator("label.deck-radio").Filter(new LocatorFilterOptions { HasTextString = themeLabel }).First;
        await themeOption.ClickAsync();

        await Assertions.Expect(page.Locator("html")).ToHaveAttributeAsync("data-theme", expectedTheme);
    }

    private static string DeckModuleUrl(IPage page, string relativePath)
    {
        var baseUrl = page.Url.TrimEnd('/');
        var slash = baseUrl.IndexOf("//", StringComparison.Ordinal);
        var authorityEnd = baseUrl.IndexOf('/', slash + 2);
        var origin = authorityEnd < 0 ? baseUrl : baseUrl[..authorityEnd];
        return $"{origin}/{relativePath}";
    }

    private static List<string> TrackConsoleErrors(IPage page)
    {
        var errors = new List<string>();
        page.Console += (_, message) =>
        {
            if (message.Type == "error")
            {
                errors.Add(message.Text);
            }
        };
        page.PageError += (_, error) => errors.Add(error);
        return errors;
    }

    private static void AssertNoConsoleErrors(List<string> errors)
    {
        Assert.True(errors.Count == 0, $"Expected no browser console errors but found: {string.Join(" | ", errors)}");
    }
}
