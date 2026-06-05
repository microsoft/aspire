// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Threading.Channels;
using Aspire.Dashboard.Components.Controls;
using Aspire.Dashboard.Components.Resize;
using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Tests.Shared;
using Aspire.Dashboard.Utils;
using Aspire.Hosting.ConsoleLogs;
using Aspire.Tests.Shared.DashboardModel;
using Aspire.TestUtilities;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.FluentUI.AspNetCore.Components;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Pages;

[UseCulture("en-US")]
public partial class ConsoleLogsTests : DashboardTestContext
{
    private readonly ITestOutputHelper _testOutputHelper;

    public ConsoleLogsTests(ITestOutputHelper testOutputHelper)
    {
        _testOutputHelper = testOutputHelper;
    }

    [Fact]
    public async Task NoResourceName_SingleResource_RedirectToResource()
    {
        // Arrange
        ILogger logger = null!;
        var subscribedResourceNamesChannel = Channel.CreateUnbounded<string>();
        var consoleLogsChannel = Channel.CreateUnbounded<IReadOnlyList<ResourceLogLine>>();
        var resourceChannel = Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>();
        var testResource = ModelTestHelpers.CreateResource(resourceName: "test-resource", state: KnownResourceState.Running);
        var dashboardClient = new TestDashboardClient(
            isEnabled: true,
            consoleLogsChannelProvider: name =>
            {
                logger.LogInformation($"Requesting logs for: {name}");
                subscribedResourceNamesChannel.Writer.TryWrite(name);
                return consoleLogsChannel;
            },
            resourceChannelProvider: () => resourceChannel,
            initialResources: [testResource]);

        SetupConsoleLogsServices(dashboardClient);

        logger = Services.GetRequiredService<ILogger<ConsoleLogsTests>>();

        var navigationManager = Services.GetRequiredService<NavigationManager>();

        var targetLocationTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var changeHandler = navigationManager.RegisterLocationChangingHandler(c =>
        {
            targetLocationTcs.SetResult(c.TargetLocation);
            return ValueTask.CompletedTask;
        });

        var dimensionManager = Services.GetRequiredService<DimensionManager>();
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        dimensionManager.InvokeOnViewportInformationChanged(viewport);

        // Act 1
        var cut = RenderComponent<Components.Pages.ConsoleLogs>(builder =>
        {
            builder.Add(p => p.ViewportInformation, viewport);
        });

        var instance = cut.Instance;
        var loc = Services.GetRequiredService<IStringLocalizer<Resources.ConsoleLogs>>();

        // Assert 1
        Assert.Equal("/consolelogs/resource/test-resource", await targetLocationTcs.Task.DefaultTimeout());
    }

    [Fact]
    public async Task ResourceName_SubscribeOnLoadAndChange_SubscribeConsoleLogsOnce()
    {
        // Arrange
        ILogger logger = null!;
        var subscribedResourceNamesChannel = Channel.CreateUnbounded<string>();
        var consoleLogsChannel = Channel.CreateUnbounded<IReadOnlyList<ResourceLogLine>>();
        var resourceChannel = Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>();
        var testResource = ModelTestHelpers.CreateResource(resourceName: "test-resource", state: KnownResourceState.Running);
        var testResource2 = ModelTestHelpers.CreateResource(resourceName: "test-resource2", state: KnownResourceState.Running);
        var dashboardClient = new TestDashboardClient(
            isEnabled: true,
            consoleLogsChannelProvider: name =>
            {
                logger.LogInformation($"Requesting logs for: {name}");
                subscribedResourceNamesChannel.Writer.TryWrite(name);
                return consoleLogsChannel;
            },
            resourceChannelProvider: () => resourceChannel,
            initialResources: [testResource, testResource2]);

        SetupConsoleLogsServices(dashboardClient);

        logger = Services.GetRequiredService<ILogger<ConsoleLogsTests>>();

        var navigationManager = Services.GetRequiredService<NavigationManager>();
        navigationManager.NavigateTo(DashboardUrls.ConsoleLogsUrl(resource: "test-resource"));

        var dimensionManager = Services.GetRequiredService<DimensionManager>();
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        dimensionManager.InvokeOnViewportInformationChanged(viewport);

        // Act 1
        var cut = RenderComponent<Components.Pages.ConsoleLogs>(builder =>
        {
            builder.Add(p => p.ResourceName, "test-resource");
            builder.Add(p => p.ViewportInformation, viewport);
        });

        var instance = cut.Instance;
        var loc = Services.GetRequiredService<IStringLocalizer<Resources.ConsoleLogs>>();

        // Assert 1
        logger.LogInformation("Waiting for selected resource.");
        cut.WaitForState(() => instance.PageViewModel.SelectedResource.Id?.InstanceId == testResource.Name);
        cut.WaitForState(() => instance.PageViewModel.Status == loc[nameof(Resources.ConsoleLogs.ConsoleLogsWatchingLogs)]);

        consoleLogsChannel.Writer.TryWrite([new ResourceLogLine(1, "Test content", IsErrorMessage: false)]);
        consoleLogsChannel.Writer.Complete();

        logger.LogInformation("Waiting for finish message.");
        cut.WaitForState(() => instance.PageViewModel.Status == loc[nameof(Resources.ConsoleLogs.ConsoleLogsFinishedWatchingLogs)]);

        var subscribedResourceName1 = await subscribedResourceNamesChannel.Reader.ReadAsync().DefaultTimeout();
        Assert.Equal("test-resource", subscribedResourceName1);

        navigationManager.LocationChanged += (sender, e) =>
        {
            var expectedUrl = DashboardUrls.ConsoleLogsUrl(resource: "test-resource2");
            Assert.EndsWith(expectedUrl, e.Location);

            cut.SetParametersAndRender(builder =>
            {
                builder.Add(m => m.ResourceName, "test-resource2");
            });
        };
        consoleLogsChannel = Channel.CreateUnbounded<IReadOnlyList<ResourceLogLine>>();

        // Act 2
        logger.LogInformation("Changing resource.");
        var resourceSelect = cut.FindComponent<ResourceSelect>();
        var innerSelect = resourceSelect.Find("fluent-select");
        innerSelect.Change("test-resource2");

        // Assert 2
        logger.LogInformation("Waiting for selected resource.");
        cut.WaitForState(() => instance.PageViewModel.SelectedResource.Id?.InstanceId == testResource2.Name);
        cut.WaitForState(() => instance.PageViewModel.Status == loc[nameof(Resources.ConsoleLogs.ConsoleLogsWatchingLogs)]);

        var subscribedResourceName2 = await subscribedResourceNamesChannel.Reader.ReadAsync().DefaultTimeout();
        Assert.Equal("test-resource2", subscribedResourceName2);

        subscribedResourceNamesChannel.Writer.Complete();
        Assert.False(await subscribedResourceNamesChannel.Reader.WaitToReadAsync().DefaultTimeout());
    }

    [Fact]
    public void ToggleHiddenResources_HiddenResourceVisibilityAndSelection_WorksCorrectly()
    {
        // Arrange
        var regularResource = ModelTestHelpers.CreateResource(resourceName: "regular-resource", state: KnownResourceState.Running);
        var hiddenResource = ModelTestHelpers.CreateResource(resourceName: "hidden-resource", state: KnownResourceState.Running, hidden: true);

        var consoleLogsChannel = Channel.CreateUnbounded<IReadOnlyList<ResourceLogLine>>();
        var resourceChannel = Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>();
        var dashboardClient = new TestDashboardClient(
            isEnabled: true,
            consoleLogsChannelProvider: name => consoleLogsChannel,
            resourceChannelProvider: () => resourceChannel,
            initialResources: [regularResource, hiddenResource]);

        SetupConsoleLogsServices(dashboardClient);

        var dimensionManager = Services.GetRequiredService<DimensionManager>();
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        dimensionManager.InvokeOnViewportInformationChanged(viewport);

        // Act & Assert 1: Render component - initially hidden resource should not be visible
        var cut = RenderComponent<Components.Pages.ConsoleLogs>(builder =>
        {
            builder.Add(p => p.ViewportInformation, viewport);
        });

        var instance = cut.Instance;

        // Wait for resources to load - use resource select component as proxy
        cut.WaitForAssertion(() =>
        {
            var resourceSelect = cut.FindComponent<ResourceSelect>();
            var selectElement = resourceSelect.Find("fluent-select");
            var selectOptions = selectElement.QuerySelectorAll("fluent-option");

            // Should have at least 1 option (regular resource) when resources are loaded
            Assert.True(selectOptions.Length >= 1);
        });

        // Initially, hidden resources should not be shown
        var resourceSelect = cut.FindComponent<ResourceSelect>();
        var selectElement = resourceSelect.Find("fluent-select");
        var selectOptions = selectElement.QuerySelectorAll("fluent-option");

        // Should only have regular resource (hidden resource filtered out)
        Assert.Equal(1, selectOptions.Length); // regular-resource
        var optionValues = selectOptions.Select(opt => opt.GetAttribute("value")).ToList();
        Assert.Contains("regular-resource", optionValues);
        Assert.DoesNotContain("hidden-resource", optionValues);

        // Act & Assert 2: Click the settings menu button to show the menu, then click "Show hidden resources"
        var settingsMenuButton = cut.Find("fluent-button[title='" + Resources.ConsoleLogs.ConsoleLogsSettings + "']");
        Assert.NotNull(settingsMenuButton);
        settingsMenuButton.Click();

        // Find and click the "Show hidden resources" menu item
        cut.WaitForAssertion(() =>
        {
            var showHiddenMenuItem = cut.Find("fluent-menu-item:contains('" + Resources.ControlsStrings.ShowHiddenResources + "')");
            Assert.NotNull(showHiddenMenuItem);
            showHiddenMenuItem.Click();
        });

        // Wait for UI to update
        cut.WaitForAssertion(() =>
        {
            var updatedOptions = selectElement.QuerySelectorAll("fluent-option");
            // Should now have both resources
            Assert.Equal(3, updatedOptions.Length); // "None" + regular-resource + hidden-resource
            var updatedOptionValues = updatedOptions.Select(opt => opt.GetAttribute("value")).ToList();
            Assert.Contains("regular-resource", updatedOptionValues);
            Assert.Contains("hidden-resource", updatedOptionValues);
        });

        // Act & Assert 3: Select the hidden resource
        var hiddenResourceOption = selectElement.QuerySelector("fluent-option[value='hidden-resource']");
        Assert.NotNull(hiddenResourceOption);
        selectElement.Change("hidden-resource");

        cut.WaitForState(() => instance.PageViewModel.SelectedResource?.Name == "hidden-resource");

        // Act & Assert 4: Click the settings menu button again and click "Hide hidden resources" to hide them again
        settingsMenuButton.Click();

        cut.WaitForAssertion(() =>
        {
            var hideHiddenMenuItem = cut.Find("fluent-menu-item:contains('" + Resources.ControlsStrings.HideHiddenResources + "')");
            Assert.NotNull(hideHiddenMenuItem);
            hideHiddenMenuItem.Click();
        });

        // Wait for UI to update - hidden resource should be filtered out and selection should be cleared
        cut.WaitForAssertion(() =>
        {
            var finalOptions = selectElement.QuerySelectorAll("fluent-option");
            // Should be back to regular resource only
            Assert.Equal(1, finalOptions.Length); // regular-resource
            var finalOptionValues = finalOptions.Select(opt => opt.GetAttribute("value")).ToList();
            Assert.Contains("regular-resource", finalOptionValues);
            Assert.DoesNotContain("hidden-resource", finalOptionValues);
        });

        // Selection should be cleared since selected resource is now hidden
        cut.WaitForState(() => instance.PageViewModel.SelectedResource.Id?.InstanceId == regularResource.Name);
    }

    [Fact]
    public async Task ResourceName_ViaUrlAndResourceLoaded_LogViewerUpdated()
    {
        // Arrange
        var testResource = ModelTestHelpers.CreateResource(resourceName: "test-resource", state: KnownResourceState.Running);
        var subscribedResourceNameTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var consoleLogsChannel = Channel.CreateUnbounded<IReadOnlyList<ResourceLogLine>>();
        var resourceChannel = Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>();
        var dashboardClient = new TestDashboardClient(
            isEnabled: true,
            consoleLogsChannelProvider: name =>
            {
                subscribedResourceNameTcs.TrySetResult(name);
                return consoleLogsChannel;
            },
            resourceChannelProvider: () => resourceChannel,
            initialResources: [testResource]);

        SetupConsoleLogsServices(dashboardClient);

        var dimensionManager = Services.GetRequiredService<DimensionManager>();
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        dimensionManager.InvokeOnViewportInformationChanged(viewport);

        // Act
        var cut = RenderComponent<Components.Pages.ConsoleLogs>(builder =>
        {
            builder.Add(p => p.ResourceName, "test-resource");
            builder.Add(p => p.ViewportInformation, viewport);
        });

        var instance = cut.Instance;
        var logger = Services.GetRequiredService<ILogger<ConsoleLogsTests>>();
        var loc = Services.GetRequiredService<IStringLocalizer<Resources.ConsoleLogs>>();

        // Assert
        logger.LogInformation("Resource and subscription should be set immediately on first render.");
        cut.WaitForState(() => instance.PageViewModel.SelectedResource.Id?.InstanceId == testResource.Name);
        cut.WaitForState(() => instance.PageViewModel.Status == loc[nameof(Resources.ConsoleLogs.ConsoleLogsWatchingLogs)]);

        var subscribedResource = await subscribedResourceNameTcs.Task;
        Assert.Equal("test-resource", subscribedResource);

        logger.LogInformation("Log results are added to log viewer.");
        consoleLogsChannel.Writer.TryWrite([new ResourceLogLine(1, "Hello world", IsErrorMessage: false)]);
        cut.WaitForState(() => instance._logEntries.EntriesCount > 0);
    }

    [Fact]
    [QuarantinedTest("https://github.com/dotnet/aspire/issues/12740")]
    public async Task ReadingLogs_ErrorDuringRead_SetStatusAndLog()
    {
        // Arrange
        var testResource = ModelTestHelpers.CreateResource(resourceName: "test-resource", state: KnownResourceState.Running);
        var subscribedResourceNameTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var consoleLogsChannel = Channel.CreateUnbounded<IReadOnlyList<ResourceLogLine>>();
        var resourceChannel = Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>();
        var dashboardClient = new TestDashboardClient(
            isEnabled: true,
            consoleLogsChannelProvider: name =>
            {
                subscribedResourceNameTcs.TrySetResult(name);
                return consoleLogsChannel;
            },
            resourceChannelProvider: () => resourceChannel,
            initialResources: [testResource]);

        SetupConsoleLogsServices(dashboardClient);

        var dimensionManager = Services.GetRequiredService<DimensionManager>();
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        dimensionManager.InvokeOnViewportInformationChanged(viewport);

        // Act
        var cut = RenderComponent<Components.Pages.ConsoleLogs>(builder =>
        {
            builder.Add(p => p.ResourceName, "test-resource");
            builder.Add(p => p.ViewportInformation, viewport);
        });

        var instance = cut.Instance;
        var logger = Services.GetRequiredService<ILogger<ConsoleLogsTests>>();
        var loc = Services.GetRequiredService<IStringLocalizer<Resources.ConsoleLogs>>();

        // Assert
        logger.LogInformation("Resource and subscription should be set immediately on first render.");
        cut.WaitForState(() => instance.PageViewModel.SelectedResource.Id?.InstanceId == testResource.Name);
        cut.WaitForState(() => instance.PageViewModel.Status == loc[nameof(Resources.ConsoleLogs.ConsoleLogsWatchingLogs)]);

        var subscribedResource = await subscribedResourceNameTcs.Task;
        Assert.Equal("test-resource", subscribedResource);

        logger.LogInformation("Throw error from console logs subscription.");
        consoleLogsChannel.Writer.Complete(new InvalidOperationException("Error!"));

        cut.WaitForState(() => instance.PageViewModel.Status == loc[nameof(Resources.ConsoleLogs.ConsoleLogsErrorWatchingLogs)]);
    }

    [Fact]
    public async Task ReadingLogs_ErrorDuringReadAfterDispose_StatusUnchanged()
    {
        // Arrange
        var testResource = ModelTestHelpers.CreateResource(resourceName: "test-resource", state: KnownResourceState.Running);
        var subscribedResourceNameTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var consoleLogsChannel = Channel.CreateUnbounded<IReadOnlyList<ResourceLogLine>>();
        var resourceChannel = Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>();
        var dashboardClient = new TestDashboardClient(
            isEnabled: true,
            consoleLogsChannelProvider: name =>
            {
                subscribedResourceNameTcs.TrySetResult(name);
                return consoleLogsChannel;
            },
            resourceChannelProvider: () => resourceChannel,
            initialResources: [testResource]);

        SetupConsoleLogsServices(dashboardClient);

        var dimensionManager = Services.GetRequiredService<DimensionManager>();
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        dimensionManager.InvokeOnViewportInformationChanged(viewport);

        // Act
        var cut = RenderComponent<Components.Pages.ConsoleLogs>(builder =>
        {
            builder.Add(p => p.ResourceName, "test-resource");
            builder.Add(p => p.ViewportInformation, viewport);
        });

        var instance = cut.Instance;
        var logger = Services.GetRequiredService<ILogger<ConsoleLogsTests>>();
        var loc = Services.GetRequiredService<IStringLocalizer<Resources.ConsoleLogs>>();

        // Assert
        logger.LogInformation("Resource and subscription should be set immediately on first render.");
        cut.WaitForState(() => instance.PageViewModel.SelectedResource.Id?.InstanceId == testResource.Name);
        cut.WaitForState(() => instance.PageViewModel.Status == loc[nameof(Resources.ConsoleLogs.ConsoleLogsWatchingLogs)]);

        var subscribedResource = await subscribedResourceNameTcs.Task;
        Assert.Equal("test-resource", subscribedResource);

        await instance.DisposeAsync().DefaultTimeout();

        logger.LogInformation("Throw error from console logs subscription.");
        consoleLogsChannel.Writer.Complete(new InvalidOperationException("Error!"));

        cut.WaitForState(() => instance.PageViewModel.Status == loc[nameof(Resources.ConsoleLogs.ConsoleLogsWatchingLogs)]);
    }

    [Fact]
    public void ClearLogEntries_AllResources_LogsFilteredOut()
    {
        // Arrange
        var consoleLogsChannel = Channel.CreateUnbounded<IReadOnlyList<ResourceLogLine>>();
        var resourceChannel = Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>();
        var testResource = ModelTestHelpers.CreateResource(resourceName: "test-resource", state: KnownResourceState.Running);
        var dashboardClient = new TestDashboardClient(
            isEnabled: true,
            consoleLogsChannelProvider: name => consoleLogsChannel,
            resourceChannelProvider: () => resourceChannel,
            initialResources: [testResource]);
        var timeProvider = new TestTimeProvider();

        SetupConsoleLogsServices(dashboardClient, timeProvider: timeProvider);

        var dimensionManager = Services.GetRequiredService<DimensionManager>();
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        dimensionManager.InvokeOnViewportInformationChanged(viewport);

        // Act
        var cut = RenderComponent<Components.Pages.ConsoleLogs>(builder =>
        {
            builder.Add(p => p.ResourceName, "test-resource");
            builder.Add(p => p.ViewportInformation, viewport);
        });

        var instance = cut.Instance;
        var logger = Services.GetRequiredService<ILogger<ConsoleLogsTests>>();
        var loc = Services.GetRequiredService<IStringLocalizer<Resources.ConsoleLogs>>();

        // Assert
        logger.LogInformation("Waiting for selected resource.");
        cut.WaitForState(() => instance.PageViewModel.SelectedResource.Id?.InstanceId == testResource.Name);
        cut.WaitForState(() => instance.PageViewModel.Status == loc[nameof(Resources.ConsoleLogs.ConsoleLogsWatchingLogs)]);

        logger.LogInformation("Log results are added to log viewer.");
        consoleLogsChannel.Writer.TryWrite([new ResourceLogLine(1, "2025-02-08T10:16:08Z Hello world", IsErrorMessage: false)]);
        cut.WaitForState(() => instance._logEntries.EntriesCount > 0);

        // Set current time to the date of the first entry so all entries are cleared.
        var earliestEntry = instance._logEntries.GetEntries()[0];
        timeProvider.UtcNow = earliestEntry.Timestamp!.Value;

        logger.LogInformation("Clear current entries.");
        cut.Find(".clear-button").Click();

        cut.WaitForElement("#clear-menu-all");
        cut.Find("#clear-menu-all").Click();

        cut.WaitForState(() => instance._logEntries.EntriesCount == 0);

        logger.LogInformation("New log results are added to log viewer.");
        consoleLogsChannel.Writer.TryWrite([new ResourceLogLine(2, "2025-03-08T10:16:08Z Hello world", IsErrorMessage: false)]);
        cut.WaitForState(() => instance._logEntries.EntriesCount > 0);
    }

    [Fact]
    public async Task ConsoleLogsManager_ClearLogs_LogsFilteredOutAsync()
    {
        // Arrange
        var consoleLogsChannel = Channel.CreateUnbounded<IReadOnlyList<ResourceLogLine>>();
        var resourceChannel = Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>();
        var testResource = ModelTestHelpers.CreateResource(resourceName: "test-resource", state: KnownResourceState.Running);
        var dashboardClient = new TestDashboardClient(
            isEnabled: true,
            consoleLogsChannelProvider: name => consoleLogsChannel,
            resourceChannelProvider: () => resourceChannel,
            initialResources: [testResource]);
        var timeProvider = new TestTimeProvider();

        SetupConsoleLogsServices(dashboardClient, timeProvider: timeProvider);

        var dimensionManager = Services.GetRequiredService<DimensionManager>();
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        dimensionManager.InvokeOnViewportInformationChanged(viewport);
        var consoleLogsManager = Services.GetRequiredService<ConsoleLogsManager>();

        // Act
        var cut = RenderComponent<Components.Pages.ConsoleLogs>(builder =>
        {
            builder.Add(p => p.ResourceName, "test-resource");
            builder.Add(p => p.ViewportInformation, viewport);
        });

        var instance = cut.Instance;
        var logger = Services.GetRequiredService<ILogger<ConsoleLogsTests>>();
        var loc = Services.GetRequiredService<IStringLocalizer<Resources.ConsoleLogs>>();

        // Assert
        Assert.Single(consoleLogsManager.GetSubscriptions());

        logger.LogInformation("Waiting for selected resource.");
        cut.WaitForState(() => instance.PageViewModel.SelectedResource.Id?.InstanceId == testResource.Name);
        cut.WaitForState(() => instance.PageViewModel.Status == loc[nameof(Resources.ConsoleLogs.ConsoleLogsWatchingLogs)]);

        logger.LogInformation("Log results are added to log viewer.");
        consoleLogsChannel.Writer.TryWrite([new ResourceLogLine(1, "2025-02-08T10:16:08Z Hello world", IsErrorMessage: false)]);
        cut.WaitForState(() => instance._logEntries.EntriesCount > 0);

        // Set current time to the date of the first entry so all entries are cleared.
        var earliestEntry = instance._logEntries.GetEntries()[0];
        timeProvider.UtcNow = earliestEntry.Timestamp!.Value;

        await consoleLogsManager.UpdateFiltersAsync(new ConsoleLogsFilters { FilterAllLogsDate = earliestEntry.Timestamp!.Value });

        cut.WaitForState(() => instance._logEntries.EntriesCount == 0);

        logger.LogInformation("New log results are added to log viewer.");
        consoleLogsChannel.Writer.TryWrite([new ResourceLogLine(2, "2025-03-08T10:16:08Z Hello world", IsErrorMessage: false)]);
        cut.WaitForState(() => instance._logEntries.EntriesCount > 0);
    }

    [Fact]
    public void MenuButtons_SelectedResourceChanged_ButtonsUpdated()
    {
        // Arrange
        var testResource = ModelTestHelpers.CreateResource(
            resourceName: "test-resource",
            state: KnownResourceState.Running,
            commands: [new CommandViewModel("test-name", CommandViewModelState.Enabled, "test-displayname", "test-displaydescription", confirmationMessage: "", parameter: null, isHighlighted: true, iconName: string.Empty, iconVariant: IconVariant.Regular)]);
        var subscribedResourceNameTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var consoleLogsChannel = Channel.CreateUnbounded<IReadOnlyList<ResourceLogLine>>();
        var resourceChannel = Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>();
        var dashboardClient = new TestDashboardClient(
            isEnabled: true,
            consoleLogsChannelProvider: name =>
            {
                subscribedResourceNameTcs.TrySetResult(name);
                return consoleLogsChannel;
            },
            resourceChannelProvider: () => resourceChannel,
            initialResources: [testResource]);

        SetupConsoleLogsServices(dashboardClient);

        var dimensionManager = Services.GetRequiredService<DimensionManager>();
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        dimensionManager.InvokeOnViewportInformationChanged(viewport);

        // Act 1
        var cut = RenderComponent<Components.Pages.ConsoleLogs>(builder =>
        {
            builder.Add(p => p.ResourceName, "test-resource");
            builder.Add(p => p.ViewportInformation, viewport);
        });

        var instance = cut.Instance;
        var logger = Services.GetRequiredService<ILogger<ConsoleLogsTests>>();
        var loc = Services.GetRequiredService<IStringLocalizer<Resources.ConsoleLogs>>();

        // Assert 1
        cut.WaitForState(() => instance.PageViewModel.SelectedResource.Id?.InstanceId == testResource.Name);

        cut.WaitForAssertion(() =>
        {
            var highlightedCommands = cut.FindAll(".highlighted-command");
            Assert.Single(highlightedCommands);
        });

        // Act 2
        testResource = ModelTestHelpers.CreateResource(
            resourceName: "test-resource",
            state: KnownResourceState.Running,
            commands: []);
        resourceChannel.Writer.TryWrite([
            new ResourceViewModelChange(ResourceViewModelChangeType.Upsert, testResource)
        ]);

        // Assert 2
        cut.WaitForAssertion(() =>
        {
            var highlightedCommands = cut.FindAll(".highlighted-command");
            Assert.Empty(highlightedCommands);
        });
    }

    [Fact]
    public async Task ExecuteCommand_DelayExecuting_IsExecutingReturnsTrueWhileRunning()
    {
        // Arrange
        var testResource = ModelTestHelpers.CreateResource(
            resourceName: "test-resource",
            state: KnownResourceState.Running,
            commands: [new CommandViewModel("test-name", CommandViewModelState.Enabled, "test-displayname", "test-displaydescription", confirmationMessage: "", parameter: null, isHighlighted: true, iconName: string.Empty, iconVariant: IconVariant.Regular)]);
        var subscribedResourceNameTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var consoleLogsChannel = Channel.CreateUnbounded<IReadOnlyList<ResourceLogLine>>();
        var resourceChannel = Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>();
        var resourceCommandChannel = Channel.CreateUnbounded<ResourceCommandResponseViewModel>();
        var dashboardClient = new TestDashboardClient(
            isEnabled: true,
            consoleLogsChannelProvider: name =>
            {
                subscribedResourceNameTcs.TrySetResult(name);
                return consoleLogsChannel;
            },
            resourceChannelProvider: () => resourceChannel,
            resourceCommandsChannel: resourceCommandChannel,
            initialResources: [testResource]);

        SetupConsoleLogsServices(dashboardClient);

        var dimensionManager = Services.GetRequiredService<DimensionManager>();
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        dimensionManager.InvokeOnViewportInformationChanged(viewport);

        var dashboardCommandExecutor = Services.GetRequiredService<DashboardCommandExecutor>();

        var cut = RenderComponent<Components.Pages.ConsoleLogs>(builder =>
        {
            builder.Add(p => p.ResourceName, "test-resource");
            builder.Add(p => p.ViewportInformation, viewport);
        });

        var instance = cut.Instance;
        var logger = Services.GetRequiredService<ILogger<ConsoleLogsTests>>();
        var loc = Services.GetRequiredService<IStringLocalizer<Resources.ConsoleLogs>>();

        AngleSharp.Dom.IElement highlightedCommand = default!;
        cut.WaitForState(() => instance.PageViewModel.SelectedResource.Id?.InstanceId == testResource.Name);
        cut.WaitForAssertion(() =>
        {
            var highlightedCommands = cut.FindAll(".highlighted-command");
            highlightedCommand = Assert.Single(highlightedCommands);
        });

        // Act
        highlightedCommand.Click();

        // Assert
        await AsyncTestHelpers.AssertIsTrueRetryAsync(() => dashboardCommandExecutor.IsExecuting("test-resource", "test-name"), "Command start executing");

        resourceCommandChannel.Writer.TryWrite(new ResourceCommandResponseViewModel
        {
            Kind = ResourceCommandResponseKind.Succeeded
        });

        await AsyncTestHelpers.AssertIsTrueRetryAsync(() => !dashboardCommandExecutor.IsExecuting("test-resource", "test-name"), "Command finish executing");
    }

    [Fact]
    public void PauseResumeButton_TogglePauseResume_LogsPausedAndResumed()
    {
        // Arrange
        var testResource = ModelTestHelpers.CreateResource(resourceName: "test-resource", state: KnownResourceState.Running);
        var consoleLogsChannel = Channel.CreateUnbounded<IReadOnlyList<ResourceLogLine>>();
        var resourceChannel = Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>();
        var dashboardClient = new TestDashboardClient(
            isEnabled: true,
            consoleLogsChannelProvider: name => consoleLogsChannel,
            resourceChannelProvider: () => resourceChannel,
            initialResources: [testResource]);

        SetupConsoleLogsServices(dashboardClient);

        var pauseManager = Services.GetRequiredService<PauseManager>();
        var timeProvider = Services.GetRequiredService<BrowserTimeProvider>();
        var loc = Services.GetRequiredService<IStringLocalizer<Resources.ConsoleLogs>>();
        var dimensionManager = Services.GetRequiredService<DimensionManager>();
        var logger = Services.GetRequiredService<ILogger<ConsoleLogsTests>>();
        var browserTimeProvider = Services.GetRequiredService<BrowserTimeProvider>();
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        dimensionManager.InvokeOnViewportInformationChanged(viewport);

        // Act
        var cut = RenderComponent<Components.Pages.ConsoleLogs>(builder =>
        {
            builder.Add(p => p.ResourceName, "test-resource");
            builder.Add(p => p.ViewportInformation, viewport);
        });

        var instance = cut.Instance;

        // Assert initial state
        cut.WaitForState(() => instance.PageViewModel.SelectedResource.Id?.InstanceId == testResource.Name);

        logger.LogInformation("Check logs are empty.");
        PrintCurrentLogEntries(cut.Instance._logEntries);
        Assert.Empty(cut.Instance._logEntries.GetEntries());

        logger.LogInformation("Pause logs.");
        var pauseResumeButton = cut.FindComponent<PauseIncomingDataSwitch>().WaitForElement("fluent-button");
        pauseResumeButton.Click();
        cut.WaitForAssertion(() => Assert.True(pauseManager.ConsoleLogsPaused));

        logger.LogInformation("Wait for pause log.");
        var pauseConsoleLogLine = cut.WaitForElement(".log-pause");

        logger.LogInformation("Adding filtered logs during pause.");
        // Add new logs while paused and assert that the log viewer shows correct filtered count
        var pauseContent = $"{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ss.fffK} Log while paused";
        consoleLogsChannel.Writer.TryWrite([new ResourceLogLine(1, pauseContent, IsErrorMessage: false)]);
        consoleLogsChannel.Writer.TryWrite([new ResourceLogLine(2, pauseContent, IsErrorMessage: false)]);
        consoleLogsChannel.Writer.TryWrite([new ResourceLogLine(3, pauseContent, IsErrorMessage: false)]);

        logger.LogInformation("Assert that the only log is still the pause log.");
        cut.WaitForAssertion(() =>
        {
            var pauseLog = Assert.Single(cut.Instance._logEntries.GetEntries());
            Assert.Equal(
                string.Format(
                    loc[Resources.ConsoleLogs.ConsoleLogsPauseActive],
                    FormatHelpers.FormatTimeWithOptionalDate(timeProvider, pauseLog.Pause!.StartTime, MillisecondsDisplay.Truncated),
                    3),
                pauseConsoleLogLine.TextContent);
        });

        logger.LogInformation("Resume logs.");
        // Check that
        // - the pause line has been replaced with pause details
        // - the log viewer shows the new log
        // - the log viewer does not show the discarded log
        pauseResumeButton.Click();
        cut.WaitForAssertion(() => Assert.False(pauseManager.ConsoleLogsPaused));

        logger.LogInformation("Assert that pause log has expected content.");
        cut.WaitForAssertion(() =>
        {
            PrintCurrentLogEntries(cut.Instance._logEntries);

            var pauseEntry = Assert.Single(cut.Instance._logEntries.GetEntries());
            var pause = pauseEntry.Pause;
            Assert.NotNull(pause);
            Assert.NotNull(pause.EndTime);
            Assert.Equal(
                string.Format(
                    loc[Resources.ConsoleLogs.ConsoleLogsPauseDetails],
                    FormatHelpers.FormatTimeWithOptionalDate(timeProvider, pause.StartTime, MillisecondsDisplay.Truncated),
                    FormatHelpers.FormatTimeWithOptionalDate(timeProvider, pause.EndTime.Value, MillisecondsDisplay.Truncated),
                    3),
                pauseConsoleLogLine.TextContent);
        });

        logger.LogInformation("Write a new log after resume.");
        var resumeContent = $"{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ss.fffK} Log after resume";
        consoleLogsChannel.Writer.TryWrite([new ResourceLogLine(4, resumeContent, IsErrorMessage: false)]);

        logger.LogInformation("Assert that log entries discarded aren't in log viewer and log entries that should be logged are in log viewer.");
        cut.WaitForAssertion(() =>
        {
            var logViewer = cut.FindComponent<LogViewer>();
            PrintCurrentLogEntries(logViewer.Instance.LogEntries!);

            var newLog = Assert.Single(logViewer.Instance.LogEntries!.GetEntries(), e => e.RawContent == resumeContent);
            // We discarded one log while paused, so the new log should be line 3, skipping one
            Assert.Equal(4, newLog.LineNumber);
            Assert.DoesNotContain(pauseContent, logViewer.Instance.LogEntries!.GetEntries().Select(e => e.RawContent));
        });

        void PrintCurrentLogEntries(LogEntries logEntries)
        {
            logger.LogInformation($"Log entries count: {logEntries.EntriesCount}");

            foreach (var logEntry in logEntries.GetEntries())
            {
                logger.LogInformation($"Log line. Type = {logEntry.Type}, Raw content = {logEntry.RawContent ?? "no content"}, Pause content: {logEntry.Pause?.GetDisplayText(loc, browserTimeProvider) ?? "n/a"}");
            }
        }
    }

    private void SetupConsoleLogsServices(TestDashboardClient? dashboardClient = null, TestTimeProvider? timeProvider = null)
    {
        FluentUISetupHelpers.SetupFluentDivider(this);
        FluentUISetupHelpers.SetupFluentInputLabel(this);
        FluentUISetupHelpers.SetupFluentList(this);
        FluentUISetupHelpers.SetupFluentSearch(this);
        FluentUISetupHelpers.SetupFluentKeyCode(this);
        FluentUISetupHelpers.SetupFluentMenu(this);
        FluentUISetupHelpers.SetupFluentAnchor(this);
        FluentUISetupHelpers.SetupFluentAnchoredRegion(this);
        FluentUISetupHelpers.SetupFluentToolbar(this);

        JSInterop.SetupVoid("initializeContinuousScroll");
        JSInterop.SetupVoid("resetContinuousScrollPosition");

        FluentUISetupHelpers.AddCommonDashboardServices(this, browserTimeProvider: timeProvider);

        var loggerFactory = IntegrationTestHelpers.CreateLoggerFactory(_testOutputHelper);

        Services.AddSingleton<ILoggerFactory>(loggerFactory);
        Services.AddSingleton<IToastService, ToastService>();
        Services.AddSingleton<IconResolver>();
        Services.AddSingleton<IDashboardClient>(dashboardClient ?? new TestDashboardClient());
        Services.AddSingleton<DashboardCommandExecutor>();
        Services.AddSingleton<ConsoleLogsManager>();
    }
}
