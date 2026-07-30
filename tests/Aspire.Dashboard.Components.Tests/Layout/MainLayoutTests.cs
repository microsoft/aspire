// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Layout;
using Aspire.Dashboard.Components.Resize;
using Aspire.Dashboard.Components.Dialogs;
using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Configuration;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Tests.Shared;
using Aspire.Dashboard.Utils;
using Aspire.Tests.Shared;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Layout;

[UseCulture("en-US")]
public partial class MainLayoutTests : DashboardTestContext
{
    [Fact]
    public async Task OnInitialize_UnsecuredOtlp_NotDismissed_DisplayMessageBar()
    {
        // Arrange
        var testLocalStorage = new TestLocalStorage();
        var messageService = new DashboardMessageService();

        SetupMainLayoutServices(localStorage: testLocalStorage, messageService: messageService);

        DashboardMessage? message = null;
        var messageShownTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        messageService.OnChange += () =>
        {
            if (messageService.GetMessages() is [var shownMessage])
            {
                message = shownMessage;
                messageShownTcs.TrySetResult();
            }
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

        await message.CloseAsync();

        Assert.True(await dismissedSettingSetTcs.Task.DefaultTimeout());
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task OnInitialize_UnsecuredOtlp_Dismissed_NoMessageBar(bool unsecuredTelemetryMessageDismissedKey, bool unsecuredEndpointMessageDismissedKey)
    {
        // Arrange
        var testLocalStorage = new TestLocalStorage();
        var messageService = new DashboardMessageService();

        SetupMainLayoutServices(localStorage: testLocalStorage, messageService: messageService);

        var messageShownTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        messageService.OnChange += () =>
        {
            messageShownTcs.TrySetResult();
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
        Assert.Empty(messageService.GetMessages());
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task OnInitialize_UnsecuredOtlp_SuppressConfigured_NoMessageBar(bool expectMessageBar, bool telemetrySuppressUnsecuredMessage)
    {
        // Arrange
        var testLocalStorage = new TestLocalStorage();
        var messageService = new DashboardMessageService();

        SetupMainLayoutServices(localStorage: testLocalStorage, messageService: messageService, configureOptions: o =>
        {
            o.Otlp.SuppressUnsecuredMessage = telemetrySuppressUnsecuredMessage;
        });

        var messageShownTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        messageService.OnChange += () =>
        {
            messageShownTcs.TrySetResult();
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
            Assert.Empty(messageService.GetMessages());
        }
        else
        {
            // When not suppressed, message should be displayed since it wasn't dismissed
            await messageShownTcs.Task.DefaultTimeout();
            Assert.NotEmpty(messageService.GetMessages());
        }
    }

    [Theory]
    [InlineData(false, "dashboard-navigation-button", "HelpDialog", "dashboard-navigation-button")]
    [InlineData(false, "dashboard-navigation-button", "SettingsDialog", "dashboard-navigation-button")]
    public async Task HeaderDialogClose_RestoresFocusToLaunchButton(bool isDesktop, string launchButtonId, string expectedDialogId, string expectedFocusId)
    {
        DeckDialogParameters? capturedParameters = null;
        TestDialogService? dialogService = null;
        dialogService = new TestDialogService(onShowDialog: (_, parameters) =>
        {
            capturedParameters = parameters;
            return Task.FromResult<IDeckDialogReference>(new TestDialogReference(parameters.Id));
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
            await cut.InvokeAsync(() => cut.FindAll(".mobile-nav-menu-item").Single(item => item.TextContent.Contains(menuItemName, StringComparison.OrdinalIgnoreCase)).Click());
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

    [Theory]
    [InlineData(false, true, "dashboard-navigation-button", "HelpDialog", "dashboard-help-button")]
    [InlineData(false, true, "dashboard-navigation-button", "SettingsDialog", "dashboard-settings-button")]
    public async Task HeaderDialogClose_AfterViewportChange_RestoresFocusToVisibleLaunchButton(
        bool initialIsDesktop,
        bool closingIsDesktop,
        string launchButtonId,
        string expectedDialogId,
        string expectedFocusId)
    {
        DeckDialogParameters? capturedParameters = null;
        TestDialogService? dialogService = null;
        dialogService = new TestDialogService(onShowDialog: (_, parameters) =>
        {
            capturedParameters = parameters;
            return Task.FromResult<IDeckDialogReference>(new TestDialogReference(parameters.Id));
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
            await cut.InvokeAsync(() => cut.FindAll(".mobile-nav-menu-item").Single(item => item.TextContent.Contains(menuItemName, StringComparison.OrdinalIgnoreCase)).Click());
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
    [InlineData(true)]
    [InlineData(false)]
    public async Task Help_Desktop_OpensDeckPaneNotModalDialog(bool viaShortcut)
    {
        // On desktop the help button and the Help keyboard shortcut both open the Deck help pane
        // (HelpPane), not the modal HelpDialog. (Mobile still uses the dialog.)
        DeckDialogParameters? capturedParameters = null;
        TestDialogService? dialogService = null;
        dialogService = new TestDialogService(onShowDialog: (_, parameters) =>
        {
            capturedParameters = parameters;
            return Task.FromResult<IDeckDialogReference>(new TestDialogReference(parameters.Id));
        });

        SetupMainLayoutServices(dialogService: dialogService);
        JSInterop.SetupVoid("focusElement", _ => true);

        var cut = RenderComponent<MainLayout>(builder =>
        {
            builder.Add(p => p.ViewportInformation, new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false));
        });

        if (viaShortcut)
        {
            await cut.InvokeAsync(() => cut.Instance.OnPageKeyDownAsync(AspireKeyboardShortcut.Help));
        }
        else
        {
            await cut.InvokeAsync(() => cut.Find("#dashboard-help-button").Click());
        }

        cut.WaitForAssertion(() => Assert.True(cut.Instance._showHelpPane));
        Assert.Null(capturedParameters);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Settings_Desktop_OpensDeckPaneNotModalDialog(bool viaShortcut)
    {
        // On desktop the settings button and the Settings keyboard shortcut both open the Deck
        // settings pane (SettingsPane), not the modal SettingsDialog. (Mobile still uses the dialog.)
        DeckDialogParameters? capturedParameters = null;
        TestDialogService? dialogService = null;
        dialogService = new TestDialogService(onShowDialog: (_, parameters) =>
        {
            capturedParameters = parameters;
            return Task.FromResult<IDeckDialogReference>(new TestDialogReference(parameters.Id));
        });

        SetupMainLayoutServices(dialogService: dialogService);
        JSInterop.SetupVoid("focusElement", _ => true);

        var cut = RenderComponent<MainLayout>(builder =>
        {
            builder.Add(p => p.ViewportInformation, new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false));
        });

        if (viaShortcut)
        {
            await cut.InvokeAsync(() => cut.Instance.OnPageKeyDownAsync(AspireKeyboardShortcut.Settings));
        }
        else
        {
            await cut.InvokeAsync(() => cut.Find("#dashboard-settings-button").Click());
        }

        cut.WaitForAssertion(() => Assert.True(cut.Instance._showSettingsPane));
        Assert.Null(capturedParameters);
    }

    private void SetupMainLayoutServices(
        TestLocalStorage? localStorage = null,
        DashboardMessageService? messageService = null,
        Action<DashboardOptions>? configureOptions = null,
        DeckDialogService? dialogService = null)
    {
        DashboardSetupHelpers.AddCommonDashboardServices(this, localStorage: localStorage, messageService: messageService);

        if (dialogService is not null)
        {
            Services.AddSingleton(dialogService);
        }

        Services.AddOptions();
        Services.AddSingleton<IThemeResolver, TestThemeResolver>();
        Services.AddSingleton<IDashboardClient, TestDashboardClient>();
        Services.Configure<DashboardOptions>(o =>
        {
            // Configure OTLP endpoint URLs so they can be parsed
            o.Otlp.GrpcEndpointUrl = "http://localhost:4317";
            o.Otlp.AuthMode = OtlpAuthMode.Unsecured;
            configureOptions?.Invoke(o);
            // Call TryParseOptions to populate parsed endpoint addresses
            o.Otlp.TryParseOptions(out _);
        });

        DashboardSetupHelpers.SetupDialogProvider(this);
        DashboardSetupHelpers.SetupOverflow(this);
        DashboardSetupHelpers.SetupAnchor(this);
        DashboardSetupHelpers.SetupButton(this);
        DashboardSetupHelpers.SetupMenu(this);
        DashboardSetupHelpers.SetupAnchoredRegion(this);
        DashboardSetupHelpers.SetupDivider(this);

        var themeModule = JSInterop.SetupModule("/js/app-theme.js");

        JSInterop.SetupModule("window.registerGlobalKeydownListener", _ => true);
        JSInterop.SetupModule("window.registerOpenTextVisualizerOnClick", _ => true);

        JSInterop.Setup<BrowserInfo>("window.getBrowserInfo").SetResult(new BrowserInfo { TimeZone = "abc", UserAgent = "mozilla" });

        // The Deck Drawer (used by SettingsPane / HelpPane / the interaction pane) imports a JS
        // module to wire Escape-to-close. Set it up so those panes can render under bUnit's strict
        // JSInterop without throwing on the module import.
        var drawerModule = JSInterop.SetupModule("./Components/Deck/Drawer.razor.js");
        drawerModule.SetupVoid("registerDrawerEscape", _ => true);
        drawerModule.SetupVoid("disposeDrawerEscape", _ => true);

        var mobileNavModule = JSInterop.SetupModule("./Components/Layout/MobileNavMenu.razor.js");
        mobileNavModule.SetupVoid("initializeMobileNavMenu", _ => true);
        mobileNavModule.SetupVoid("disposeMobileNavMenu", _ => true);
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
