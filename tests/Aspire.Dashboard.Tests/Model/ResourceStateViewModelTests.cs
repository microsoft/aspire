// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;
using Aspire.Dashboard.Resources;
using Aspire.Tests.Shared.DashboardModel;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.FluentUI.AspNetCore.Components;
using Xunit;
using Enum = System.Enum;

namespace Aspire.Dashboard.Tests.Model;

public class ResourceStateViewModelTests
{
    [Theory]
    // Resource is no longer running
    [InlineData(
        /* state */ "Container", KnownResourceState.Exited, null, null,null,
        /* expected output */ $"Localized:{nameof(Columns.StateColumnResourceExited)}:Container", "RecordStop", Color.Info, "Exited")]
    [InlineData(
        /* state */ "Container", KnownResourceState.Exited, 3, null, null,
        /* expected output */ $"Localized:{nameof(Columns.StateColumnResourceExitedUnexpectedly)}:Container+3", "ErrorCircle", Color.Error, "Exited")]
    [InlineData(
        /* state */ "Container", KnownResourceState.Exited, 0, null, null,
        /* expected output */ $"Localized:{nameof(Columns.StateColumnResourceExited)}:Container", "RecordStop", Color.Info, "Exited")]
    [InlineData(
        /* state */ "Container", KnownResourceState.Finished, 0, null, null,
        /* expected output */ $"Localized:{nameof(Columns.StateColumnResourceExited)}:Container", "RecordStop", Color.Info, "Finished")]
    [InlineData(
        /* state */ "CustomResource", KnownResourceState.Finished, null, null, null,
        /* expected output */ $"Localized:{nameof(Columns.StateColumnResourceExited)}:CustomResource", "RecordStop", Color.Info, "Finished")]
    // Resource failed to start - should use dedicated message, not "no longer running"
    [InlineData(
        /* state */ "Container", KnownResourceState.FailedToStart, null, null, null,
        /* expected output */ $"Localized:{nameof(Columns.StateColumnResourceFailedToStart)}:Container", "Warning", Color.Warning, "Failed to start")]
    [InlineData(
        /* state */ "CustomResource", KnownResourceState.FailedToStart, null, null, null,
        /* expected output */ $"Localized:{nameof(Columns.StateColumnResourceFailedToStart)}:CustomResource", "Warning", Color.Warning, "Failed to start")]
    [InlineData(
        /* state */ "Container", KnownResourceState.Unknown, null, null, null,
        /* expected output */ "Unknown", "CircleHint", Color.Info, "Unknown")]
    // Health checks
    [InlineData(
        /* state */ "Container", KnownResourceState.Running, null, "Healthy", null,
        /* expected output */ "Running", "CheckmarkCircle", Color.Success, "Running")]
    [InlineData(
        /* state */ "Container", KnownResourceState.Running, null, "", null,
        /* expected output */ $"Localized:{nameof(Columns.RunningAndUnhealthyResourceStateToolTip)}", "CheckmarkCircleWarning", Color.Warning, "Running (Unhealthy)")]
    [InlineData(
        /* state */ "Container", KnownResourceState.Running, null, "Unhealthy", null,
        /* expected output */ $"Localized:{nameof(Columns.RunningAndUnhealthyResourceStateToolTip)}", "CheckmarkCircleWarning", Color.Warning, "Running (Unhealthy)")]
    [InlineData(
        /* state */ "Container", KnownResourceState.Running, null, "Healthy", "warning",
        /* expected output */ "Running", "Warning", Color.Warning, "Running")]
    [InlineData(
        /* state */ "Container", KnownResourceState.Running, null, "Healthy", "NOT_A_VALID_STATE_STYLE",
        /* expected output */ "Running", "Circle", Color.Default, "Running")]
    [InlineData(
        /* state */ "Container", KnownResourceState.Running, null, null, "info",
        /* expected output */ "Running", "Info", Color.Info, "Running")]
    [InlineData(
        /* state */ "Container", KnownResourceState.RuntimeUnhealthy, null, null, null,
        /* expected output */ $"Localized:{nameof(Columns.StateColumnResourceContainerRuntimeUnhealthy)}", "Warning", Color.Warning, "Runtime unhealthy")]
    public void ResourceViewModel_ReturnsCorrectIconAndTooltip(
        string resourceType,
        KnownResourceState state,
        int? exitCode,
        string? healthStatusString,
        string? stateStyle,
        string? expectedTooltip,
        string expectedIconName,
        Color expectedColor,
        string expectedText)
    {
        // Arrange
        HealthStatus? healthStatus = string.IsNullOrEmpty(healthStatusString) ? null : Enum.Parse<HealthStatus>(healthStatusString);
        var propertiesDictionary = new Dictionary<string, ResourcePropertyViewModel>();
        if (exitCode is not null)
        {
            propertiesDictionary.TryAdd(KnownProperties.Resource.ExitCode, new ResourcePropertyViewModel(KnownProperties.Resource.ExitCode, Value.ForNumber((double)exitCode), false, null, 0, displayName: null, isHighlighted: false));
        }

        var resource = ModelTestHelpers.CreateResource(
            state: state,
            reportHealthStatus: healthStatus,
            createNullHealthReport: healthStatusString == "",
            stateStyle: stateStyle,
            resourceType: resourceType,
            properties: propertiesDictionary);

        if (exitCode is not null)
        {
            resource.Properties.TryAdd(KnownProperties.Resource.ExitCode, new ResourcePropertyViewModel(KnownProperties.Resource.ExitCode, Value.ForNumber((double)exitCode), false, null, 0, displayName: null, isHighlighted: false));
        }

        var localizer = new TestStringLocalizer<Columns>();

        // Act
        var tooltip = ResourceStateViewModel.GetResourceStateTooltip(resource, localizer);
        var vm = ResourceStateViewModel.GetStateViewModel(resource, localizer);

        // Assert
        Assert.Equal(expectedTooltip, tooltip);

        Assert.Equal(expectedIconName, vm.Icon.Name);
        Assert.Equal(expectedColor, vm.Color);
        Assert.Equal(expectedText, vm.Text);
    }

    [Fact]
    public void WaitingResourceTooltipIncludesWaitingForDependenciesWhenPresent()
    {
        var resource = ModelTestHelpers.CreateResource(
            state: KnownResourceState.Waiting,
            properties: new Dictionary<string, ResourcePropertyViewModel>
            {
                [KnownProperties.Resource.WaitingFor] = new(
                    KnownProperties.Resource.WaitingFor,
                    Value.ForList(Value.ForString("nginx"), Value.ForString("redis")),
                    isValueSensitive: false,
                    knownProperty: null,
                    sortOrder: 0,
                    displayName: null,
                    isHighlighted: false)
            });

        var localizer = new TestStringLocalizer<Columns>();

        var tooltip = ResourceStateViewModel.GetResourceStateTooltip(resource, localizer);

        Assert.Equal($"Localized:{nameof(Columns.StateColumnResourceWaitingFor)}:nginx, redis", tooltip);
    }

    [Theory]
    [InlineData(KnownResourceState.Starting, "Starting")]
    [InlineData(KnownResourceState.FailedToStart, "Localized:StateColumnResourceFailedToStart:Container")]
    [InlineData(KnownResourceState.Waiting, "Localized:StateColumnResourceWaiting")]
    public void BlockedResourceTooltipNamesMissingReferencedParameters(KnownResourceState state, string stateTooltip)
    {
        var parameter = ModelTestHelpers.CreateResource(
            resourceName: "optional-selector",
            resourceType: KnownResourceTypes.Parameter,
            state: KnownResourceState.ValueMissing,
            properties: new Dictionary<string, ResourcePropertyViewModel>
            {
                [KnownProperties.Parameter.Value] = new(
                    KnownProperties.Parameter.Value, Value.ForString("do-not-disclose"),
                    isValueSensitive: true, knownProperty: null, sortOrder: 0,
                    displayName: null, isHighlighted: false)
            });
        var resource = ModelTestHelpers.CreateResource(
            resourceName: "api",
            state: state,
            relationships: [new(parameter.Name, KnownRelationshipTypes.Reference)]);

        var tooltip = ResourceStateViewModel.GetResourceStateTooltip(
            resource, new TestStringLocalizer<Columns>(), [resource, parameter]);

        Assert.Equal($"{stateTooltip} Localized:{nameof(Columns.StateColumnResourceMissingParameters)}:optional-selector", tooltip);
    }

    [Fact]
    public void WaitingResourceTooltipIncludesTransitiveMissingParametersWithoutDuplicatesOrCycles()
    {
        var parameter = ModelTestHelpers.CreateResource(
            resourceName: "optional-selector", resourceType: KnownResourceTypes.Parameter,
            state: KnownResourceState.ValueMissing);
        var api = ModelTestHelpers.CreateResource(
            resourceName: "api-abcxyz", displayName: "api", state: KnownResourceState.Starting,
            relationships: [new(parameter.Name, KnownRelationshipTypes.Reference), new("frontend", KnownRelationshipTypes.Reference)]);
        var frontend = ModelTestHelpers.CreateResource(
            resourceName: "frontend", state: KnownResourceState.Waiting,
            relationships: [new("api", KnownRelationshipTypes.WaitFor), new(api.Name, KnownRelationshipTypes.Reference)]);

        var tooltip = ResourceStateViewModel.GetResourceStateTooltip(
            frontend, new TestStringLocalizer<Columns>(), [frontend, api, parameter]);

        Assert.Equal(
            $"Localized:{nameof(Columns.StateColumnResourceWaiting)} Localized:{nameof(Columns.StateColumnResourceMissingParameters)}:optional-selector",
            tooltip);
    }

    [Fact]
    public void WaitingResourceTooltipFindsMissingParametersAcrossReplicas()
    {
        var firstParameter = ModelTestHelpers.CreateResource(
            resourceName: "first-selector", resourceType: KnownResourceTypes.Parameter,
            state: KnownResourceState.ValueMissing);
        var secondParameter = ModelTestHelpers.CreateResource(
            resourceName: "second-selector", resourceType: KnownResourceTypes.Parameter,
            state: KnownResourceState.ValueMissing);
        var firstApi = ModelTestHelpers.CreateResource(
            resourceName: "api-abcxyz", displayName: "api", state: KnownResourceState.Starting,
            relationships: [new(firstParameter.Name, KnownRelationshipTypes.Reference)]);
        var secondApi = ModelTestHelpers.CreateResource(
            resourceName: "api-defuvw", displayName: "api", state: KnownResourceState.Starting,
            relationships: [new(secondParameter.Name, KnownRelationshipTypes.Reference)]);
        var frontend = ModelTestHelpers.CreateResource(
            resourceName: "frontend", state: KnownResourceState.Waiting,
            relationships: [new("api", KnownRelationshipTypes.WaitFor)]);

        var tooltip = ResourceStateViewModel.GetResourceStateTooltip(
            frontend, new TestStringLocalizer<Columns>(), [frontend, firstApi, secondApi, secondParameter, firstParameter]);

        Assert.Equal(
            $"Localized:{nameof(Columns.StateColumnResourceWaiting)} Localized:{nameof(Columns.StateColumnResourceMissingParameters)}:first-selector, second-selector",
            tooltip);

        var resources = new CopyToThrowingResourceCollection(frontend, firstApi, secondApi, secondParameter, firstParameter);
        Assert.Equal(["first-selector", "second-selector"], frontend.GetMissingParameterDependencies(resources));
        Assert.Equal(1, resources.EnumerationCount);
    }

    [Fact]
    public void WaitingResourceTooltipIncludesMissingParameterOnImplicitParentDependency()
    {
        var parameter = ModelTestHelpers.CreateResource(
            resourceName: "password", resourceType: KnownResourceTypes.Parameter,
            state: KnownResourceState.ValueMissing);
        var server = ModelTestHelpers.CreateResource(
            resourceName: "server-abcxyz", displayName: "server", state: KnownResourceState.Starting,
            relationships: [new(parameter.Name, KnownRelationshipTypes.Reference)]);
        var database = ModelTestHelpers.CreateResource(
            resourceName: "database", state: KnownResourceState.Waiting,
            relationships: [new(server.Name, KnownRelationshipTypes.Parent)]);
        var consumer = ModelTestHelpers.CreateResource(
            resourceName: "api", state: KnownResourceState.Waiting,
            relationships: [new(database.Name, KnownRelationshipTypes.WaitFor)],
            properties: new Dictionary<string, ResourcePropertyViewModel>
            {
                [KnownProperties.Resource.WaitingFor] = new(
                    KnownProperties.Resource.WaitingFor,
                    Value.ForList(Value.ForString(database.Name), Value.ForString(server.Name)),
                    isValueSensitive: false, knownProperty: null, sortOrder: 0,
                    displayName: null, isHighlighted: false)
            });

        var tooltip = ResourceStateViewModel.GetResourceStateTooltip(
            consumer, new TestStringLocalizer<Columns>(), [consumer, database, server, parameter]);

        Assert.Equal(
            $"Localized:{nameof(Columns.StateColumnResourceWaitingFor)}:database, server Localized:{nameof(Columns.StateColumnResourceMissingParameters)}:password",
            tooltip);
    }

    [Theory]
    [InlineData(KnownResourceState.Running, KnownResourceState.ValueMissing, KnownRelationshipTypes.Reference)]
    [InlineData(KnownResourceState.Starting, KnownResourceState.Running, KnownRelationshipTypes.Reference)]
    [InlineData(KnownResourceState.FailedToStart, KnownResourceState.ValueMissing, KnownRelationshipTypes.Parent)]
    public void ResourceTooltipDoesNotReportUnrelatedOrResolvedParameters(
        KnownResourceState resourceState, KnownResourceState parameterState, string relationshipType)
    {
        var parameter = ModelTestHelpers.CreateResource(
            resourceName: "optional-selector", resourceType: KnownResourceTypes.Parameter, state: parameterState);
        var resource = ModelTestHelpers.CreateResource(
            resourceName: "api", state: resourceState,
            relationships: [new(parameter.Name, relationshipType)]);
        var localizer = new TestStringLocalizer<Columns>();

        Assert.Equal(
            ResourceStateViewModel.GetResourceStateTooltip(resource, localizer),
            ResourceStateViewModel.GetResourceStateTooltip(resource, localizer, [resource, parameter]));
    }

    [Fact]
    public void WaitingResourceTooltipUsesDisplayNamesForNonReplicaDependencies()
    {
        var dependency = ModelTestHelpers.CreateResource(
            resourceName: "messaging-abcxyz",
            displayName: "messaging");

        var resource = ModelTestHelpers.CreateResource(
            state: KnownResourceState.Waiting,
            properties: new Dictionary<string, ResourcePropertyViewModel>
            {
                [KnownProperties.Resource.WaitingFor] = new(
                    KnownProperties.Resource.WaitingFor,
                    Value.ForList(Value.ForString("messaging-abcxyz")),
                    isValueSensitive: false,
                    knownProperty: null,
                    sortOrder: 0,
                    displayName: null,
                    isHighlighted: false)
            });

        var localizer = new TestStringLocalizer<Columns>();

        var tooltip = ResourceStateViewModel.GetResourceStateTooltip(resource, localizer, [resource, dependency]);

        Assert.Equal($"Localized:{nameof(Columns.StateColumnResourceWaitingFor)}:messaging", tooltip);
    }

    [Fact]
    public void WaitingResourceTooltipUsesUniqueNamesForReplicaDependencies()
    {
        var firstDependency = ModelTestHelpers.CreateResource(
            resourceName: "messaging-abcxyz",
            displayName: "messaging");
        var secondDependency = ModelTestHelpers.CreateResource(
            resourceName: "messaging-defuvw",
            displayName: "messaging");

        var resource = ModelTestHelpers.CreateResource(
            state: KnownResourceState.Waiting,
            properties: new Dictionary<string, ResourcePropertyViewModel>
            {
                [KnownProperties.Resource.WaitingFor] = new(
                    KnownProperties.Resource.WaitingFor,
                    Value.ForList(Value.ForString("messaging-abcxyz")),
                    isValueSensitive: false,
                    knownProperty: null,
                    sortOrder: 0,
                    displayName: null,
                    isHighlighted: false)
            });

        var localizer = new TestStringLocalizer<Columns>();

        var tooltip = ResourceStateViewModel.GetResourceStateTooltip(resource, localizer, [resource, firstDependency, secondDependency]);

        Assert.Equal($"Localized:{nameof(Columns.StateColumnResourceWaitingFor)}:messaging-abcxyz", tooltip);
    }

    [Fact]
    public void TryGetResolvedWaitingForDependenciesDoesNotMaterializeAllResources()
    {
        var dependency = ModelTestHelpers.CreateResource(
            resourceName: "messaging-abcxyz",
            displayName: "messaging");

        var resource = ModelTestHelpers.CreateResource(
            state: KnownResourceState.Waiting,
            properties: new Dictionary<string, ResourcePropertyViewModel>
            {
                [KnownProperties.Resource.WaitingFor] = new(
                    KnownProperties.Resource.WaitingFor,
                    Value.ForList(Value.ForString("messaging-abcxyz")),
                    isValueSensitive: false,
                    knownProperty: null,
                    sortOrder: 0,
                    displayName: null,
                    isHighlighted: false)
            });

        var resources = new CopyToThrowingResourceCollection(resource, dependency);

        var result = resource.TryGetResolvedWaitingForDependencies(resources, out var dependencies);

        Assert.True(result);
        Assert.Equal(["messaging"], dependencies);
    }

    private sealed class CopyToThrowingResourceCollection(params ResourceViewModel[] resources) : ICollection<ResourceViewModel>, IReadOnlyCollection<ResourceViewModel>
    {
        public int EnumerationCount { get; private set; }

        public int Count => resources.Length;

        public bool IsReadOnly => true;

        public void Add(ResourceViewModel item) => throw new NotSupportedException();

        public void Clear() => throw new NotSupportedException();

        public bool Contains(ResourceViewModel item) => resources.Contains(item);

        public void CopyTo(ResourceViewModel[] array, int arrayIndex) => throw new InvalidOperationException("The resources collection should not be copied.");

        public IEnumerator<ResourceViewModel> GetEnumerator()
        {
            EnumerationCount++;
            return resources.AsEnumerable().GetEnumerator();
        }

        public bool Remove(ResourceViewModel item) => throw new NotSupportedException();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
