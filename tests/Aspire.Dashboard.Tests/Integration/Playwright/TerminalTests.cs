// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using System.Text.Json;
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

    [Fact]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task TerminalFocusNavigation_MovesToExpectedControlsWithoutForwardingInput()
    {
        await RunTestAsync(async page =>
        {
            await using var connection = await OpenTerminalAsync(page);

            var terminalScreen = page.Locator(".xterm-screen");
            var decreaseFontButton = page.Locator("#font-minus");
            var settingsButton = page.Locator($"fluent-button[title='{Dashboard.Resources.ConsoleLogs.ConsoleLogsSettings}'][aria-haspopup='menu']").First;

            await Assertions.Expect(terminalScreen).ToBeVisibleAsync();
            await Assertions.Expect(decreaseFontButton).ToBeEnabledAsync();

            await terminalScreen.ClickAsync();
            await page.Keyboard.PressAsync("F6");
            Assert.Equal("font-minus", await page.EvaluateAsync<string?>("() => document.activeElement?.id"));

            var settingsButtonId = await settingsButton.GetAttributeAsync("id");
            Assert.False(string.IsNullOrEmpty(settingsButtonId));

            await terminalScreen.ClickAsync();
            await page.Keyboard.PressAsync("Shift+F6");
            Assert.Equal(settingsButtonId, await page.EvaluateAsync<string?>("() => document.activeElement?.id"));

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

    private async Task<TestTerminalConnection> OpenTerminalAsync(IPage page)
    {
        await _dashboardServerFixture.TerminalResolver.DiscardPendingConnectionsAsync();
        await page.GotoAsync($"/consolelogs/resource/{ResourceName}").DefaultTimeout();

        var connection = await _dashboardServerFixture.TerminalResolver.AcceptConnectionAsync(CancellationToken.None).DefaultTimeout();
        var clientHello = await connection.ReadUntilFrameAsync(TestHmp1FrameType.ClientHello, CancellationToken.None).DefaultTimeout();
        Assert.NotEmpty(clientHello.Payload);

        await connection.SendHelloAsync(ProducerColumns, ProducerRows, CancellationToken.None).DefaultTimeout();
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
