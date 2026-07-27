// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Threading.Channels;
using Aspire.Dashboard.Components.Pages;
using Aspire.Dashboard.Components.Resize;
using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Telemetry;
using Aspire.Dashboard.Tests;
using Aspire.Dashboard.Tests.Shared;
using Aspire.DashboardService.Proto.V1;
using Bunit;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Interactions;

[UseCulture("en-US")]
public partial class InteractionsProviderTests : DashboardTestContext
{
    private readonly ITestOutputHelper _testOutputHelper;

    public InteractionsProviderTests(ITestOutputHelper testOutputHelper)
    {
        _testOutputHelper = testOutputHelper;
    }

    private static readonly ViewportInformation s_desktop = new(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);

    [Fact]
    public async Task Initialize_DashboardClientNotEnabled_ProviderDisabledAsync()
    {
        // Arrange
        var dashboardClient = new TestDashboardClient(isEnabled: false);

        SetupInteractionProviderServices(dashboardClient);

        // Act
        var cut = RenderComponent<Components.Interactions.InteractionsProvider>();

        var instance = cut.Instance;

        // Assert
        cut.WaitForAssertion(() =>
        {
            Assert.False(instance._enabled);
        });

        await instance.DisposeAsync().DefaultTimeout();
    }

    [Fact]
    public async Task Initialize_DashboardClientEnabled_ProviderEnabledAsync()
    {
        // Arrange
        var interactionsChannel = Channel.CreateUnbounded<WatchInteractionsResponseUpdate>();

        var dashboardClient = new TestDashboardClient(isEnabled: true, interactionChannelProvider: () => interactionsChannel);

        SetupInteractionProviderServices(dashboardClient);

        // Act
        var cut = RenderComponent<Components.Interactions.InteractionsProvider>(builder =>
        {
            builder.Add(p => p.ViewportInformation, s_desktop);
        });

        var instance = cut.Instance;

        // Assert
        cut.WaitForAssertion(() =>
        {
            Assert.True(instance._enabled);
        });

        await instance.DisposeAsync().DefaultTimeout();
    }

    [Fact]
    public async Task ReceiveData_MessageBoxOpen_OpenPaneThenCloseOnComplete()
    {
        // Arrange
        var interactionsChannel = Channel.CreateUnbounded<WatchInteractionsResponseUpdate>();
        var dashboardClient = new TestDashboardClient(isEnabled: true, interactionChannelProvider: () => interactionsChannel);

        SetupInteractionProviderServices(dashboardClient: dashboardClient);

        // Act 1: a message box opens the blocking interaction pane.
        var cut = RenderComponent<Components.Interactions.InteractionsProvider>(builder =>
        {
            builder.Add(p => p.ViewportInformation, s_desktop);
        });

        var instance = cut.Instance;

        await interactionsChannel.Writer.WriteAsync(new WatchInteractionsResponseUpdate
        {
            InteractionId = 1,
            MessageBox = new InteractionMessageBox()
        });

        // Assert 1
        await AsyncTestHelpers.AssertIsTrueRetryAsync(() => instance.CurrentDialog?.InteractionId == 1, "Wait for dialog to open.");

        // Act 2: the server completes the interaction, which closes the pane.
        await interactionsChannel.Writer.WriteAsync(new WatchInteractionsResponseUpdate
        {
            InteractionId = 1,
            Complete = new InteractionComplete()
        });

        // Assert 2
        await AsyncTestHelpers.AssertIsTrueRetryAsync(() => instance.CurrentDialog == null, "Wait for dialog to close.");

        await instance.DisposeAsync().DefaultTimeout();
    }

    [Fact]
    public async Task ReceiveData_MessageBoxPrimary_SendsMessageBoxResult()
    {
        // Arrange
        var interactionsChannel = Channel.CreateUnbounded<WatchInteractionsResponseUpdate>();
        var sendInteractionUpdatesChannel = Channel.CreateUnbounded<WatchInteractionsRequestUpdate>();
        var dashboardClient = new TestDashboardClient(isEnabled: true,
            interactionChannelProvider: () => interactionsChannel,
            sendInteractionUpdateChannel: sendInteractionUpdatesChannel);

        SetupInteractionProviderServices(dashboardClient: dashboardClient);

        // Act 1
        var cut = RenderComponent<Components.Interactions.InteractionsProvider>(builder =>
        {
            builder.Add(p => p.ViewportInformation, s_desktop);
        });

        var instance = cut.Instance;

        await interactionsChannel.Writer.WriteAsync(new WatchInteractionsResponseUpdate
        {
            InteractionId = 1,
            MessageBox = new InteractionMessageBox()
        });

        await AsyncTestHelpers.AssertIsTrueRetryAsync(() => instance.CurrentDialog?.InteractionId == 1, "Wait for dialog to open.");

        // Act 2: confirm the message box (primary).
        await cut.InvokeAsync(instance.OnMessageBoxPrimaryAsync).DefaultTimeout();

        // Assert: a MessageBox response is sent for the interaction.
        var update = await sendInteractionUpdatesChannel.Reader.ReadAsync().AsTask().DefaultTimeout();
        Assert.Equal(1, update.InteractionId);
        Assert.Equal(WatchInteractionsRequestUpdate.KindOneofCase.MessageBox, update.KindCase);

        await instance.DisposeAsync().DefaultTimeout();
    }

    [Fact]
    public async Task ReceiveData_InputDialogOpenAndCancel_OpenPaneAndSendCompletion()
    {
        // Arrange
        var interactionsChannel = Channel.CreateUnbounded<WatchInteractionsResponseUpdate>();
        var sendInteractionUpdatesChannel = Channel.CreateUnbounded<WatchInteractionsRequestUpdate>();
        var dashboardClient = new TestDashboardClient(isEnabled: true,
            interactionChannelProvider: () => interactionsChannel,
            sendInteractionUpdateChannel: sendInteractionUpdatesChannel);

        SetupInteractionProviderServices(dashboardClient: dashboardClient);

        // Act 1
        var cut = RenderComponent<Components.Interactions.InteractionsProvider>(builder =>
        {
            builder.Add(p => p.ViewportInformation, s_desktop);
        });

        var instance = cut.Instance;

        await interactionsChannel.Writer.WriteAsync(new WatchInteractionsResponseUpdate
        {
            InteractionId = 1,
            InputsDialog = new InteractionInputsDialog()
        });

        await AsyncTestHelpers.AssertIsTrueRetryAsync(() => instance.CurrentDialog?.InteractionId == 1, "Wait for dialog to open.");

        // Act 2: cancel the dialog.
        await cut.InvokeAsync(instance.OnDialogCancelAsync).DefaultTimeout();

        // Assert: a Complete request is sent for the interaction.
        var update = await sendInteractionUpdatesChannel.Reader.ReadAsync().AsTask().DefaultTimeout();
        Assert.Equal(1, update.InteractionId);
        Assert.Equal(WatchInteractionsRequestUpdate.KindOneofCase.Complete, update.KindCase);

        await instance.DisposeAsync().DefaultTimeout();
    }

    [Fact]
    public async Task ReceiveData_InputDialogOpenAndSubmit_OpenPaneAndSendInputsDialog()
    {
        // Arrange
        var interactionsChannel = Channel.CreateUnbounded<WatchInteractionsResponseUpdate>();
        var sendInteractionUpdatesChannel = Channel.CreateUnbounded<WatchInteractionsRequestUpdate>();
        var dashboardClient = new TestDashboardClient(isEnabled: true,
            interactionChannelProvider: () => interactionsChannel,
            sendInteractionUpdateChannel: sendInteractionUpdatesChannel);

        SetupInteractionProviderServices(dashboardClient: dashboardClient);

        // Act 1
        var cut = RenderComponent<Components.Interactions.InteractionsProvider>(builder =>
        {
            builder.Add(p => p.ViewportInformation, s_desktop);
        });

        var instance = cut.Instance;

        await interactionsChannel.Writer.WriteAsync(new WatchInteractionsResponseUpdate
        {
            InteractionId = 1,
            InputsDialog = new InteractionInputsDialog()
        });

        await AsyncTestHelpers.AssertIsTrueRetryAsync(() => instance.CurrentDialog?.InteractionId == 1, "Wait for dialog to open.");

        // Act 2: submit the dialog.
        await cut.InvokeAsync(() => instance.OnDialogSubmitAsync(new Dictionary<string, string>())).DefaultTimeout();

        // Assert: an InputsDialog request (not Complete) is sent; the server drives completion.
        var update = await sendInteractionUpdatesChannel.Reader.ReadAsync().AsTask().DefaultTimeout();
        Assert.Equal(1, update.InteractionId);
        Assert.Equal(WatchInteractionsRequestUpdate.KindOneofCase.InputsDialog, update.KindCase);
        Assert.False(update.ResponseUpdate);

        await instance.DisposeAsync().DefaultTimeout();
    }

    [Fact]
    public async Task ReceiveData_NotificationReceivedTwice_IgnoreReplayedNotification()
    {
        // Arrange
        var interactionsChannel = Channel.CreateUnbounded<WatchInteractionsResponseUpdate>();
        var dashboardClient = new TestDashboardClient(isEnabled: true, interactionChannelProvider: () => interactionsChannel);

        SetupInteractionProviderServices(dashboardClient: dashboardClient);

        // Act 1
        var cut = RenderComponent<Components.Interactions.InteractionsProvider>(builder =>
        {
            builder.Add(p => p.ViewportInformation, s_desktop);
        });

        var instance = cut.Instance;

        var response = new WatchInteractionsResponseUpdate
        {
            InteractionId = 1,
            Notification = new InteractionNotification()
        };
        await interactionsChannel.Writer.WriteAsync(response);

        // Assert 1: one notification toast, one message processed.
        await AsyncTestHelpers.AssertIsTrueRetryAsync(async () =>
        {
            var notification = instance.OpenNotifications.SingleOrDefault();
            if (notification is null || notification.InteractionId != 1)
            {
                return false;
            }

            return await instance.GetMessagesProcessedAsync() == 1;
        }, "Wait for notification created.");

        // Act 2: the same notification is replayed (e.g. reconnect).
        await interactionsChannel.Writer.WriteAsync(response);

        // Assert 2: still one toast, but two messages processed (the replay was ignored).
        await AsyncTestHelpers.AssertIsTrueRetryAsync(async () =>
        {
            var notification = instance.OpenNotifications.SingleOrDefault();
            if (notification is null || notification.InteractionId != 1)
            {
                return false;
            }

            return await instance.GetMessagesProcessedAsync() == 2;
        }, "Wait for replayed notification to be ignored.");

        await instance.DisposeAsync().DefaultTimeout();
    }

    [Fact]
    public async Task ReceiveData_MessageBoxReceivedTwice_UpdatesOpenDialogInPlace()
    {
        // Arrange
        var interactionsChannel = Channel.CreateUnbounded<WatchInteractionsResponseUpdate>();
        var dashboardClient = new TestDashboardClient(isEnabled: true, interactionChannelProvider: () => interactionsChannel);

        SetupInteractionProviderServices(dashboardClient: dashboardClient);

        // Act 1
        var cut = RenderComponent<Components.Interactions.InteractionsProvider>(builder =>
        {
            builder.Add(p => p.ViewportInformation, s_desktop);
        });

        var instance = cut.Instance;

        var response = new WatchInteractionsResponseUpdate
        {
            InteractionId = 1,
            MessageBox = new InteractionMessageBox()
        };
        await interactionsChannel.Writer.WriteAsync(response);

        // Assert 1
        await AsyncTestHelpers.AssertIsTrueRetryAsync(async () =>
            instance.CurrentDialog?.InteractionId == 1 && await instance.GetMessagesProcessedAsync() == 1,
            "Wait for dialog created.");

        // Act 2: the same interaction is sent again; it updates the open dialog in place (no second dialog).
        await interactionsChannel.Writer.WriteAsync(response);

        // Assert 2
        await AsyncTestHelpers.AssertIsTrueRetryAsync(async () =>
            instance.CurrentDialog?.InteractionId == 1 && await instance.GetMessagesProcessedAsync() == 2,
            "Wait for dialog updated.");

        await instance.DisposeAsync().DefaultTimeout();
    }

    [Theory]
    [InlineData(true, "**Hello** _World_! <b>Bold</b>", "<strong>Hello</strong> <em>World</em>! &lt;b&gt;Bold&lt;/b&gt;")]
    [InlineData(false, "**Hello** _World_! <b>Bold</b>", "**Hello** _World_! &lt;b&gt;Bold&lt;/b&gt;")]
    [InlineData(true, "Para1\r\n\r\nPara2", "<p>Para1</p>\r\n<p>Para2</p>")]
    [InlineData(true, "This is a test://www.localhost.com", "This is a test://www.localhost.com")]
    [InlineData(true, "This is a [test](test://www.localhost.com)", "This is a <a href=\"test://www.localhost.com\" target=\"_blank\" rel=\"noopener noreferrer nofollow\">test</a>")]
    public async Task ReceiveData_InputDialogWithMarkdownMessage_ExpectedResolvedMessage(bool markdownSupported, string message, string expectedMessage)
    {
        // Arrange
        var interactionsChannel = Channel.CreateUnbounded<WatchInteractionsResponseUpdate>();
        var dashboardClient = new TestDashboardClient(isEnabled: true, interactionChannelProvider: () => interactionsChannel);

        SetupInteractionProviderServices(dashboardClient: dashboardClient);

        // Act
        var cut = RenderComponent<Components.Interactions.InteractionsProvider>(builder =>
        {
            builder.Add(p => p.ViewportInformation, s_desktop);
        });

        var instance = cut.Instance;

        await interactionsChannel.Writer.WriteAsync(new WatchInteractionsResponseUpdate
        {
            InteractionId = 1,
            Message = message,
            EnableMessageMarkdown = markdownSupported,
            InputsDialog = new InteractionInputsDialog()
        });

        // Assert: the dialog message is resolved (Markdown -> sanitized HTML, or HTML-encoded when disabled).
        await AsyncTestHelpers.AssertIsTrueRetryAsync(() => instance.CurrentDialog?.InteractionId == 1, "Wait for dialog to open.");

        Assert.Equal(expectedMessage, instance.CurrentDialogMessage?.Trim(), ignoreLineEndingDifferences: true);

        await instance.DisposeAsync().DefaultTimeout();
    }

    private void SetupInteractionProviderServices(TestDashboardClient? dashboardClient = null)
    {
        var loggerFactory = IntegrationTestHelpers.CreateLoggerFactory(_testOutputHelper);

        Services.AddLocalization();
        Services.AddSingleton<ILoggerFactory>(loggerFactory);

        Services.AddSingleton<IDashboardClient>(dashboardClient ?? new TestDashboardClient());
        Services.AddSingleton<DashboardTelemetryService>();
        Services.AddSingleton<IDashboardTelemetrySender, TestDashboardTelemetrySender>();
        Services.AddSingleton<ComponentTelemetryContextProvider>();
        Services.AddSingleton<DimensionManager>();
    }
}
