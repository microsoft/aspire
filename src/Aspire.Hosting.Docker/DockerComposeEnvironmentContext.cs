// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Docker;

internal sealed class DockerComposeEnvironmentContext(DockerComposeEnvironmentResource environment, ILogger logger)
{
    public async Task<DockerComposeServiceResource> CreateDockerComposeServiceResourceAsync(IResource resource, DistributedApplicationExecutionContext executionContext, CancellationToken cancellationToken)
    {
        if (environment.ResourceMapping.TryGetValue(resource, out var existingResource))
        {
            return existingResource;
        }

        logger.LogInformation("Creating Docker Compose resource for {ResourceName}", resource.Name);

        var serviceResource = new DockerComposeServiceResource(resource.Name, resource, environment);
        environment.ResourceMapping[resource] = serviceResource;

        // Process endpoints
        ProcessEndpoints(serviceResource);

        // Process volumes
        ProcessVolumes(serviceResource);

        // Process environment variables
        await ProcessEnvironmentVariablesAsync(serviceResource, executionContext, cancellationToken).ConfigureAwait(false);

        // Process command line arguments
        await ProcessArgumentsAsync(serviceResource, executionContext, cancellationToken).ConfigureAwait(false);

        return serviceResource;
    }

    private void ProcessEndpoints(DockerComposeServiceResource serviceResource)
    {
        var resolvedEndpoints = serviceResource.TargetResource.ResolveEndpoints(environment.PortAllocator);

        foreach (var resolved in resolvedEndpoints)
        {
            var endpoint = resolved.Endpoint;

            // Convert target port to string for Docker Compose
            // For default ProjectResource endpoints (TargetPort.Value is null), use container port placeholder
            string internalPort = resolved.TargetPort.Value is int port
                ? port.ToString(CultureInfo.InvariantCulture)
                : serviceResource.AsContainerPortPlaceholder();

            // Docker Compose can handle dynamic port allocation, so only use exposed port if it was explicitly specified
            // Skip allocated or implicit ports - only use explicit ports
            var exposedPort = (resolved.ExposedPort.IsAllocated || resolved.ExposedPort.IsImplicit) ? null : resolved.ExposedPort.Value;

            serviceResource.EndpointMappings.Add(endpoint.Name,
                new(serviceResource.TargetResource,
                    endpoint.UriScheme,
                    serviceResource.TargetResource.Name,
                    internalPort,
                    exposedPort,
                    endpoint.IsExternal,
                    endpoint.Name));
        }
    }

    private void ProcessVolumes(DockerComposeServiceResource serviceResource)
    {
        if (!serviceResource.TargetResource.TryGetContainerMounts(out var mounts))
        {
            return;
        }

        var bindMountIndex = 0;

        foreach (var mount in mounts)
        {
            if (mount.Source is null || mount.Target is null)
            {
                throw new InvalidOperationException("Volume source and target must be set");
            }

            var source = mount.Source;
            var name = mount.Source;

            // For bind mounts, create environment placeholders for the source path
            // Skip the docker socket which should be left as-is for portability
            if (mount.Type == ContainerMountType.BindMount && !IsDockerSocket(mount.Source))
            {
                // Create environment variable name: {RESOURCE_NAME}_BINDMOUNT_{INDEX}
                var envVarName = $"{serviceResource.Name.ToUpperInvariant().Replace("-", "_").Replace(".", "_")}_BINDMOUNT_{bindMountIndex}";
                bindMountIndex++;

                // Add the placeholder to captured environment variables so it gets written to the .env file
                // Use the original source path as the default value and pass the ContainerMountAnnotation as the source
                var placeholder = environment.AddEnvironmentVariable(
                    envVarName,
                    description: $"Bind mount source for {serviceResource.Name}:{mount.Target}",
                    defaultValue: mount.Source,
                    source: mount,
                    resource: serviceResource.TargetResource);

                // Log warning about host-specific path
                logger.BindMountHostSpecificPath(serviceResource.Name, mount.Source, envVarName);

                // Use the placeholder in the compose file
                source = placeholder;
                name = envVarName;
            }

            serviceResource.Volumes.Add(new Resources.ServiceNodes.Volume
            {
                Name = name,
                Source = source,
                Target = mount.Target,
                Type = mount.Type == ContainerMountType.BindMount ? "bind" : "volume",
                ReadOnly = mount.IsReadOnly
            });
        }
    }

    /// <summary>
    /// Checks if the source path is the Docker socket path.
    /// </summary>
    private static bool IsDockerSocket(string source)
    {
        // Check for common Docker socket paths across different platforms
        return source.Equals("/var/run/docker.sock", StringComparison.OrdinalIgnoreCase) ||
               source.Equals("//var/run/docker.sock", StringComparison.OrdinalIgnoreCase) ||  // WSL-style path
               source.Equals(@"\\.\pipe\docker_engine", StringComparison.OrdinalIgnoreCase);  // Windows named pipe
    }

    private static async Task ProcessEnvironmentVariablesAsync(DockerComposeServiceResource serviceResource, DistributedApplicationExecutionContext executionContext, CancellationToken cancellationToken)
    {
        if (serviceResource.TargetResource.TryGetAnnotationsOfType<EnvironmentCallbackAnnotation>(out var environmentCallbacks))
        {
            var context = new EnvironmentCallbackContext(
                executionContext,
                serviceResource.TargetResource,
                serviceResource.EnvironmentVariables,
                cancellationToken: cancellationToken);

            foreach (var callback in environmentCallbacks)
            {
                await callback.Callback(context).ConfigureAwait(false);
            }

            // Remove HTTPS service discovery variables as Docker Compose doesn't handle certificates
            RemoveHttpsServiceDiscoveryVariables(context.EnvironmentVariables);
        }
    }

    private static void RemoveHttpsServiceDiscoveryVariables(Dictionary<string, object> environmentVariables)
    {
        var keysToRemove = environmentVariables
            .Where(kvp => kvp.Value is EndpointReference epRef && epRef.Scheme == "https" && kvp.Key.StartsWith("services__"))
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in keysToRemove)
        {
            environmentVariables.Remove(key);
        }
    }

    private static async Task ProcessArgumentsAsync(DockerComposeServiceResource serviceResource, DistributedApplicationExecutionContext executionContext, CancellationToken cancellationToken)
    {
        if (serviceResource.TargetResource.TryGetAnnotationsOfType<CommandLineArgsCallbackAnnotation>(out var commandLineArgsCallbacks))
        {
            var context = new CommandLineArgsCallbackContext(serviceResource.Args, serviceResource.TargetResource, cancellationToken: cancellationToken)
            {
                ExecutionContext = executionContext
            };

            foreach (var callback in commandLineArgsCallbacks)
            {
                await callback.Callback(context).ConfigureAwait(false);
            }
        }
    }
}
