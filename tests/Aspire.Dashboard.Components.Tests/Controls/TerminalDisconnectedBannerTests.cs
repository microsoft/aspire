// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Threading.Channels;
using Aspire.Dashboard.Components.Controls;
using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Tests.Shared;
using Aspire.Tests.Shared.DashboardModel;
using Bunit;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FluentUI.AspNetCore.Components;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Controls;

[UseCulture("en-US")]
public class TerminalDisconnectedBannerTests : DashboardTestContext
{
    [Theory]
    [InlineData("0", "Terminal disconnected (exit code 0). The last output has been preserved.", "Terminal disconnected (exit code 0). Show recovery actions")]
    [InlineData("42", "Terminal disconnected (exit code 42). The last output has been preserved.", "Terminal disconnected (exit code 42). Show recovery actions")]
    [InlineData("-1", "Terminal disconnected (exit code -1). The last output has been preserved.", "Terminal disconnected (exit code -1). Show recovery actions")]
    [InlineData(null, "Terminal disconnected. The last output has been preserved.", "Terminal disconnected. Show recovery actions")]
    public async Task Message_TracksReportedExitCodeAndClearsWhenResourceIsRemoved(string? exitCode, string message, string label)
    {
        var updates = Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>();
        var initial = ModelTestHelpers.CreateResource(resourceName: "shell");
        var client = new TestDashboardClient(isEnabled: true, initialResources: [initial], resourceChannelProvider: () => updates);
        TerminalSetupHelpers.SetupTerminalComponents(this, client);
        var cut = Render<TerminalDisconnectedBanner>(builder => builder.Add(p => p.ResourceName, "shell"));
        cut.WaitForAssertion(() => Assert.Equal(1, client.ResourceSubscriptionCount));
        Assert.Equal("Terminal disconnected. The last output has been preserved.", cut.Find("[role=status]").TextContent);

        var resource = ModelTestHelpers.CreateResource(resourceName: "shell", state: KnownResourceState.Finished, properties: exitCode is null ? [] : new()
        {
            [KnownProperties.Resource.ExitCode] = new(KnownProperties.Resource.ExitCode, Value.ForString(exitCode),
                isValueSensitive: false, knownProperty: null, sortOrder: 0, displayName: null, isHighlighted: false)
        });
        await updates.Writer.WriteAsync([new(ResourceViewModelChangeType.Upsert, resource)]);
        cut.WaitForAssertion(() => Assert.Equal(message, cut.Find("[role=status]").TextContent));
        await cut.Find(".terminal-show-output").ClickAsync(new());
        Assert.Equal(label, cut.Find(".terminal-show-banner").GetAttribute("aria-label"));
        Assert.Equal(label, cut.Find(".terminal-show-banner").GetAttribute("title"));

        await updates.Writer.WriteAsync([new(ResourceViewModelChangeType.Delete, resource)]);
        cut.WaitForAssertion(() => Assert.Equal("Terminal disconnected. Show recovery actions", cut.Find(".terminal-show-banner").GetAttribute("aria-label")));
        await cut.Find(".terminal-show-banner").ClickAsync(new());
        Assert.Equal("Terminal disconnected. The last output has been preserved.", cut.Find("[role=status]").TextContent);
    }

    [Theory]
    [InlineData(CommandViewModel.StartCommand, ResourceCommandResponseKind.Succeeded)]
    [InlineData(CommandViewModel.RestartCommand, ResourceCommandResponseKind.Succeeded)]
    [InlineData(CommandViewModel.StartCommand, ResourceCommandResponseKind.Failed)]
    public async Task Restart_ExecutesAdvertisedCommandForExactReplicaAndReportsResult(string commandName, ResourceCommandResponseKind resultKind)
    {
        var executions = new ConcurrentQueue<(string Resource, string Command)>();
        var response = new TaskCompletionSource<ResourceCommandResponseViewModel>(TaskCreationOptions.RunContinuationsAsynchronously);
        var resource = ModelTestHelpers.CreateResource(resourceName: "shell-replica",
            displayName: "shell", commands: [CreateCommand(commandName)]);
        var client = new TestDashboardClient(isEnabled: true, initialResources: [resource],
            resourceChannelProvider: () => Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>(),
            executeResourceCommand: (name, _, command, _, _) =>
            {
                executions.Enqueue((name, command.Name));
                return response.Task;
            });
        TerminalSetupHelpers.SetupTerminalComponents(this, client);
        Render<FluentToastProvider>();
        var cut = Render<TerminalDisconnectedBanner>(builder => builder.Add(p => p.ResourceName, "shell-replica"));
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".terminal-restart-resource")));
        Assert.Equal("Restart resource", cut.Find(".terminal-restart-resource").TextContent.Trim());
        Assert.Equal("Terminal disconnected. The last output has been preserved.", cut.Find("[role=status]").TextContent);

        var click = cut.InvokeAsync(() => cut.Find(".terminal-restart-resource").ClickAsync(new()));
        cut.WaitForAssertion(() => Assert.Equal([("shell-replica", commandName)], executions.ToArray()));
        Assert.True(cut.FindComponents<FluentButton>().Single(b => b.Instance.Class == "terminal-restart-resource").Instance.Disabled);
        response.SetResult(new ResourceCommandResponseViewModel { Kind = resultKind, Message = "Command result" });
        await click;
        Assert.False(cut.FindComponents<FluentButton>().Single(b => b.Instance.Class == "terminal-restart-resource").Instance.Disabled);
        var notification = Assert.Single(Services.GetRequiredService<Dashboard.Model.INotificationService>().GetNotifications()).Entry;
        Assert.Equal(resultKind == ResourceCommandResponseKind.Succeeded ? MessageBarIntent.Success : MessageBarIntent.Error,
            notification.Intent);
        Assert.Single(cut.FindAll("[role=status]"));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Restart_ReadOnlyViewsDoNotOfferResourceMutation(bool readOnly, bool clientReadOnly)
    {
        var resource = ModelTestHelpers.CreateResource(resourceName: "shell",
            commands: [CreateCommand(CommandViewModel.RestartCommand)]);
        var client = new TestDashboardClient(isEnabled: true, isReadOnly: clientReadOnly, initialResources: [resource],
            resourceChannelProvider: () => Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>());
        TerminalSetupHelpers.SetupTerminalComponents(this, client);
        var cut = Render<TerminalDisconnectedBanner>(builder => builder
            .Add(p => p.ResourceName, "shell")
            .Add(p => p.ReadOnly, readOnly));
        cut.WaitForAssertion(() => Assert.Equal(1, client.ResourceSubscriptionCount));
        Assert.Equal(readOnly || clientReadOnly ? 0 : 1, cut.FindAll(".terminal-restart-resource").Count);
    }

    [Fact]
    public async Task Restart_TracksCommandAvailabilityAndResourceRemoval()
    {
        var updates = Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>();
        var resource = ModelTestHelpers.CreateResource(resourceName: "shell", commands:
            [CreateCommand(CommandViewModel.RestartCommand, CommandViewModelState.Hidden)]);
        var client = new TestDashboardClient(isEnabled: true, initialResources: [resource], resourceChannelProvider: () => updates);
        TerminalSetupHelpers.SetupTerminalComponents(this, client);
        var cut = Render<TerminalDisconnectedBanner>(builder => builder.Add(p => p.ResourceName, "shell"));
        cut.WaitForAssertion(() => Assert.Equal(1, client.ResourceSubscriptionCount));
        Assert.Empty(cut.FindAll(".terminal-restart-resource"));

        await updates.Writer.WriteAsync([new(ResourceViewModelChangeType.Upsert, ModelTestHelpers.CreateResource(resourceName: "shell",
            commands: [CreateCommand(CommandViewModel.RestartCommand, CommandViewModelState.Disabled)]))]);
        cut.WaitForAssertion(() => Assert.True(cut.FindComponents<FluentButton>().Single(b => b.Instance.Class == "terminal-restart-resource").Instance.Disabled));

        await updates.Writer.WriteAsync([new(ResourceViewModelChangeType.Upsert, ModelTestHelpers.CreateResource(resourceName: "shell",
            commands: [CreateCommand(CommandViewModel.RestartCommand, CommandViewModelState.Disabled),
                CreateCommand(CommandViewModel.StartCommand)]))]);
        cut.WaitForAssertion(() => Assert.False(cut.FindComponents<FluentButton>().Single(b => b.Instance.Class == "terminal-restart-resource").Instance.Disabled));

        await updates.Writer.WriteAsync([new(ResourceViewModelChangeType.Delete, resource)]);
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".terminal-restart-resource")));
        Assert.Single(cut.FindAll("[role=status]"));
    }

    [Fact]
    public async Task Close_InvokesOwningSurfaceWithoutSubscribingToResources()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var closeCount = 0;
        var client = TerminalSetupHelpers.CreateTerminalDashboardClient();
        TerminalSetupHelpers.SetupTerminalComponents(this, client);
        var cut = Render<TerminalDisconnectedBanner>(builder => builder.Add(p => p.OnClose,
            EventCallback.Factory.Create(this, () =>
            {
                closeCount++;
                return completion.Task;
            })));
        Assert.Equal("Close tab", cut.Find(".terminal-close-tab").TextContent.Trim());
        var click = cut.InvokeAsync(() => cut.Find(".terminal-close-tab").ClickAsync(new()));
        cut.WaitForAssertion(() => Assert.Equal(1, closeCount));
        Assert.True(cut.FindComponents<FluentButton>().Single(b => b.Instance.Class == "terminal-close-tab").Instance.Disabled);
        completion.SetResult();
        await click;
        Assert.False(cut.FindComponents<FluentButton>().Single(b => b.Instance.Class == "terminal-close-tab").Instance.Disabled);
        Assert.Equal(0, client.ResourceSubscriptionCount);
    }

    private static CommandViewModel CreateCommand(string name, CommandViewModelState state = CommandViewModelState.Enabled) =>
        new(name, state, name, string.Empty, string.Empty, [], false, "ArrowClockwise", IconVariant.Regular);
}
