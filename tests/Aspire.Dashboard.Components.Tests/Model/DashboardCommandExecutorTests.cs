// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Tests.Shared;
using Aspire.Dashboard.Utils;
using Aspire.Tests.Shared.DashboardModel;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FluentUI.AspNetCore.Components;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Model;

public class DashboardCommandExecutorTests : DashboardTestContext
{
    [Theory]
    [InlineData("Playwright exited with code 1.\nExpected: <button>Retry</button>\nExpected: 1 > 0")]
    [InlineData("<div>Failure</div>")]
    [InlineData("<div>Failure</div><img src=\"x\" onerror=\"alert(1)\"><script>alert(1)</script>")]
    [InlineData("Already escaped: &lt;button&gt; & \"quoted\"")]
    [InlineData("Command failed")]
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
