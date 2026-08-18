// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.FluentUI.AspNetCore.Components;
using Microsoft.JSInterop;
using Xunit;
using Icons = Microsoft.FluentUI.AspNetCore.Components.Icons;

namespace Aspire.Dashboard.Components.Tests.Layout;

[UseCulture("en-US")]
public partial class MainLayoutTests : DashboardTestContext
{
    private IRenderedComponent<FluentMessageBarProvider>? _messageBarProvider;

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
            messageBarProvider.WaitForAssertion(() =>
            {
                var messageBar = Assert.Single(messageBarProvider.FindComponents<DashboardMessageBar>());
                Assert.Contains("intent-warning", messageBar.Find("[role='alert']").ClassList);
            });
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
            var menuItemName = expectedDialogId == "HelpDialog"
                ? "Help"
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
    public void DashboardRunSelect_Supported_IsDisplayedBeforeHeaderButtons(bool isDesktop)
    {
        SetupMainLayoutServices();

        var cut = RenderComponent<MainLayout>(builder =>
        {
            builder.Add(p => p.ViewportInformation, new ViewportInformation(IsDesktop: isDesktop, IsUltraLowHeight: false, IsUltraLowWidth: false));
        });

        var existingButtonId = isDesktop ? "dashboard-help-button" : "dashboard-navigation-button";

        Assert.Single(cut.FindComponents<DashboardRunSelect>());
        Assert.Contains("class=\"application-run-select\"", cut.Markup, StringComparison.Ordinal);
        Assert.True(
            cut.Markup.IndexOf("class=\"application-run-select\"", StringComparison.Ordinal) <
            cut.Markup.IndexOf($"id=\"{existingButtonId}\"", StringComparison.Ordinal));
    }

    [Fact]
    public void DashboardRunSelect_PrunedHistoricalRunIsNotDisplayed()
    {
        var historicalRun = new DashboardRunDescriptor(
            RunId: "historical",
            SchemaVersion: DashboardRunStore.SchemaVersion,
            StartedAtUtc: DateTimeOffset.UnixEpoch,
            EndedAtUtc: DateTimeOffset.UnixEpoch,
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
        SetupMainLayoutServices(dashboardRunStore: runStore);
        var cut = RenderComponent<MainLayout>(builder =>
        {
            builder.Add(
                component => component.ViewportInformation,
                new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false));
        });

        historicalRun.IsPruned = true;
        var runSelect = cut.FindComponent<DashboardRunSelect>();
        runSelect.Find("fluent-button").Click();

        var menuItem = Assert.Single(runSelect.FindComponent<AspireMenu>().Instance.Items);
        Assert.Equal("Live run", menuItem.Text);
        Assert.True(menuItem.Checked);
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
            OnGetAsync = _ => throw new InvalidOperationException("Run selection should not be read.")
        };
        SetupMainLayoutServices(dashboardRunStore: runStore, sessionStorage: sessionStorage);
        var runSelection = Assert.IsType<FluentUISetupHelpers.TestDashboardRunSelection>(Services.GetRequiredService<IDashboardRunSelection>());
        var getRunsCallCount = runStore.GetRunsCallCount;

        var cut = RenderComponent<MainLayout>(builder =>
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
    public async Task DashboardRunSelect_ChangeStoresAndAppliesSelectionWithoutNavigation()
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
        SetupMainLayoutServices(dashboardRunStore: runStore, sessionStorage: sessionStorage);
        var expectedHistoricalRunText = FormatHelpers.FormatTimeWithOptionalDate(
            Services.GetRequiredService<BrowserTimeProvider>(),
            historicalRun.StartedAtUtc.UtcDateTime);
        JSInterop.SetupVoid("focusElement", _ => true).SetVoidResult();
        var initializedCount = 0;
        var disposedCount = 0;
        var cut = RenderComponent<MainLayout>(builder =>
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

        var runSelect = cut.FindComponent<DashboardRunSelect>();
        var menuButton = runSelect.FindComponent<AspireMenuButton>();
        var statusIcon = runSelect.Find(".application-run-status");
        Assert.Equal("start", statusIcon.GetAttribute("slot"));
        Assert.Contains("fill: var(--success)", statusIcon.GetAttribute("style"), StringComparison.Ordinal);
        Assert.Equal("Live run", menuButton.Instance.Text);
        Assert.True(menuButton.Instance.HideIcon);
        Assert.Empty(menuButton.FindComponents<AspireMenu>());
        var menuButtonElement = runSelect.Find("fluent-button");
        var menuButtonId = menuButtonElement.Id;
        Assert.Equal("Select run: Live run", menuButtonElement.GetAttribute("aria-label"));

        var navigationOccurred = false;
        Services.GetRequiredService<NavigationManager>().LocationChanged += (_, _) => navigationOccurred = true;
        runSelect.Find("fluent-button").Click();
        Assert.Collection(
            menuButton.FindComponent<AspireMenu>().Instance.Items,
            item =>
            {
                Assert.Equal("Live run", item.Text);
                Assert.Equal(MenuItemRole.Radio, item.Role);
                Assert.True(item.Checked);
                Assert.IsType<Icons.Regular.Size16.Checkmark>(item.Icon);
                Assert.IsType<Icons.Regular.Size16.Pin>(item.SecondaryActionIcon);
                Assert.Equal("Pin run", item.SecondaryActionAriaLabel);
                Assert.False(item.IsSecondaryActionSelected);
            },
            item => Assert.True(item.IsDivider),
            item =>
            {
                Assert.Equal(expectedHistoricalRunText, item.Text);
                Assert.Equal(MenuItemRole.Radio, item.Role);
                Assert.False(item.Checked);
                Assert.IsType<Icons.Regular.Size16.Checkmark>(item.Icon);
                Assert.IsType<Icons.Regular.Size16.Pin>(item.SecondaryActionIcon);
                Assert.Equal("Pin run", item.SecondaryActionAriaLabel);
                Assert.False(item.IsSecondaryActionSelected);
            });

        var menuItems = cut.WaitForElements("fluent-menu-item");
        Assert.Single(cut.FindAll("fluent-divider"));
        Assert.Empty(menuItems[0].QuerySelectorAll("span[slot='start']"));
        Assert.Empty(menuItems[1].QuerySelectorAll("span[slot='start']"));
        Assert.Single(menuItems[0].QuerySelectorAll("[slot='indicator']"));
        Assert.Single(menuItems[1].QuerySelectorAll("[slot='indicator']"));
        Assert.Equal("menuitemradio", menuItems[0].GetAttribute("role"));
        Assert.True(menuItems[0].HasAttribute("checked"));
        Assert.Equal("menuitemradio", menuItems[1].GetAttribute("role"));
        Assert.False(menuItems[1].HasAttribute("checked"));
        var runSelection = Assert.IsType<FluentUISetupHelpers.TestDashboardRunSelection>(Services.GetRequiredService<IDashboardRunSelection>());
        var currentRun = runStore.GetRuns().Single(run => run.IsCurrent);
        Assert.Single(menuItems[0].QuerySelectorAll("fluent-button")).Click();

        Assert.True(currentRun.IsPinned);
        Assert.Null(runSelection.SelectedRunId);
        Assert.True(runSelect.FindComponent<AspireMenu>().Instance.Open);
        menuButton = runSelect.FindComponent<AspireMenuButton>();
        Assert.IsType<Icons.Filled.Size16.Pin>(menuButton.Instance.Items[0].SecondaryActionIcon);
        Assert.True(menuButton.Instance.Items[0].IsSecondaryActionSelected);

        menuItems = cut.WaitForElements("fluent-menu-item");
        var pinButton = Assert.Single(menuItems[1].QuerySelectorAll("fluent-button"));
        pinButton.Click();

        Assert.True(historicalRun.IsPinned);
        Assert.Null(storedRunId);
        Assert.Null(runSelection.SelectedRunId);
        Assert.True(runSelect.FindComponent<AspireMenu>().Instance.Open);
        menuButton = runSelect.FindComponent<AspireMenuButton>();
        var historicalItem = menuButton.Instance.Items[2];
        Assert.IsType<Icons.Filled.Size16.Pin>(historicalItem.SecondaryActionIcon);
        Assert.Equal("Unpin run", historicalItem.SecondaryActionAriaLabel);
        Assert.True(historicalItem.IsSecondaryActionSelected);

        menuItems = cut.WaitForElements("fluent-menu-item");
        menuItems[1].TriggerEvent("onmenuitemchange", new MenuItemEventArgs { Id = menuItems[1].Id, Text = menuItems[1].TextContent, Checked = true });

        Assert.Equal("historical", storedRunId);
        Assert.Equal("historical", runSelection.SelectedRunId);
        Assert.False(navigationOccurred);
        Assert.Equal(2, initializedCount);
        Assert.Equal(1, disposedCount);
        runSelect = cut.FindComponent<DashboardRunSelect>();
        menuButton = runSelect.FindComponent<AspireMenuButton>();
        statusIcon = runSelect.Find(".application-run-status");
        Assert.Contains("fill: var(--warning)", statusIcon.GetAttribute("style"), StringComparison.Ordinal);
        Assert.Equal(expectedHistoricalRunText, menuButton.Instance.Text);
        Assert.False(menuButton.FindComponent<AspireMenu>().Instance.Open);
        Assert.Equal(menuButtonId, runSelect.Find("fluent-button").Id);
        Assert.Equal($"Select run: {expectedHistoricalRunText}", runSelect.Find("fluent-button").GetAttribute("aria-label"));
        Assert.Contains(
            JSInterop.Invocations,
            invocation => invocation.Identifier == "focusElement" && invocation.Arguments.Single() is string id && id == menuButtonId);

        runSelect.Find("fluent-button").Click();
        Assert.Collection(
            menuButton.FindComponent<AspireMenu>().Instance.Items,
            item =>
            {
                Assert.False(item.Checked);
                Assert.IsType<Icons.Regular.Size16.Checkmark>(item.Icon);
            },
            item => Assert.True(item.IsDivider),
            item =>
            {
                Assert.True(item.Checked);
                Assert.IsType<Icons.Regular.Size16.Checkmark>(item.Icon);
            });
        menuItems = cut.WaitForElements("fluent-menu-item");
        Assert.Single(cut.FindAll("fluent-divider"));
        Assert.Empty(menuItems[0].QuerySelectorAll("span[slot='start']"));
        Assert.Empty(menuItems[1].QuerySelectorAll("span[slot='start']"));
        menuItems[0].TriggerEvent("onmenuitemchange", new MenuItemEventArgs { Id = menuItems[0].Id, Text = menuItems[0].TextContent, Checked = true });

        Assert.Equal(string.Empty, storedRunId);
        Assert.Null(runSelection.SelectedRunId);
        Assert.False(navigationOccurred);
        Assert.Equal(3, initializedCount);
        Assert.Equal(2, disposedCount);
        runSelect = cut.FindComponent<DashboardRunSelect>();
        menuButton = runSelect.FindComponent<AspireMenuButton>();
        statusIcon = runSelect.Find(".application-run-status");
        Assert.Contains("fill: var(--success)", statusIcon.GetAttribute("style"), StringComparison.Ordinal);
        Assert.Equal("Live run", menuButton.Instance.Text);
        Assert.False(menuButton.FindComponent<AspireMenu>().Instance.Open);
    }

    [Fact]
    public void DashboardRunSelect_SelectionFailure_KeepsCurrentRunAndCircuitActive()
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
        JSInterop.SetupVoid("focusElement", _ => true).SetVoidResult();
        var runSelection = Assert.IsType<FluentUISetupHelpers.TestDashboardRunSelection>(Services.GetRequiredService<IDashboardRunSelection>());
        var exception = new InvalidOperationException("The historical database could not be opened.");
        runSelection.OnSelectRun = runId =>
        {
            if (runId == "historical")
            {
                throw exception;
            }
        };

        var cut = RenderComponent<MainLayout>(builder =>
        {
            builder.Add(component => component.ViewportInformation, new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false));
            builder.Add(component => component.Body, bodyBuilder => bodyBuilder.AddMarkupContent(0, "<div id=\"body-content\"></div>"));
        });
        var runSelect = cut.FindComponent<DashboardRunSelect>();
        runSelect.Find("fluent-button").Click();

        var menuItem = cut.WaitForElements("fluent-menu-item")[1];
        menuItem.TriggerEvent("onmenuitemchange", new MenuItemEventArgs { Id = menuItem.Id, Text = menuItem.TextContent, Checked = true });

        Assert.NotNull(cut.Find("#body-content"));
        Assert.True(runSelection.SelectedRun.IsCurrent);
        Assert.Equal(string.Empty, storedRunId);
        Assert.Equal("Live run", cut.FindComponent<DashboardRunSelect>().FindComponent<AspireMenuButton>().Instance.Text);
        var errorLog = Assert.Single(testSink.Writes);
        Assert.Equal(LogLevel.Error, errorLog.LogLevel);
        Assert.Equal("Failed to switch to dashboard run 'historical'. Keeping dashboard run 'current' selected.", errorLog.Message);
        Assert.Same(exception, errorLog.Exception);
    }

    [Fact]
    public void DashboardRunSelect_PinningFailure_KeepsMenuAndCircuitActive()
    {
        var currentRun = new DashboardRunDescriptor(
            RunId: "current",
            SchemaVersion: DashboardRunStore.SchemaVersion,
            StartedAtUtc: DateTimeOffset.UnixEpoch,
            EndedAtUtc: null,
            CleanShutdown: false,
            ApplicationName: "TestApp",
            DatabasePath: string.Empty,
            IsCurrent: true);
        var historicalRun = new DashboardRunDescriptor(
            RunId: "historical",
            SchemaVersion: DashboardRunStore.SchemaVersion,
            StartedAtUtc: DateTimeOffset.UnixEpoch,
            EndedAtUtc: DateTimeOffset.UnixEpoch,
            CleanShutdown: true,
            ApplicationName: "TestApp",
            DatabasePath: string.Empty,
            IsCurrent: false);
        var runStore = new FluentUISetupHelpers.TestDashboardRunStore([currentRun, historicalRun]);
        var exception = new IOException("The run metadata could not be written.");
        runStore.OnSetRunPinned = (run, _) =>
        {
            if (run.RunId == historicalRun.RunId)
            {
                throw exception;
            }
        };
        var testSink = new TestSink();
        Services.AddSingleton<ILogger<DashboardRunSelect>>(new TestLogger<DashboardRunSelect>(new TestLoggerFactory(testSink, enabled: true)));
        SetupMainLayoutServices(dashboardRunStore: runStore);
        var cut = RenderComponent<DashboardRunSelect>(builder =>
        {
            builder.Add(component => component.SelectedRunId, currentRun.RunId);
            builder.Add(component => component.SelectedRunIsCurrent, true);
            builder.Add(component => component.SelectedRunStartedAtUtc, currentRun.StartedAtUtc);
        });
        cut.Find("fluent-button").Click();

        var historicalMenuItem = cut.WaitForElements("fluent-menu-item")[1];
        Assert.Single(historicalMenuItem.QuerySelectorAll("fluent-button")).Click();

        Assert.False(historicalRun.IsPinned);
        Assert.True(cut.FindComponent<AspireMenu>().Instance.Open);
        Assert.NotNull(cut.Find("fluent-button"));
        var errorLog = Assert.Single(testSink.Writes);
        Assert.Equal(LogLevel.Error, errorLog.LogLevel);
        Assert.Equal("Failed to update the pinned state of dashboard run 'historical'.", errorLog.Message);
        Assert.Same(exception, errorLog.Exception);
    }

    [Fact]
    public void DashboardRunSelect_SortsHistoricalRunsByPinnedThenDateDescendingAndUpdatesOrderWhenPinned()
    {
        var currentRun = new DashboardRunDescriptor(
            RunId: "current",
            SchemaVersion: DashboardRunStore.SchemaVersion,
            StartedAtUtc: DateTimeOffset.UnixEpoch,
            EndedAtUtc: null,
            CleanShutdown: false,
            ApplicationName: "TestApp",
            DatabasePath: string.Empty,
            IsCurrent: true);
        var historicalRuns = new[]
        {
            new DashboardRunDescriptor("unpinned-b", DashboardRunStore.SchemaVersion, new(2025, 1, 2, 12, 0, 0, TimeSpan.Zero), null, true, "TestApp", string.Empty, IsCurrent: false),
            new DashboardRunDescriptor("pinned-b", DashboardRunStore.SchemaVersion, new(2025, 1, 2, 11, 0, 0, TimeSpan.Zero), null, true, "TestApp", string.Empty, IsCurrent: false),
            new DashboardRunDescriptor("unpinned-a", DashboardRunStore.SchemaVersion, new(2025, 1, 2, 9, 0, 0, TimeSpan.Zero), null, true, "TestApp", string.Empty, IsCurrent: false),
            new DashboardRunDescriptor("pinned-a", DashboardRunStore.SchemaVersion, new(2025, 1, 2, 10, 0, 0, TimeSpan.Zero), null, true, "TestApp", string.Empty, IsCurrent: false)
        };
        var runStore = new FluentUISetupHelpers.TestDashboardRunStore([currentRun, .. historicalRuns]);
        runStore.SetRunPinned(historicalRuns[1], isPinned: true);
        runStore.SetRunPinned(historicalRuns[3], isPinned: true);
        SetupMainLayoutServices(dashboardRunStore: runStore);
        var browserTimeProvider = Services.GetRequiredService<BrowserTimeProvider>();
        var expectedHistoricalTexts = historicalRuns
            .OrderByDescending(run => run.IsPinned)
            .ThenByDescending(run => run.StartedAtUtc)
            .Select(run => FormatHelpers.FormatTimeWithOptionalDate(browserTimeProvider, run.StartedAtUtc.UtcDateTime))
            .ToArray();
        var cut = RenderComponent<DashboardRunSelect>(builder =>
        {
            builder.Add(component => component.SelectedRunId, currentRun.RunId);
            builder.Add(component => component.SelectedRunIsCurrent, true);
            builder.Add(component => component.SelectedRunStartedAtUtc, currentRun.StartedAtUtc);
        });

        cut.Find("fluent-button").Click();

        var items = cut.FindComponent<AspireMenuButton>().Instance.Items;
        Assert.Equal("Live run", items[0].Text);
        Assert.IsType<Icons.Regular.Size16.Pin>(items[0].SecondaryActionIcon);
        Assert.False(items[0].IsSecondaryActionSelected);
        Assert.True(items[1].IsDivider);
        Assert.Equal(expectedHistoricalTexts, items.Skip(2).Select(item => item.Text));
        Assert.All(items.Skip(2).Take(2), item => Assert.True(item.IsSecondaryActionSelected));
        Assert.All(items.Skip(4), item => Assert.False(item.IsSecondaryActionSelected));

        var menuItems = cut.WaitForElements("fluent-menu-item");
        Assert.Single(menuItems[3].QuerySelectorAll("fluent-button")).Click();

        expectedHistoricalTexts = historicalRuns
            .OrderByDescending(run => run.IsPinned)
            .ThenByDescending(run => run.StartedAtUtc)
            .Select(run => FormatHelpers.FormatTimeWithOptionalDate(browserTimeProvider, run.StartedAtUtc.UtcDateTime))
            .ToArray();
        items = cut.FindComponent<AspireMenuButton>().Instance.Items;
        Assert.Equal(expectedHistoricalTexts, items.Skip(2).Select(item => item.Text));
        Assert.All(items.Skip(2).Take(3), item => Assert.True(item.IsSecondaryActionSelected));
        Assert.False(items[5].IsSecondaryActionSelected);
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
            OnGetTaskAsync = _ => runSelectionSource.Task
        };
        SetupMainLayoutServices(dashboardRunStore: runStore, sessionStorage: sessionStorage);
        var runSelection = Assert.IsType<FluentUISetupHelpers.TestDashboardRunSelection>(Services.GetRequiredService<IDashboardRunSelection>());
        var getRunsCallCount = runStore.GetRunsCallCount;

        var cut = RenderComponent<MainLayout>(builder =>
        {
            builder.Add(p => p.ViewportInformation, new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false));
            builder.Add(p => p.Body, bodyBuilder => bodyBuilder.AddMarkupContent(0, "<div id=\"body-content\"></div>"));
        });

        Assert.NotNull(cut.Find("#body-content"));
        Assert.True(runSelection.SelectedRun.IsCurrent);
        var runSelect = cut.FindComponent<DashboardRunSelect>();
        var menuButton = runSelect.FindComponent<AspireMenuButton>();
        Assert.Equal("Live run", menuButton.Instance.Text);
        Assert.Empty(menuButton.FindComponents<AspireMenu>());
        Assert.Equal(getRunsCallCount, runStore.GetRunsCallCount);

        runSelect.Find("fluent-button").Click();

        Assert.Single(cut.WaitForElements("fluent-menu-item"));
        Assert.Equal(getRunsCallCount + 1, runStore.GetRunsCallCount);

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
            OnGetTaskAsync = _ => runSelectionSource.Task
        };
        SetupMainLayoutServices(dashboardRunStore: runStore, sessionStorage: sessionStorage);

        var cut = RenderComponent<MainLayout>(builder =>
        {
            builder.Add(p => p.ViewportInformation, new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false));
            builder.Add(p => p.Body, bodyBuilder => bodyBuilder.AddMarkupContent(0, "<div id=\"body-content\"></div>"));
        });

        Assert.Equal("Live run", cut.FindComponent<DashboardRunSelect>().FindComponent<AspireMenuButton>().Instance.Text);

        await cut.InvokeAsync(() => runSelectionSource.SetResult((true, "historical")));

        var expectedHistoricalRunText = FormatHelpers.FormatTimeWithOptionalDate(
            Services.GetRequiredService<BrowserTimeProvider>(),
            historicalRun.StartedAtUtc.UtcDateTime);
        cut.WaitForAssertion(() =>
        {
            var menuButton = cut.FindComponent<DashboardRunSelect>().FindComponent<AspireMenuButton>();
            Assert.Equal(expectedHistoricalRunText, menuButton.Instance.Text);
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
            OnGetAsync = _ => (true, "historical"),
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

        var cut = RenderComponent<MainLayout>(builder =>
        {
            builder.Add(p => p.ViewportInformation, new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false));
        });

        Assert.Equal(["historical", null], selectedRunIds);
        Assert.True(runSelection.SelectedRun.IsCurrent);
        Assert.Null(runSelection.SelectedRunId);
        Assert.Equal(string.Empty, storedRunId);
        Assert.Equal("Live run", cut.FindComponent<DashboardRunSelect>().FindComponent<AspireMenuButton>().Instance.Text);
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
            OnGetTaskAsync = _ => runSelectionSource.Task,
            OnSetAsync = (_, value) => storedRunId = Assert.IsType<string>(value)
        };
        SetupMainLayoutServices(dashboardRunStore: runStore, sessionStorage: sessionStorage);
        JSInterop.SetupVoid("focusElement", _ => true).SetVoidResult();

        var cut = RenderComponent<MainLayout>(builder =>
        {
            builder.Add(p => p.ViewportInformation, new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false));
        });
        var runSelect = cut.FindComponent<DashboardRunSelect>();
        runSelect.Find("fluent-button").Click();
        var menuItem = cut.WaitForElements("fluent-menu-item")[0];
        menuItem.TriggerEvent("onmenuitemchange", new MenuItemEventArgs { Id = menuItem.Id, Text = menuItem.TextContent, Checked = true });

        Assert.Equal(string.Empty, storedRunId);
        await cut.InvokeAsync(() => runSelectionSource.SetResult((true, "historical")));

        var runSelection = Assert.IsType<FluentUISetupHelpers.TestDashboardRunSelection>(Services.GetRequiredService<IDashboardRunSelection>());
        Assert.True(runSelection.SelectedRun.IsCurrent);
        Assert.Equal("Live run", cut.FindComponent<DashboardRunSelect>().FindComponent<AspireMenuButton>().Instance.Text);
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
            var menuItemName = expectedDialogId == "HelpDialog"
                ? "Help"
                : "Settings";

            await cut.InvokeAsync(() => cut.Find("#dashboard-navigation-button").Click());
            var menuItem = cut.FindAll("fluent-menu-item").Single(item => item.TextContent.Contains(menuItemName, StringComparison.OrdinalIgnoreCase));
            await cut.InvokeAsync(() => menuItem.TriggerEvent("onmenuitemchange", new MenuItemEventArgs { Id = menuItem.Id, Text = menuItem.TextContent }));
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
        Action<DashboardOptions>? configureOptions = null,
        IDialogService? dialogService = null,
        BrowserTimeProvider? browserTimeProvider = null,
        IDashboardRunStore? dashboardRunStore = null,
        ISessionStorage? sessionStorage = null)
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
        var dashboardClient = new TestDashboardClient();
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

        _messageBarProvider = RenderComponent<FluentMessageBarProvider>(builder =>
        {
            builder.Add(p => p.Section, DashboardUIHelpers.MessageBarSection);
        });
        FluentUISetupHelpers.SetupFluentInputLabel(this);
        FluentUISetupHelpers.SetupFluentList(this);
        FluentUISetupHelpers.SetupFluentCombobox(this);
        FluentUISetupHelpers.SetupAspireMenuButtonModule(this);

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
