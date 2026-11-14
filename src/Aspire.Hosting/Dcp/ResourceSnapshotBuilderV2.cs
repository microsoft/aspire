// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Collections.Immutable;
using Aspire.Dashboard.Model;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Dashboard;
using Aspire.Hosting.Dcp.Model;
using V2 = Aspire.Hosting.Dcp.Model.V2;

namespace Aspire.Hosting.Dcp;

/// <summary>
/// Projects logical V2 workload state and controller-published endpoints into dashboard snapshots.
/// </summary>
internal sealed class ResourceSnapshotBuilderV2(
    IReadOnlyList<DcpWorkloadV2> workloads,
    IReadOnlyList<DcpServiceV2> services,
    ConcurrentDictionary<string, V2.ServiceEndpoint> endpoints,
    ConcurrentDictionary<string, V2.LogicalService> observedServices)
{
    public CustomResourceSnapshot ToSnapshot(CustomResource resource, IResource modelResource, CustomResourceSnapshot previous)
    {
        var type = resource switch
        {
            V2.LogicalContainer => KnownResourceTypes.Container,
            V2.LogicalContainerExec => KnownResourceTypes.ContainerExec,
            _ => modelResource.GetResourceType()
        };
        var status = GetStatus(resource);
        var (args, env, exitCode) = resource switch
        {
            V2.LogicalProcess p => (p.Spec.Process?.Args, p.Spec.Process?.Env, p.Status?.ExitCode),
            V2.LogicalContainer c => (c.Spec.Container?.Command, c.Spec.Container?.Env, c.Status?.ExitCode),
            V2.LogicalContainerExec e => (e.Spec.Execution.Args, e.Spec.Execution.Env, e.Status?.ExitCode),
            _ => throw new InvalidOperationException($"Unexpected V2 workload '{resource.Kind}'.")
        };
        resource.TryGetAnnotationAsObjectList(CustomResource.ResourceAppArgsAnnotation, out List<AppLaunchArgumentAnnotation>? displayArguments);
        var launchArguments = DcpSnapshotUtilities.GetLaunchArguments(displayArguments, args);
        var properties = previous.Properties.SetResourcePropertyRange(
        [
            new(KnownProperties.Resource.AppArgs, launchArguments?.Args) { IsSensitive = launchArguments?.IsSensitive ?? false },
            new(KnownProperties.Resource.AppArgsSensitivity, launchArguments?.ArgsAreSensitive) { IsSensitive = launchArguments?.IsSensitive ?? false }
        ]);
        if (resource is V2.LogicalProcess process)
        {
            properties = properties.SetResourcePropertyRange(
            [
                ResourcePropertySnapshotMetadata.Create(type, KnownProperties.Executable.Path, process.Spec.Process?.ExecutablePath),
                ResourcePropertySnapshotMetadata.Create(type, KnownProperties.Executable.WorkDir, process.Spec.Process?.WorkingDirectory),
                ResourcePropertySnapshotMetadata.Create(type, KnownProperties.Executable.Args, args ?? [], isSensitive: true),
                ResourcePropertySnapshotMetadata.Create(type, KnownProperties.Executable.Pid, process.Status?.Pid)
            ]);
            if (modelResource.TryGetProjectMetadata(out var metadata))
            {
                properties = properties.SetResourceProperty(KnownProperties.Project.Path, metadata.ProjectPath);
            }
        }
        if (resource is V2.LogicalContainer container)
        {
            properties = properties.SetResourcePropertyRange(
            [
                ResourcePropertySnapshotMetadata.Create(type, KnownProperties.Container.Image, container.Status?.Image ?? container.Spec.Container?.Image?.Image),
                ResourcePropertySnapshotMetadata.Create(type, KnownProperties.Container.Id, container.Status?.ContainerId),
                ResourcePropertySnapshotMetadata.Create(type, KnownProperties.Container.Command, container.Spec.Container?.Entrypoint),
                ResourcePropertySnapshotMetadata.Create(type, KnownProperties.Container.Args, args ?? [], isSensitive: true),
                ResourcePropertySnapshotMetadata.Create(type, KnownProperties.Container.Ports, container.Spec.Ports?.Select(p => p.ContainerPort).ToImmutableArray() ?? []),
                ResourcePropertySnapshotMetadata.Create(type, KnownProperties.Container.Lifetime,
                    modelResource.HasPersistentLifetime() ? ContainerLifetime.Persistent : ContainerLifetime.Session)
            ]);
        }
        if (resource is V2.LogicalContainerExec exec)
        {
            properties = properties.SetResourcePropertyRange(
            [
                ResourcePropertySnapshotMetadata.Create(KnownResourceTypes.Executable, KnownProperties.Executable.Path, exec.Spec.Execution.Command),
                ResourcePropertySnapshotMetadata.Create(KnownResourceTypes.Executable, KnownProperties.Executable.WorkDir, exec.Spec.Execution.WorkingDirectory),
                ResourcePropertySnapshotMetadata.Create(KnownResourceTypes.Executable, KnownProperties.Executable.Args, args ?? [], isSensitive: true)
            ]);
        }
        return previous with
        {
            ResourceType = previous.ResourceType ?? type,
            State = resource.AppModelInitialState == "Hidden" ? "Hidden" :
                workloads.FirstOrDefault(w => w.DcpResourceName == resource.Metadata.Name)?.ExplicitStart == true && status.State is null or "Starting" or "Unknown"
                    ? KnownResourceStates.NotStarted : status.State,
            ExitCode = exitCode is Conventions.UnknownExitCode ? null : exitCode,
            CreationTimeStamp = resource.Metadata.CreationTimestamp?.ToUniversalTime(),
            StartTimeStamp = status.StartupTimestamp,
            StopTimeStamp = status.FinishedTimestamp,
            Properties = properties,
            EnvironmentVariables = DcpSnapshotUtilities.GetEnvironmentVariables(
                env?.Select(e => new EnvVar { Name = e.Name, Value = e.Value }).ToList(),
                env?.Select(e => new EnvVar { Name = e.Name, Value = e.Value }).ToList()),
            Relationships = ApplicationModel.ResourceSnapshotBuilder.BuildRelationships(modelResource),
            Urls = GetUrls(resource, modelResource, status.State),
            Volumes = modelResource.Annotations.OfType<ContainerMountAnnotation>().Select(m => new VolumeSnapshot(
                m.Source, m.Target, m.Type == ContainerMountType.BindMount ? "bind" : "volume", m.IsReadOnly)).ToImmutableArray()
        };
    }

    internal static ResourceStatus GetStatus(CustomResource resource)
    {
        var (phase, start, finish) = resource switch
        {
            V2.LogicalProcess p => (p.Status?.Phase, p.Status?.IdentityTimestamp, p.Status?.FinishedAt),
            V2.LogicalContainer c => (c.Status?.Phase, c.Status?.StartedAt, c.Status?.FinishedAt),
            V2.LogicalContainerExec e => (e.Status?.Phase, e.Status?.StartedAt, e.Status?.FinishedAt),
            _ => throw new InvalidOperationException($"Unexpected V2 workload '{resource.Kind}'.")
        };
        var state = phase switch
        {
            "Pending" => KnownResourceStates.Starting,
            "Failed" => KnownResourceStates.FailedToStart,
            "Exited" when resource is V2.LogicalProcess or V2.LogicalContainerExec => KnownResourceStates.Finished,
            _ => phase
        };
        return new(state, start?.ToUniversalTime(), finish?.ToUniversalTime());
    }

    private ImmutableArray<UrlSnapshot> GetUrls(CustomResource resource, IResource modelResource, string? state)
    {
        if (!modelResource.TryGetUrls(out var urls))
        {
            return [];
        }
        return urls.Select(url =>
        {
            var active = state == KnownResourceStates.Running;
            if (url.Endpoint is { } reference)
            {
                var service = services.FirstOrDefault(s =>
                    string.Equals(s.ModelResource.Name, reference.Resource.Name, StringComparisons.ResourceName) &&
                    string.Equals(s.Endpoint.Name, reference.EndpointName, StringComparisons.EndpointAnnotationName));
                active = service is not null &&
                    observedServices.TryGetValue(service.DcpResourceName, out var observedService) &&
                    observedService.Status?.Phase == V2.LogicalServicePhase.Ready && endpoints.Values.Any(e =>
                    e.Spec.ServiceRef == service.DcpResourceName && e.Spec.Addresses.Count > 0 &&
                    (!ReferenceEquals(service.ModelResource, modelResource) ||
                        e.Metadata.OwnerReferences?.Any(owner => owner.Kind == resource.Kind && owner.Uid == resource.Metadata.Uid) == true));
            }
            return new UrlSnapshot(url.Endpoint?.EndpointName, url.Url, url.IsInternal)
            {
                IsInactive = !active, DisplayProperties = new(url.DisplayText ?? "", url.DisplayOrder ?? 0)
            };
        }).ToImmutableArray();
    }
}
