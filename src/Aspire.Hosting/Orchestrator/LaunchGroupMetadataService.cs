// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRELAUNCHGROUPS001 // Internal use of launch-group annotations.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Backchannel;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Orchestrator;

/// <summary>
/// Captures the launch decisions made during the before-start pipeline.
/// </summary>
internal sealed class LaunchGroupMetadataService(ILogger<LaunchGroupMetadataService> logger)
{
    // The .NET build coordinator consumes the public Hosting surface, so it reports
    // decisions through this keyed delegate rather than exposing a new public API.
    // Arguments are resource, initial status (null while pending), persistence, and generated-helper status.
    internal const string BuildPlanReporterKey = "Aspire.Hosting.LaunchGroups.BuildPlanReporter";
    internal const string BuildPlanFinalizerKey = "Aspire.Hosting.LaunchGroups.BuildPlanFinalizer";
    internal const string BuildPlanStartupKey = "Aspire.Hosting.LaunchGroups.BuildPlanStartup";

    private readonly Dictionary<IResource, ResourceSelection> _selection = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<IResource, LaunchGroupBuildDisposition> _builds = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<IResource> _generatedBuilds = new(ReferenceEqualityComparer.Instance);
    private string? _selectedGroup;
    private string[]? _groups;
    private GetLaunchGroupsResponse? _information;

    internal void CaptureSelection(IEnumerable<IResource> resources, string? selectedGroup, IEnumerable<string> groups)
    {
        _selectedGroup = selectedGroup;
        _groups = SortGroups(groups);
        _selection.Clear();
        foreach (var resource in resources)
        {
            CaptureResource(resource);
        }
    }

    internal void ValidateBuildSelection(string? selectedGroup, Func<IResource, bool> hasUserExplicitStart)
    {
        if (_builds.Count == 0)
        {
            return;
        }

        if (!StringComparer.OrdinalIgnoreCase.Equals(_selectedGroup, selectedGroup))
        {
            throw new DistributedApplicationException(
                "The launch group selection changed after the coordinated .NET build plan was materialized. " +
                "Set Aspire:LaunchGroup before the before-start pipeline runs.");
        }

        if (selectedGroup is null)
        {
            return;
        }

        foreach (var resource in _builds.Keys)
        {
            if (_generatedBuilds.Contains(resource) || !_selection.TryGetValue(resource, out var previous))
            {
                continue;
            }

            var excludedFromSelection = resource.HasAnnotationOfType<ExcludeFromLaunchGroupSelectionAnnotation>();
            var excludedByGroup = !excludedFromSelection && !LaunchGroupService.BelongsToLaunchGroup(resource, selectedGroup);
            if (previous.ExplicitStart != hasUserExplicitStart(resource) ||
                previous.ExcludedByGroup != excludedByGroup ||
                previous.ExcludedFromSelection != excludedFromSelection)
            {
                throw new DistributedApplicationException(
                    $"The launch eligibility for resource '{resource.Name}' changed after the coordinated .NET build plan was materialized. " +
                    "Configure launch groups and explicit start before the before-start pipeline completes.");
            }
        }
    }

    internal void CaptureResource(IResource resource)
    {
        if (_selection.ContainsKey(resource))
        {
            return;
        }

        var source = LaunchGroupService.GetEffectiveLaunchGroupSource(resource);
        var groups = source is null
            ? []
            : SortGroups(source.Annotations.OfType<LaunchGroupAnnotation>().Select(annotation => annotation.LaunchGroupName));
        var excludedFromSelection = resource.HasAnnotationOfType<ExcludeFromLaunchGroupSelectionAnnotation>();
        _selection.Add(resource, new ResourceSelection(
            Groups: groups,
            InheritedFrom: source is not null && !ReferenceEquals(source, resource) ? source.Name : null,
            ExplicitStart: !_generatedBuilds.Contains(resource) && resource.HasAnnotationOfType<ExplicitStartupAnnotation>(),
            ExcludedByGroup: _selectedGroup is not null && !excludedFromSelection &&
                !groups.Contains(_selectedGroup, StringComparer.OrdinalIgnoreCase),
            ExcludedFromSelection: excludedFromSelection));
    }

    internal void RecordBuild(IResource resource, bool? isInitial, bool hasPersistentLifetime, bool isGeneratedBuild)
    {
        if (isGeneratedBuild)
        {
            _generatedBuilds.Add(resource);
        }

        CaptureResource(resource);
        var selection = _selection[resource];
        _builds[resource] = isInitial switch
        {
            null => LaunchGroupBuildDisposition.Pending,
            false => LaunchGroupBuildDisposition.Deferred,
            true when _selectedGroup is not null && (selection.ExplicitStart || selection.ExcludedByGroup) =>
                hasPersistentLifetime ? LaunchGroupBuildDisposition.InitialPersistent : LaunchGroupBuildDisposition.InitialShared,
            true => LaunchGroupBuildDisposition.Initial
        };
    }

    internal void FinalizeModel(DistributedApplicationModel model)
    {
        foreach (var resource in model.Resources)
        {
            CaptureResource(resource);
        }

        var groups = _groups ?? SortGroups(_selection.Values.SelectMany(selection => selection.Groups));
        var resources = model.Resources
            .OrderBy(resource => resource.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(resource => resource.Name, StringComparer.Ordinal)
            .Select(resource =>
            {
                var selection = _selection[resource];
                return new LaunchGroupResourceInfo
                {
                    Name = resource.Name,
                    Groups = selection.Groups,
                    InheritedFrom = selection.InheritedFrom,
                    ExplicitStart = selection.ExplicitStart,
                    ExcludedByGroup = selection.ExcludedByGroup,
                    ExcludedFromSelection = selection.ExcludedFromSelection,
                    BuildDisposition = _builds.GetValueOrDefault(resource, LaunchGroupBuildDisposition.NotCoordinated)
                };
            })
            .ToArray();

        _information = new GetLaunchGroupsResponse
        {
            SelectedGroup = _selectedGroup,
            Groups = groups,
            Resources = resources
        };

        if (_selectedGroup is not null)
        {
            var localResources = model.Resources
                .Where(LaunchGroupService.IsLocalExecutionResource)
                .Where(resource => resource is not IResourceWithParent || ApplicationOrchestrator.ResourceHasOwnLifetime(resource))
                .Where(resource => !_generatedBuilds.Contains(resource))
                .Where(resource => !resource.Annotations.OfType<HiddenAnnotation>().Any(annotation => annotation.Behavior == HiddenBehavior.Always))
                .Select(resource => resources.Single(info => info.Name == resource.Name))
                .ToArray();
            logger.LogInformation(
                "Launch group '{LaunchGroup}' (visible resources): eligible for automatic startup: {Eligible}; excluded by group: {Excluded}; explicit start: {Explicit}.",
                _selectedGroup,
                FormatNames(localResources.Where(resource => !resource.ExplicitStart && !resource.ExcludedByGroup &&
                    resource.BuildDisposition is not LaunchGroupBuildDisposition.Deferred)),
                FormatNames(localResources.Where(resource => resource.ExcludedByGroup)),
                FormatNames(localResources.Where(resource => resource.ExplicitStart)));
            logger.LogInformation(
                "Coordinated builds: initial: {Initial}; eager for persistent resources: {Persistent}; shared with initial builds: {Shared}; deferred: {Deferred}. AppHost project-reference builds may also run eagerly.",
                FormatNames(localResources.Where(resource => resource.BuildDisposition is LaunchGroupBuildDisposition.Initial)),
                FormatNames(localResources.Where(resource => resource.BuildDisposition is LaunchGroupBuildDisposition.InitialPersistent)),
                FormatNames(localResources.Where(resource => resource.BuildDisposition is LaunchGroupBuildDisposition.InitialShared)),
                FormatNames(localResources.Where(resource => resource.BuildDisposition is LaunchGroupBuildDisposition.Deferred)));
        }
        else if (groups.Length > 0)
        {
            logger.LogInformation("No launch group is selected; normal automatic startup applies.");
        }
    }

    internal GetLaunchGroupsResponse GetInformation() =>
        _information ?? throw new InvalidOperationException("Launch-group metadata is not available before the startup plan is finalized.");

    internal GetLaunchGroupsResponse? GetInformationOrDefault() => _information;

    private static string[] SortGroups(IEnumerable<string> groups) => groups
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Order(StringComparer.OrdinalIgnoreCase)
        .ThenBy(group => group, StringComparer.Ordinal)
        .ToArray();

    private static string FormatNames(IEnumerable<LaunchGroupResourceInfo> resources)
    {
        var names = resources.Select(resource => resource.Name).ToArray();
        return names.Length == 0 ? "(none)" : string.Join(", ", names);
    }

    private sealed record ResourceSelection(
        string[] Groups,
        string? InheritedFrom,
        bool ExplicitStart,
        bool ExcludedByGroup,
        bool ExcludedFromSelection);
}
