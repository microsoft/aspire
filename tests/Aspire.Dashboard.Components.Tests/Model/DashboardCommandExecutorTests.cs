// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Tests.Shared;
using Aspire.Dashboard.Utils;
using Aspire.Tests.Shared.DashboardModel;
using Aspire.Tests.Shared;
using Microsoft.AspNetCore.InternalTesting;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FluentUI.AspNetCore.Components;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Model;

public class DashboardCommandExecutorTests : DashboardTestContext
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsyncCore_AppHostRestartsDuringConfirmation_DoesNotDispatchOldCommand(bool restart)
    {
        var calls = 0;
        var client = new TestDashboardClient(isEnabled: true, executeResourceCommand: (_, _, _, options, _) =>
        {
            Interlocked.Increment(ref calls);
            Assert.Equal("first", options.ExpectedAppHostRunId);
            return Task.FromResult(new ResourceCommandResponseViewModel { Kind = ResourceCommandResponseKind.Succeeded });
        }) { AppHostRunId = "first" };
        var shown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dialogs = new TestDialogService(onShowDialog: (_, _) =>
        {
            shown.TrySetResult();
            return Task.CompletedTask;
        });
        FluentUISetupHelpers.AddCommonDashboardServices(this);
        FluentUISetupHelpers.SetupFluentUIComponents(this);
        Services.AddSingleton<IDashboardClient>(client);
        Services.AddSingleton<Microsoft.FluentUI.AspNetCore.Components.IDialogService>(dialogs);
        Services.AddScoped<DashboardCommandExecutor>();
        var toasts = Render<FluentToastProvider>();
        var executor = Services.GetRequiredService<DashboardCommandExecutor>();
        var command = new CommandViewModel("delete", CommandViewModelState.Enabled, "Delete", "", "Confirm deletion?", [], false, "", IconVariant.Regular);
        var resource = ModelTestHelpers.CreateResource(resourceName: "api", commands: [command]);
        Task<bool>? execution = null;
        await toasts.InvokeAsync(() => { execution = executor.ExecuteAsyncCore(resource, command, r => r.DisplayName); });
        await shown.Task.DefaultTimeout();
        if (restart)
        {
            client.AppHostRunId = "second";
        }
        await dialogs.LastInstance!.CloseAsync(DialogResult.Ok(true));
        Assert.Equal(!restart, await execution!.DefaultTimeout());
        Assert.Equal(restart ? 0 : 1, calls);
    }

    [Fact]
    public async Task ExecuteAsyncCore_ConfirmationCompletesOffline_FinalizesCancelledResult()
    {
        var client = new TestDashboardClient(isEnabled: true, executeResourceCommand: (_, _, _, _, _) =>
            Task.FromResult(new ResourceCommandResponseViewModel { Kind = ResourceCommandResponseKind.Cancelled }));
        var shown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dialogs = new TestDialogService(onShowDialog: (_, _) =>
        {
            shown.TrySetResult();
            return Task.CompletedTask;
        });
        FluentUISetupHelpers.AddCommonDashboardServices(this);
        FluentUISetupHelpers.SetupFluentUIComponents(this);
        Services.AddSingleton<IDashboardClient>(client);
        Services.AddSingleton<Microsoft.FluentUI.AspNetCore.Components.IDialogService>(dialogs);
        Services.AddScoped<DashboardCommandExecutor>();
        var toasts = Render<FluentToastProvider>();
        var executor = Services.GetRequiredService<DashboardCommandExecutor>();
        var command = new CommandViewModel("delete", CommandViewModelState.Enabled, "Delete", "", "Confirm deletion?", [], false, "", IconVariant.Regular);
        var resource = ModelTestHelpers.CreateResource(resourceName: "api", commands: [command]);
        Task<bool>? execution = null;
        await toasts.InvokeAsync(() => { execution = executor.ExecuteAsyncCore(resource, command, r => r.DisplayName); });
        await shown.Task.DefaultTimeout();
        client.SetConnectionState(DashboardConnectionState.Disconnected);

        await dialogs.LastInstance!.CloseAsync(DialogResult.Ok(true));

        Assert.False(await execution!.DefaultTimeout());
        var notification = Assert.Single(Services.GetRequiredService<Aspire.Dashboard.Model.INotificationService>().GetNotifications());
        Assert.Null(notification.Entry.PrimaryAction);
    }

    [Theory]
    [InlineData("Playwright exited with code 1.\nExpected: <button>Retry</button>\nExpected: 1 > 0")]
    [InlineData("<div>Failure</div>")]
    [InlineData("<div>Failure</div><img src=\"x\" onerror=\"alert(1)\"><script>alert(1)</script>")]
    [InlineData("Already escaped: &lt;button&gt; & \"quoted\"")]
    [InlineData("Command failed")]
    [InlineData("Command failed \U0001F600")]
    [InlineData("")]
    [InlineData(null)]
    public async Task ExecuteAsyncCore_FailedCommand_RendersMessageAsPlainText(string? message)
    {
        await AssertFailureMessageAsync(message, message);
    }

    [Theory]
    [InlineData('&', 249)]
    [InlineData('&', 250)]
    [InlineData('&', 251)]
    [InlineData('&', 10000)]
    [InlineData('<', 249)]
    [InlineData('<', 250)]
    [InlineData('<', 251)]
    [InlineData('<', 10000)]
    public async Task ExecuteAsyncCore_FailedCommand_TruncatesPlainTextBeforeEncoding(char character, int messageLength)
    {
        var message = new string(character, messageLength);
        var expectedMessage = messageLength <= 250 ? message : message[..249] + FormatHelpers.Ellipsis;

        await AssertFailureMessageAsync(message, expectedMessage);
    }

    [Theory]
    [InlineData(247, true)]
    [InlineData(248, false)]
    [InlineData(249, false)]
    public async Task ExecuteAsyncCore_FailedCommand_TruncationPreservesSurrogatePairs(int prefixLength, bool includeEmoji)
    {
        var prefix = new string('a', prefixLength);
        var message = prefix + "\U0001F600" + " Additional diagnostic text";
        var expectedMessage = prefix + (includeEmoji ? "\U0001F600" : "") + FormatHelpers.Ellipsis;

        await AssertFailureMessageAsync(message, expectedMessage);
    }

    private async Task AssertFailureMessageAsync(string? message, string? expectedMessage)
    {
        var response = new ResourceCommandResponseViewModel
        {
            Kind = ResourceCommandResponseKind.Failed,
            Message = message
        };
        var dashboardClient = new TestDashboardClient(
            isEnabled: true,
            executeResourceCommand: (_, _, _, _, _) => Task.FromResult(response));
        FluentUISetupHelpers.AddCommonDashboardServices(this);
        FluentUISetupHelpers.SetupFluentUIComponents(this);
        Services.AddSingleton<IDashboardClient>(dashboardClient);
        Services.AddScoped<DashboardCommandExecutor>();

        var toasts = Render<FluentToastProvider>();
        var executor = Services.GetRequiredService<DashboardCommandExecutor>();
        var command = new CommandViewModel(
            name: "test",
            state: CommandViewModelState.Enabled,
            displayName: "Run tests",
            displayDescription: "Run tests",
            confirmationMessage: "",
            argumentInputs: [],
            isHighlighted: false,
            iconName: "",
            iconVariant: IconVariant.Regular);
        var resource = ModelTestHelpers.CreateResource(resourceName: "api", commands: [command]);

        await toasts.InvokeAsync(() => executor.ExecuteAsyncCore(resource, command, r => r.DisplayName))
            .WaitAsync(TimeSpan.FromSeconds(10));
        toasts.Render();

        var toast = Assert.Single(toasts.FindComponents<FluentToast>());
        Assert.Equal(ToastIntent.Error, toast.Instance.Intent);
        if (string.IsNullOrEmpty(expectedMessage))
        {
            Assert.Null(toast.Instance.ChildContent);
        }
        else
        {
            var body = toast.Find($"[id=\"{toast.Instance.Id}-body\"]");
            Assert.Equal(expectedMessage, body.TextContent);
            Assert.Empty(body.Children);
        }

        var notifications = Services.GetRequiredService<Aspire.Dashboard.Model.INotificationService>();
        Assert.Equal(message, Assert.Single(notifications.GetNotifications()).Entry.Body);
        Assert.Equal(message, response.Message);
    }
}
