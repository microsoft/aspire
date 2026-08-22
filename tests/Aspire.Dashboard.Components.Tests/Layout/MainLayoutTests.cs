// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Controls;
using Aspire.Dashboard.Components.Layout;
using Aspire.Dashboard.Components.Resize;
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
using Microsoft.FluentUI.AspNetCore.Components;
using Microsoft.JSInterop;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Layout;

[UseCulture("en-US")]
public partial class MainLayoutTests : DashboardTestContext
{
    private IRenderedComponent<FluentMessageBarProvider>? _messageBarProvider;

    [Fact]
    public void NotificationChange_RefreshesToastProvider()
    {
        SetupMainLayoutServices();
        var cut = RenderComponent<MainLayout>(builder =>
        {
            builder.Add(p => p.ViewportInformation, new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false));
        });
        var notificationService = Services.GetRequiredService<Aspire.Dashboard.Model.INotificationService>();

        Assert.Equal("0", cut.Find("[data-update-version]").GetAttribute("data-update-version"));

        notificationService.AddNotification(new NotificationEntry
        {
            Title = "Test notification",
            Intent = MessageBarIntent.Info
        });

        cut.WaitForAssertion(() => Assert.Equal("1", cut.Find("[data-update-version]").GetAttribute("data-update-version")));
    }
    [Fact]
    public async Task OnInitialize_UnsecuredOtlp_NotDismissed_DisplayMessageBar()
    {
        // Arrange
        var testLocalStorage = new TestLocalStorage();
        SetupMainLayoutServices(localStorage: testLocalStorage);

        testLocalStorage.OnGetUnprotectedAsync = key =>
        {
            switch (key)
            {
                case BrowserStorageKeys.NavMenuExpanded:
                    return (true, false);
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
        var dismissButton = _messageBarProvider!.WaitForElement($"fluent-button[aria-label='{Aspire.Dashboard.Resources.Dialogs.NotificationEntryDismiss}']");
        dismissButton.Click();

        Assert.True(await dismissedSettingSetTcs.Task.DefaultTimeout());
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task OnInitialize_UnsecuredOtlp_Dismissed_NoMessageBar(bool unsecuredTelemetryMessageDismissedKey, bool unsecuredEndpointMessageDismissedKey)
    {
        // Arrange
        var testLocalStorage = new TestLocalStorage();
        SetupMainLayoutServices(localStorage: testLocalStorage);

        testLocalStorage.OnGetUnprotectedAsync = key =>
        {
            switch (key)
            {
                case BrowserStorageKeys.NavMenuExpanded:
                    return (true, false);
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
        Assert.Empty(_messageBarProvider!.FindComponents<DashboardMessageBar>());
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task OnInitialize_UnsecuredOtlp_SuppressConfigured_NoMessageBar(bool expectMessageBar, bool telemetrySuppressUnsecuredMessage)
    {
        // Arrange
        var testLocalStorage = new TestLocalStorage();
        SetupMainLayoutServices(localStorage: testLocalStorage, configureOptions: o =>
        {
            o.Otlp.SuppressUnsecuredMessage = telemetrySuppressUnsecuredMessage;
        });

        testLocalStorage.OnGetUnprotectedAsync = key =>
        {
            switch (key)
            {
                case BrowserStorageKeys.NavMenuExpanded:
                    return (true, false);
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
            Assert.Empty(_messageBarProvider!.FindComponents<DashboardMessageBar>());
        }
        else
        {
            var messageBarProvider = _messageBarProvider!;
            messageBarProvider.WaitForAssertion(() => Assert.Single(messageBarProvider.FindComponents<DashboardMessageBar>()));
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NavMenuExpanded_RestoresAndPersistsToggledState(bool storedExpanded)
    {
        object? persistedValue = null;
        var localStorage = new TestLocalStorage
        {
            OnGetUnprotectedAsync = key => key switch
            {
                BrowserStorageKeys.NavMenuExpanded => (true, storedExpanded),
                BrowserStorageKeys.UnsecuredTelemetryMessageDismissedKey => (false, false),
                BrowserStorageKeys.UnsecuredEndpointMessageDismissedKey => (false, false),
                _ => throw new InvalidOperationException("Unexpected key.")
            },
            OnSetUnprotectedAsync = (key, value) =>
            {
                Assert.Equal(BrowserStorageKeys.NavMenuExpanded, key);
                persistedValue = value;
            }
        };

        SetupMainLayoutServices(localStorage: localStorage);

        var cut = RenderComponent<MainLayout>(builder =>
        {
            builder.Add(p => p.ViewportInformation, new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false));
        });

        cut.WaitForAssertion(() => Assert.Contains(storedExpanded ? "nav-expanded" : "nav-collapsed", cut.Find(".layout").ClassList));

        await cut.InvokeAsync(() => cut.Find(".nav-toggle-button").Click());

        cut.WaitForAssertion(() => Assert.Contains(storedExpanded ? "nav-collapsed" : "nav-expanded", cut.Find(".layout").ClassList));
        Assert.Equal(!storedExpanded, Assert.IsType<bool>(persistedValue));
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
            return Task.CompletedTask;
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
            var shortcut = expectedDialogId == "HelpDialog" ? AspireKeyboardShortcut.Help : AspireKeyboardShortcut.Settings;
            await cut.InvokeAsync(() => cut.Instance.OnPageKeyDownAsync(shortcut));
        }

        Assert.NotNull(capturedParameters);
        Assert.Equal(expectedDialogId, capturedParameters.Id);

        await cut.InvokeAsync(() => capturedParameters.OnDialogClosing.InvokeAsync(dialogService.LastInstance!));

        cut.WaitForAssertion(() =>
        {
            Assert.Contains(JSInterop.Invocations, invocation =>
                invocation.Identifier == "focusElement" &&
                invocation.Arguments.Count == 1 &&
                string.Equals((string?)invocation.Arguments[0], expectedFocusId, StringComparison.Ordinal));
        });
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
            return Task.CompletedTask;
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
            var shortcut = expectedDialogId == "HelpDialog" ? AspireKeyboardShortcut.Help : AspireKeyboardShortcut.Settings;
            await cut.InvokeAsync(() => cut.FindComponent<MainLayout>().Instance.OnPageKeyDownAsync(shortcut));
        }

        Assert.NotNull(capturedParameters);
        Assert.Equal(expectedDialogId, capturedParameters.Id);

        cut.SetParametersAndRender(parameters =>
        {
            parameters.Add(p => p.Value, new ViewportInformation(IsDesktop: closingIsDesktop, IsUltraLowHeight: false, IsUltraLowWidth: false));
            parameters.AddChildContent<MainLayout>();
        });

        await cut.InvokeAsync(() => capturedParameters.OnDialogClosing.InvokeAsync(dialogService.LastInstance!));

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
            return Task.CompletedTask;
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

        await cut.InvokeAsync(() => capturedParameters.OnDialogClosing.InvokeAsync(dialogService.LastInstance!));

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
        Action<DashboardOptions>? configureOptions = null,
        IDialogService? dialogService = null)
    {
        FluentUISetupHelpers.AddCommonDashboardServices(this, localStorage: localStorage);

        if (dialogService is not null)
        {
            Services.AddSingleton(dialogService);
        }

        Services.AddOptions();
        Services.AddSingleton<IThemeResolver, TestThemeResolver>();
        Services.AddSingleton<IDashboardClient, TestDashboardClient>();
        Services.AddSingleton<ITooltipService, TooltipService>();
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
        FluentUISetupHelpers.SetupFluentTextField(this);
        FluentUISetupHelpers.SetupFluentMenu(this);
        FluentUISetupHelpers.SetupFluentAnchoredRegion(this);
        FluentUISetupHelpers.SetupFluentDivider(this);

        var themeModule = JSInterop.SetupModule("/js/app-theme.js");
        JSInterop.SetupVoid("Blazor.theme.setThemeMode", _ => true);

        JSInterop.SetupModule("window.registerGlobalKeydownListener", _ => true);
        JSInterop.SetupModule("window.registerOpenTextVisualizerOnClick", _ => true);
        LayoutSetupHelpers.SetupMobileNavMenuKeyboardNavigation(this);

        _messageBarProvider = RenderComponent<FluentMessageBarProvider>(builder =>
        {
            builder.Add(p => p.Section, DashboardUIHelpers.MessageBarSection);
        });

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
