// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using Aspire.Dashboard.Model;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Dashboard;
using Aspire.Hosting.Dcp.Model;

namespace Aspire.Hosting.Dcp;

internal class ResourceSnapshotBuilder
{
    private readonly DcpResourceState _resourceState;

    public ResourceSnapshotBuilder(DcpResourceState resourceState)
    {
        _resourceState = resourceState;
    }

    public CustomResourceSnapshot ToSnapshot(Container container, CustomResourceSnapshot previous)
    {
        var containerId = container.Status?.ContainerId;
        var urls = GetUrls(container, container.Status?.State);
        var volumes = GetVolumes(container);

        var effectiveArgs = container.Status?.EffectiveArgs;
        var environment = GetEnvironmentVariables(container.Status?.EffectiveEnv ?? container.Spec.Env, container.Spec.Env);
        var state = container.Status?.State;

        if (container.Spec.Start is false && (state == null || state == ContainerState.Pending))
        {
            state = KnownResourceStates.NotStarted;
        }

        var relationships = ImmutableArray<RelationshipSnapshot>.Empty;

        (ImmutableArray<string> Args, ImmutableArray<int>? ArgsAreSensitive, bool IsSensitive)? launchArguments = null;

        if (container.AppModelResourceName is not null &&
            _resourceState.ApplicationModel.TryGetValue(container.AppModelResourceName, out var appModelResource))
        {
            relationships = ApplicationModel.ResourceSnapshotBuilder.BuildRelationships(appModelResource);
            launchArguments = GetLaunchArgs(container, effectiveArgs);
        }

        return previous with
        {
            ResourceType = previous.ResourceType ?? KnownResourceTypes.Container,
            State = state,
            // Map a container exit code of -1 (unknown) to null
            ExitCode = container.Status?.ExitCode is null or Conventions.UnknownExitCode ? null : container.Status.ExitCode,
            Properties = previous.Properties.SetResourcePropertyRange([
                ResourcePropertySnapshotMetadata.Create(KnownResourceTypes.Container, KnownProperties.Container.Image, container.Spec.Image),
                ResourcePropertySnapshotMetadata.Create(KnownResourceTypes.Container, KnownProperties.Container.Id, containerId),
                ResourcePropertySnapshotMetadata.Create(KnownResourceTypes.Container, KnownProperties.Container.Command, container.Spec.Command),
                ResourcePropertySnapshotMetadata.Create(KnownResourceTypes.Container, KnownProperties.Container.Args, effectiveArgs ?? [], isSensitive: true),
                ResourcePropertySnapshotMetadata.Create(KnownResourceTypes.Container, KnownProperties.Container.Ports, GetPorts()),
                ResourcePropertySnapshotMetadata.Create(KnownResourceTypes.Container, KnownProperties.Container.Lifetime, GetContainerLifetime()),
                new(KnownProperties.Resource.AppArgs, launchArguments?.Args) { IsSensitive = launchArguments?.IsSensitive ?? false },
                new(KnownProperties.Resource.AppArgsSensitivity, launchArguments?.ArgsAreSensitive) { IsSensitive = launchArguments?.IsSensitive ?? false },
            ]),
            EnvironmentVariables = environment,
            CreationTimeStamp = container.Metadata.CreationTimestamp?.ToUniversalTime(),
            StartTimeStamp = container.Status?.StartupTimestamp?.ToUniversalTime(),
            StopTimeStamp = container.Status?.FinishTimestamp?.ToUniversalTime(),
            Urls = urls,
            Volumes = volumes,
            Relationships = relationships
        };

        ImmutableArray<int> GetPorts()
        {
            if (container.Spec.Ports is null)
            {
                return [];
            }

            var ports = ImmutableArray.CreateBuilder<int>();
            foreach (var port in container.Spec.Ports)
            {
                if (port.ContainerPort != null)
                {
                    ports.Add(port.ContainerPort.Value);
                }
            }
            return ports.ToImmutable();
        }

        ContainerLifetime GetContainerLifetime()
        {
            return (container.Spec.Persistent ?? false) ? ContainerLifetime.Persistent : ContainerLifetime.Session;
        }
    }

    public CustomResourceSnapshot ToSnapshot(ContainerExec executable, CustomResourceSnapshot previous)
    {
        IResource? appModelResource = null;
        _ = executable.AppModelResourceName is not null && _resourceState.ApplicationModel.TryGetValue(executable.AppModelResourceName, out appModelResource);

        var state = executable.AppModelInitialState is "Hidden" ? "Hidden" : executable.Status?.State;
        var environment = GetEnvironmentVariables(executable.Status?.EffectiveEnv, executable.Spec.Env);
        var effectiveArgs = executable.Status?.EffectiveArgs;
        var launchArguments = GetLaunchArgs(executable, effectiveArgs);

        var relationships = ImmutableArray<RelationshipSnapshot>.Empty;
        if (appModelResource != null)
        {
            relationships = ApplicationModel.ResourceSnapshotBuilder.BuildRelationships(appModelResource);
        }

        return previous with
        {
            ResourceType = previous.ResourceType ?? KnownResourceTypes.Executable,
            State = state,
            ExitCode = executable.Status?.ExitCode,
            Properties = previous.Properties.SetResourcePropertyRange([
                ResourcePropertySnapshotMetadata.Create(KnownResourceTypes.Executable, KnownProperties.Executable.WorkDir, executable.Spec.WorkingDirectory),
                ResourcePropertySnapshotMetadata.Create(KnownResourceTypes.Executable, KnownProperties.Executable.Args, effectiveArgs ?? [], isSensitive: true),
                new(KnownProperties.Resource.AppArgs, launchArguments?.Args) { IsSensitive = launchArguments?.IsSensitive ?? false },
                new(KnownProperties.Resource.AppArgsSensitivity, launchArguments?.ArgsAreSensitive) { IsSensitive = launchArguments?.IsSensitive ?? false },
            ]),
            EnvironmentVariables = environment,
            CreationTimeStamp = executable.Metadata.CreationTimestamp?.ToUniversalTime(),
            StartTimeStamp = executable.Status?.StartupTimestamp?.ToUniversalTime(),
            StopTimeStamp = executable.Status?.FinishTimestamp?.ToUniversalTime(),
            Relationships = relationships
        };
    }

    public CustomResourceSnapshot ToSnapshot(Executable executable, CustomResourceSnapshot previous)
    {
        string? projectPath = null;
        string? launchProfileName = null;
        IResource? appModelResource = null;

        if (executable.AppModelResourceName is not null &&
            _resourceState.ApplicationModel.TryGetValue(executable.AppModelResourceName, out appModelResource))
        {
            if (appModelResource is ProjectResource projectResource)
            {
                projectPath = projectResource.GetProjectMetadata().ProjectPath;
                launchProfileName = projectResource.GetEffectiveLaunchProfile()?.Name;
            }
        }

        var state = executable.AppModelInitialState is "Hidden" ? "Hidden" : executable.Status?.State;
        if (executable.Spec.Start is false && IsNotStartedExecutableState(state))
        {
            state = KnownResourceStates.NotStarted;
        }

        var urls = GetUrls(executable, executable.Status?.State);

        var effectiveArgs = executable.Status?.EffectiveArgs;
        var environment = GetEnvironmentVariables(executable.Status?.EffectiveEnv, executable.Spec.Env);

        var relationships = ImmutableArray<RelationshipSnapshot>.Empty;
        if (appModelResource != null)
        {
            relationships = ApplicationModel.ResourceSnapshotBuilder.BuildRelationships(appModelResource);
        }

        var launchArguments = GetLaunchArgs(executable, effectiveArgs);

        if (projectPath is not null)
        {
            return previous with
            {
                ResourceType = previous.ResourceType ?? KnownResourceTypes.Project,
                State = state,
                ExitCode = executable.Status?.ExitCode,
                Properties = previous.Properties.SetResourcePropertyRange([
                    ResourcePropertySnapshotMetadata.Create(KnownResourceTypes.Project, KnownProperties.Executable.Path, executable.Spec.ExecutablePath),
                    ResourcePropertySnapshotMetadata.Create(KnownResourceTypes.Project, KnownProperties.Executable.WorkDir, executable.Spec.WorkingDirectory),
                    ResourcePropertySnapshotMetadata.Create(KnownResourceTypes.Project, KnownProperties.Executable.Args, effectiveArgs ?? [], isSensitive: true),
                    ResourcePropertySnapshotMetadata.Create(KnownResourceTypes.Project, KnownProperties.Executable.Pid, executable.Status?.ProcessId),
                    ResourcePropertySnapshotMetadata.Create(KnownResourceTypes.Project, KnownProperties.Project.Path, projectPath),
                    ResourcePropertySnapshotMetadata.Create(KnownResourceTypes.Project, KnownProperties.Project.LaunchProfile, launchProfileName),
                    new(KnownProperties.Resource.AppArgs, launchArguments?.Args) { IsSensitive = launchArguments?.IsSensitive ?? false },
                    new(KnownProperties.Resource.AppArgsSensitivity, launchArguments?.ArgsAreSensitive) { IsSensitive = launchArguments?.IsSensitive ?? false },
                ]),
                EnvironmentVariables = environment,
                CreationTimeStamp = executable.Metadata.CreationTimestamp?.ToUniversalTime(),
                StartTimeStamp = executable.Status?.StartupTimestamp?.ToUniversalTime(),
                StopTimeStamp = executable.Status?.FinishTimestamp?.ToUniversalTime(),
                Urls = urls,
                Relationships = relationships
            };
        }

        return previous with
        {
            ResourceType = previous.ResourceType ?? KnownResourceTypes.Executable,
            State = state,
            ExitCode = executable.Status?.ExitCode,
            Properties = previous.Properties.SetResourcePropertyRange([
                ResourcePropertySnapshotMetadata.Create(KnownResourceTypes.Executable, KnownProperties.Executable.Path, executable.Spec.ExecutablePath),
                ResourcePropertySnapshotMetadata.Create(KnownResourceTypes.Executable, KnownProperties.Executable.WorkDir, executable.Spec.WorkingDirectory),
                ResourcePropertySnapshotMetadata.Create(KnownResourceTypes.Executable, KnownProperties.Executable.Args, effectiveArgs ?? [], isSensitive: true),
                ResourcePropertySnapshotMetadata.Create(KnownResourceTypes.Executable, KnownProperties.Executable.Pid, executable.Status?.ProcessId),
                new(KnownProperties.Resource.AppArgs, launchArguments?.Args) { IsSensitive = launchArguments?.IsSensitive ?? false },
                new(KnownProperties.Resource.AppArgsSensitivity, launchArguments?.ArgsAreSensitive) { IsSensitive = launchArguments?.IsSensitive ?? false },
            ]),
            EnvironmentVariables = environment,
            CreationTimeStamp = executable.Metadata.CreationTimestamp?.ToUniversalTime(),
            StartTimeStamp = executable.Status?.StartupTimestamp?.ToUniversalTime(),
            StopTimeStamp = executable.Status?.FinishTimestamp?.ToUniversalTime(),
            Urls = urls,
            Relationships = relationships
        };
    }

    private static bool IsNotStartedExecutableState(string? state)
    {
        return string.IsNullOrEmpty(state) || state == ExecutableState.Unknown;
    }

    private static (ImmutableArray<string> Args, ImmutableArray<int>? ArgsAreSensitive, bool IsSensitive)? GetLaunchArgs(CustomResource resource, IReadOnlyList<string>? effectiveArgs)
    {
        if (!resource.TryGetAnnotationAsObjectList(CustomResource.ResourceAppArgsAnnotation, out List<AppLaunchArgumentAnnotation>? launchArgumentAnnotations))
        {
            return null;
        }

        var launchArgsBuilder = ImmutableArray.CreateBuilder<string>();
        var argsAreSensitiveBuilder = ImmutableArray.CreateBuilder<int>();

        var anySensitive = false;
        foreach (var annotation in launchArgumentAnnotations)
        {
            if (annotation.IsSensitive)
            {
                anySensitive = true;
            }

            launchArgsBuilder.Add(GetArgumentValue(annotation, effectiveArgs));
            argsAreSensitiveBuilder.Add(Convert.ToInt32(annotation.IsSensitive));
        }

        return (launchArgsBuilder.ToImmutable(), argsAreSensitiveBuilder.ToImmutable(), anySensitive);

        static string GetArgumentValue(AppLaunchArgumentAnnotation annotation, IReadOnlyList<string>? effectiveArgs)
        {
            if (annotation.EffectiveArgumentIndex is int index &&
                effectiveArgs is not null &&
                (uint)index < (uint)effectiveArgs.Count)
            {
                return effectiveArgs[index];
            }

            return annotation.Argument;
        }
    }

    private ImmutableArray<UrlSnapshot> GetUrls(CustomResource resource, string? resourceState)
    {
        var urls = ImmutableArray.CreateBuilder<UrlSnapshot>();
        var appModelResourceName = resource.AppModelResourceName;

        if (appModelResourceName is string resourceName &&
            _resourceState.ApplicationModel.TryGetValue(resourceName, out var appModelResource) &&
            appModelResource.TryGetUrls(out var resourceUrls))
        {
            var endpointUrls = resourceUrls.Where(u => u.Endpoint is not null).ToList();
            var nonEndpointUrls = resourceUrls.Where(u => u.Endpoint is null).ToList();

            var resourceServices = _resourceState.AppResources.OfType<ServiceWithModelResource>()
                .Where(r => r.Service.AppModelResourceName == resource.AppModelResourceName)
                .Select(s => s.Service)
                .ToList();
            var name = resource.Metadata.Name;

            // Add the endpoint URLs for endpoints belonging to the current resource
            var serviceEndpoints = new HashSet<(string EndpointName, string ServiceMetadataName)>(
                resourceServices
                    .Where(s => !string.IsNullOrEmpty(s.EndpointName))
                    .Select(s => (s.EndpointName!, s.Metadata.Name)));
            var processedEndpointUrls = new HashSet<ResourceUrlAnnotation>();

            foreach (var endpoint in serviceEndpoints)
            {
                var (endpointName, serviceName) = endpoint;
                var urlsForEndpoint = endpointUrls.Where(u =>
                        string.Equals(endpointName, u.Endpoint?.EndpointName, StringComparisons.EndpointAnnotationName)
                        && u.Endpoint?.Resource.Name == appModelResource.Name)
                    .ToList();

                foreach (var endpointUrl in urlsForEndpoint)
                {
                    var activeEndpoint = _resourceState.EndpointsMap.SingleOrDefault(e =>
                            e.Value.Spec.ServiceName == serviceName
                            && e.Value.Metadata.OwnerReferences?.Any(or => or.Kind == resource.Kind && or.Name == name) == true)
                        .Value;
                    var isInactive = activeEndpoint is null;

                    urls.Add(
                        new(Name: endpointUrl.Endpoint!.EndpointName,
                            Url: endpointUrl.Url,
                            IsInternal:
                            endpointUrl.IsInternal)
                        {
                            IsInactive = isInactive,
                            DisplayProperties = new(endpointUrl.DisplayText ?? "", endpointUrl.DisplayOrder ?? 0)
                        });
                    processedEndpointUrls.Add(endpointUrl);
                }
            }

            // Add endpoint URLs that reference endpoints from other resources
            var crossResourceEndpointUrls = endpointUrls.Where(u => !processedEndpointUrls.Contains(u)).ToList();
            var resourceRunning = string.Equals(resourceState, KnownResourceStates.Running, StringComparisons.ResourceState);
            foreach (var endpointUrl in crossResourceEndpointUrls)
            {
                var endpointOwnerResourceName = endpointUrl.Endpoint!.Resource.Name;
                var endpointName = endpointUrl.Endpoint.EndpointName;

                // Find the DCP service representing the appmodel endpoint in the owning resource
                var endpointOwnerEndpoint = _resourceState.AppResources.OfType<ServiceWithModelResource>()
                    .Where(r => r.Service.AppModelResourceName == endpointOwnerResourceName)
                    .Select(s => s.Service)
                    .FirstOrDefault(s => string.Equals(endpointName, s.EndpointName, StringComparisons.EndpointAnnotationName));
                // The endpoint is active if there is an active DCP endpoint for the owning resource's service endpoint
                var isActive = _resourceState.EndpointsMap.Any(e => e.Value.Spec.ServiceName == endpointOwnerEndpoint?.Metadata.Name);

                urls.Add(
                    new(Name: endpointName, Url: endpointUrl.Url, IsInternal: endpointUrl.IsInternal)
                    {
                        IsInactive = !isActive,
                        DisplayProperties = new(endpointUrl.DisplayText ?? "", endpointUrl.DisplayOrder ?? 0)
                    });
            }

            // Add the non-endpoint URLs
            foreach (var url in nonEndpointUrls)
            {
                urls.Add(
                    new(Name: null, Url: url.Url, IsInternal: url.IsInternal)
                    {
                        IsInactive = !resourceRunning,
                        DisplayProperties = new(url.DisplayText ?? "", url.DisplayOrder ?? 0)
                    });
            }
        }

        return urls.ToImmutable();
    }

    private static ImmutableArray<VolumeSnapshot> GetVolumes(CustomResource resource)
    {
        if (resource is Container container)
        {
            return container.Spec.VolumeMounts?.Select(v => new VolumeSnapshot(v.Source, v.Target ?? "", v.Type, v.IsReadOnly)).ToImmutableArray() ?? [];
        }

        return [];
    }

    private static ImmutableArray<EnvironmentVariableSnapshot> GetEnvironmentVariables(List<EnvVar>? effectiveSource, List<EnvVar>? specSource)
    {
        if (effectiveSource is null or { Count: 0 })
        {
            return [];
        }

        var environment = ImmutableArray.CreateBuilder<EnvironmentVariableSnapshot>(effectiveSource.Count);

        foreach (var env in effectiveSource)
        {
            if (env.Name is not null)
            {
                var isFromSpec = specSource?.Any(e => string.Equals(e.Name, env.Name, StringComparison.Ordinal)) is true or null;

                environment.Add(new(env.Name, env.Value ?? "", isFromSpec));
            }
        }

        environment.Sort((v1, v2) => string.Compare(v1.Name, v2.Name, StringComparison.Ordinal));

        return environment.ToImmutable();
    }
}
