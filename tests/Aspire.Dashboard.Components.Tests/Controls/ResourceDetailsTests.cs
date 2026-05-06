// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Collections.Immutable;
using Aspire.Dashboard.Components.Controls;
using Aspire.Dashboard.Resources;
using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Model;
using Aspire.Tests.Shared.DashboardModel;
using Bunit;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.FluentUI.AspNetCore.Components;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Controls;

[UseCulture("en-US")]
public class ResourceDetailsTests : DashboardTestContext
{
    [Fact]
    public async Task ClickMaskAllSwitch_UpdatedResource_MaskChanged()
    {
        // Arrange
        ResourceSetupHelpers.SetupResourceDetails(this);

        var resource1 = ModelTestHelpers.CreateResource(
            "app1",
            environment: new List<EnvironmentVariableViewModel>
            {
                new EnvironmentVariableViewModel("envvar1", "value!", fromSpec: true),
                new EnvironmentVariableViewModel("envvar2", "value!", fromSpec: true)
            }.ToImmutableArray());

        // Act
        var cut = RenderComponent<ResourceDetails>(builder =>
        {
            builder.Add(p => p.ShowSpecOnlyToggle, true);
            builder.Add(p => p.Resource, resource1);
            builder.Add(p => p.ResourceByName, new ConcurrentDictionary<string, ResourceViewModel>([new KeyValuePair<string, ResourceViewModel> (resource1.Name, resource1)]));
        });

        // Assert
        Assert.Collection(cut.Instance.FilteredEnvironmentVariables,
            e =>
            {
                Assert.Equal("envvar1", e.Name);
                Assert.Equal("value!", e.Value);
                Assert.True(e.IsValueMasked);
            },
            e =>
            {
                Assert.Equal("envvar2", e.Name);
                Assert.Equal("value!", e.Value);
                Assert.True(e.IsValueMasked);
            });

        var actionsButton = cut.Find(".resource-details-actions");
        await actionsButton.ClickAsync(new MouseEventArgs());

        var maskAllSwitch = cut.Find(".mask-all-switch");

        // HACK. Calling OnClick on the element isn't triggering the event correctly. Instead, call OnClick on the component.
        var item = cut.FindComponents<FluentMenuItem>().Single(s => s.Instance.Class == maskAllSwitch.Attributes["class"]!.Value);
        await cut.InvokeAsync(() => item.Instance.OnClick.InvokeAsync(new MouseEventArgs()));

        Assert.Collection(cut.Instance.FilteredEnvironmentVariables,
            e =>
            {
                Assert.Equal("envvar1", e.Name);
                Assert.False(e.IsValueMasked);
            },
            e =>
            {
                Assert.Equal("envvar2", e.Name);
                Assert.False(e.IsValueMasked);
            });

        var resource2 = ModelTestHelpers.CreateResource(
            "app1",
            environment: new List<EnvironmentVariableViewModel>
            {
                new EnvironmentVariableViewModel("envvar1", "value!", fromSpec: true),
                new EnvironmentVariableViewModel("envvar2", "value!", fromSpec: true),
                new EnvironmentVariableViewModel("envvar3", "value!", fromSpec: true)
            }.ToImmutableArray());

        cut.SetParametersAndRender(builder =>
        {
            builder.Add(p => p.Resource, resource2);
        });

        Assert.Collection(cut.Instance.FilteredEnvironmentVariables,
            e =>
            {
                Assert.Equal("envvar1", e.Name);
                Assert.False(e.IsValueMasked);
            },
            e =>
            {
                Assert.Equal("envvar2", e.Name);
                Assert.False(e.IsValueMasked);
            },
            e =>
            {
                Assert.Equal("envvar3", e.Name);
                Assert.False(e.IsValueMasked);
            });
    }

    [Fact]
    public async Task ClickMaskAllSwitch_NewResource_MaskChanged()
    {
        // Arrange
        ResourceSetupHelpers.SetupResourceDetails(this);

        var resource1 = ModelTestHelpers.CreateResource(
            "app1",
            environment: new List<EnvironmentVariableViewModel>
            {
                new EnvironmentVariableViewModel("envvar1", "value!", fromSpec: true),
                new EnvironmentVariableViewModel("envvar2", "value!", fromSpec: true)
            }.ToImmutableArray());

        // Act
        var cut = RenderComponent<ResourceDetails>(builder =>
        {
            builder.Add(p => p.ShowSpecOnlyToggle, true);
            builder.Add(p => p.Resource, resource1);
            builder.Add(p => p.ResourceByName, new ConcurrentDictionary<string, ResourceViewModel>([new KeyValuePair<string, ResourceViewModel> (resource1.Name, resource1)]));
        });

        // Assert
        Assert.Collection(cut.Instance.FilteredEnvironmentVariables,
            e =>
            {
                Assert.Equal("envvar1", e.Name);
                Assert.Equal("value!", e.Value);
                Assert.True(e.IsValueMasked);
            },
            e =>
            {
                Assert.Equal("envvar2", e.Name);
                Assert.Equal("value!", e.Value);
                Assert.True(e.IsValueMasked);
            });

        var actionsButton = cut.Find(".resource-details-actions");
        await actionsButton.ClickAsync(new MouseEventArgs());

        var maskAllSwitch = cut.Find(".mask-all-switch");

        // HACK. Calling OnClick on the element isn't triggering the event correctly. Instead, call OnClick on the component.
        var item = cut.FindComponents<FluentMenuItem>().Single(s => s.Instance.Class == maskAllSwitch.Attributes["class"]!.Value);
        await cut.InvokeAsync(() => item.Instance.OnClick.InvokeAsync(new MouseEventArgs()));

        Assert.Collection(cut.Instance.FilteredEnvironmentVariables,
            e =>
            {
                Assert.Equal("envvar1", e.Name);
                Assert.False(e.IsValueMasked);
            },
            e =>
            {
                Assert.Equal("envvar2", e.Name);
                Assert.False(e.IsValueMasked);
            });

        var resource2 = ModelTestHelpers.CreateResource(
            "app2",
            environment: new List<EnvironmentVariableViewModel>
            {
                new EnvironmentVariableViewModel("envvar1", "value!", fromSpec: true),
                new EnvironmentVariableViewModel("envvar2", "value!", fromSpec: true),
                new EnvironmentVariableViewModel("envvar3", "value!", fromSpec: true)
            }.ToImmutableArray());

        cut.SetParametersAndRender(builder =>
        {
            builder.Add(p => p.Resource, resource2);
        });

        Assert.Collection(cut.Instance.FilteredEnvironmentVariables,
            e =>
            {
                Assert.Equal("envvar1", e.Name);
                Assert.True(e.IsValueMasked);
            },
            e =>
            {
                Assert.Equal("envvar2", e.Name);
                Assert.True(e.IsValueMasked);
            },
            e =>
            {
                Assert.Equal("envvar3", e.Name);
                Assert.True(e.IsValueMasked);
            });
    }

    [Fact]
    public async Task ClickMaskEnvVarSwitch_UpdatedResource_MaskChanged()
    {
        // Arrange
        ResourceSetupHelpers.SetupResourceDetails(this);

        var resource1 = ModelTestHelpers.CreateResource(
            "app1",
            environment: new List<EnvironmentVariableViewModel>
            {
                new EnvironmentVariableViewModel("envvar1", "value!", fromSpec: true),
                new EnvironmentVariableViewModel("envvar2", "value!", fromSpec: true)
            }.ToImmutableArray());

        // Act
        var cut = RenderComponent<ResourceDetails>(builder =>
        {
            builder.Add(p => p.ShowSpecOnlyToggle, true);
            builder.Add(p => p.Resource, resource1);
            builder.Add(p => p.ResourceByName, new ConcurrentDictionary<string, ResourceViewModel>([new KeyValuePair<string, ResourceViewModel> (resource1.Name, resource1)]));
        });

        // Assert
        Assert.Collection(cut.Instance.FilteredEnvironmentVariables,
            e =>
            {
                Assert.Equal("envvar1", e.Name);
                Assert.Equal("value!", e.Value);
                Assert.True(e.IsValueMasked);
            },
            e =>
            {
                Assert.Equal("envvar2", e.Name);
                Assert.Equal("value!", e.Value);
                Assert.True(e.IsValueMasked);
            });

        var maskValueButton = cut.Find(".env-var-properties .grid-value-mask-button");
        await maskValueButton.ClickAsync(new MouseEventArgs());

        Assert.Collection(cut.Instance.FilteredEnvironmentVariables,
            e =>
            {
                Assert.Equal("envvar1", e.Name);
                Assert.False(e.IsValueMasked);
            },
            e =>
            {
                Assert.Equal("envvar2", e.Name);
                Assert.True(e.IsValueMasked);
            });

        var resource2 = ModelTestHelpers.CreateResource(
            "app1",
            environment: new List<EnvironmentVariableViewModel>
            {
                new EnvironmentVariableViewModel("envvar1", "value!", fromSpec: true),
                new EnvironmentVariableViewModel("envvar2", "value!", fromSpec: true),
                new EnvironmentVariableViewModel("envvar3", "value!", fromSpec: true)
            }.ToImmutableArray());

        cut.SetParametersAndRender(builder =>
        {
            builder.Add(p => p.Resource, resource2);
        });

        Assert.Collection(cut.Instance.FilteredEnvironmentVariables,
            e =>
            {
                Assert.Equal("envvar1", e.Name);
                Assert.False(e.IsValueMasked);
            },
            e =>
            {
                Assert.Equal("envvar2", e.Name);
                Assert.True(e.IsValueMasked);
            },
            e =>
            {
                Assert.Equal("envvar3", e.Name);
                Assert.True(e.IsValueMasked);
            });
    }

    [Fact]
    public async Task ClickMaskEnvVarSwitch_NewResource_MaskChanged()
    {
        // Arrange
        ResourceSetupHelpers.SetupResourceDetails(this);

        var resource1 = ModelTestHelpers.CreateResource(
            "app1",
            environment: new List<EnvironmentVariableViewModel>
            {
                new EnvironmentVariableViewModel("envvar1", "value!", fromSpec: true),
                new EnvironmentVariableViewModel("envvar2", "value!", fromSpec: true)
            }.ToImmutableArray());

        // Act
        var cut = RenderComponent<ResourceDetails>(builder =>
        {
            builder.Add(p => p.ShowSpecOnlyToggle, true);
            builder.Add(p => p.Resource, resource1);
            builder.Add(p => p.ResourceByName, new ConcurrentDictionary<string, ResourceViewModel>([new KeyValuePair<string, ResourceViewModel> (resource1.Name, resource1)]));
        });

        // Assert
        Assert.Collection(cut.Instance.FilteredEnvironmentVariables,
            e =>
            {
                Assert.Equal("envvar1", e.Name);
                Assert.Equal("value!", e.Value);
                Assert.True(e.IsValueMasked);
            },
            e =>
            {
                Assert.Equal("envvar2", e.Name);
                Assert.Equal("value!", e.Value);
                Assert.True(e.IsValueMasked);
            });

        var maskValueButton = cut.Find(".env-var-properties .grid-value-mask-button");
        await maskValueButton.ClickAsync(new MouseEventArgs());

        Assert.Collection(cut.Instance.FilteredEnvironmentVariables,
            e =>
            {
                Assert.Equal("envvar1", e.Name);
                Assert.False(e.IsValueMasked);
            },
            e =>
            {
                Assert.Equal("envvar2", e.Name);
                Assert.True(e.IsValueMasked);
            });

        var resource2 = ModelTestHelpers.CreateResource(
            "app2",
            environment: new List<EnvironmentVariableViewModel>
            {
                new EnvironmentVariableViewModel("envvar1", "value!", fromSpec: true),
                new EnvironmentVariableViewModel("envvar2", "value!", fromSpec: true),
                new EnvironmentVariableViewModel("envvar3", "value!", fromSpec: true)
            }.ToImmutableArray());

        cut.SetParametersAndRender(builder =>
        {
            builder.Add(p => p.Resource, resource2);
        });

        Assert.Collection(cut.Instance.FilteredEnvironmentVariables,
            e =>
            {
                Assert.Equal("envvar1", e.Name);
                Assert.True(e.IsValueMasked);
            },
            e =>
            {
                Assert.Equal("envvar2", e.Name);
                Assert.True(e.IsValueMasked);
            },
            e =>
            {
                Assert.Equal("envvar3", e.Name);
                Assert.True(e.IsValueMasked);
            });
    }

    [Fact]
    public void Render_StateDescription_ShowsAsResourceDetailEntry()
    {
        // Arrange
        ResourceSetupHelpers.SetupResourceDetails(this);

        var resource = ModelTestHelpers.CreateResource(
            resourceName: "app1",
            state: KnownResourceState.Waiting);

        // Act
        var cut = RenderComponent<ResourceDetails>(builder =>
        {
            builder.Add(p => p.Resource, resource);
            builder.Add(p => p.ResourceByName, new ConcurrentDictionary<string, ResourceViewModel>([new KeyValuePair<string, ResourceViewModel>(resource.Name, resource)]));
        });

        // Assert
        var resourcePropertyGrid = cut.FindAll(".property-grid")[0];
        Assert.Contains(ControlsStrings.ResourceDetailsStateDescriptionHeader, resourcePropertyGrid.TextContent);
        Assert.Contains(Columns.StateColumnResourceWaiting, resourcePropertyGrid.TextContent);
    }

    [Fact]
    public void FilteredEnvironmentVariables_SortedByName()
    {
        // Arrange
        ResourceSetupHelpers.SetupResourceDetails(this);

        var resource = ModelTestHelpers.CreateResource(
            "app1",
            environment: new List<EnvironmentVariableViewModel>
            {
                new EnvironmentVariableViewModel("ZEBRA", "value1", fromSpec: true),
                new EnvironmentVariableViewModel("alpha", "value2", fromSpec: true),
                new EnvironmentVariableViewModel("Beta", "value3", fromSpec: true),
                new EnvironmentVariableViewModel("GAMMA", "value4", fromSpec: true),
                new EnvironmentVariableViewModel("delta", "value5", fromSpec: true)
            }.ToImmutableArray());

        // Act
        var cut = RenderComponent<ResourceDetails>(builder =>
        {
            builder.Add(p => p.ShowSpecOnlyToggle, true);
            builder.Add(p => p.Resource, resource);
            builder.Add(p => p.ResourceByName, new ConcurrentDictionary<string, ResourceViewModel>([new KeyValuePair<string, ResourceViewModel>(resource.Name, resource)]));
        });

        // Assert - verify environment variables are sorted alphabetically (case-insensitive)
        var envVars = cut.Instance.FilteredEnvironmentVariables.ToList();
        Assert.Equal(5, envVars.Count);
        Assert.Collection(envVars,
            e => Assert.Equal("alpha", e.Name),
            e => Assert.Equal("Beta", e.Name),
            e => Assert.Equal("delta", e.Name),
            e => Assert.Equal("GAMMA", e.Name),
            e => Assert.Equal("ZEBRA", e.Name));
    }

    [Fact]
    public void FilteredVolumes_SortedBySource()
    {
        // Arrange
        ResourceSetupHelpers.SetupResourceDetails(this);

        var resource = ModelTestHelpers.CreateResource(
            "app1",
            volumes: new List<VolumeViewModel>
            {
                new VolumeViewModel(0, "zebra-volume", "/target1", "bind", false),
                new VolumeViewModel(1, "Alpha-Volume", "/target2", "volume", false),
                new VolumeViewModel(2, "beta-volume", "/target3", "bind", true),
                new VolumeViewModel(3, "GAMMA-Volume", "/target4", "volume", false),
                new VolumeViewModel(4, "delta-volume", "/target5", "bind", false)
            }.ToImmutableArray());

        // Act
        var cut = RenderComponent<ResourceDetails>(builder =>
        {
            builder.Add(p => p.ShowSpecOnlyToggle, true);
            builder.Add(p => p.Resource, resource);
            builder.Add(p => p.ResourceByName, new ConcurrentDictionary<string, ResourceViewModel>([new KeyValuePair<string, ResourceViewModel>(resource.Name, resource)]));
        });

        // Assert - verify volumes are sorted alphabetically by source (case-insensitive)
        var volumes = cut.Instance.FilteredVolumes.ToList();
        Assert.Equal(5, volumes.Count);
        Assert.Collection(volumes,
            v => Assert.Equal("Alpha-Volume", v.Source),
            v => Assert.Equal("beta-volume", v.Source),
            v => Assert.Equal("delta-volume", v.Source),
            v => Assert.Equal("GAMMA-Volume", v.Source),
            v => Assert.Equal("zebra-volume", v.Source));
    }

    [Fact]
    public void FilteredHealthReports_SortedByName()
    {
        // Arrange
        ResourceSetupHelpers.SetupResourceDetails(this);

        var resource = ModelTestHelpers.CreateResource(
            "app1",
            healthReports: new List<HealthReportViewModel>
            {
                new HealthReportViewModel("zebra-check", HealthStatus.Healthy, "OK", null),
                new HealthReportViewModel("Alpha-Check", HealthStatus.Healthy, "OK", null),
                new HealthReportViewModel("beta-check", HealthStatus.Degraded, "Warning", null),
                new HealthReportViewModel("GAMMA-Check", HealthStatus.Healthy, "OK", null),
                new HealthReportViewModel("delta-check", HealthStatus.Unhealthy, "Failed", null)
            }.ToImmutableArray());

        // Act
        var cut = RenderComponent<ResourceDetails>(builder =>
        {
            builder.Add(p => p.ShowSpecOnlyToggle, true);
            builder.Add(p => p.Resource, resource);
            builder.Add(p => p.ResourceByName, new ConcurrentDictionary<string, ResourceViewModel>([new KeyValuePair<string, ResourceViewModel>(resource.Name, resource)]));
        });

        // Assert - verify health reports are sorted alphabetically by name (case-insensitive)
        var healthReports = cut.Instance.FilteredHealthReports.ToList();
        Assert.Equal(5, healthReports.Count);
        Assert.Collection(healthReports,
            h => Assert.Equal("Alpha-Check", h.Name),
            h => Assert.Equal("beta-check", h.Name),
            h => Assert.Equal("delta-check", h.Name),
            h => Assert.Equal("GAMMA-Check", h.Name),
            h => Assert.Equal("zebra-check", h.Name));
    }

    [Fact]
    public async Task UnsetParameterResource_RendersValueNotSetButton_AndInvokesSetCommand()
    {
        ResourceSetupHelpers.SetupResourceDetails(this);

        var setCommand = new CommandViewModel(
            CommandViewModel.SetParameterCommand,
            CommandViewModelState.Enabled,
            displayName: "Set value",
            displayDescription: "Set the parameter value",
            confirmationMessage: string.Empty,
            argumentInputs: [],
            isHighlighted: false,
            iconName: string.Empty,
            iconVariant: IconVariant.Regular);

        var properties = new Dictionary<string, ResourcePropertyViewModel>
        {
            [KnownProperties.Parameter.Value] = new ResourcePropertyViewModel(
                KnownProperties.Parameter.Value,
                Value.ForString("Parameter 'p' not found in configuration."),
                isValueSensitive: false,
                knownProperty: new KnownProperty(KnownProperties.Parameter.Value, _ => "Value"),
                priority: 0)
        };

        var resource = CreateParameterResource(
            resourceName: "myparameter",
            state: nameof(KnownResourceState.ValueMissing),
            stateStyle: "warning",
            properties: properties,
            commands: ImmutableArray.Create(setCommand));

        CommandViewModel? capturedCommand = null;
        var cut = RenderComponent<ResourceDetails>(builder =>
        {
            builder.Add(p => p.Resource, resource);
            builder.Add(p => p.ResourceByName, new ConcurrentDictionary<string, ResourceViewModel>([new KeyValuePair<string, ResourceViewModel>(resource.Name, resource)]));
            builder.Add(p => p.CommandSelected, EventCallback.Factory.Create<CommandViewModel>(this, c => capturedCommand = c));
            builder.Add(p => p.IsCommandExecuting, (_, _) => false);
        });

        var button = cut.Find("button.value-not-set-link");
        Assert.Equal("Value not set", button.TextContent.Trim());

        await button.ClickAsync(new MouseEventArgs());

        Assert.NotNull(capturedCommand);
        Assert.Equal(CommandViewModel.SetParameterCommand, capturedCommand!.Name);
    }

    [Fact]
    public async Task UnsetParameterResource_WhenSetCommandIsExecuting_DisablesValueNotSetButton()
    {
        ResourceSetupHelpers.SetupResourceDetails(this);

        var setCommand = new CommandViewModel(
            CommandViewModel.SetParameterCommand,
            CommandViewModelState.Enabled,
            displayName: "Set value",
            displayDescription: "Set the parameter value",
            confirmationMessage: string.Empty,
            argumentInputs: [],
            isHighlighted: false,
            iconName: string.Empty,
            iconVariant: IconVariant.Regular);

        var properties = new Dictionary<string, ResourcePropertyViewModel>
        {
            [KnownProperties.Parameter.Value] = new ResourcePropertyViewModel(
                KnownProperties.Parameter.Value,
                Value.ForString("Parameter 'p' not found in configuration."),
                isValueSensitive: false,
                knownProperty: new KnownProperty(KnownProperties.Parameter.Value, _ => "Value"),
                priority: 0)
        };

        var resource = CreateParameterResource(
            resourceName: "myparameter",
            state: nameof(KnownResourceState.ValueMissing),
            stateStyle: "warning",
            properties: properties,
            commands: ImmutableArray.Create(setCommand));

        CommandViewModel? capturedCommand = null;
        var cut = RenderComponent<ResourceDetails>(builder =>
        {
            builder.Add(p => p.Resource, resource);
            builder.Add(p => p.ResourceByName, new ConcurrentDictionary<string, ResourceViewModel>([new KeyValuePair<string, ResourceViewModel>(resource.Name, resource)]));
            builder.Add(p => p.CommandSelected, EventCallback.Factory.Create<CommandViewModel>(this, c => capturedCommand = c));
            builder.Add(p => p.IsCommandExecuting, (currentResource, command) =>
                currentResource.Name == resource.Name && command.Name == CommandViewModel.SetParameterCommand);
        });

        var button = cut.Find("button.value-not-set-link");
        Assert.True(button.HasAttribute("disabled"));

        await button.ClickAsync(new MouseEventArgs());

        Assert.Null(capturedCommand);
    }

    [Fact]
    public void UnsetSecretParameterResource_RendersValueNotSetButton()
    {
        ResourceSetupHelpers.SetupResourceDetails(this);

        var setCommand = new CommandViewModel(
            CommandViewModel.SetParameterCommand,
            CommandViewModelState.Enabled,
            displayName: "Set value",
            displayDescription: "Set the parameter value",
            confirmationMessage: string.Empty,
            argumentInputs: [],
            isHighlighted: false,
            iconName: string.Empty,
            iconVariant: IconVariant.Regular);

        var properties = new Dictionary<string, ResourcePropertyViewModel>
        {
            [KnownProperties.Parameter.Value] = new ResourcePropertyViewModel(
                KnownProperties.Parameter.Value,
                Value.ForString("Parameter 'p' not found in configuration."),
                isValueSensitive: true,
                knownProperty: new KnownProperty(KnownProperties.Parameter.Value, _ => "Value"),
                priority: 0)
        };

        var resource = CreateParameterResource(
            resourceName: "mysecretparameter",
            state: nameof(KnownResourceState.ValueMissing),
            stateStyle: "warning",
            properties: properties,
            commands: ImmutableArray.Create(setCommand));

        var cut = RenderComponent<ResourceDetails>(builder =>
        {
            builder.Add(p => p.Resource, resource);
            builder.Add(p => p.ResourceByName, new ConcurrentDictionary<string, ResourceViewModel>([new KeyValuePair<string, ResourceViewModel>(resource.Name, resource)]));
            builder.Add(p => p.CommandSelected, EventCallback.Factory.Create<CommandViewModel>(this, _ => Task.CompletedTask));
            builder.Add(p => p.IsCommandExecuting, (_, _) => false);
        });

        Assert.NotNull(cut.Find("button.value-not-set-link"));
        Assert.Empty(cut.FindAll(".grid-value-mask-button"));
    }

    [Fact]
    public void ErrorParameterResource_DisplaysErrorMessage_InsteadOfValueNotSetButton()
    {
        ResourceSetupHelpers.SetupResourceDetails(this);

        const string errorMessage = "Failed to initialize parameter from external provider.";
        var properties = new Dictionary<string, ResourcePropertyViewModel>
        {
            [KnownProperties.Parameter.Value] = new ResourcePropertyViewModel(
                KnownProperties.Parameter.Value,
                Value.ForString(errorMessage),
                isValueSensitive: false,
                knownProperty: new KnownProperty(KnownProperties.Parameter.Value, _ => "Value"),
                priority: 0)
        };

        var resource = ModelTestHelpers.CreateResource(
            resourceName: "myparameter",
            resourceType: KnownResourceTypes.Parameter,
            stateStyle: "error",
            properties: properties);

        var cut = RenderComponent<ResourceDetails>(builder =>
        {
            builder.Add(p => p.Resource, resource);
            builder.Add(p => p.ResourceByName, new ConcurrentDictionary<string, ResourceViewModel>([new KeyValuePair<string, ResourceViewModel>(resource.Name, resource)]));
            builder.Add(p => p.IsCommandExecuting, (_, _) => false);
        });

        Assert.Empty(cut.FindAll("button.value-not-set-link"));
        Assert.Contains(errorMessage, cut.FindAll(".grid-value").Select(e => e.TextContent.Trim()));
    }

    [Fact]
    public void RunningParameterResource_DoesNotRenderValueNotSetButton()
    {
        ResourceSetupHelpers.SetupResourceDetails(this);

        var properties = new Dictionary<string, ResourcePropertyViewModel>
        {
            [KnownProperties.Parameter.Value] = new ResourcePropertyViewModel(
                KnownProperties.Parameter.Value,
                Value.ForString("resolved-value"),
                isValueSensitive: false,
                knownProperty: new KnownProperty(KnownProperties.Parameter.Value, _ => "Value"),
                priority: 0)
        };

        var resource = ModelTestHelpers.CreateResource(
            resourceName: "myparameter",
            resourceType: KnownResourceTypes.Parameter,
            state: KnownResourceState.Running,
            properties: properties);

        var cut = RenderComponent<ResourceDetails>(builder =>
        {
            builder.Add(p => p.Resource, resource);
            builder.Add(p => p.ResourceByName, new ConcurrentDictionary<string, ResourceViewModel>([new KeyValuePair<string, ResourceViewModel>(resource.Name, resource)]));
            builder.Add(p => p.IsCommandExecuting, (_, _) => false);
        });

        Assert.Empty(cut.FindAll("button.value-not-set-link"));
    }

    private static ResourceViewModel CreateParameterResource(
        string resourceName,
        string? state,
        string? stateStyle,
        Dictionary<string, ResourcePropertyViewModel> properties,
        ImmutableArray<CommandViewModel>? commands = null)
    {
        return new ResourceViewModel
        {
            Name = resourceName,
            ResourceType = KnownResourceTypes.Parameter,
            DisplayName = resourceName,
            Uid = resourceName,
            ReplicaIndex = 0,
            State = state,
            KnownState = state is not null && System.Enum.TryParse<KnownResourceState>(state, out var knownState) ? knownState : null,
            StateStyle = stateStyle,
            CreationTimeStamp = DateTime.UtcNow,
            StartTimeStamp = DateTime.UtcNow,
            StopTimeStamp = DateTime.UtcNow,
            Environment = [],
            Urls = [],
            Volumes = [],
            Relationships = [],
            Properties = properties.ToImmutableDictionary(),
            Commands = commands ?? [],
            HealthReports = [],
        };
    }
}
