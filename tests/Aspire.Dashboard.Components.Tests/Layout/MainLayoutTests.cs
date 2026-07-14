// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Layout;
using Aspire.Dashboard.Components.Resize;
using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Configuration;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Model.BrowserStorage;
using Aspire.Dashboard.Tests.Shared;
using Aspire.Dashboard.Utils;
using Aspire.Tests.Shared;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FluentUI.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components.Components.Tooltip;
using Microsoft.JSInterop;
using Xunit;
using DashboardRunsDialog = Aspire.Dashboard.Components.Dialogs.DashboardRunsDialog;
using DashboardRunsDialogViewModel = Aspire.Dashboard.Components.Dialogs.DashboardRunsDialogViewModel;

namespace Aspire.Dashboard.Components.Tests.Layout;

[UseCulture("en-US")]
public partial class MainLayoutTests : DashboardTestContext
{
    [Fact]
    public async Task OnInitialize_UnsecuredOtlp_NotDismissed_DisplayMessageBar()
    {
        // Arrange
        var testLocalStorage = new TestLocalStorage();
        var messageService = new MessageService();

        SetupMainLayoutServices(localStorage: testLocalStorage, messageService: messageService);

        Message? message = null;
        var messageShownTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        messageService.OnMessageItemsUpdatedAsync += () =>
        {
            message = messageService.AllMessages.Single();
            messageShownTcs.TrySetResult();
            return Task.CompletedTask;
        };

        testLocalStorage.OnGetUnprotectedAsync = key =>
        {
            switch (key)
            {
                case BrowserStorageKeys.UnsecuredTelemetryMessageDismissedKey:
                case BrowserStorageKeys.UnsecuredEndpointMessageDismissedKey:
                    return (false, false);
                default:
                    throw new InvalidOperationException("Unexpected key.");
            }
        };

        var dismissedSettingSetTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        testLocalStorage.OnSetUnprotectedAsync = (key, value) =>
        {
            switch (key)
            {
                case BrowserStorageKeys.UnsecuredTelemetryMessageDismissedKey:
                case BrowserStorageKeys.UnsecuredEndpointMessageDismissedKey:
                    dismissedSettingSetTcs.TrySetResult((bool)value!);
                    break;
                default:
                    throw new InvalidOperationException("Unexpected key.");
            }
        };

        // Act
        var cut = RenderComponent<MainLayout>(builder =>
        {
            builder.Add(p => p.ViewportInformation, new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false));
        });

        // Assert
        await messageShownTcs.Task.DefaultTimeout();

        Assert.NotNull(message);

        message.Close();

        Assert.True(await dismissedSettingSetTcs.Task.DefaultTimeout());
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task OnInitialize_UnsecuredOtlp_Dismissed_NoMessageBar(bool unsecuredTelemetryMessageDismissedKey, bool unsecuredEndpointMessageDismissedKey)
    {
        // Arrange
        var testLocalStorage = new TestLocalStorage();
        var messageService = new MessageService();

        SetupMainLayoutServices(localStorage: testLocalStorage, messageService: messageService);

        var messageShownTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        messageService.OnMessageItemsUpdatedAsync += () =>
        {
            messageShownTcs.TrySetResult();
            return Task.CompletedTask;
        };

        testLocalStorage.OnGetUnprotectedAsync = key =>
        {
            switch (key)
            {
                case BrowserStorageKeys.UnsecuredTelemetryMessageDismissedKey:
                    return (unsecuredTelemetryMessageDismissedKey, unsecuredTelemetryMessageDismissedKey);
                case BrowserStorageKeys.UnsecuredEndpointMessageDismissedKey:
                    return (unsecuredEndpointMessageDismissedKey, unsecuredEndpointMessageDismissedKey);
                default:
                    throw new InvalidOperationException("Unexpected key.");
            }
        };

        // Act
        var cut = RenderComponent<MainLayout>(builder =>
        {
            builder.Add(p => p.ViewportInformation, new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false));
        });

        // Assert
        var timeoutTask = Task.Delay(100);
        var completedTask = await Task.WhenAny(messageShownTcs.Task, timeoutTask).DefaultTimeout();

        // It's hard to test something not happening.
        // In this case of checking for a message, apply a small display and then double check that no message was displayed.
        Assert.True(completedTask != messageShownTcs.Task, "No message bar should be displayed.");
        Assert.Empty(messageService.AllMessages);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task OnInitialize_UnsecuredOtlp_SuppressConfigured_NoMessageBar(bool expectMessageBar, bool telemetrySuppressUnsecuredMessage)
    {
        // Arrange
        var testLocalStorage = new TestLocalStorage();
        var messageService = new MessageService();

        SetupMainLayoutServices(localStorage: testLocalStorage, messageService: messageService, configureOptions: o =>
        {
            o.Otlp.SuppressUnsecuredMessage = telemetrySuppressUnsecuredMessage;
        });

        var messageShownTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        messageService.OnMessageItemsUpdatedAsync += () =>
        {
            messageShownTcs.TrySetResult();
            return Task.CompletedTask;
        };

        testLocalStorage.OnGetUnprotectedAsync = key =>
        {
            switch (key)
            {
                case BrowserStorageKeys.UnsecuredTelemetryMessageDismissedKey:
                case BrowserStorageKeys.UnsecuredEndpointMessageDismissedKey:
                    return (false, false); // Message not dismissed, but should be suppressed by config if suppressUnsecuredMessage is true
                default:
                    throw new InvalidOperationException("Unexpected key.");
            }
        };

        // Act
        var cut = RenderComponent<MainLayout>(builder =>
        {
            builder.Add(p => p.ViewportInformation, new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false));
        });

        // Assert
        if (!expectMessageBar)
        {
            var timeoutTask = Task.Delay(100);
            var completedTask = await Task.WhenAny(messageShownTcs.Task, timeoutTask).DefaultTimeout();

            // When suppressed, no message should be displayed
            Assert.True(completedTask != messageShownTcs.Task, "No message bar should be displayed when suppressed by configuration.");
            Assert.Empty(messageService.AllMessages);
        }
        else
        {
            // When not suppressed, message should be displayed since it wasn't dismissed
            await messageShownTcs.Task.DefaultTimeout();
            Assert.NotEmpty(messageService.AllMessages);
        }
    }

    [Theory]
    [InlineData(true, "dashboard-help-button", "HelpDialog", "dashboard-help-button")]
    [InlineData(true, "dashboard-settings-button", "SettingsDialog", "dashboard-settings-button")]
    [InlineData(false, "dashboard-navigation-button", "HelpDialog", "dashboard-navigation-button")]
    [InlineData(false, "dashboard-navigation-button", "SettingsDialog", "dashboard-navigation-button")]
    public async Task HeaderDialogClose_RestoresFocusToLaunchButton(bool isDesktop, string launchButtonId, string expectedDialogId, string expectedFocusId)
    {
        DialogParameters? capturedParameters = null;
        TestDialogService? dialogService = null;
        dialogService = new TestDialogService(onShowDialog: (_, parameters) =>
        {
            capturedParameters = parameters;
            return Task.FromResult<IDialogReference>(new DialogReference(parameters.Id, dialogService!));
        });

        SetupMainLayoutServices(dialogService: dialogService);
        JSInterop.SetupVoid("focusElement", _ => true);

        var cut = RenderComponent<MainLayout>(builder =>
        {
            builder.Add(p => p.ViewportInformation, new ViewportInformation(IsDesktop: isDesktop, IsUltraLowHeight: false, IsUltraLowWidth: false));
        });

        if (isDesktop)
        {
            await cut.InvokeAsync(() => cut.Find($"#{launchButtonId}").Click());
        }
        else
        {
            var menuItemName = expectedDialogId == "HelpDialog"
                ? "Help"
                : "Settings";

            await cut.InvokeAsync(() => cut.Find("#dashboard-navigation-button").Click());
            await cut.InvokeAsync(() => cut.FindAll("fluent-menu-item").Single(item => item.TextContent.Contains(menuItemName, StringComparison.OrdinalIgnoreCase)).Click());
        }

        Assert.NotNull(capturedParameters);
        Assert.Equal(expectedDialogId, capturedParameters.Id);

        await cut.InvokeAsync(() => capturedParameters.OnDialogClosing.InvokeAsync(null!));

        cut.WaitForAssertion(() =>
        {
            Assert.Contains(JSInterop.Invocations, invocation =>
                invocation.Identifier == "focusElement" &&
                invocation.Arguments.Count == 1 &&
                string.Equals((string?)invocation.Arguments[0], expectedFocusId, StringComparison.Ordinal));
        });
    }

    [Fact]
    public void DashboardRunsButton_Click_OpensDashboardRunsDialog()
    {
        DialogParameters? capturedParameters = null;
        TestDialogService? dialogService = null;
        dialogService = new TestDialogService(onShowDialog: (_, parameters) =>
        {
            capturedParameters = parameters;
            return Task.FromResult<IDialogReference>(new DialogReference(parameters.Id, dialogService!));
        });

        SetupMainLayoutServices(dialogService: dialogService);

        var cut = RenderComponent<MainLayout>(builder =>
        {
            builder.Add(p => p.ViewportInformation, new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false));
        });

        cut.Find("#dashboard-runs-button").Click();

        Assert.NotNull(capturedParameters);
        Assert.Equal(nameof(DashboardRunsDialog), capturedParameters.Id);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DashboardRunsButton_Unsupported_IsHiddenAndStaleSelectionIsIgnored(bool isDesktop)
    {
        var runStore = new FluentUISetupHelpers.TestDashboardRunStore(
        [
            new(
                RunId: "current",
                StartedAtUtc: DateTimeOffset.UnixEpoch,
                EndedAtUtc: null,
                CleanShutdown: false,
                ApplicationName: "TestApp",
                DatabasePath: string.Empty,
                IsCurrent: true)
        ],
        supportsRunSelection: false);
        var sessionStorage = new TestSessionStorage
        {
            OnGetAsync = _ => throw new InvalidOperationException("Run selection should not be read.")
        };
        SetupMainLayoutServices(dashboardRunStore: runStore, sessionStorage: sessionStorage);

        var cut = RenderComponent<MainLayout>(builder =>
        {
            builder.Add(
                component => component.ViewportInformation,
                new ViewportInformation(IsDesktop: isDesktop, IsUltraLowHeight: false, IsUltraLowWidth: false));
        });

        Assert.Empty(cut.FindAll("#dashboard-runs-button"));
        var runSelection = Assert.IsType<FluentUISetupHelpers.TestDashboardRunSelection>(Services.GetRequiredService<IDashboardRunSelection>());
        Assert.Null(runSelection.SelectedRunId);
    }

    [Fact]
    public async Task DashboardRunsDialog_OkStoresSelectionAndReloadsWithoutQueryString()
    {
        var historicalRun = new DashboardRunDescriptor(
            RunId: "historical",
            StartedAtUtc: new DateTimeOffset(2025, 1, 2, 12, 30, 0, TimeSpan.Zero),
            EndedAtUtc: new DateTimeOffset(2025, 1, 2, 13, 30, 0, TimeSpan.Zero),
            CleanShutdown: true,
            ApplicationName: "TestApp",
            DatabasePath: string.Empty,
            IsCurrent: false);
        var runStore = new FluentUISetupHelpers.TestDashboardRunStore(
        [
            new(
                RunId: "current",
                StartedAtUtc: DateTimeOffset.UnixEpoch,
                EndedAtUtc: null,
                CleanShutdown: false,
                ApplicationName: "TestApp",
                DatabasePath: string.Empty,
                IsCurrent: true),
            historicalRun
        ]);
        string? storedRunId = null;
        var sessionStorage = new TestSessionStorage
        {
            OnSetAsync = (_, value) => storedRunId = Assert.IsType<string>(value)
        };
        DashboardRunsDialogViewModel? content = null;
        DialogParameters? capturedParameters = null;
        TestDialogService? dialogService = null;
        dialogService = new TestDialogService(onShowDialog: (dialogContent, parameters) =>
        {
            content = Assert.IsType<DashboardRunsDialogViewModel>(dialogContent);
            capturedParameters = parameters;
            return Task.FromResult<IDialogReference>(new DialogReference(parameters.Id, dialogService!));
        });

        SetupMainLayoutServices(dialogService: dialogService, dashboardRunStore: runStore, sessionStorage: sessionStorage);
        var cut = RenderComponent<MainLayout>(builder =>
        {
            builder.Add(component => component.ViewportInformation, new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false));
        });

        cut.Find("#dashboard-runs-button").Click();
        content!.SelectedRun = historicalRun;
        await cut.InvokeAsync(() => capturedParameters!.OnDialogResult.InvokeAsync(DialogResult.Ok(true)));

        Assert.Equal("historical", storedRunId);
        Assert.Empty(new Uri(Services.GetRequiredService<NavigationManager>().Uri).Query);
    }

    [Fact]
    public void HistoricalRun_DisplaysLocalStartTimeAfterApplicationName()
    {
        var timeProvider = new TestTimeProvider();
        var runStore = new FluentUISetupHelpers.TestDashboardRunStore(
        [
            new(
                RunId: "current",
                StartedAtUtc: DateTimeOffset.UnixEpoch,
                EndedAtUtc: null,
                CleanShutdown: false,
                ApplicationName: "TestApp",
                DatabasePath: string.Empty,
                IsCurrent: true),
            new(
                RunId: "historical",
                StartedAtUtc: new DateTimeOffset(2025, 1, 2, 12, 30, 0, TimeSpan.Zero),
                EndedAtUtc: new DateTimeOffset(2025, 1, 2, 13, 30, 0, TimeSpan.Zero),
                CleanShutdown: true,
                ApplicationName: "TestApp",
                DatabasePath: string.Empty,
                IsCurrent: false)
        ]);

        var sessionStorage = new TestSessionStorage
        {
            OnGetAsync = key => key == BrowserStorageKeys.SelectedDashboardRunId
                ? (true, "historical")
                : (false, null)
        };
        SetupMainLayoutServices(browserTimeProvider: timeProvider, dashboardRunStore: runStore, sessionStorage: sessionStorage);

        var cut = RenderComponent<MainLayout>(builder =>
        {
            builder.Add(p => p.ViewportInformation, new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false));
        });

        Assert.Equal("Started 1/2/2025 1:30 PM", cut.Find(".application-run-start").TextContent, ignoreWhiteSpaceDifferences: true);
    }

    [Theory]
    [InlineData(true, false, "dashboard-help-button", "HelpDialog", "dashboard-navigation-button")]
    [InlineData(true, false, "dashboard-settings-button", "SettingsDialog", "dashboard-navigation-button")]
    [InlineData(false, true, "dashboard-navigation-button", "HelpDialog", "dashboard-help-button")]
    [InlineData(false, true, "dashboard-navigation-button", "SettingsDialog", "dashboard-settings-button")]
    public async Task HeaderDialogClose_AfterViewportChange_RestoresFocusToVisibleLaunchButton(
        bool initialIsDesktop,
        bool closingIsDesktop,
        string launchButtonId,
        string expectedDialogId,
        string expectedFocusId)
    {
        DialogParameters? capturedParameters = null;
        TestDialogService? dialogService = null;
        dialogService = new TestDialogService(onShowDialog: (_, parameters) =>
        {
            capturedParameters = parameters;
            return Task.FromResult<IDialogReference>(new DialogReference(parameters.Id, dialogService!));
        });

        SetupMainLayoutServices(dialogService: dialogService);
        JSInterop.SetupVoid("focusElement", _ => true);

        var cut = RenderComponent<CascadingValue<ViewportInformation>>(builder =>
        {
            builder.Add(p => p.Value, new ViewportInformation(IsDesktop: initialIsDesktop, IsUltraLowHeight: false, IsUltraLowWidth: false));
            builder.AddChildContent<MainLayout>();
        });

        if (initialIsDesktop)
        {
            await cut.InvokeAsync(() => cut.Find($"#{launchButtonId}").Click());
        }
        else
        {
            var menuItemName = expectedDialogId == "HelpDialog"
                ? "Help"
                : "Settings";

            await cut.InvokeAsync(() => cut.Find("#dashboard-navigation-button").Click());
            await cut.InvokeAsync(() => cut.FindAll("fluent-menu-item").Single(item => item.TextContent.Contains(menuItemName, StringComparison.OrdinalIgnoreCase)).Click());
        }

        Assert.NotNull(capturedParameters);
        Assert.Equal(expectedDialogId, capturedParameters.Id);

        cut.SetParametersAndRender(parameters =>
        {
            parameters.Add(p => p.Value, new ViewportInformation(IsDesktop: closingIsDesktop, IsUltraLowHeight: false, IsUltraLowWidth: false));
            parameters.AddChildContent<MainLayout>();
        });

        await cut.InvokeAsync(() => capturedParameters.OnDialogClosing.InvokeAsync(null!));

        cut.WaitForAssertion(() =>
        {
            Assert.Contains(JSInterop.Invocations, invocation =>
                invocation.Identifier == "focusElement" &&
                invocation.Arguments.Count == 1 &&
                string.Equals((string?)invocation.Arguments[0], expectedFocusId, StringComparison.Ordinal));
        });
    }

    [Theory]
    [InlineData(AspireKeyboardShortcut.Help, "dashboard-help-button", "HelpDialog")]
    [InlineData(AspireKeyboardShortcut.Settings, "dashboard-settings-button", "SettingsDialog")]
    public async Task HeaderDialogShortcutClose_RestoresFocusToLaunchButton(AspireKeyboardShortcut shortcut, string launchButtonId, string expectedDialogId)
    {
        DialogParameters? capturedParameters = null;
        TestDialogService? dialogService = null;
        dialogService = new TestDialogService(onShowDialog: (_, parameters) =>
        {
            capturedParameters = parameters;
            return Task.FromResult<IDialogReference>(new DialogReference(parameters.Id, dialogService!));
        });

        SetupMainLayoutServices(dialogService: dialogService);
        JSInterop.SetupVoid("focusElement", _ => true);

        var cut = RenderComponent<MainLayout>(builder =>
        {
            builder.Add(p => p.ViewportInformation, new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false));
        });

        await cut.InvokeAsync(() => cut.Instance.OnPageKeyDownAsync(shortcut));

        Assert.NotNull(capturedParameters);
        Assert.Equal(expectedDialogId, capturedParameters.Id);

        await cut.InvokeAsync(() => capturedParameters.OnDialogClosing.InvokeAsync(null!));

        cut.WaitForAssertion(() =>
        {
            Assert.Contains(JSInterop.Invocations, invocation =>
                invocation.Identifier == "focusElement" &&
                invocation.Arguments.Count == 1 &&
                string.Equals((string?)invocation.Arguments[0], launchButtonId, StringComparison.Ordinal));
        });
    }

    private void SetupMainLayoutServices(
        TestLocalStorage? localStorage = null,
        MessageService? messageService = null,
        Action<DashboardOptions>? configureOptions = null,
        IDialogService? dialogService = null,
        BrowserTimeProvider? browserTimeProvider = null,
        IDashboardRunStore? dashboardRunStore = null,
        ISessionStorage? sessionStorage = null)
    {
        FluentUISetupHelpers.AddCommonDashboardServices(
            this,
            localStorage: localStorage,
            messageService: messageService,
            browserTimeProvider: browserTimeProvider,
            dashboardRunStore: dashboardRunStore,
            sessionStorage: sessionStorage);

        if (dialogService is not null)
        {
            Services.AddSingleton(dialogService);
        }

        Services.AddOptions();
        Services.AddSingleton<IThemeResolver, TestThemeResolver>();
        Services.AddSingleton<IDashboardClient, TestDashboardClient>();
        Services.AddSingleton<ITooltipService, TooltipService>();
        Services.AddSingleton<IToastService, ToastService>();
        Services.Configure<DashboardOptions>(o =>
        {
            // Configure OTLP endpoint URLs so they can be parsed
            o.Otlp.GrpcEndpointUrl = "http://localhost:4317";
            o.Otlp.AuthMode = OtlpAuthMode.Unsecured;
            configureOptions?.Invoke(o);
            // Call TryParseOptions to populate parsed endpoint addresses
            o.Otlp.TryParseOptions(out _);
        });

        FluentUISetupHelpers.SetupFluentDialogProvider(this);
        FluentUISetupHelpers.SetupFluentOverflow(this);
        FluentUISetupHelpers.SetupFluentAnchor(this);
        FluentUISetupHelpers.SetupFluentButton(this);
        FluentUISetupHelpers.SetupFluentMenu(this);
        FluentUISetupHelpers.SetupFluentAnchoredRegion(this);
        FluentUISetupHelpers.SetupFluentDivider(this);

        var themeModule = JSInterop.SetupModule("/js/app-theme.js");

        JSInterop.SetupModule("window.registerGlobalKeydownListener", _ => true);
        JSInterop.SetupModule("window.registerOpenTextVisualizerOnClick", _ => true);
        LayoutSetupHelpers.SetupMobileNavMenuKeyboardNavigation(this);

        JSInterop.Setup<BrowserInfo>("window.getBrowserInfo").SetResult(new BrowserInfo { TimeZone = "abc", UserAgent = "mozilla" });
    }

    private sealed class RecordingJSRuntime : IJSRuntime
    {
        public List<Invocation> Invocations { get; } = [];

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        {
            Invocations.Add(new Invocation(identifier, args ?? []));
            return ValueTask.FromResult(default(TValue)!);
        }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            Invocations.Add(new Invocation(identifier, args ?? []));
            return ValueTask.FromResult(default(TValue)!);
        }

        public sealed record Invocation(string Identifier, object?[] Arguments);
    }
}
