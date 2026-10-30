// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Threading.Channels;
using Aspire.Dashboard.Components.Controls;
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
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.FluentUI.AspNetCore.Components;
using Microsoft.JSInterop;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Layout;

[UseCulture("en-US")]
public partial class MainLayoutTests : DashboardTestContext
{
    private IRenderedComponent<FluentMessageBarProvider>? _messageBarProvider;

    [Fact]
    public async Task HeaderHelp_IsSingleActionThatOpensHelpDialog()
    {
        DialogParameters? capturedParameters = null;
        var dialogService = new TestDialogService(onShowDialog: (_, parameters) =>
        {
            capturedParameters = parameters;
            return Task.CompletedTask;
        });
        SetupMainLayoutServices(dialogService: dialogService);
        var cut = Render<MainLayout>(builder => builder.Add(p => p.ViewportInformation,
            new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false)));

        var helpButton = cut.Find($"#{MainLayout.HelpButtonId}");
        Assert.Equal(Resources.Layout.MainLayoutHelp, helpButton.GetAttribute("aria-label"));
        Assert.Equal("?", helpButton.GetAttribute("aria-keyshortcuts"));
        Assert.Empty(cut.FindAll($"header a[aria-label='{Resources.Layout.MainLayoutAspireDashboardHelpLink}']"));

        await cut.InvokeAsync(() => helpButton.Click());

        Assert.NotNull(capturedParameters);
        Assert.Equal("HelpDialog", capturedParameters.Id);
        Assert.Equal(Resources.Layout.MainLayoutHelp, capturedParameters.Title);
    }

    [Theory]
    [InlineData(".logo")]
    [InlineData(".application-name")]
    public async Task Logo_OpensAllResourcesOverviewWithoutRestoringSelection(string selector)
    {
        var stored = new Dictionary<string, object?>();
        SetupMainLayoutServices(
            sessionStorage: new TestSessionStorage { OnSetAsync = (key, value) => stored[key] = value },
            dashboardClient: new TestDashboardClient(isEnabled: true, resourceChannelProvider: Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>));
        var navigationManager = Services.GetRequiredService<NavigationManager>();
        var cut = Render<MainLayout>(builder => builder.Add(p => p.ViewportInformation,
            new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false)));

        await cut.InvokeAsync(() => cut.Find($"header {selector}").Click());

        Assert.Equal(DashboardUrls.ResourceListUrl(), "/" + navigationManager.ToBaseRelativePath(navigationManager.Uri));
        Assert.Empty(Assert.IsType<List<string>>(stored[BrowserStorageKeys.LastSelectedResources]));
    }

    [Theory]
    [InlineData(AspireKeyboardShortcut.GoToResources, "/resources")]
    [InlineData(AspireKeyboardShortcut.GoToParameters, "/parameters")]
    [InlineData(AspireKeyboardShortcut.GoToGraph, "/resources")]
    [InlineData(AspireKeyboardShortcut.GoToExtensions, "/extensions")]
    [InlineData(AspireKeyboardShortcut.GoToTelemetrySources, "/structuredlogs?pane=telemetry")]
    [InlineData(AspireKeyboardShortcut.GoToConsoleLogs, "/consolelogs")]
    public async Task NavigationShortcut_OpensDestination(AspireKeyboardShortcut shortcut, string expectedUrl)
    {
        SetupMainLayoutServices(dashboardClient: new TestDashboardClient(isEnabled: true,
            resourceChannelProvider: () => Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>()));
        var navigationManager = Services.GetRequiredService<NavigationManager>();
        navigationManager.NavigateTo("/consolelogs/resource/api?resource=api&resource=worker");
        var cut = Render<MainLayout>(builder => builder.Add(p => p.ViewportInformation,
            new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false)));
        var shortcuts = Services.GetRequiredService<ShortcutManager>();
        Assert.True(shortcuts.IsShortcutAvailable(shortcut));

        await cut.InvokeAsync(() => shortcuts.OnGlobalKeyDown(shortcut));

        Assert.Equal(navigationManager.ToAbsoluteUri(expectedUrl).AbsoluteUri, navigationManager.Uri);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task NavigationShortcuts_RespectResourceServiceAndGraphAvailability(bool resourceServiceEnabled, bool graphDisabled)
    {
        SetupMainLayoutServices(dashboardClient: new TestDashboardClient(isEnabled: resourceServiceEnabled,
            resourceChannelProvider: () => Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>()),
            configureOptions: options => options.UI.DisableResourceGraph = graphDisabled);
        var navigationManager = Services.GetRequiredService<NavigationManager>();
        var cut = Render<MainLayout>(builder => builder.Add(p => p.ViewportInformation,
            new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false)));
        var shortcuts = Services.GetRequiredService<ShortcutManager>();

        foreach (var shortcut in new[]
        {
            AspireKeyboardShortcut.GoToResources,
            AspireKeyboardShortcut.GoToParameters,
            AspireKeyboardShortcut.GoToGraph,
            AspireKeyboardShortcut.GoToExtensions,
            AspireKeyboardShortcut.GoToConsoleLogs
        })
        {
            var expectedAvailable = resourceServiceEnabled && (shortcut != AspireKeyboardShortcut.GoToGraph || !graphDisabled);
            Assert.Equal(expectedAvailable, shortcuts.IsShortcutAvailable(shortcut));
            if (!expectedAvailable)
            {
                var initialUri = navigationManager.Uri;
                await cut.InvokeAsync(() => cut.Instance.OnPageKeyDownAsync(shortcut));
                Assert.Equal(initialUri, navigationManager.Uri);
            }
        }

        Assert.True(shortcuts.IsShortcutAvailable(AspireKeyboardShortcut.GoToTelemetrySources));
        await cut.InvokeAsync(() => shortcuts.OnGlobalKeyDown(AspireKeyboardShortcut.GoToTelemetrySources));
        Assert.Equal(navigationManager.ToAbsoluteUri(DashboardUrls.TelemetrySourcesUrl()).AbsoluteUri, navigationManager.Uri);
    }

    [Fact]
    public void OnInitialize_BrowserInfoCircuitDisconnected_DoesNotThrow()
    {
        SetupMainLayoutServices();
        JSInterop.Setup<BrowserInfo>("window.getBrowserInfo").SetException(new JSDisconnectedException("The circuit disconnected."));

        var cut = Render<MainLayout>(builder => builder
            .Add(p => p.ViewportInformation, new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false)));

        cut.WaitForAssertion(() => Assert.Single(JSInterop.Invocations["window.getBrowserInfo"]));
        Assert.False(Renderer.UnhandledException.IsCompleted);
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
                case BrowserStorageKeys.UnsecuredTelemetryMessageDismissedKey:
                case BrowserStorageKeys.UnsecuredEndpointMessageDismissedKey:
                    return (false, false);
                case BrowserStorageKeys.DashboardFeedbackPromptDismissed:
                    return (true, true);
                // The view preferences are read with the layout. None are stored here.
                case BrowserStorageKeys.ResourceListDensity:
                case BrowserStorageKeys.GroupOverview:
                case BrowserStorageKeys.RecordingsStyle:
                    return (false, null);
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
        var cut = Render<MainLayout>(builder =>
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
                case BrowserStorageKeys.UnsecuredTelemetryMessageDismissedKey:
                    return (unsecuredTelemetryMessageDismissedKey, unsecuredTelemetryMessageDismissedKey);
                case BrowserStorageKeys.UnsecuredEndpointMessageDismissedKey:
                    return (unsecuredEndpointMessageDismissedKey, unsecuredEndpointMessageDismissedKey);
                case BrowserStorageKeys.DashboardFeedbackPromptDismissed:
                    return (true, true);
                // The view preferences are read with the layout. None are stored here.
                case BrowserStorageKeys.ResourceListDensity:
                case BrowserStorageKeys.GroupOverview:
                case BrowserStorageKeys.RecordingsStyle:
                    return (false, null);
                default:
                    throw new InvalidOperationException("Unexpected key.");
            }
        };

        // Act
        var cut = Render<MainLayout>(builder =>
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
                case BrowserStorageKeys.UnsecuredTelemetryMessageDismissedKey:
                case BrowserStorageKeys.UnsecuredEndpointMessageDismissedKey:
                    return (false, false); // Message not dismissed, but should be suppressed by config if suppressUnsecuredMessage is true
                case BrowserStorageKeys.DashboardFeedbackPromptDismissed:
                    return (true, true);
                // The view preferences are read with the layout. None are stored here.
                case BrowserStorageKeys.ResourceListDensity:
                case BrowserStorageKeys.GroupOverview:
                case BrowserStorageKeys.RecordingsStyle:
                    return (false, null);
                default:
                    throw new InvalidOperationException("Unexpected key.");
            }
        };

        // Act
        var cut = Render<MainLayout>(builder =>
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
            messageBarProvider.WaitForAssertion(() =>
            {
                var messageBar = Assert.Single(messageBarProvider.FindComponents<DashboardMessageBar>());
                Assert.Contains("intent-warning", messageBar.Find("[role='alert']").ClassList);
            });
        }
    }

    [Theory]
    [InlineData(BrowserStorageKeys.UnsecuredEndpointMessageDismissedKey)]
    [InlineData(BrowserStorageKeys.UnsecuredTelemetryMessageDismissedKey)]
    public void OnInitialize_BrowserStorageReadFails_ContinuesInitialization(string failedKey)
    {
        var readKeys = new List<string>();
        var localStorage = new TestLocalStorage
        {
            OnGetUnprotectedAsync = key =>
            {
                // The view preferences are read with the layout too, and aren't what this test is about.
                if (key == BrowserStorageKeys.DashboardFeedbackPromptDismissed)
                {
                    return (true, true);
                }

                if (key is BrowserStorageKeys.ResourceListDensity or BrowserStorageKeys.GroupOverview or BrowserStorageKeys.RecordingsStyle)
                {
                    return (false, null);
                }

                readKeys.Add(key);
                return key == failedKey ? (false, false) : (true, false);
            }
        };
        SetupMainLayoutServices(localStorage: localStorage);

        var cut = Render<MainLayout>(builder =>
        {
            builder.Add(p => p.ViewportInformation, new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false));
        });

        Assert.Equal(
            [BrowserStorageKeys.UnsecuredEndpointMessageDismissedKey, BrowserStorageKeys.UnsecuredTelemetryMessageDismissedKey],
            readKeys);
        var messageBarProvider = _messageBarProvider!;
        messageBarProvider.WaitForAssertion(() => Assert.Single(messageBarProvider.FindComponents<DashboardMessageBar>()));
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

        var cut = Render<MainLayout>(builder =>
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
                ? Resources.Layout.MainLayoutKeyboardShortcuts
                : "Settings";

            await cut.InvokeAsync(() => cut.Find("#dashboard-navigation-button").Click());
            var menuItem = cut.FindAll("fluent-menu-item").Single(item => item.TextContent.Contains(menuItemName, StringComparison.OrdinalIgnoreCase));
            await cut.InvokeAsync(() => menuItem.TriggerEvent("onmenuitemchange", new MenuItemEventArgs { Id = menuItem.Id, Text = menuItem.TextContent }));
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
    [InlineData(true)]
    [InlineData(false)]
    public void DashboardRunSelect_Supported_IsDisplayedWithTheOtherHeaderButtons(bool isDesktop)
    {
        SetupMainLayoutServices();

        var cut = Render<MainLayout>(builder =>
        {
            builder.Add(p => p.ViewportInformation, new ViewportInformation(IsDesktop: isDesktop, IsUltraLowHeight: false, IsUltraLowWidth: false));
        });

        Assert.Single(cut.FindComponents<DashboardRunSelect>());
        var runSelectIndex = cut.Markup.IndexOf($"id=\"{MainLayout.RunSelectButtonId}\"", StringComparison.Ordinal);
        Assert.True(runSelectIndex >= 0);
        if (isDesktop)
        {
            // Run selection is the first app-wide action after navigation.
            Assert.True(runSelectIndex < cut.Markup.IndexOf($"id=\"{MainLayout.HelpButtonId}\"", StringComparison.Ordinal));
        }
        else
        {
            Assert.True(runSelectIndex < cut.Markup.IndexOf($"id=\"{MainLayout.NavigationButtonId}\"", StringComparison.Ordinal));
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RecordingsMenuSetting_ShowsTheLiveRunMenuInsteadOfTheRecordingsButton(bool isDesktop)
    {
        var localStorage = new TestLocalStorage
        {
            OnGetUnprotectedAsync = key => key == BrowserStorageKeys.RecordingsStyle ? (true, "Menu") : (false, null)
        };
        SetupMainLayoutServices(localStorage: localStorage);

        var cut = Render<MainLayout>(builder =>
        {
            builder.Add(p => p.ViewportInformation, new ViewportInformation(IsDesktop: isDesktop, IsUltraLowHeight: false, IsUltraLowWidth: false));
        });

        cut.WaitForAssertion(() => Assert.Single(cut.FindComponents<DashboardRunMenu>()));
        Assert.Empty(cut.FindComponents<DashboardRunSelect>());
        Assert.Empty(cut.FindComponents<DashboardRunPill>());
        var menuButton = Assert.Single(cut.FindComponent<DashboardRunMenu>().FindComponents<AspireMenuButton>());
        Assert.Equal("Live run", menuButton.Instance.Text);
        Assert.Equal(["Live run", "Learn about runs"], menuButton.Instance.ItemsProvider().Where(item => !item.IsDivider).Select(item => item.Text));
        if (isDesktop)
        {
            // Where it was before recordings had their own panel: with the header's buttons, ahead of the help button.
            var menuIndex = cut.Markup.IndexOf("application-run-select", StringComparison.Ordinal);
            Assert.True(menuIndex < cut.Markup.IndexOf($"id=\"{MainLayout.HelpButtonId}\"", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task DashboardRunSelect_Click_OpensRecordingsPanel()
    {
        object? capturedContent = null;
        DialogParameters? capturedParameters = null;
        var dialogService = new TestDialogService(onShowDialog: (content, parameters) =>
        {
            capturedContent = content;
            capturedParameters = parameters;
            return Task.CompletedTask;
        });
        SetupMainLayoutServices(dialogService: dialogService);
        var cut = Render<MainLayout>(builder => builder.Add(p => p.ViewportInformation,
            new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false)));

        var button = cut.Find($"#{MainLayout.RunSelectButtonId}");
        Assert.Equal("Recordings, viewing Live view", button.GetAttribute("aria-label"));
        Assert.Equal("Recordings", button.GetAttribute("data-tooltip"));
        Assert.Equal("dialog", button.GetAttribute("aria-haspopup"));
        Assert.Single(cut.FindAll($"#{MainLayout.RunSelectButtonId} .dashboard-run-indicator.live"));
        Assert.Empty(cut.FindAll(".dashboard-run-pill"));

        await cut.InvokeAsync(() => button.Click());

        Assert.NotNull(capturedParameters);
        Assert.Equal("RecordingsDialog", capturedParameters.Id);
        Assert.Equal("Recordings", capturedParameters.Title);
        Assert.Equal(HorizontalAlignment.Right, capturedParameters.Alignment);
        var content = Assert.IsType<DashboardRunsDialogContent>(capturedContent);
        Assert.Equal(Services.GetRequiredService<IDashboardRunSelection>().SelectedRun.RunId, content.SelectedRunId);
    }

    [Fact]
    public async Task DashboardRunSelect_SwitchingRunsStoresAndAppliesSelectionWithoutNavigation()
    {
        var historicalRun = new DashboardRunDescriptor(
            RunId: "historical",
            SchemaVersion: DashboardRunStore.SchemaVersion,
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
                SchemaVersion: DashboardRunStore.SchemaVersion,
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
        DashboardRunsDialogContent? content = null;
        var dialogService = new TestDialogService(onShowDialog: (dialogContent, _) =>
        {
            content = Assert.IsType<DashboardRunsDialogContent>(dialogContent);
            return Task.CompletedTask;
        });
        var localStorage = new TestLocalStorage
        {
            OnGetUnprotectedAsync = key => key == BrowserStorageKeys.RecordingsStyle ? (true, "Panel") : (false, null)
        };
        SetupMainLayoutServices(localStorage: localStorage, dialogService: dialogService, dashboardRunStore: runStore, sessionStorage: sessionStorage);
        await Services.GetRequiredService<DashboardViewPreferences>().EnsureLoadedAsync();
        var expectedHistoricalRunText = FormatHelpers.FormatDateTime(
            Services.GetRequiredService<BrowserTimeProvider>(),
            historicalRun.StartedAtUtc.UtcDateTime);
        var initializedCount = 0;
        var disposedCount = 0;
        var cut = Render<MainLayout>(builder =>
        {
            builder.Add(component => component.ViewportInformation, new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false));
            builder.Add(component => component.Body, bodyBuilder =>
            {
                bodyBuilder.OpenComponent<LifecycleTestComponent>(0);
                bodyBuilder.AddComponentParameter(1, nameof(LifecycleTestComponent.Initialized), (Action)(() => initializedCount++));
                bodyBuilder.AddComponentParameter(2, nameof(LifecycleTestComponent.Disposed), (Action)(() => disposedCount++));
                bodyBuilder.CloseComponent();
            });
        });
        var navigationOccurred = false;
        Services.GetRequiredService<NavigationManager>().LocationChanged += (_, _) => navigationOccurred = true;
        var runSelection = Assert.IsType<FluentUISetupHelpers.TestDashboardRunSelection>(Services.GetRequiredService<IDashboardRunSelection>());

        await cut.InvokeAsync(() => cut.Find($"#{MainLayout.RunSelectButtonId}").Click());
        Assert.NotNull(content);
        await cut.InvokeAsync(() => content.SelectRunAsync("historical"));

        Assert.Equal("historical", storedRunId);
        Assert.Equal("historical", runSelection.SelectedRunId);
        Assert.False(navigationOccurred);
        Assert.Equal(2, initializedCount);
        Assert.Equal(1, disposedCount);
        var pill = cut.Find(".dashboard-run-pill");
        Assert.Equal(expectedHistoricalRunText, pill.QuerySelector(".dashboard-run-pill-text")!.TextContent);
        Assert.Equal($"Recordings, viewing {expectedHistoricalRunText}", pill.GetAttribute("aria-label"));
        Assert.Single(pill.QuerySelectorAll(".dashboard-run-indicator.recording"));
        var historicalRunButton = cut.Find($"#{MainLayout.RunSelectButtonId}");
        Assert.Equal($"Recordings, viewing {expectedHistoricalRunText}", historicalRunButton.GetAttribute("aria-label"));
        Assert.Single(historicalRunButton.QuerySelectorAll(".dashboard-run-indicator.recording"));

        // The pill opens the same recordings panel as the header button. The panel closes itself when a run is
        // selected; the test dialog service doesn't render it, so close it here.
        await cut.InvokeAsync(() => dialogService.LastInstance!.CloseAsync());
        content = null;
        // The layout sees the first panel as closed once its result completes asynchronously.
        cut.WaitForAssertion(() =>
        {
            cut.Find(".dashboard-run-pill").Click();
            Assert.NotNull(content);
        });
        Assert.NotNull(content);

        await cut.InvokeAsync(() => content.SelectRunAsync(null));

        Assert.Equal(string.Empty, storedRunId);
        Assert.Null(runSelection.SelectedRunId);
        Assert.False(navigationOccurred);
        Assert.Equal(3, initializedCount);
        Assert.Equal(2, disposedCount);
        Assert.Empty(cut.FindAll(".dashboard-run-pill"));
        Assert.Single(cut.FindAll($"#{MainLayout.RunSelectButtonId} .dashboard-run-indicator.live"));
    }

    [Fact]
    public async Task DashboardRunSelect_SelectionFailure_KeepsCurrentRunAndCircuitActive()
    {
        var runStore = new FluentUISetupHelpers.TestDashboardRunStore(
        [
            new(
                RunId: "current",
                SchemaVersion: DashboardRunStore.SchemaVersion,
                StartedAtUtc: DateTimeOffset.UnixEpoch,
                EndedAtUtc: null,
                CleanShutdown: false,
                ApplicationName: "TestApp",
                DatabasePath: string.Empty,
                IsCurrent: true),
            new(
                RunId: "historical",
                SchemaVersion: DashboardRunStore.SchemaVersion,
                StartedAtUtc: DateTimeOffset.UnixEpoch,
                EndedAtUtc: DateTimeOffset.UnixEpoch,
                CleanShutdown: true,
                ApplicationName: "TestApp",
                DatabasePath: string.Empty,
                IsCurrent: false)
        ]);
        string? storedRunId = null;
        var sessionStorage = new TestSessionStorage
        {
            OnSetAsync = (_, value) => storedRunId = Assert.IsType<string>(value)
        };
        var testSink = new TestSink();
        Services.AddSingleton<ILogger<MainLayout>>(new TestLogger<MainLayout>(new TestLoggerFactory(testSink, enabled: true)));
        SetupMainLayoutServices(dashboardRunStore: runStore, sessionStorage: sessionStorage);
        var runSelection = Assert.IsType<FluentUISetupHelpers.TestDashboardRunSelection>(Services.GetRequiredService<IDashboardRunSelection>());
        var exception = new InvalidOperationException("The historical database could not be opened.");
        runSelection.OnSelectRun = runId =>
        {
            if (runId == "historical")
            {
                throw exception;
            }
        };

        var cut = Render<MainLayout>(builder =>
        {
            builder.Add(component => component.ViewportInformation, new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false));
            builder.Add(component => component.Body, bodyBuilder => bodyBuilder.AddMarkupContent(0, "<div id=\"body-content\"></div>"));
        });

        await cut.InvokeAsync(() => cut.Instance.SwitchDashboardRunAsync("historical"));

        Assert.NotNull(cut.Find("#body-content"));
        Assert.True(runSelection.SelectedRun.IsCurrent);
        Assert.Equal(string.Empty, storedRunId);
        Assert.Empty(cut.FindAll(".dashboard-run-pill"));
        var errorLog = Assert.Single(testSink.Writes);
        Assert.Equal(LogLevel.Error, errorLog.LogLevel);
        Assert.Equal("Failed to switch to dashboard run 'historical'. Keeping dashboard run 'current' selected.", errorLog.Message);
        Assert.Same(exception, errorLog.Exception);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DashboardRunSelect_Unsupported_IsHiddenAndStaleSelectionIsIgnored(bool isDesktop)
    {
        var runStore = new FluentUISetupHelpers.TestDashboardRunStore(
        [
            new(
                RunId: "current",
                SchemaVersion: DashboardRunStore.SchemaVersion,
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
            OnGetAsync = key => key == BrowserStorageKeys.DashboardFeedbackPromptDismissedForSession
                ? (false, false)
                : throw new InvalidOperationException("Run selection should not be read.")
        };
        SetupMainLayoutServices(dashboardRunStore: runStore, sessionStorage: sessionStorage);
        var runSelection = Assert.IsType<FluentUISetupHelpers.TestDashboardRunSelection>(Services.GetRequiredService<IDashboardRunSelection>());
        var getRunsCallCount = runStore.GetRunsCallCount;

        var cut = Render<MainLayout>(builder =>
        {
            builder.Add(
                component => component.ViewportInformation,
                new ViewportInformation(IsDesktop: isDesktop, IsUltraLowHeight: false, IsUltraLowWidth: false));
        });

        Assert.Empty(cut.FindComponents<DashboardRunSelect>());
        Assert.Equal(getRunsCallCount, runStore.GetRunsCallCount);
        Assert.Null(runSelection.SelectedRunId);
    }

    [Fact]
    public void DashboardRunSelect_GetSortedRuns_FiltersAndSortsRuns()
    {
        var startedAtUtc = new DateTimeOffset(2025, 1, 2, 12, 0, 0, TimeSpan.Zero);
        var currentRun = new DashboardRunDescriptor("current", DashboardRunStore.SchemaVersion, DateTimeOffset.UnixEpoch, null, false, "TestApp", string.Empty, IsCurrent: true);
        var pinnedRunB = new DashboardRunDescriptor("pinned-b", DashboardRunStore.SchemaVersion, startedAtUtc, null, true, "TestApp", string.Empty, IsCurrent: false) { IsPinned = true };
        var pinnedRunA = new DashboardRunDescriptor("pinned-a", DashboardRunStore.SchemaVersion, startedAtUtc, null, true, "TestApp", string.Empty, IsCurrent: false) { IsPinned = true };
        var olderPinnedRun = new DashboardRunDescriptor("pinned-older", DashboardRunStore.SchemaVersion, startedAtUtc.AddDays(-1), null, true, "TestApp", string.Empty, IsCurrent: false) { IsPinned = true };
        var unpinnedRun = new DashboardRunDescriptor("unpinned", DashboardRunStore.SchemaVersion, startedAtUtc.AddDays(1), null, true, "TestApp", string.Empty, IsCurrent: false);
        var incompatibleRun = new DashboardRunDescriptor("incompatible", DashboardRunStore.SchemaVersion - 1, startedAtUtc.AddDays(2), null, true, "TestApp", string.Empty, IsCurrent: false) { IsSelectable = false };
        var unavailableRun = new DashboardRunDescriptor("unavailable", DashboardRunStore.SchemaVersion, startedAtUtc.AddDays(3), null, true, "TestApp", string.Empty, IsCurrent: false) { IsSelectable = false };
        var prunedRun = new DashboardRunDescriptor("pruned", DashboardRunStore.SchemaVersion, startedAtUtc.AddDays(4), null, true, "TestApp", string.Empty, IsCurrent: false) { IsPruned = true };

        var runs = DashboardRunSelect.GetSortedRuns(
        [
            unavailableRun,
            pinnedRunB,
            unpinnedRun,
            prunedRun,
            olderPinnedRun,
            incompatibleRun,
            currentRun,
            pinnedRunA
        ]);

        Assert.Equal(
            ["current", "pinned-a", "pinned-b", "pinned-older", "incompatible", "unpinned"],
            runs.Select(run => run.RunId));
    }

    [Fact]
    public void DashboardRunSelect_FormatRunAge_UsesRelativeTime()
    {
        SetupMainLayoutServices();
        var loc = Services.GetRequiredService<IStringLocalizer<Aspire.Dashboard.Resources.Layout>>();
        var now = new DateTimeOffset(2025, 1, 10, 12, 0, 0, TimeSpan.Zero);

        Assert.Equal("a moment ago", DashboardRunSelect.FormatRunAge(now.AddSeconds(-59), now, loc));
        Assert.Equal("a moment ago", DashboardRunSelect.FormatRunAge(now.AddSeconds(5), now, loc));
        Assert.Equal("1 minute ago", DashboardRunSelect.FormatRunAge(now.AddMinutes(-1), now, loc));
        Assert.Equal("45 minutes ago", DashboardRunSelect.FormatRunAge(now.AddMinutes(-45).AddSeconds(-30), now, loc));
        Assert.Equal("1h ago", DashboardRunSelect.FormatRunAge(now.AddMinutes(-60), now, loc));
        Assert.Equal("23h ago", DashboardRunSelect.FormatRunAge(now.AddHours(-23).AddMinutes(-59), now, loc));
        Assert.Equal("1 day ago", DashboardRunSelect.FormatRunAge(now.AddDays(-1), now, loc));
        Assert.Equal("2 days ago", DashboardRunSelect.FormatRunAge(now.AddDays(-2).AddHours(-5), now, loc));
    }

    [Fact]
    public void DashboardRunSelect_FormatRunDuration_UsesRecordedEndTime()
    {
        var startedAtUtc = new DateTimeOffset(2025, 1, 2, 12, 0, 0, TimeSpan.Zero);
        DashboardRunDescriptor CreateRun(DateTimeOffset? endedAtUtc, bool isCurrent = false) =>
            new("run", DashboardRunStore.SchemaVersion, startedAtUtc, endedAtUtc, CleanShutdown: endedAtUtc is not null, "TestApp", string.Empty, isCurrent);

        Assert.Equal("(26m 6s)", DashboardRunSelect.FormatRunDuration(CreateRun(startedAtUtc.AddMinutes(26).AddSeconds(6.4))));
        Assert.Null(DashboardRunSelect.FormatRunDuration(CreateRun(endedAtUtc: null)));
        Assert.Null(DashboardRunSelect.FormatRunDuration(CreateRun(startedAtUtc.AddMinutes(-1))));
        Assert.Null(DashboardRunSelect.FormatRunDuration(CreateRun(startedAtUtc.AddMinutes(5), isCurrent: true)));
    }

    [Fact]
    public async Task RunSelectionPending_RendersCurrentRunWithoutLoadingRuns()
    {
        var runStore = new FluentUISetupHelpers.TestDashboardRunStore(
        [
            new(
                RunId: "current",
                SchemaVersion: DashboardRunStore.SchemaVersion,
                StartedAtUtc: DateTimeOffset.UnixEpoch,
                EndedAtUtc: null,
                CleanShutdown: false,
                ApplicationName: "TestApp",
                DatabasePath: string.Empty,
                IsCurrent: true)
        ]);
        var runSelectionSource = new TaskCompletionSource<(bool Success, object? Value)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sessionStorage = new TestSessionStorage
        {
            OnGetTaskAsync = key => key == BrowserStorageKeys.DashboardFeedbackPromptDismissedForSession
                ? Task.FromResult<(bool Success, object? Value)>((false, false))
                : runSelectionSource.Task
        };
        var localStorage = new TestLocalStorage
        {
            OnGetUnprotectedAsync = key => key == BrowserStorageKeys.RecordingsStyle ? (true, "Panel") : (false, null)
        };
        SetupMainLayoutServices(localStorage: localStorage, dashboardRunStore: runStore, sessionStorage: sessionStorage);
        var runSelection = Assert.IsType<FluentUISetupHelpers.TestDashboardRunSelection>(Services.GetRequiredService<IDashboardRunSelection>());
        var getRunsCallCount = runStore.GetRunsCallCount;

        var cut = Render<MainLayout>(builder =>
        {
            builder.Add(p => p.ViewportInformation, new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false));
            builder.Add(p => p.Body, bodyBuilder => bodyBuilder.AddMarkupContent(0, "<div id=\"body-content\"></div>"));
        });

        Assert.NotNull(cut.Find("#body-content"));
        Assert.True(runSelection.SelectedRun.IsCurrent);
        Assert.Single(cut.FindComponents<DashboardRunSelect>());
        Assert.Empty(cut.FindAll(".dashboard-run-pill"));
        // Runs are listed by the recordings panel, so rendering the header button doesn't load them.
        Assert.Equal(getRunsCallCount, runStore.GetRunsCallCount);

        await cut.InvokeAsync(() => runSelectionSource.SetResult((false, null)));
    }

    [Fact]
    public async Task RunSelectionPending_StoredHistoricalRunReplacesCurrentRun()
    {
        var historicalRun = new DashboardRunDescriptor(
            RunId: "historical",
            SchemaVersion: DashboardRunStore.SchemaVersion,
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
                SchemaVersion: DashboardRunStore.SchemaVersion,
                StartedAtUtc: DateTimeOffset.UnixEpoch,
                EndedAtUtc: null,
                CleanShutdown: false,
                ApplicationName: "TestApp",
                DatabasePath: string.Empty,
                IsCurrent: true),
            historicalRun
        ]);
        var runSelectionSource = new TaskCompletionSource<(bool Success, object? Value)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sessionStorage = new TestSessionStorage
        {
            OnGetTaskAsync = key => key == BrowserStorageKeys.DashboardFeedbackPromptDismissedForSession
                ? Task.FromResult<(bool Success, object? Value)>((false, false))
                : runSelectionSource.Task
        };
        SetupMainLayoutServices(dashboardRunStore: runStore, sessionStorage: sessionStorage);
        await Services.GetRequiredService<DashboardViewPreferences>().EnsureLoadedAsync();

        var cut = Render<MainLayout>(builder =>
        {
            builder.Add(p => p.ViewportInformation, new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false));
            builder.Add(p => p.Body, bodyBuilder => bodyBuilder.AddMarkupContent(0, "<div id=\"body-content\"></div>"));
        });

        Assert.Empty(cut.FindAll(".dashboard-run-pill"));

        await cut.InvokeAsync(() => runSelectionSource.SetResult((true, "historical")));

        var expectedHistoricalRunDate = FormatHelpers.FormatDateTime(
            Services.GetRequiredService<BrowserTimeProvider>(),
            historicalRun.StartedAtUtc.UtcDateTime);
        cut.WaitForAssertion(() =>
        {
            Assert.Equal(expectedHistoricalRunDate, cut.Find(".dashboard-run-pill-text").TextContent);
        });
    }

    [Fact]
    public void StoredHistoricalRunFailure_FallsBackToCurrentRunAndClearsSelection()
    {
        var runStore = new FluentUISetupHelpers.TestDashboardRunStore(
        [
            new(
                RunId: "current",
                SchemaVersion: DashboardRunStore.SchemaVersion,
                StartedAtUtc: DateTimeOffset.UnixEpoch,
                EndedAtUtc: null,
                CleanShutdown: false,
                ApplicationName: "TestApp",
                DatabasePath: string.Empty,
                IsCurrent: true),
            new(
                RunId: "historical",
                SchemaVersion: DashboardRunStore.SchemaVersion,
                StartedAtUtc: DateTimeOffset.UnixEpoch,
                EndedAtUtc: DateTimeOffset.UnixEpoch,
                CleanShutdown: true,
                ApplicationName: "TestApp",
                DatabasePath: string.Empty,
                IsCurrent: false)
        ]);
        string? storedRunId = null;
        var sessionStorage = new TestSessionStorage
        {
            OnGetAsync = key => key == BrowserStorageKeys.DashboardFeedbackPromptDismissedForSession
                ? (false, false)
                : (true, "historical"),
            OnSetAsync = (_, value) => storedRunId = Assert.IsType<string>(value)
        };
        var testSink = new TestSink();
        Services.AddSingleton<ILogger<MainLayout>>(new TestLogger<MainLayout>(new TestLoggerFactory(testSink, enabled: true)));
        SetupMainLayoutServices(dashboardRunStore: runStore, sessionStorage: sessionStorage);
        var runSelection = Assert.IsType<FluentUISetupHelpers.TestDashboardRunSelection>(Services.GetRequiredService<IDashboardRunSelection>());
        var selectedRunIds = new List<string?>();
        var exception = new InvalidOperationException("The historical database could not be opened.");
        runSelection.OnSelectRun = runId =>
        {
            selectedRunIds.Add(runId);
            if (runId == "historical")
            {
                throw exception;
            }
        };

        var cut = Render<MainLayout>(builder =>
        {
            builder.Add(p => p.ViewportInformation, new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false));
        });

        Assert.Equal(["historical", null], selectedRunIds);
        Assert.True(runSelection.SelectedRun.IsCurrent);
        Assert.Null(runSelection.SelectedRunId);
        Assert.Equal(string.Empty, storedRunId);
        Assert.Empty(cut.FindAll(".dashboard-run-pill"));
        var errorLog = Assert.Single(testSink.Writes);
        Assert.Equal(LogLevel.Error, errorLog.LogLevel);
        Assert.Equal("Failed to restore dashboard run 'historical'. Falling back to the current run.", errorLog.Message);
        Assert.Same(exception, errorLog.Exception);
    }

    [Fact]
    public async Task RunSelectionPending_UserSelectionWinsOverStoredSelection()
    {
        var historicalRun = new DashboardRunDescriptor(
            RunId: "historical",
            SchemaVersion: DashboardRunStore.SchemaVersion,
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
                SchemaVersion: DashboardRunStore.SchemaVersion,
                StartedAtUtc: DateTimeOffset.UnixEpoch,
                EndedAtUtc: null,
                CleanShutdown: false,
                ApplicationName: "TestApp",
                DatabasePath: string.Empty,
                IsCurrent: true),
            historicalRun
        ]);
        var runSelectionSource = new TaskCompletionSource<(bool Success, object? Value)>(TaskCreationOptions.RunContinuationsAsynchronously);
        string? storedRunId = null;
        var sessionStorage = new TestSessionStorage
        {
            OnGetTaskAsync = key => key == BrowserStorageKeys.DashboardFeedbackPromptDismissedForSession
                ? Task.FromResult<(bool Success, object? Value)>((false, false))
                : runSelectionSource.Task,
            OnSetAsync = (_, value) => storedRunId = Assert.IsType<string>(value)
        };
        SetupMainLayoutServices(dashboardRunStore: runStore, sessionStorage: sessionStorage);
        JSInterop.SetupVoid("focusElement", _ => true).SetVoidResult();

        var cut = Render<MainLayout>(builder =>
        {
            builder.Add(p => p.ViewportInformation, new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false));
        });
        await cut.InvokeAsync(() => cut.Instance.SwitchDashboardRunAsync(null));

        Assert.Equal(string.Empty, storedRunId);
        await cut.InvokeAsync(() => runSelectionSource.SetResult((true, "historical")));

        var runSelection = Assert.IsType<FluentUISetupHelpers.TestDashboardRunSelection>(Services.GetRequiredService<IDashboardRunSelection>());
        Assert.True(runSelection.SelectedRun.IsCurrent);
        Assert.Empty(cut.FindAll(".dashboard-run-pill"));
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

        var cut = Render<CascadingValue<ViewportInformation>>(builder =>
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
                ? Resources.Layout.MainLayoutKeyboardShortcuts
                : "Settings";

            await cut.InvokeAsync(() => cut.Find("#dashboard-navigation-button").Click());
            var menuItem = cut.FindAll("fluent-menu-item").Single(item => item.TextContent.Contains(menuItemName, StringComparison.OrdinalIgnoreCase));
            await cut.InvokeAsync(() => menuItem.TriggerEvent("onmenuitemchange", new MenuItemEventArgs { Id = menuItem.Id, Text = menuItem.TextContent }));
        }

        Assert.NotNull(capturedParameters);
        Assert.Equal(expectedDialogId, capturedParameters.Id);

        cut.Render(parameters =>
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
            return Task.CompletedTask;
        });

        SetupMainLayoutServices(dialogService: dialogService);
        JSInterop.SetupVoid("focusElement", _ => true);

        var cut = Render<MainLayout>(builder =>
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

    [Theory]
    [InlineData(AspireKeyboardShortcut.GoToResources)]
    [InlineData(AspireKeyboardShortcut.GoToConsoleLogs)]
    public async Task ResourceServiceShortcut_WhenResourceServiceIsDisabled_DoesNotNavigate(AspireKeyboardShortcut shortcut)
    {
        SetupMainLayoutServices(dashboardClient: new TestDashboardClient(isEnabled: false));

        var cut = Render<MainLayout>(builder =>
        {
            builder.Add(p => p.ViewportInformation, new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false));
        });
        var navigationManager = Services.GetRequiredService<NavigationManager>();
        var initialUri = navigationManager.Uri;

        await cut.InvokeAsync(() => cut.Instance.OnPageKeyDownAsync(shortcut));

        Assert.False(cut.Instance.SubscribedShortcuts.Contains(shortcut));
        Assert.Equal(initialUri, navigationManager.Uri);
    }

    private void SetupMainLayoutServices(
        TestLocalStorage? localStorage = null,
        Action<DashboardOptions>? configureOptions = null,
        IDialogService? dialogService = null,
        BrowserTimeProvider? browserTimeProvider = null,
        IDashboardRunStore? dashboardRunStore = null,
        ISessionStorage? sessionStorage = null,
        TestDashboardClient? dashboardClient = null)
    {
        FluentUISetupHelpers.AddCommonDashboardServices(
            this,
            localStorage: localStorage,
            browserTimeProvider: browserTimeProvider,
            dashboardRunStore: dashboardRunStore,
            sessionStorage: sessionStorage);

        if (dialogService is not null)
        {
            Services.AddSingleton(dialogService);
        }

        Services.AddOptions();
        Services.AddSingleton<IThemeResolver, TestThemeResolver>();
        dashboardClient ??= new TestDashboardClient();
        Services.AddSingleton<IDashboardClient>(dashboardClient);
        Services.AddKeyedSingleton<IDashboardClient>(DashboardClient.LiveAppHostServiceKey, dashboardClient);
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
        FluentUISetupHelpers.SetupFluentMenu(this);
        FluentUISetupHelpers.SetupFluentAnchoredRegion(this);
        FluentUISetupHelpers.SetupFluentDivider(this);
        FluentUISetupHelpers.SetupFluentKeyCode(this);

        _messageBarProvider = Render<FluentMessageBarProvider>(builder =>
        {
            builder.Add(p => p.Section, DashboardUIHelpers.MessageBarSection);
        });
        FluentUISetupHelpers.SetupFluentInputLabel(this);
        FluentUISetupHelpers.SetupFluentList(this);
        FluentUISetupHelpers.SetupFluentCombobox(this);
        FluentUISetupHelpers.SetupAspireMenuButtonModule(this);

        var themeModule = JSInterop.SetupModule("/js/app-theme.js");
        themeModule.Setup<string>("updateTheme", _ => true).SetResult(ThemeManager.ThemeSettingSystem);
        JSInterop.SetupVoid("Blazor.theme.setThemeMode", _ => true).SetVoidResult();

        JSInterop.SetupModule("window.registerGlobalKeydownListener", _ => true);
        JSInterop.SetupModule("window.registerOpenTextVisualizerOnClick", _ => true);
        JSInterop.SetupVoid("registerResourceServiceConnectionProvider", _ => true).SetVoidResult();
        JSInterop.SetupVoid("updateResourceServiceConnectionState", _ => true).SetVoidResult();
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
