// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Resize;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Otlp.Storage;
using Aspire.Dashboard.Resources;
using Aspire.Dashboard.Tests.TelemetryRepositoryTests;
using Aspire.Tests.Shared;
using Aspire.Tests.Shared.DashboardModel;
using Aspire.Tests.Shared.Telemetry;
using Google.Protobuf.Collections;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.FluentUI.AspNetCore.Components;
using OpenTelemetry.Proto.Trace.V1;
using Xunit;

namespace Aspire.Dashboard.Tests.Model;

public sealed class ResourceMenuBuilderTests
{
    private static readonly DateTime s_testTime = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private readonly IconResolver _iconResolver = new IconResolver(NullLogger<IconResolver>.Instance);
    private readonly DashboardDialogService _dialogService;

    public ResourceMenuBuilderTests()
    {
        var dimensionManager = new DimensionManager();
        dimensionManager.InvokeOnViewportInformationChanged(new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false));
        _dialogService = new DashboardDialogService(
            new TestDialogService(),
            new TestStringLocalizer<Dialogs>(),
            dimensionManager);
    }

    private ResourceMenuBuilder CreateResourceMenuBuilder(TelemetryRepository repository, TestAIContextProvider aiContextProvider)
    {
        return new ResourceMenuBuilder(
            new TestNavigationManager(),
            repository,
            aiContextProvider,
            new TestStringLocalizer<ControlsStrings>(),
            new TestStringLocalizer<Resources.Resources>(),
            new TestStringLocalizer<Resources.AIAssistant>(),
            new TestStringLocalizer<Resources.AIPrompts>(),
            _iconResolver,
            _dialogService);
    }

    [Fact]
    public void AddMenuItems_NoTelemetry_NoTelemetryItems()
    {
        // Arrange
        var resource = ModelTestHelpers.CreateResource();
        var repository = TelemetryTestHelpers.CreateRepository();
        var aiContextProvider = new TestAIContextProvider();
        var resourceMenuBuilder = CreateResourceMenuBuilder(repository, aiContextProvider);

        // Act
        var menuItems = new List<MenuButtonItem>();
        resourceMenuBuilder.AddMenuItems(
            menuItems,
            resource,
            new Dictionary<string, ResourceViewModel>(StringComparer.OrdinalIgnoreCase) { [resource.Name] = resource },
            EventCallback.Empty,
            EventCallback<CommandViewModel>.Empty,
            (_, _) => false,
            showViewDetails: true,
            showConsoleLogsItem: true,
            showUrls: true);

        // Assert
        Assert.Collection(menuItems,
            e => Assert.Equal("Localized:ActionViewDetailsText", e.Text),
            e => Assert.Equal("Localized:ResourceActionConsoleLogsText", e.Text),
            e => Assert.Equal("Localized:ViewJson", e.Text));
    }

    [Fact]
    public void AddMenuItems_UninstrumentedPeer_TraceItem()
    {
        // Arrange
        var resource = ModelTestHelpers.CreateResource(resourceName: "test-abc");
        var outgoingPeerResolver = new TestOutgoingPeerResolver(onResolve: attributes => (resource.Name, resource));
        var repository = TelemetryTestHelpers.CreateRepository(outgoingPeerResolvers: [outgoingPeerResolver]);
        var aiContextProvider = new TestAIContextProvider();
        var addContext = new AddContext();
        repository.AddTraces(addContext, new RepeatedField<ResourceSpans>()
        {
            new ResourceSpans
            {
                Resource = TelemetryTestHelpers.CreateResource(name: "source", instanceId: "abc"),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = TelemetryTestHelpers.CreateScope(),
                        Spans =
                        {
                            TelemetryTestHelpers.CreateSpan(traceId: "1", spanId: "1-1", startTime: s_testTime.AddMinutes(1), endTime: s_testTime.AddMinutes(10), attributes: [KeyValuePair.Create(OtlpSpan.PeerServiceAttributeKey, "value-1")], kind: Span.Types.SpanKind.Client),
                            TelemetryTestHelpers.CreateSpan(traceId: "1", spanId: "1-2", startTime: s_testTime.AddMinutes(5), endTime: s_testTime.AddMinutes(10), parentSpanId: "1-1", attributes: [KeyValuePair.Create(OtlpSpan.PeerServiceAttributeKey, "value-2")], kind: Span.Types.SpanKind.Client)
                        }
                    }
                }
            }
        });

        var resourceMenuBuilder = CreateResourceMenuBuilder(repository, aiContextProvider);

        // Act
        var menuItems = new List<MenuButtonItem>();
        resourceMenuBuilder.AddMenuItems(
            menuItems,
            resource,
            new Dictionary<string, ResourceViewModel>(StringComparer.OrdinalIgnoreCase) { [resource.Name] = resource },
            EventCallback.Empty,
            EventCallback<CommandViewModel>.Empty,
            (_, _) => false,
            showViewDetails: true,
            showConsoleLogsItem: true,
            showUrls: true);

        // Assert
        Assert.Collection(menuItems,
            e => Assert.Equal("Localized:ActionViewDetailsText", e.Text),
            e => Assert.Equal("Localized:ResourceActionConsoleLogsText", e.Text),
            e => Assert.Equal("Localized:ViewJson", e.Text),
            e => Assert.True(e.IsDivider),
            e => Assert.Equal("Localized:ResourceActionTracesText", e.Text));
    }

    [Fact]
    public void AddMenuItems_HasTelemetry_TelemetryItems()
    {
        // Arrange
        var resource = ModelTestHelpers.CreateResource(resourceName: "test-abc");
        var repository = TelemetryTestHelpers.CreateRepository();
        var aiContextProvider = new TestAIContextProvider();
        var addContext = new AddContext();
        repository.AddTraces(addContext, new RepeatedField<ResourceSpans>()
        {
            new ResourceSpans
            {
                Resource = TelemetryTestHelpers.CreateResource(name: "test", instanceId: "abc"),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = TelemetryTestHelpers.CreateScope(),
                        Spans =
                        {
                            TelemetryTestHelpers.CreateSpan(traceId: "1", spanId: "1-1", startTime: s_testTime.AddMinutes(1), endTime: s_testTime.AddMinutes(10))
                        }
                    }
                }
            }
        });

        var resourceMenuBuilder = CreateResourceMenuBuilder(repository, aiContextProvider);

        // Act
        var menuItems = new List<MenuButtonItem>();
        resourceMenuBuilder.AddMenuItems(
            menuItems,
            resource,
            new Dictionary<string, ResourceViewModel>(StringComparer.OrdinalIgnoreCase) { [resource.Name] = resource },
            EventCallback.Empty,
            EventCallback<CommandViewModel>.Empty,
            (_, _) => false,
            showViewDetails: true,
            showConsoleLogsItem: true,
            showUrls: true);

        // Assert
        Assert.Collection(menuItems,
            e => Assert.Equal("Localized:ActionViewDetailsText", e.Text),
            e => Assert.Equal("Localized:ResourceActionConsoleLogsText", e.Text),
            e => Assert.Equal("Localized:ViewJson", e.Text),
            e => Assert.True(e.IsDivider),
            e => Assert.Equal("Localized:ResourceActionStructuredLogsText", e.Text),
            e => Assert.Equal("Localized:ResourceActionTracesText", e.Text),
            e => Assert.Equal("Localized:ResourceActionMetricsText", e.Text));
    }

    [Fact]
    public void AddMenuItems_WithFromSpecEnvVars_ExportEnvMenuItemShown()
    {
        // Arrange
        var resource = ModelTestHelpers.CreateResource(
            environment: [
                new EnvironmentVariableViewModel("SPEC_VAR", "spec-value", fromSpec: true),
                new EnvironmentVariableViewModel("RUNTIME_VAR", "runtime-value", fromSpec: false)
            ]);
        var repository = TelemetryTestHelpers.CreateRepository();
        var aiContextProvider = new TestAIContextProvider();
        var resourceMenuBuilder = CreateResourceMenuBuilder(repository, aiContextProvider);

        // Act
        var menuItems = new List<MenuButtonItem>();
        resourceMenuBuilder.AddMenuItems(
            menuItems,
            resource,
            new Dictionary<string, ResourceViewModel>(StringComparer.OrdinalIgnoreCase) { [resource.Name] = resource },
            EventCallback.Empty,
            EventCallback<CommandViewModel>.Empty,
            (_, _) => false,
            showViewDetails: true,
            showConsoleLogsItem: true,
            showUrls: true);

        // Assert
        Assert.Collection(menuItems,
            e => Assert.Equal("Localized:ActionViewDetailsText", e.Text),
            e => Assert.Equal("Localized:ResourceActionConsoleLogsText", e.Text),
            e => Assert.Equal("Localized:ViewJson", e.Text),
            e => Assert.Equal("Localized:ExportEnv", e.Text));
    }

    [Fact]
    public void AddMenuItems_WithoutFromSpecEnvVars_ExportEnvMenuItemNotShown()
    {
        // Arrange - only runtime env vars (fromSpec: false), no spec env vars
        var resource = ModelTestHelpers.CreateResource(
            environment: [
                new EnvironmentVariableViewModel("RUNTIME_VAR1", "value1", fromSpec: false),
                new EnvironmentVariableViewModel("RUNTIME_VAR2", "value2", fromSpec: false)
            ]);
        var repository = TelemetryTestHelpers.CreateRepository();
        var aiContextProvider = new TestAIContextProvider();
        var resourceMenuBuilder = CreateResourceMenuBuilder(repository, aiContextProvider);

        // Act
        var menuItems = new List<MenuButtonItem>();
        resourceMenuBuilder.AddMenuItems(
            menuItems,
            resource,
            new Dictionary<string, ResourceViewModel>(StringComparer.OrdinalIgnoreCase) { [resource.Name] = resource },
            EventCallback.Empty,
            EventCallback<CommandViewModel>.Empty,
            (_, _) => false,
            showViewDetails: true,
            showConsoleLogsItem: true,
            showUrls: true);

        // Assert - ExportEnv should NOT be in the menu
        Assert.Collection(menuItems,
            e => Assert.Equal("Localized:ActionViewDetailsText", e.Text),
            e => Assert.Equal("Localized:ResourceActionConsoleLogsText", e.Text),
            e => Assert.Equal("Localized:ViewJson", e.Text));
    }

    [Fact]
    public void AddMenuItems_IncludesStartCommandLikeOtherVisibleCommands()
    {
        var startCommand = new CommandViewModel(
            CommandViewModel.StartCommand,
            CommandViewModelState.Enabled,
            "Start",
            "Start the resource.",
            confirmationMessage: "",
            argumentInputs: [],
            isHighlighted: true,
            iconName: string.Empty,
            iconVariant: IconVariant.Regular);
        var stopCommand = new CommandViewModel(
            CommandViewModel.StopCommand,
            CommandViewModelState.Enabled,
            "Stop",
            "Stop the resource.",
            confirmationMessage: "",
            argumentInputs: [],
            isHighlighted: true,
            iconName: string.Empty,
            iconVariant: IconVariant.Regular);
        var resource = ModelTestHelpers.CreateResource(commands: [startCommand, stopCommand]);
        var repository = TelemetryTestHelpers.CreateRepository();
        var aiContextProvider = new TestAIContextProvider();
        var resourceMenuBuilder = CreateResourceMenuBuilder(repository, aiContextProvider);

        var menuItems = new List<MenuButtonItem>();
        resourceMenuBuilder.AddMenuItems(
            menuItems,
            resource,
            new Dictionary<string, ResourceViewModel>(StringComparer.OrdinalIgnoreCase) { [resource.Name] = resource },
            EventCallback.Empty,
            EventCallback<CommandViewModel>.Empty,
            (_, _) => false,
            showViewDetails: false,
            showConsoleLogsItem: false,
            showUrls: false);

        Assert.Collection(menuItems,
            e => Assert.Equal("Localized:ViewJson", e.Text),
            e => Assert.True(e.IsDivider),
            e => Assert.Equal("Start", e.Text),
            e => Assert.Equal("Stop", e.Text));
    }

    [Fact]
    public void AddMenuItems_UnresolvableIconName_UsesFallbackIcon()
    {
        // A command with an icon name that doesn't map to any FluentUI icon should
        // get a QuestionCircle fallback instead of null, preventing the overflow issue in #18385.
        var command = new CommandViewModel(
            "test-command",
            CommandViewModelState.Enabled,
            "Test Command",
            "A command with a bad icon name.",
            confirmationMessage: "",
            argumentInputs: [],
            isHighlighted: false,
            iconName: "NotARealIconName",
            iconVariant: IconVariant.Regular);
        var resource = ModelTestHelpers.CreateResource(commands: [command]);
        var repository = TelemetryTestHelpers.CreateRepository();
        var aiContextProvider = new TestAIContextProvider();
        var resourceMenuBuilder = CreateResourceMenuBuilder(repository, aiContextProvider);

        var menuItems = new List<MenuButtonItem>();
        resourceMenuBuilder.AddMenuItems(
            menuItems,
            resource,
            new Dictionary<string, ResourceViewModel>(StringComparer.OrdinalIgnoreCase) { [resource.Name] = resource },
            EventCallback.Empty,
            EventCallback<CommandViewModel>.Empty,
            (_, _) => false,
            showViewDetails: false,
            showConsoleLogsItem: false,
            showUrls: false);

        Assert.Collection(menuItems,
            e => Assert.Equal("Localized:ViewJson", e.Text),
            e => Assert.True(e.IsDivider),
            e =>
            {
                Assert.Equal("Test Command", e.Text);
                Assert.IsType<Microsoft.FluentUI.AspNetCore.Components.Icons.Regular.Size16.QuestionCircle>(e.Icon);
            });
    }

    [Fact]
    public void AddMenuItems_NoIconName_IconIsNull()
    {
        // A command with no icon name should have a null icon in the menu
        // (menu items have text labels so no fallback icon is needed).
        var command = new CommandViewModel(
            "test-command",
            CommandViewModelState.Enabled,
            "Test Command",
            "A command with no icon.",
            confirmationMessage: "",
            argumentInputs: [],
            isHighlighted: false,
            iconName: string.Empty,
            iconVariant: IconVariant.Regular);
        var resource = ModelTestHelpers.CreateResource(commands: [command]);
        var repository = TelemetryTestHelpers.CreateRepository();
        var aiContextProvider = new TestAIContextProvider();
        var resourceMenuBuilder = CreateResourceMenuBuilder(repository, aiContextProvider);

        var menuItems = new List<MenuButtonItem>();
        resourceMenuBuilder.AddMenuItems(
            menuItems,
            resource,
            new Dictionary<string, ResourceViewModel>(StringComparer.OrdinalIgnoreCase) { [resource.Name] = resource },
            EventCallback.Empty,
            EventCallback<CommandViewModel>.Empty,
            (_, _) => false,
            showViewDetails: false,
            showConsoleLogsItem: false,
            showUrls: false);

        Assert.Collection(menuItems,
            e => Assert.Equal("Localized:ViewJson", e.Text),
            e => Assert.True(e.IsDivider),
            e =>
            {
                Assert.Equal("Test Command", e.Text);
                Assert.Null(e.Icon);
            });
    }

    private sealed class TestNavigationManager : NavigationManager
    {
    }
}
