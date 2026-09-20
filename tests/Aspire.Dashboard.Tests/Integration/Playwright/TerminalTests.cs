// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;
using Aspire.Dashboard.Terminal;
using Aspire.Dashboard.Tests.Integration.Playwright.Infrastructure;
using Aspire.TestUtilities;
using Aspire.Tests.Shared.DashboardModel;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using Xunit;

namespace Aspire.Dashboard.Tests.Integration.Playwright;

[RequiresFeature(TestFeature.Playwright)]
public sealed class TerminalTests(TerminalTests.TerminalDashboardServerFixture fixture)
    : PlaywrightTestsBase<TerminalTests.TerminalDashboardServerFixture>(fixture)
{
    private const string ResourceName = "terminal-resource";
    private const string Endpoint = "/api/terminal?resource=terminal-resource&replica=0";

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task ReadOnly_BlocksKeyboardAndPasteWithoutInterruptingOutput(bool initialReadOnly, bool chromeless)
    {
        await RunTestAsync(async page =>
        {
            await fixture.TerminalResolver.DiscardPendingConnectionsAsync();
            await page.GotoAsync("/").DefaultTimeout();
            using var session = fixture.DashboardApp.Services.GetRequiredService<TerminalViewSessionRegistry>()
                .Create(Endpoint, initialReadOnly);
            var terminalId = await MountModuleAsync(page, session, chromeless);
            await using var connection = await fixture.TerminalResolver.AcceptConnectionAsync(CancellationToken.None).DefaultTimeout();
            await WaitForConnectedAsync(page, terminalId);
            await MountObserverAsync(page, requestPrimary: true);
            await connection.WaitForPeerHandshakesAsync(CancellationToken.None).DefaultTimeout();
            var primaryId = connection.Presentation.PrimaryPeerId;
            Assert.NotNull(primaryId);

            var terminal = page.GetByTestId("module-terminal");
            var terminalElement = await terminal.Locator("textarea").ElementHandleAsync();
            Assert.NotNull(terminalElement);
            var input = terminal.GetByRole(AriaRole.Textbox);
            await ExpectReadOnlyAsync(page, initialReadOnly);
            await SetReadOnlyAsync(page, terminalId, session, true);
            await ExpectReadOnlyAsync(page, true);

            connection.Workload.Write("\u001b[?25lOutput while read-only\r\n");
            await connection.WaitForProducerTextAsync("Output while read-only", CancellationToken.None).DefaultTimeout();
            await ExpectObserverTextAsync(page, "Output while read-only");
            // The overlay scrollbar has its own canvas after the terminal surface.
            await Assertions.Expect(terminal.Locator("canvas").First).ToBeVisibleAsync();
            await page.EvaluateAsync("() => window.moduleTerminal.focus()");
            await page.Keyboard.TypeAsync("blocked-keyboard");
            await PasteAsync(input, "blocked-paste");
            Assert.Equal(["Terminal view does not accept input", "Terminal view does not accept input"],
                await page.EvaluateAsync<string[]>("""
                    async () => {
                        const errors = [];
                        for (const action of [
                            () => window.moduleTerminal.paste('blocked-direct-paste'),
                            () => window.moduleTerminal.runAction('pasteClipboard')
                        ]) {
                            try {
                                await action();
                                errors.push('Input unexpectedly accepted');
                            } catch (error) {
                                errors.push(error.message);
                            }
                        }
                        return errors;
                    }
                    """));
            await page.EvaluateAsync("""
                async id => {
                    const module = await import('/Components/Controls/TerminalView.razor.js');
                    module.setFontSizeFromHost(id, 20);
                    module.setSizeModeFromHost(id, '80x24');
                }
                """, terminalId);

            // The independent viewer remains usable while this view is read-only.
            // Its ordered input also provides a barrier for the blocked input above.
            await page.EvaluateAsync("() => window.terminalObserver.paste('other-view')");
            Assert.Equal("other-view", await connection.ReadInputTextAsync("other-view".Length, CancellationToken.None).DefaultTimeout());
            Assert.Equal(primaryId, connection.Presentation.PrimaryPeerId);
            Assert.True(await page.EvaluateAsync<bool>("""
                async id => {
                    const module = await import('/Components/Controls/TerminalView.razor.js');
                    const state = module.getToolbarState(id);
                    return state.fontPx === 13 && state.cols === 137 && state.rows === 41;
                }
                """, terminalId));

            await SetReadOnlyAsync(page, terminalId, session, false);
            await ExpectReadOnlyAsync(page, false);
            await input.FocusAsync();
            await page.Keyboard.TypeAsync("x");
            await PasteAsync(input, "allowed-paste");
            Assert.Equal("xallowed-paste", await connection.ReadInputTextAsync("xallowed-paste".Length, CancellationToken.None).DefaultTimeout());
            Assert.Equal(primaryId, connection.Presentation.PrimaryPeerId);

            await SetReadOnlyAsync(page, terminalId, session, true);
            await ExpectReadOnlyAsync(page, true);
            await page.EvaluateAsync("() => window.moduleTerminal.focus()");
            await page.Keyboard.TypeAsync("blocked-again");
            await PasteAsync(input, "blocked-paste-again");
            await page.EvaluateAsync("() => window.terminalObserver.paste('still-active')");
            Assert.Equal("still-active", await connection.ReadInputTextAsync("still-active".Length, CancellationToken.None).DefaultTimeout());
            await SetReadOnlyAsync(page, terminalId, session, false);
            await PasteAsync(input, "final-check");
            Assert.Equal("final-check", await connection.ReadInputTextAsync("final-check".Length, CancellationToken.None).DefaultTimeout());
            connection.Workload.Write("Output after policy changes\r\n");
            await ExpectObserverTextAsync(page, "Output after policy changes");
            await WaitForConnectedAsync(page, terminalId);
            Assert.True(await terminalElement.EvaluateAsync<bool>("element => element.isConnected"));
            Assert.Equal(2, connection.ConnectionCount);
        });
    }

    [Fact]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task TerminalFocusNavigation_MovesToExpectedControlsWithoutForwardingInput()
    {
        await RunTestAsync(async page =>
        {
            await using var connection = await OpenTerminalAsync(page);
            var input = page.GetByRole(AriaRole.Textbox, new() { Name = "Interactive terminal input", Exact = true });
            var decreaseFontButton = page.GetByRole(AriaRole.Button, new() { Name = "Decrease font size", Exact = true });
            var precedingControl = page.GetByRole(AriaRole.Button, new() { Name = "Settings", Exact = true });
            var focusHint = page.Locator(".terminal-focus-hint");

            await Assertions.Expect(decreaseFontButton).ToBeEnabledAsync();
            await input.FocusAsync();
            await Assertions.Expect(focusHint).ToBeVisibleAsync();
            await page.Keyboard.PressAsync("F6");
            await Assertions.Expect(decreaseFontButton).ToBeFocusedAsync();
            await Assertions.Expect(focusHint).ToBeHiddenAsync();

            await page.Keyboard.PressAsync("F6");
            await Assertions.Expect(input).ToBeFocusedAsync();
            await Assertions.Expect(focusHint).ToBeVisibleAsync();
            await page.Keyboard.PressAsync("Shift+F6");
            await Assertions.Expect(precedingControl).ToBeFocusedAsync();
            await Assertions.Expect(focusHint).ToBeHiddenAsync();

            // The producer's first key must be this character, not an intercepted F6 key.
            await input.FocusAsync();
            await Assertions.Expect(focusHint).ToBeVisibleAsync();
            await page.Keyboard.TypeAsync("x");
            Assert.Equal("x", await connection.ReadInputTextAsync(1, CancellationToken.None).DefaultTimeout());
        });
    }

    [Fact]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task SecondaryTypingAndPaste_PreservePrimaryAndProducerDimensions()
    {
        await RunTestAsync(async page =>
        {
            await using var connection = await OpenTerminalAsync(page);
            await MountObserverAsync(page, requestPrimary: true);
            var primaryId = connection.Presentation.PrimaryPeerId;
            Assert.NotNull(primaryId);
            await ExpectProducerDimensionsAsync(page);

            var input = page.GetByRole(AriaRole.Textbox, new() { Name = "Interactive terminal input", Exact = true });
            await input.FocusAsync();
            await page.Keyboard.TypeAsync("x");
            await PasteAsync(input, "paste");
            Assert.Equal("xpaste", await connection.ReadInputTextAsync(6, CancellationToken.None).DefaultTimeout());
            Assert.Equal(primaryId, connection.Presentation.PrimaryPeerId);
            await ExpectProducerDimensionsAsync(page);
        });
    }

    [Fact]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task InitialConnection_UsesProducerDimensions()
    {
        await RunTestAsync(async page =>
        {
            await using var connection = await OpenTerminalAsync(page);
            await ExpectProducerDimensionsAsync(page);
        });
    }

    [Fact]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task ExplicitSizing_TakesPrimaryAndAppliesRequestedGrid()
    {
        await RunTestAsync(async page =>
        {
            await using var connection = await OpenTerminalAsync(page);
            await MountObserverAsync(page, requestPrimary: true);
            var primaryId = connection.Presentation.PrimaryPeerId;
            var dimensions = page.GetByRole(AriaRole.Combobox, new() { Name = "Terminal dimensions", Exact = true });
            await dimensions.ClickAsync();
            await page.GetByRole(AriaRole.Option, new() { Name = "80×24", Exact = true }).ClickAsync();
            await Assertions.Expect(dimensions).ToHaveJSPropertyAsync("value", "80x24");
            Assert.NotNull(connection.Presentation.PrimaryPeerId);
            Assert.NotEqual(primaryId, connection.Presentation.PrimaryPeerId);
            await page.WaitForFunctionAsync("() => window.terminalObserver.geometry.columns === 80 && window.terminalObserver.geometry.rows === 24").DefaultTimeout();
        });
    }

    [Theory]
    [InlineData("Decrease font size", 8, -1)]
    [InlineData("Increase font size", 32, 1)]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task FontSizeControls_TakePrimaryAndRespectPackageBounds(string name, int bound, int delta)
    {
        await RunTestAsync(async page =>
        {
            await using var connection = await OpenTerminalAsync(page);
            await MountObserverAsync(page, requestPrimary: true);
            var primaryId = connection.Presentation.PrimaryPeerId;
            var button = page.GetByRole(AriaRole.Button, new() { Name = name, Exact = true });
            for (var fontSize = 13 + delta; fontSize != bound + delta; fontSize += delta)
            {
                await button.ClickAsync();
                await Assertions.Expect(page.Locator(".terminal-font-size")).ToHaveTextAsync($"{fontSize}px");
            }
            await Assertions.Expect(button).ToBeDisabledAsync();
            Assert.NotNull(connection.Presentation.PrimaryPeerId);
            Assert.NotEqual(primaryId, connection.Presentation.PrimaryPeerId);
        });
    }

    [Fact]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task ProducerOutput_RendersInCanvasAndPublicClientSnapshot()
    {
        await RunTestAsync(async page =>
        {
            await using var connection = await OpenTerminalAsync(page);
            await MountObserverAsync(page, requestPrimary: false);
            connection.Workload.Write("\u001b[?25lready");
            await ExpectObserverTextAsync(page, "ready");
            var canvas = page.Locator(".terminal-view canvas").First;
            var before = await canvas.ScreenshotAsync();
            connection.Workload.Write("\r\nTerminal browser output is visible");
            await ExpectObserverTextAsync(page, "Terminal browser output is visible");
            await Assertions.Expect(canvas).ToBeVisibleAsync();
            await AsyncTestHelpers.AssertIsTrueRetryAsync(async () =>
            {
                var after = await canvas.ScreenshotAsync();
                return !before.AsSpan().SequenceEqual(after);
            }, "The dashboard canvas should present the producer's new output.");
        });
    }

    [Fact]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task ModifiedF6_DoesNotMoveFocusFromTerminal()
    {
        await RunTestAsync(async page =>
        {
            await using var connection = await OpenTerminalAsync(page);
            var input = page.GetByRole(AriaRole.Textbox, new() { Name = "Interactive terminal input", Exact = true });
            foreach (var key in new[] { "Control+F6", "Alt+F6", "Meta+F6" })
            {
                await input.FocusAsync();
                await page.Keyboard.PressAsync(key);
                await Assertions.Expect(input).ToBeFocusedAsync();
            }
        });
    }

    [Fact]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task HeaderCopy_CopiesFullTruncatedValuesWithIndependentFeedback()
    {
        await RunTestAsync(async page =>
        {
            await page.SetViewportSizeAsync(800, 700);
            await using var connection = await OpenTerminalAsync(page);
            await GrantClipboardPermissionsAsync(page);
            var title = string.Concat(Enumerable.Repeat("Build <main> & \u03bb project ", 10));
            const string directory = "/workspace/team/shared projects/distributed applications/services/backend/build output/project";
            await WriteMetadataAsync(page, connection, title, directory);

            var titleButton = page.Locator(".terminal-title-button");
            var directoryButton = page.Locator(".terminal-directory");
            await page.WaitForFunctionAsync("""
                () => {
                    const title = document.querySelector('.terminal-title');
                    const path = document.querySelector('.terminal-directory-display');
                    return title.scrollWidth > title.clientWidth && path.textContent.includes('\u2026');
                }
                """).DefaultTimeout();
            await Assertions.Expect(titleButton).ToHaveAttributeAsync("aria-label", $"Copy terminal title: {title}");
            await Assertions.Expect(directoryButton).ToHaveAttributeAsync("aria-label", $"Copy working directory: {directory}");

            // Freeze browser time after the real terminal and metadata have settled.
            // Overlapping copies must not reset the other button's independent 1500 ms timer.
            await page.Clock.InstallAsync(new() { Time = "2026-01-01T00:00:00Z" });
            await page.Clock.PauseAtAsync("2026-01-01T00:00:01Z");
            await titleButton.ClickAsync();
            await ExpectClipboardTextAsync(page, title);
            await Assertions.Expect(titleButton.Locator(".checkmark-icon")).ToBeVisibleAsync();
            await Assertions.Expect(directoryButton.Locator(".checkmark-icon")).ToBeHiddenAsync();

            await page.Clock.RunForAsync(500);
            await directoryButton.ClickAsync();
            await ExpectClipboardTextAsync(page, directory);
            await Assertions.Expect(titleButton.Locator(".checkmark-icon")).ToBeVisibleAsync();
            await Assertions.Expect(directoryButton.Locator(".checkmark-icon")).ToBeVisibleAsync();

            await page.Clock.RunForAsync(1000);
            await Assertions.Expect(titleButton.Locator(".checkmark-icon")).ToBeHiddenAsync();
            await Assertions.Expect(titleButton.Locator(".copy-icon")).ToBeVisibleAsync();
            await Assertions.Expect(directoryButton.Locator(".checkmark-icon")).ToBeVisibleAsync();

            await page.Clock.RunForAsync(500);
            await Assertions.Expect(directoryButton.Locator(".checkmark-icon")).ToBeHiddenAsync();
            await Assertions.Expect(directoryButton.Locator(".copy-icon")).ToBeVisibleAsync();
        });
    }

    [Theory]
    [InlineData("Enter")]
    [InlineData("Space")]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task HeaderCopy_KeyboardActivationPreservesFocusAndDoesNotForwardInput(string key)
    {
        await RunTestAsync(async page =>
        {
            await using var connection = await OpenTerminalAsync(page);
            await GrantClipboardPermissionsAsync(page);
            var title = $"Keyboard {key} <title> & \u03bb";
            const string directory = "/work/project with spaces/\u03bb";
            await WriteMetadataAsync(page, connection, title, directory);
            var titleButton = page.Locator(".terminal-title-button");
            var directoryButton = page.Locator(".terminal-directory");

            await titleButton.FocusAsync();
            await page.Keyboard.PressAsync(key);
            await ExpectClipboardTextAsync(page, title);
            await Assertions.Expect(titleButton).ToBeFocusedAsync();
            await page.Keyboard.PressAsync("Tab");
            await Assertions.Expect(directoryButton).ToBeFocusedAsync();
            await page.Keyboard.PressAsync(key);
            await ExpectClipboardTextAsync(page, directory);
            await Assertions.Expect(directoryButton).ToBeFocusedAsync();
            await page.Keyboard.PressAsync("Shift+Tab");
            await Assertions.Expect(titleButton).ToBeFocusedAsync();

            var input = page.GetByRole(AriaRole.Textbox, new() { Name = "Interactive terminal input", Exact = true });
            await input.FocusAsync();
            await page.Keyboard.TypeAsync("x");
            Assert.Equal("x", await connection.ReadInputTextAsync(1, CancellationToken.None).DefaultTimeout());
        });
    }

    [Fact]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task HeaderMetadata_LiveUpdatesAndFallbackPreserveFocusAndCopyCurrentValues()
    {
        await RunTestAsync(async page =>
        {
            await using var connection = await OpenTerminalAsync(page);
            await GrantClipboardPermissionsAsync(page);
            var titleButton = page.Locator(".terminal-title-button");
            var directoryButton = page.Locator(".terminal-directory");
            var input = page.GetByRole(AriaRole.Textbox, new() { Name = "Interactive terminal input", Exact = true });
            var inputElement = await input.ElementHandleAsync();
            Assert.NotNull(inputElement);
            await titleButton.ClickAsync();
            await ExpectClipboardTextAsync(page, ResourceName);

            await input.FocusAsync();
            await WriteMetadataAsync(page, connection, "Building <app> & \u03bb", "/work/source files");
            connection.Workload.Write("\u001b]9;4;1;42\u0007");
            await Assertions.Expect(page.Locator(".terminal-progress")).ToHaveAttributeAsync("data-state", "normal");
            await Assertions.Expect(page.Locator(".terminal-progress-percentage")).ToHaveTextAsync("42%");
            await Assertions.Expect(input).ToBeFocusedAsync();
            await titleButton.ClickAsync();
            await ExpectClipboardTextAsync(page, "Building <app> & \u03bb");
            await directoryButton.ClickAsync();
            await ExpectClipboardTextAsync(page, "/work/source files");

            await directoryButton.FocusAsync();
            await WriteMetadataAsync(page, connection, "Build failed", "/work/build output");
            connection.Workload.Write("\u001b]9;4;2;75\u0007");
            await Assertions.Expect(page.Locator(".terminal-progress")).ToHaveAttributeAsync("data-state", "error");
            await Assertions.Expect(page.Locator(".terminal-progress-percentage")).ToHaveTextAsync("75%");
            await Assertions.Expect(directoryButton).ToBeFocusedAsync();
            await page.Keyboard.PressAsync("Enter");
            await ExpectClipboardTextAsync(page, "/work/build output");
            await titleButton.ClickAsync();
            await ExpectClipboardTextAsync(page, "Build failed");

            await input.FocusAsync();
            connection.Workload.Write("\u001b]2;\u0007\u001b]9;4;0\u0007");
            await Assertions.Expect(page.Locator(".terminal-title")).ToHaveTextAsync(ResourceName);
            await Assertions.Expect(page.Locator(".terminal-progress")).ToHaveCountAsync(0);
            await Assertions.Expect(input).ToBeFocusedAsync();
            Assert.True(await inputElement.EvaluateAsync<bool>("element => element.isConnected"));
            Assert.Equal(1, connection.ConnectionCount);
            await titleButton.ClickAsync();
            await ExpectClipboardTextAsync(page, ResourceName);
        });
    }

    private static Task GrantClipboardPermissionsAsync(IPage page) =>
        page.Context.GrantPermissionsAsync(["clipboard-read", "clipboard-write"], new()
        {
            Origin = new Uri(page.Url).GetLeftPart(UriPartial.Authority)
        });

    private static Task ExpectClipboardTextAsync(IPage page, string expected) =>
        AsyncTestHelpers.AssertIsTrueRetryAsync(
            async () => await page.EvaluateAsync<string>("() => navigator.clipboard.readText()") == expected,
            $"The browser clipboard should contain the full value '{expected}'.");

    private static async Task WriteMetadataAsync(IPage page, TestTerminalConnection connection, string title, string directory)
    {
        // OSC 2;title BEL and OSC 7;file:///percent-encoded/path BEL travel through
        // the real producer, transport and browser client rather than mocked toolbar state.
        var directoryUri = new Uri("file://" + directory).AbsoluteUri;
        connection.Workload.Write($"\u001b]2;{title}\u0007\u001b]7;{directoryUri}\u0007");
        await Assertions.Expect(page.Locator(".terminal-title")).ToHaveTextAsync(title);
        await Assertions.Expect(page.Locator(".terminal-metadata")).ToHaveAttributeAsync("data-directory", directory);
    }

    private async Task<TestTerminalConnection> OpenTerminalAsync(IPage page)
    {
        await fixture.TerminalResolver.DiscardPendingConnectionsAsync();
        await page.GotoAsync($"/consolelogs/resource/{ResourceName}").DefaultTimeout();
        var connection = await fixture.TerminalResolver.AcceptConnectionAsync(CancellationToken.None).DefaultTimeout();
        await connection.WaitForPeerHandshakesAsync(CancellationToken.None).DefaultTimeout();
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Decrease font size", Exact = true })).ToBeEnabledAsync();
        return connection;
    }

    private static Task ExpectProducerDimensionsAsync(IPage page) =>
        Assertions.Expect(page.GetByRole(AriaRole.Combobox, new() { Name = "Terminal dimensions", Exact = true }))
            .ToHaveJSPropertyAsync("value", $"{TestTerminalConnection.Columns}x{TestTerminalConnection.Rows}");

    private static Task SetReadOnlyAsync(IPage page, int terminalId, TerminalViewSession session, bool readOnly)
    {
        // Match the component ordering: enforce policy on the server before changing UI.
        session.ReadOnly = readOnly;
        return page.EvaluateAsync("""
            async ({ terminalId, readOnly }) => {
                const module = await import('/Components/Controls/TerminalView.razor.js');
                module.setReadOnly(terminalId, readOnly);
            }
            """, new { terminalId, readOnly });
    }

    private static async Task ExpectReadOnlyAsync(IPage page, bool readOnly)
    {
        await page.WaitForFunctionAsync("""
            readOnly => window.moduleTerminal?.readOnly === readOnly
            """, readOnly).DefaultTimeout();
        var input = page.GetByTestId("module-terminal").Locator("textarea");
        if (readOnly)
        {
            await Assertions.Expect(input).ToBeDisabledAsync();
        }
        else
        {
            await Assertions.Expect(input).ToBeEnabledAsync();
        }
    }

    private static Task PasteAsync(ILocator input, string text) =>
        input.EvaluateAsync("""
            (element, text) => {
                const data = new DataTransfer();
                data.setData('text/plain', text);
                element.dispatchEvent(new ClipboardEvent('paste', { clipboardData: data, bubbles: true, cancelable: true }));
            }
            """, text);

    private static async Task WaitForConnectedAsync(IPage page, int terminalId)
    {
        await page.WaitForFunctionAsync("""
            id => window.terminalModule.getToolbarState(id)?.connected === true
            """, terminalId).DefaultTimeout();
    }

    private static Task<int> MountModuleAsync(IPage page, TerminalViewSession session, bool chromeless) =>
        page.EvaluateAsync<int>("""
            async ({ endpoint, viewId, readOnly, chromeless }) => {
                const module = await import('/Components/Controls/TerminalView.razor.js');
                window.terminalModule = module;
                const container = document.createElement('div');
                container.dataset.testid = 'module-terminal';
                container.style.cssText = 'position:fixed;left:0;top:0;width:700px;height:500px;z-index:10000';
                document.body.appendChild(container);
                const template = document.createElement('div');
                template.innerHTML = '<div><fluent-button aria-label="Copy">Copy</fluent-button></div>';
                template.hidden = true;
                document.body.appendChild(template);
                const footer = document.createElement('div');
                footer.tabIndex = -1;
                document.body.appendChild(footer);
                const url = new URL(endpoint, location.href);
                url.protocol = location.protocol === 'https:' ? 'wss:' : 'ws:';
                url.searchParams.set('viewId', viewId);
                // Capture the public client returned by the real mount, without replacing
                // its input implementation, to exercise direct paste/action entry points.
                const { WebTerminal } = await import('/js/hex1b-web-terminal/dist/index.min.js');
                const mount = WebTerminal.mount;
                WebTerminal.mount = async (...args) => {
                    const client = await mount.call(WebTerminal, ...args);
                    window.moduleTerminal = client;
                    return client;
                };
                try {
                    return module.initTerminal(container, url.href, null, {
                        label: 'Test terminal input', readOnly, chromeless
                    }, template, footer);
                } finally {
                    WebTerminal.mount = mount;
                }
            }
            """, new { endpoint = Endpoint, viewId = session.Id, readOnly = session.ReadOnly, chromeless });

    private static async Task MountObserverAsync(IPage page, bool requestPrimary)
    {
        await page.EvaluateAsync("""
            async ({ endpoint, requestPrimary, columns, rows }) => {
                const { WebTerminal } = await import('/js/hex1b-web-terminal/dist/index.min.js');
                const container = document.createElement('div');
                container.dataset.testid = 'observer-terminal';
                container.style.cssText = 'position:fixed;right:0;top:0;width:400px;height:200px';
                document.body.appendChild(container);
                // Use only the package's public client surface, never worker state or HWT frames.
                window.terminalObserver = await WebTerminal.mount(container, {
                    url: new URL(endpoint, location.href),
                    label: 'Observer terminal input',
                    sizing: { mode: 'fixed', columns, rows, fontSize: 13 }
                });
                if (requestPrimary) {
                    window.terminalObserver.requestPrimary();
                }
            }
            """, new { endpoint = Endpoint, requestPrimary, columns = TestTerminalConnection.Columns, rows = TestTerminalConnection.Rows });
        if (requestPrimary)
        {
            await page.WaitForFunctionAsync("() => window.terminalObserver.peer.isPrimary").DefaultTimeout();
        }
    }

    private static async Task ExpectObserverTextAsync(IPage page, string text)
    {
        await page.WaitForFunctionAsync("text => window.terminalObserver.screenText.includes(text)", text).DefaultTimeout();
    }

    public sealed class TerminalDashboardServerFixture : DashboardServerFixture
    {
        internal TestTerminalConnectionResolver TerminalResolver { get; } = new();

        protected override IReadOnlyList<ResourceViewModel> Resources =>
        [
            ModelTestHelpers.CreateResource(
                resourceName: ResourceName,
                state: KnownResourceState.Running,
                properties: new Dictionary<string, ResourcePropertyViewModel>
                {
                    [KnownProperties.Terminal.Enabled] = StringProperty(KnownProperties.Terminal.Enabled, "true"),
                    [KnownProperties.Terminal.ReplicaIndex] = StringProperty(KnownProperties.Terminal.ReplicaIndex, "0"),
                    [KnownProperties.Terminal.ReplicaCount] = StringProperty(KnownProperties.Terminal.ReplicaCount, "1"),
                })
        ];

        protected override void ConfigureServices(IServiceCollection services)
        {
            services.AddSingleton<ITerminalConnectionResolver>(_ => TerminalResolver);
        }

        private static ResourcePropertyViewModel StringProperty(string name, string value) =>
            new(name, new Value { StringValue = value }, isValueSensitive: false, knownProperty: null,
                sortOrder: 0, displayName: null, isHighlighted: false);
    }
}
