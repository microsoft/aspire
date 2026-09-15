// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Threading.Channels;
using Aspire.Dashboard.Components.Layout;
using Aspire.Dashboard.Components.Pages;
using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Telemetry;
using Aspire.Dashboard.Tests;
using Aspire.Dashboard.Tests.Shared;
using Aspire.DashboardService.Proto.V1;
using Bunit;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Layout;

public partial class TerminalDockTests
{
    [Theory]
    [InlineData("button", "User", true)]
    [InlineData("keyboard", "User", true)]
    [InlineData("activation", "AppHost", true)]
    [InlineData("snapshot", "AppHost", true)]
    [InlineData("button", "User", false)]
    [InlineData("activation", "AppHost", false)]
    public async Task Telemetry_TracksVisibleIntervalsAndOpeningTrigger(string action, string trigger, bool telemetryEnabled)
    {
        var updates = Channel.CreateUnbounded<WatchTerminalsUpdate>();
        var client = new TestDashboardClient(terminalChannelProvider: () => updates);
        TerminalSetupHelpers.SetupTerminalComponents(this, client);
        var sender = new TestDashboardTelemetrySender { IsTelemetryEnabled = telemetryEnabled };
        Services.AddSingleton<IDashboardTelemetrySender>(sender);
        var telemetryService = Services.GetRequiredService<DashboardTelemetryService>();
        await telemetryService.InitializeAsync();
        Assert.Equal(Enumerable.Repeat(TelemetryEndpoints.TelemetryPostProperty,
            telemetryEnabled ? telemetryService._defaultProperties.Count : 0), DrainTelemetryEvents(sender));
        var cut = RenderComponent<TerminalDock>();
        Assert.Null(cut.Instance.TelemetryContext);

        var renderCount = cut.RenderCount;
        await updates.Writer.WriteAsync(TerminalSetupHelpers.Snapshot("first", "second"));
        cut.WaitForAssertion(() => Assert.True(cut.RenderCount > renderCount));
        Assert.Null(cut.Instance.TelemetryContext);
        Assert.Empty(DrainTelemetryEvents(sender));

        switch (action)
        {
            case "button":
                await cut.InvokeAsync(cut.Instance.ToggleAsync);
                break;
            case "keyboard":
                await Services.GetRequiredService<ShortcutManager>().OnGlobalKeyDown(AspireKeyboardShortcut.ToggleTerminalDock);
                break;
            case "activation":
                await updates.Writer.WriteAsync(TerminalSetupHelpers.Change(TerminalChangeType.Activated, "first"));
                break;
            case "snapshot":
                var snapshot = TerminalSetupHelpers.Snapshot("first", "second");
                snapshot.Snapshot.ActivatedTerminalId = "first";
                await updates.Writer.WriteAsync(snapshot);
                break;
        }

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".terminal-dock:not(.collapsed)")));
        var firstContext = Assert.IsType<ComponentTelemetryContext>(cut.Instance.TelemetryContext);
        AssertDockTelemetryProperties(firstContext, trigger);
        Assert.Equal(telemetryEnabled ? new[]
        {
            "/telemetry/userTask - $aspire/dashboard/component/initialize",
            "/telemetry/operation - $aspire/dashboard/component/paramsSet"
        } : [], DrainTelemetryEvents(sender));

        await updates.Writer.WriteAsync(TerminalSetupHelpers.Change(TerminalChangeType.Activated, "second"));
        cut.WaitForAssertion(() => Assert.Equal("second", cut.Find("[role=tab][aria-selected=true]").TextContent.Trim()));
        await cut.FindAll("[role=tab]")[0].ClickAsync(new());
        Assert.Equal("first", cut.Find("[role=tab][aria-selected=true]").TextContent.Trim());
        var recovery = TerminalSetupHelpers.Snapshot("first", "second");
        recovery.Snapshot.ActivatedTerminalId = "second";
        await updates.Writer.WriteAsync(recovery);
        cut.WaitForAssertion(() => Assert.Equal("second", cut.Find("[role=tab][aria-selected=true]").TextContent.Trim()));
        Assert.Same(firstContext, cut.Instance.TelemetryContext);
        AssertDockTelemetryProperties(firstContext, trigger);
        Assert.Empty(DrainTelemetryEvents(sender));

        await cut.InvokeAsync(cut.Instance.ToggleAsync);
        Assert.Null(cut.Instance.TelemetryContext);
        Assert.Equal(telemetryEnabled ? new[] { "/telemetry/operation - $aspire/dashboard/component/dispose" } : [],
            DrainTelemetryEvents(sender));

        if (trigger == "User")
        {
            await updates.Writer.WriteAsync(recovery);
        }
        else
        {
            await cut.InvokeAsync(cut.Instance.ToggleAsync);
        }
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".terminal-dock:not(.collapsed)")));
        var secondContext = Assert.IsType<ComponentTelemetryContext>(cut.Instance.TelemetryContext);
        Assert.NotSame(firstContext, secondContext);
        AssertDockTelemetryProperties(secondContext, trigger == "User" ? "AppHost" : "User");
        await cut.InvokeAsync(() => cut.Instance.DisposeAsync().AsTask()).DefaultTimeout();
        await cut.InvokeAsync(() => cut.Instance.DisposeAsync().AsTask()).DefaultTimeout();
        await cut.InvokeAsync(cut.Instance.ToggleAsync);
        Assert.Null(cut.Instance.TelemetryContext);
        Assert.Equal(telemetryEnabled ? new[]
        {
            "/telemetry/userTask - $aspire/dashboard/component/initialize",
            "/telemetry/operation - $aspire/dashboard/component/paramsSet",
            "/telemetry/operation - $aspire/dashboard/component/dispose"
        } : [], DrainTelemetryEvents(sender));
    }

    private static void AssertDockTelemetryProperties(ComponentTelemetryContext context, string trigger)
    {
        (string, string)[] expected =
        [
            ("Aspire.Dashboard.ComponentId", "TerminalDock"),
            ("Aspire.Dashboard.ComponentType", "Control"),
            ("Aspire.Dashboard.TerminalDock.Trigger", trigger)
        ];
        Assert.Equal(expected, context.Properties.OrderBy(p => p.Key)
            .Select(p => (p.Key, Assert.IsType<string>(p.Value.Value))));
    }

    private static string[] DrainTelemetryEvents(TestDashboardTelemetrySender sender)
    {
        List<string> events = [];
        while (sender.ContextChannel.Reader.TryRead(out var operation))
        {
            events.Add(operation.Name);
        }
        return events.ToArray();
    }
}
