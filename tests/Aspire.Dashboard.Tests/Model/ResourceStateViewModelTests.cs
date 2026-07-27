// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Deck;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Resources;
using Aspire.Tests.Shared.DashboardModel;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Xunit;
using Enum = System.Enum;

namespace Aspire.Dashboard.Tests.Model;

public class ResourceStateViewModelTests
{
    [Theory]
    // Resource is no longer running
    [InlineData(
        /* state */ "Container", KnownResourceState.Exited, null, null,null,
        /* expected output */ $"Localized:{nameof(Columns.StateColumnResourceExited)}:Container", "info", "Exited")]
    [InlineData(
        /* state */ "Container", KnownResourceState.Exited, 3, null, null,
        /* expected output */ $"Localized:{nameof(Columns.StateColumnResourceExitedUnexpectedly)}:Container+3", "error", "Exited")]
    [InlineData(
        /* state */ "Container", KnownResourceState.Exited, 0, null, null,
        /* expected output */ $"Localized:{nameof(Columns.StateColumnResourceExited)}:Container", "info", "Exited")]
    [InlineData(
        /* state */ "Container", KnownResourceState.Finished, 0, null, null,
        /* expected output */ $"Localized:{nameof(Columns.StateColumnResourceExited)}:Container", "info", "Finished")]
    [InlineData(
        /* state */ "CustomResource", KnownResourceState.Finished, null, null, null,
        /* expected output */ $"Localized:{nameof(Columns.StateColumnResourceExited)}:CustomResource", "info", "Finished")]
    // Resource failed to start - should use dedicated message, not "no longer running"
    [InlineData(
        /* state */ "Container", KnownResourceState.FailedToStart, null, null, null,
        /* expected output */ $"Localized:{nameof(Columns.StateColumnResourceFailedToStart)}:Container", "warning", "Failed to start")]
    [InlineData(
        /* state */ "CustomResource", KnownResourceState.FailedToStart, null, null, null,
        /* expected output */ $"Localized:{nameof(Columns.StateColumnResourceFailedToStart)}:CustomResource", "warning", "Failed to start")]
    [InlineData(
        /* state */ "Container", KnownResourceState.Unknown, null, null, null,
        /* expected output */ "Unknown", "info", "Unknown")]
    // Health checks
    [InlineData(
        /* state */ "Container", KnownResourceState.Running, null, "Healthy", null,
        /* expected output */ "Running", "success", "Running")]
    [InlineData(
        /* state */ "Container", KnownResourceState.Running, null, "", null,
        /* expected output */ $"Localized:{nameof(Columns.RunningAndUnhealthyResourceStateToolTip)}", "warning", "Running (Unhealthy)")]
    [InlineData(
        /* state */ "Container", KnownResourceState.Running, null, "Unhealthy", null,
        /* expected output */ $"Localized:{nameof(Columns.RunningAndUnhealthyResourceStateToolTip)}", "warning", "Running (Unhealthy)")]
    [InlineData(
        /* state */ "Container", KnownResourceState.Running, null, "Healthy", "warning",
        /* expected output */ "Running", "warning", "Running")]
    [InlineData(
        /* state */ "Container", KnownResourceState.Running, null, "Healthy", "NOT_A_VALID_STATE_STYLE",
        /* expected output */ "Running", "neutral", "Running")]
    [InlineData(
        /* state */ "Container", KnownResourceState.Running, null, null, "info",
        /* expected output */ "Running", "info", "Running")]
    [InlineData(
        /* state */ "Container", KnownResourceState.RuntimeUnhealthy, null, null, null,
        /* expected output */ $"Localized:{nameof(Columns.StateColumnResourceContainerRuntimeUnhealthy)}", "warning", "Runtime unhealthy")]
    public void ResourceViewModel_ReturnsCorrectToneAndTooltip(
        string resourceType,
        KnownResourceState state,
        int? exitCode,
        string? healthStatusString,
        string? stateStyle,
        string? expectedTooltip,
        string expectedTone,
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

        Assert.Equal(expectedTone, ResourceStateTone.Get(resource));
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
        public int Count => resources.Length;

        public bool IsReadOnly => true;

        public void Add(ResourceViewModel item) => throw new NotSupportedException();

        public void Clear() => throw new NotSupportedException();

        public bool Contains(ResourceViewModel item) => resources.Contains(item);

        public void CopyTo(ResourceViewModel[] array, int arrayIndex) => throw new InvalidOperationException("The resources collection should not be copied.");

        public IEnumerator<ResourceViewModel> GetEnumerator() => resources.AsEnumerable().GetEnumerator();

        public bool Remove(ResourceViewModel item) => throw new NotSupportedException();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
