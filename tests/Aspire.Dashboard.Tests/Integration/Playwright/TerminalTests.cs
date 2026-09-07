// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
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
public sealed class TerminalTests : PlaywrightTestsBase<TerminalTests.TerminalDashboardServerFixture>
{
    private const string ResourceName = "terminal-resource";
    private const int ProducerColumns = 137;
    private const int ProducerRows = 41;
    private readonly TerminalDashboardServerFixture _dashboardServerFixture;

    public TerminalTests(TerminalDashboardServerFixture dashboardServerFixture)
        : base(dashboardServerFixture)
    {
        _dashboardServerFixture = dashboardServerFixture;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task AppHostWorkloadEnded_DisablesInputAndReconnectUntilEndpointChanges(bool beforeHandshake)
    {
        await RunTestAsync(async page =>
        {
            await page.GotoAsync("/").DefaultTimeout();
            await page.Clock.InstallAsync();
            var connections = Channel.CreateUnbounded<IWebSocketRoute>();
            var connectionCount = 0;
            await page.RouteWebSocketAsync("**/api/apphost-terminal?*", route =>
            {
                Interlocked.Increment(ref connectionCount);
                connections.Writer.TryWrite(route);
            });
            var terminalId = await page.EvaluateAsync<int>("""
                async () => {
                    const module = await import('/Components/Controls/TerminalView.razor.js');
                    const container = document.createElement('div');
                    container.style.cssText = 'position:fixed;inset:0;z-index:10000';
                    document.body.appendChild(container);
                    const endpoint = new URL('/api/apphost-terminal?terminalId=ended', location.href);
                    endpoint.protocol = location.protocol === 'https:' ? 'wss:' : 'ws:';
                    return await module.initTerminal(container, endpoint.href, null, {
                        chromeless: true,
                        terminalEnded: 'Terminal ended'
                    });
                }
                """);
            var connection = await connections.Reader.ReadAsync().AsTask().DefaultTimeout();
            if (!beforeHandshake)
            {
                var payload = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    peerId = "viewer",
                    width = ProducerColumns,
                    height = ProducerRows
                });
                var hello = new byte[5 + payload.Length];
                hello[0] = (byte)TestHmp1FrameType.Hello;
                BinaryPrimitives.WriteInt32LittleEndian(hello.AsSpan(1), payload.Length);
                payload.CopyTo(hello.AsSpan(5));
                connection.Send(hello);
                await page.WaitForFunctionAsync("""
                    async id => {
                        const module = await import('/Components/Controls/TerminalView.razor.js');
                        return module.getToolbarState(id)?.connected === true;
                    }
                    """, terminalId).DefaultTimeout();
            }

            connection.Send("terminal-ended");
            var terminalInput = page.Locator(".xterm-helper-textarea");
            await Assertions.Expect(terminalInput).ToHaveAttributeAsync("aria-readonly", "true");
            await Assertions.Expect(page.GetByRole(AriaRole.Status).Filter(new() { HasText = "Terminal ended" }))
                .ToBeVisibleAsync();
            await SetReadOnlyAsync(page, terminalId, false);
            await Assertions.Expect(terminalInput).ToHaveAttributeAsync("aria-readonly", "true");
            await page.Clock.RunForAsync(5_000);
            Assert.Equal(1, Volatile.Read(ref connectionCount));
            Assert.Equal("ended", await page.EvaluateAsync<string>("""
                async id => {
                    const module = await import('/Components/Controls/TerminalView.razor.js');
                    return module.getToolbarState(id).status;
                }
                """, terminalId));

            await page.EvaluateAsync("""
                async id => {
                    const module = await import('/Components/Controls/TerminalView.razor.js');
                    const endpoint = new URL('/api/apphost-terminal?terminalId=next', location.href);
                    endpoint.protocol = location.protocol === 'https:' ? 'wss:' : 'ws:';
                    module.reconnectTerminal(id, endpoint.href);
                }
                """, terminalId);
            await connections.Reader.ReadAsync().AsTask().DefaultTimeout();
            await Assertions.Expect(terminalInput).ToHaveAttributeAsync("aria-readonly", "false");
        });
    }

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
            await _dashboardServerFixture.TerminalResolver.DiscardPendingConnectionsAsync();
            await page.GotoAsync("/").DefaultTimeout();
            var terminalId = await page.EvaluateAsync<int>("""
                async ({ resourceName, initialReadOnly, chromeless }) => {
                    const module = await import('/Components/Controls/TerminalView.razor.js');
                    const container = document.createElement('div');
                    container.style.cssText = 'position:fixed;inset:0;z-index:10000';
                    document.body.appendChild(container);
                    const endpoint = new URL('/api/terminal', location.href);
                    endpoint.protocol = location.protocol === 'https:' ? 'wss:' : 'ws:';
                    endpoint.searchParams.set('resource', resourceName);
                    endpoint.searchParams.set('replica', '0');
                    return await module.initTerminal(container, endpoint.href, null, { readOnly: initialReadOnly, chromeless });
                }
                """, new { resourceName = ResourceName, initialReadOnly, chromeless });

            await using var connection = await _dashboardServerFixture.TerminalResolver.AcceptConnectionAsync(CancellationToken.None).DefaultTimeout();
            await connection.ReadUntilFrameAsync(TestHmp1FrameType.ClientHello, CancellationToken.None).DefaultTimeout();
            await connection.SendHelloAsync(ProducerColumns, ProducerRows, CancellationToken.None).DefaultTimeout();
            await connection.SendStateSyncAsync(CancellationToken.None).DefaultTimeout();
            await page.WaitForFunctionAsync("""
                async id => {
                    const module = await import('/Components/Controls/TerminalView.razor.js');
                    return module.getToolbarState(id)?.role === 'secondary';
                }
                """, terminalId).DefaultTimeout();
            if (chromeless && !initialReadOnly)
            {
                Assert.Equal(TestHmp1FrameType.RequestPrimary, (await connection.ReadFrameAsync(CancellationToken.None).DefaultTimeout()).Type);
            }

            var terminalInput = page.Locator(".xterm-helper-textarea");
            await Assertions.Expect(terminalInput).ToHaveAttributeAsync("aria-readonly", initialReadOnly ? "true" : "false");
            if (!initialReadOnly)
            {
                await SetReadOnlyAsync(page, terminalId, true);
            }

            await Assertions.Expect(terminalInput).ToHaveAttributeAsync("aria-readonly", "true");
            await connection.SendOutputAsync("Output while read-only\r\n", CancellationToken.None).DefaultTimeout();
            await Assertions.Expect(page.Locator(".xterm-rows")).ToContainTextAsync("Output while read-only");

            await terminalInput.FocusAsync();
            await page.Keyboard.TypeAsync("blocked-keyboard");
            await PasteAsync(terminalInput, "blocked-paste");
            await page.Keyboard.PressAsync("F6");
            await Assertions.Expect(page.Locator("#font-minus")).ToBeFocusedAsync();
            await page.Locator("#font-plus").ClickAsync();

            await SetReadOnlyAsync(page, terminalId, false);
            await Assertions.Expect(terminalInput).ToHaveAttributeAsync("aria-readonly", "false");
            await terminalInput.FocusAsync();
            await page.Keyboard.TypeAsync("x");
            await PasteAsync(terminalInput, "allowed-paste");

            // HMP preserves frame ordering. Only enabling a chromeless viewer and enabled input can request primary.
            // Disabled typing, paste and font controls must not claim control from an automation peer.
            if (chromeless)
            {
                Assert.Equal(TestHmp1FrameType.RequestPrimary, (await connection.ReadFrameAsync(CancellationToken.None).DefaultTimeout()).Type);
            }
            Assert.Equal(TestHmp1FrameType.RequestPrimary, (await connection.ReadFrameAsync(CancellationToken.None).DefaultTimeout()).Type);
            var keyboard = await connection.ReadFrameAsync(CancellationToken.None).DefaultTimeout();
            Assert.Equal(TestHmp1FrameType.Input, keyboard.Type);
            Assert.Equal(TestHmp1FrameType.RequestPrimary, (await connection.ReadFrameAsync(CancellationToken.None).DefaultTimeout()).Type);
            var paste = await connection.ReadFrameAsync(CancellationToken.None).DefaultTimeout();
            Assert.Equal(TestHmp1FrameType.Input, paste.Type);
            Assert.Equal("x", Encoding.UTF8.GetString(keyboard.Payload));
            Assert.Equal("allowed-paste", Encoding.UTF8.GetString(paste.Payload));
        });
    }

    private static Task SetReadOnlyAsync(IPage page, int terminalId, bool readOnly) =>
        page.EvaluateAsync("""
            async ({ terminalId, readOnly }) => {
                const module = await import('/Components/Controls/TerminalView.razor.js');
                module.setReadOnly(terminalId, readOnly);
            }
            """, new { terminalId, readOnly });

    private static Task PasteAsync(ILocator terminalInput, string text) =>
        terminalInput.EvaluateAsync("""
            (element, text) => {
                const data = new DataTransfer();
                data.setData('text/plain', text);
                element.dispatchEvent(new ClipboardEvent('paste', { clipboardData: data, bubbles: true, cancelable: true }));
            }
            """, text);

    [Fact]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task TerminalFocusNavigation_MovesToExpectedControlsWithoutForwardingInput()
    {
        await RunTestAsync(async page =>
        {
            await using var connection = await OpenTerminalAsync(page);

            var terminalScreen = page.Locator(".xterm-screen");
            var decreaseFontButton = page.Locator("#font-minus");
            var resourceSelect = page.Locator("[id^='resource-select-']");

            await Assertions.Expect(terminalScreen).ToBeVisibleAsync();
            await Assertions.Expect(decreaseFontButton).ToBeEnabledAsync();

            await terminalScreen.ClickAsync();
            await page.Keyboard.PressAsync("F6");
            Assert.Equal("font-minus", await page.EvaluateAsync<string?>("() => document.activeElement?.id"));

            var resourceSelectId = await resourceSelect.GetAttributeAsync("id");
            Assert.False(string.IsNullOrEmpty(resourceSelectId));

            await terminalScreen.ClickAsync();
            await page.Keyboard.PressAsync("Shift+F6");
            Assert.Equal(resourceSelectId, await page.EvaluateAsync<string?>("() => document.activeElement?.id"));

            // Follow the intercepted F6 events with ordinary input. HMP preserves
            // frame ordering, so the first Input frame must be this character; an
            // earlier F6 escape sequence would make the assertion fail.
            await terminalScreen.ClickAsync();
            await page.Keyboard.TypeAsync("x");

            var input = await connection.ReadUntilFrameAsync(TestHmp1FrameType.Input, CancellationToken.None).DefaultTimeout();
            Assert.Equal("x", Encoding.UTF8.GetString(input.Payload));
        });
    }

    [Fact]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task SecondaryTyping_RequestsPrimaryAtProducerDimensions()
    {
        await RunTestAsync(async page =>
        {
            await using var connection = await OpenTerminalAsync(page);

            var dimensions = page.Locator("#terminal-dims");
            await Assertions.Expect(dimensions).ToHaveValueAsync($"{ProducerColumns}x{ProducerRows}");

            var terminalScreen = page.Locator(".xterm-screen");
            await terminalScreen.ClickAsync();
            await page.Keyboard.TypeAsync("x");

            var requestPrimary = await connection.ReadUntilFrameAsync(TestHmp1FrameType.RequestPrimary, CancellationToken.None).DefaultTimeout();
            using var payload = JsonDocument.Parse(requestPrimary.Payload);
            Assert.Equal(ProducerColumns, payload.RootElement.GetProperty("cols").GetInt32());
            Assert.Equal(ProducerRows, payload.RootElement.GetProperty("rows").GetInt32());

            var input = await connection.ReadUntilFrameAsync(TestHmp1FrameType.Input, CancellationToken.None).DefaultTimeout();
            Assert.Equal("x", Encoding.UTF8.GetString(input.Payload));
        });
    }

    [Fact]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task InitialPrimaryHello_UsesProducerDimensions()
    {
        await RunTestAsync(async page =>
        {
            await using var connection = await OpenTerminalAsync(page, makeClientPrimary: true);

            var dimensions = page.Locator("#terminal-dims");
            await Assertions.Expect(dimensions).ToHaveValueAsync($"{ProducerColumns}x{ProducerRows}");
        });
    }

    [Fact]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task ModifiedF6_DoesNotMoveFocusFromTerminal()
    {
        await RunTestAsync(async page =>
        {
            await using var connection = await OpenTerminalAsync(page);

            var terminalScreen = page.Locator(".xterm-screen");
            var terminalInput = page.Locator(".xterm-helper-textarea");
            foreach (var key in new[] { "Control+F6", "Alt+F6", "Meta+F6" })
            {
                await terminalScreen.ClickAsync();
                await page.Keyboard.PressAsync(key);

                await Assertions.Expect(terminalInput).ToBeFocusedAsync();
            }
        });
    }

    private async Task<TestTerminalConnection> OpenTerminalAsync(IPage page, bool makeClientPrimary = false)
    {
        await _dashboardServerFixture.TerminalResolver.DiscardPendingConnectionsAsync();
        await page.GotoAsync($"/consolelogs/resource/{ResourceName}").DefaultTimeout();

        var connection = await _dashboardServerFixture.TerminalResolver.AcceptConnectionAsync(CancellationToken.None).DefaultTimeout();
        var clientHello = await connection.ReadUntilFrameAsync(TestHmp1FrameType.ClientHello, CancellationToken.None).DefaultTimeout();
        Assert.NotEmpty(clientHello.Payload);

        await connection.SendHelloAsync(
            ProducerColumns,
            ProducerRows,
            CancellationToken.None,
            makeClientPrimary).DefaultTimeout();
        await connection.SendStateSyncAsync(CancellationToken.None).DefaultTimeout();
        return connection;
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
            services.AddSingleton<ITerminalConnectionResolver>(TerminalResolver);
        }

        private static ResourcePropertyViewModel StringProperty(string name, string value)
        {
            return new ResourcePropertyViewModel(
                name,
                new Value { StringValue = value },
                isValueSensitive: false,
                knownProperty: null,
                sortOrder: 0,
                displayName: null,
                isHighlighted: false);
        }
    }
}
