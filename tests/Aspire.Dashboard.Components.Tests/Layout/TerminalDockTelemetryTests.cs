// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Threading.Channels;
using Aspire.Dashboard.Components.Layout;
using Aspire.Dashboard.Components.Pages;
using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Telemetry;
using Aspire.Dashboard.Tests;
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
        var client = TerminalSetupHelpers.CreateTerminalDashboardClient(terminalChannelProvider: () => updates);
        var processed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.OnTerminalUpdateProcessed = _ => processed.TrySetResult();
        TerminalSetupHelpers.SetupTerminalComponents(this, client);
        using var fixture = new DashboardTelemetryFixture(reportedTelemetryEnabled: telemetryEnabled);
        Services.AddSingleton(fixture.Telemetry);
        Assert.Empty(DrainTelemetryEvents(fixture));
        var cut = Render<TerminalDock>();
        Assert.Null(cut.Instance.TelemetryContext);

        var renderCount = cut.RenderCount;
        await updates.Writer.WriteAsync(TerminalSetupHelpers.Snapshot("first", "second"));
        await processed.Task.DefaultTimeout();
        Assert.Equal(renderCount, cut.RenderCount);
        Assert.Null(cut.Instance.TelemetryContext);
        Assert.Empty(DrainTelemetryEvents(fixture));

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
            TelemetryEventKeys.ComponentInitialize,
            TelemetryEventKeys.ParametersSet
        } : [], DrainTelemetryEvents(fixture));

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
        Assert.Empty(DrainTelemetryEvents(fixture));

        await cut.InvokeAsync(cut.Instance.ToggleAsync);
        Assert.Null(cut.Instance.TelemetryContext);
        Assert.Equal(telemetryEnabled ? new[] { TelemetryEventKeys.ComponentDispose } : [],
            DrainTelemetryEvents(fixture));

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
            TelemetryEventKeys.ComponentInitialize,
            TelemetryEventKeys.ParametersSet,
            TelemetryEventKeys.ComponentDispose
        } : [], DrainTelemetryEvents(fixture));
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

    private static string[] DrainTelemetryEvents(DashboardTelemetryFixture fixture)
    {
        List<string> events = [];
        while (fixture.LogChannel.Reader.TryRead(out var log))
        {
            events.Add(Assert.IsType<string>(log.Message));
        }
        return events.ToArray();
    }
}
