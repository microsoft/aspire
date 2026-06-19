// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using Aspire.Dashboard.Model;
using Aspire.DashboardService.Proto.V1;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using DashboardResources = Aspire.Dashboard.Resources.Resources;
using DiagnosticsHealthStatus = Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus;

namespace Aspire.Dashboard.Tests.Model;

public sealed class ResourceViewModelTests
{
    private static readonly DateTime s_dateTime = new(2000, 12, 30, 23, 59, 59, DateTimeKind.Utc);

    [Theory]
    [InlineData(KnownResourceState.Starting, null, null)]
    [InlineData(KnownResourceState.Starting, null, new string[]{})]
    [InlineData(KnownResourceState.Starting, null, new string?[]{null})]
    // we don't have a Running + HealthReports null case because that's not a valid state - by this point, we will have received the list of HealthReports
    [InlineData(KnownResourceState.Running, DiagnosticsHealthStatus.Healthy, new string[]{})]
    [InlineData(KnownResourceState.Running, DiagnosticsHealthStatus.Healthy, new string?[] {"Healthy"})]
    [InlineData(KnownResourceState.Running, DiagnosticsHealthStatus.Unhealthy, new string?[] {null})]
    [InlineData(KnownResourceState.Running, DiagnosticsHealthStatus.Degraded, new string?[] {"Healthy", "Degraded"})]
    public void Resource_WithHealthReportAndState_ReturnsCorrectHealthStatus(KnownResourceState? state, DiagnosticsHealthStatus? expectedStatus, string?[]? healthStatusStrings)
    {
        var reports = healthStatusStrings?.Select<string?, HealthReportViewModel>((h, i) => new HealthReportViewModel(i.ToString(), h is null ? null : System.Enum.Parse<DiagnosticsHealthStatus>(h), null, null)).ToImmutableArray() ?? [];
        var actualStatus = ResourceViewModel.ComputeHealthStatus(reports, state);
        Assert.Equal(expectedStatus, actualStatus);
    }

    [Fact]
    public void ToViewModel_EmptyEnvVarName_Success()
    {
        // Arrange
        var resource = new Resource
        {
            Name = "TestName-abc",
            DisplayName = "TestName",
            CreatedAt = Timestamp.FromDateTime(s_dateTime),
            Environment =
            {
                new EnvironmentVariable { Name = string.Empty, Value = "Value!" }
            }
        };

        // Act
        var vm = ToViewModel(resource);

        // Assert
        Assert.Collection(vm.Environment,
            e =>
            {
                Assert.Empty(e.Name);
                Assert.Equal("Value!", e.Value);
            });
    }

    [Fact]
    public void ToViewModel_DuplicatePropertyNames_Success()
    {
        // Arrange
        var resource = new Resource
        {
            Name = "TestName-abc",
            DisplayName = "TestName",
            CreatedAt = Timestamp.FromDateTime(s_dateTime),
            Properties =
            {
                new ResourceProperty { Name = "test", Value = Value.ForString("one!") },
                new ResourceProperty { Name = "test", Value = Value.ForString("two!") }
            }
        };

        // Act
        var vm = ToViewModel(resource);

        // Assert
        Assert.Collection(vm.Properties,
            e =>
            {
                var (key, vm) = (e.Key, e.Value);

                Assert.Equal("test", key);
                Assert.Equal("test", vm.Name);
                Assert.Equal("two!", vm.Value.StringValue);
            });
    }

    [Fact]
    public void ToViewModel_MissingRequiredData_FailWithFriendlyError()
    {
        // Arrange
        var resource = new Resource
        {
            Name = "TestName-abc"
        };

        // Act
        var ex = Assert.Throws<InvalidOperationException>(() => ToViewModel(resource));

        // Assert
        Assert.Equal(@"Error converting resource ""TestName-abc"" to ResourceViewModel.", ex.Message);
        Assert.NotNull(ex.InnerException);
    }

    [Fact]
    public void ToViewModel_CopiesProperties()
    {
        // Arrange
        var resource = new Resource
        {
            Name = "TestName-abc",
            DisplayName = "TestName",
            CreatedAt = Timestamp.FromDateTime(s_dateTime),
            Properties =
            {
                new ResourceProperty { Name = "Property1", Value = Value.ForString("Value1"), IsSensitive = false, DisplayName = "Property one", IsHighlighted = true },
                new ResourceProperty { Name = "Property2", Value = Value.ForString("Value2"), IsSensitive = true }
            }
        };

        var kp = new KnownProperty("foo", loc => "bar");

        // Act
        var vm = ToViewModel(resource, knownPropertyLookup: new MockKnownPropertyLookup(123, kp));

        // Assert
        Assert.Collection(
            vm.Properties.OrderBy(p => p.Key),
            p =>
            {
                Assert.Equal("Property1", p.Key);
                Assert.Equal("Property1", p.Value.Name);
                Assert.Equal("Value1", p.Value.Value.StringValue);
                Assert.Equal(123, p.Value.SortOrder);
                Assert.Same(kp, p.Value.KnownProperty);
                Assert.Equal("Property one", p.Value.DisplayName);
                Assert.True(p.Value.IsHighlighted);
                Assert.False(p.Value.IsValueMasked);
                Assert.False(p.Value.IsValueSensitive);
            },
            p =>
            {
                Assert.Equal("Property2", p.Key);
                Assert.Equal("Property2", p.Value.Name);
                Assert.Equal("Value2", p.Value.Value.StringValue);
                Assert.Equal(123, p.Value.SortOrder);
                Assert.Same(kp, p.Value.KnownProperty);
                Assert.Null(p.Value.DisplayName);
                Assert.False(p.Value.IsHighlighted);
                Assert.True(p.Value.IsValueMasked);
                Assert.True(p.Value.IsValueSensitive);
            });
    }

    [Fact]
    public void ToViewModel_ProducerSuppliedPropertyMetadata_DoesNotRequireKnownProperty()
    {
        // Arrange
        var resource = new Resource
        {
            Name = "container-abc",
            DisplayName = "container",
            ResourceType = KnownResourceTypes.Container,
            CreatedAt = Timestamp.FromDateTime(s_dateTime),
            Properties =
            {
                new ResourceProperty
                {
                    Name = KnownProperties.Container.Image,
                    Value = Value.ForString("redis:latest"),
                    DisplayName = "Container image",
                    IsHighlighted = true,
                    SortOrder = 0
                }
            }
        };

        // Act
        var vm = ToViewModel(resource, new KnownPropertyLookup());

        // Assert
        var property = vm.Properties[KnownProperties.Container.Image];
        Assert.Equal("Container image", property.DisplayName);
        Assert.True(property.IsHighlighted);
        Assert.Equal(ProducerDefinedDisplaySortOrder(0), property.SortOrder);
        Assert.Null(property.KnownProperty);
    }

    [Theory]
    [InlineData(KnownResourceTypes.Container, KnownProperties.Container.Image, nameof(DashboardResources.ResourcesDetailsContainerImageProperty), 0)]
    [InlineData(KnownResourceTypes.Container, KnownProperties.Container.Id, nameof(DashboardResources.ResourcesDetailsContainerIdProperty), 1)]
    [InlineData(KnownResourceTypes.Container, KnownProperties.Container.Command, nameof(DashboardResources.ResourcesDetailsContainerCommandProperty), 2)]
    [InlineData(KnownResourceTypes.Container, KnownProperties.Container.Args, nameof(DashboardResources.ResourcesDetailsContainerArgumentsProperty), 3)]
    [InlineData(KnownResourceTypes.Container, KnownProperties.Container.Ports, nameof(DashboardResources.ResourcesDetailsContainerPortsProperty), 4)]
    [InlineData(KnownResourceTypes.Container, KnownProperties.Container.Lifetime, nameof(DashboardResources.ResourcesDetailsContainerLifetimeProperty), 5)]
    [InlineData(KnownResourceTypes.Executable, KnownProperties.Executable.Path, nameof(DashboardResources.ResourcesDetailsExecutablePathProperty), 0)]
    [InlineData(KnownResourceTypes.Executable, KnownProperties.Executable.WorkDir, nameof(DashboardResources.ResourcesDetailsExecutableWorkingDirectoryProperty), 1)]
    [InlineData(KnownResourceTypes.Executable, KnownProperties.Executable.Args, nameof(DashboardResources.ResourcesDetailsExecutableArgumentsProperty), 2)]
    [InlineData(KnownResourceTypes.Executable, KnownProperties.Executable.Pid, nameof(DashboardResources.ResourcesDetailsExecutableProcessIdProperty), 3)]
    [InlineData(KnownResourceTypes.Project, KnownProperties.Project.Path, nameof(DashboardResources.ResourcesDetailsProjectPathProperty), 0)]
    [InlineData(KnownResourceTypes.Project, KnownProperties.Project.LaunchProfile, nameof(DashboardResources.ResourcesDetailsProjectLaunchProfileProperty), 1)]
    [InlineData(KnownResourceTypes.Project, KnownProperties.Executable.Pid, nameof(DashboardResources.ResourcesDetailsExecutableProcessIdProperty), 2)]
    [InlineData(KnownResourceTypes.Parameter, KnownProperties.Parameter.Value, nameof(DashboardResources.ResourcesDetailsParameterValueProperty), 0)]
    public void ToViewModel_LegacyBuiltInResourceSpecificPropertyMetadata_AppliesFallback(string resourceType, string propertyName, string expectedDisplayNameResourceName, int expectedProducerSortOrder)
    {
        var resource = new Resource
        {
            Name = "resource-abc",
            DisplayName = "resource",
            ResourceType = resourceType,
            CreatedAt = Timestamp.FromDateTime(s_dateTime),
            Properties =
            {
                new ResourceProperty
                {
                    Name = propertyName,
                    Value = Value.ForString("value")
                }
            }
        };

        var vm = ToViewModel(resource, new KnownPropertyLookup());

        var property = vm.Properties[propertyName];
        Assert.Null(property.DisplayName);
        Assert.False(property.IsHighlighted);
        Assert.Equal(ProducerDefinedDisplaySortOrder(expectedProducerSortOrder), property.SortOrder);
        Assert.NotNull(property.KnownProperty);
        Assert.Equal(propertyName, property.KnownProperty.Key);

        var displayedProperty = new DisplayedResourcePropertyViewModel(
            property,
            new TestStringLocalizer<DashboardResources>(),
            new BrowserTimeProvider(NullLoggerFactory.Instance));
        Assert.Equal($"Localized:{expectedDisplayNameResourceName}", displayedProperty.DisplayName);
    }

    [Fact]
    public void ToViewModel_LegacyBuiltInPropertyNameOnCustomResource_DoesNotApplyFallback()
    {
        var resource = new Resource
        {
            Name = "resource-abc",
            DisplayName = "resource",
            ResourceType = "custom",
            CreatedAt = Timestamp.FromDateTime(s_dateTime),
            Properties =
            {
                new ResourceProperty
                {
                    Name = KnownProperties.Container.Image,
                    Value = Value.ForString("redis:latest")
                }
            }
        };

        var vm = ToViewModel(resource, new KnownPropertyLookup());

        var property = vm.Properties[KnownProperties.Container.Image];
        Assert.Null(property.DisplayName);
        Assert.False(property.IsHighlighted);
        Assert.Equal(int.MaxValue, property.SortOrder);
        Assert.Null(property.KnownProperty);
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void ToViewModel_ProducerMetadataPresent_DisablesLegacyFallbackForResource(bool hasDisplayName, bool isHighlighted, bool hasSortOrder)
    {
        var imageProperty = new ResourceProperty
        {
            Name = KnownProperties.Container.Image,
            Value = Value.ForString("redis:latest")
        };
        if (hasDisplayName)
        {
            imageProperty.DisplayName = "Producer container image";
        }
        if (isHighlighted)
        {
            imageProperty.IsHighlighted = true;
        }
        if (hasSortOrder)
        {
            imageProperty.SortOrder = 0;
        }

        var resource = new Resource
        {
            Name = "container-abc",
            DisplayName = "container",
            ResourceType = KnownResourceTypes.Container,
            CreatedAt = Timestamp.FromDateTime(s_dateTime),
            Properties =
            {
                imageProperty,
                new ResourceProperty
                {
                    Name = KnownProperties.Container.Id,
                    Value = Value.ForString("abc123")
                }
            }
        };

        var vm = ToViewModel(resource, new KnownPropertyLookup());

        var image = vm.Properties[KnownProperties.Container.Image];
        Assert.Equal(hasDisplayName ? "Producer container image" : null, image.DisplayName);
        Assert.Equal(isHighlighted, image.IsHighlighted);
        Assert.Equal(hasSortOrder ? ProducerDefinedDisplaySortOrder(0) : int.MaxValue, image.SortOrder);
        Assert.Null(image.KnownProperty);

        var id = vm.Properties[KnownProperties.Container.Id];
        Assert.Null(id.DisplayName);
        Assert.False(id.IsHighlighted);
        Assert.Equal(int.MaxValue, id.SortOrder);
        Assert.Null(id.KnownProperty);
    }

    private static int ProducerDefinedDisplaySortOrder(int producerSortOrder)
    {
        return KnownResourcePropertySortOrder.ConnectionString + 1 + producerSortOrder;
    }

    [Fact]
    public void ToViewModel_WithCustomIcon_SetsIconProperties()
    {
        // Arrange
        var resource = new Resource
        {
            Name = "TestResource",
            DisplayName = "Test Resource",
            ResourceType = "container",
            CreatedAt = Timestamp.FromDateTime(s_dateTime),
            IconName = "Database",
            IconVariant = IconVariant.Filled
        };

        // Act
        var vm = ToViewModel(resource);

        // Assert
        Assert.Equal("Database", vm.IconName);
        Assert.Equal(Microsoft.FluentUI.AspNetCore.Components.IconVariant.Filled, vm.IconVariant);
    }

    [Fact]
    public void ToViewModel_WithCustomIconRegularVariant_SetsIconProperties()
    {
        // Arrange
        var resource = new Resource
        {
            Name = "TestResource",
            DisplayName = "Test Resource", 
            ResourceType = "container",
            CreatedAt = Timestamp.FromDateTime(s_dateTime),
            IconName = "CloudArrowUp",
            IconVariant = IconVariant.Regular
        };

        // Act
        var vm = ToViewModel(resource);

        // Assert
        Assert.Equal("CloudArrowUp", vm.IconName);
        Assert.Equal(Microsoft.FluentUI.AspNetCore.Components.IconVariant.Regular, vm.IconVariant);
    }

    [Fact]
    public void ToViewModel_WithoutCustomIcon_IconPropertiesAreNull()
    {
        // Arrange
        var resource = new Resource
        {
            Name = "TestResource",
            DisplayName = "Test Resource",
            ResourceType = "container", 
            CreatedAt = Timestamp.FromDateTime(s_dateTime)
            // No IconName or IconVariant set
        };

        // Act
        var vm = ToViewModel(resource);

        // Assert
        Assert.Null(vm.IconName);
        Assert.Null(vm.IconVariant);
    }

    private static ResourceViewModel ToViewModel(Resource resource, IKnownPropertyLookup? knownPropertyLookup = null)
    {
        return resource.ToViewModel(replicaIndex: 0, knownPropertyLookup ?? new MockKnownPropertyLookup(), NullLogger.Instance);
    }
}
