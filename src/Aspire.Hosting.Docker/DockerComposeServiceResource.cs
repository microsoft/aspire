// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIRECONTAINERRUNTIME001

using System.Globalization;
using System.Text;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Docker.Resources.ComposeNodes;
using Aspire.Hosting.Docker.Resources.ServiceNodes;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Publishing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Docker;

/// <summary>
/// Represents a compute resource for Docker Compose with strongly-typed properties.
/// </summary>
[AspireExport(ExposeProperties = true)]
public class DockerComposeServiceResource : Resource, IResourceWithParent<DockerComposeEnvironmentResource>
{
    private readonly IResource _targetResource;
    private readonly DockerComposeEnvironmentResource _composeEnvironmentResource;

    /// <summary>
    /// Initializes a new instance of the <see cref="DockerComposeServiceResource"/> class.
    /// </summary>
    /// <param name="name">The name of the resource.</param>
    /// <param name="resource">The target resource.</param>
    /// <param name="composeEnvironmentResource">The Docker Compose environment resource.</param>
    public DockerComposeServiceResource(string name, IResource resource, DockerComposeEnvironmentResource composeEnvironmentResource) : base(name)
    {
        _targetResource = resource;
        _composeEnvironmentResource = composeEnvironmentResource;

        // Add pipeline step annotation to display endpoints after deployment
        Annotations.Add(new PipelineStepAnnotation(_ =>
        {
            var steps = new List<PipelineStep>();

            var printResourceSummary = new PipelineStep
            {
                Name = $"print-{_targetResource.Name}-summary",
                Action = async ctx => await PrintEndpointsAsync(ctx, _composeEnvironmentResource).ConfigureAwait(false),
                Tags = ["print-summary"],
                RequiredBySteps = [WellKnownPipelineSteps.Deploy]
            };

            steps.Add(printResourceSummary);

            return steps;
        }));
    }
    /// <summary>
    /// Most common shell executables used as container entrypoints in Linux containers.
    /// These are used to identify when a container's entrypoint is a shell that will execute commands.
    /// </summary>
    private static readonly HashSet<string> s_shellExecutables = new(StringComparer.OrdinalIgnoreCase)
        {
            "/bin/sh",
            "/bin/bash",
            "/sh",
            "/bash",
            "sh",
            "bash",
            "/usr/bin/sh",
            "/usr/bin/bash"
        };

    internal bool IsShellExec { get; private set; }

    internal record struct EndpointMapping(
        IResource Resource,
        string Scheme,
        string Host,
        string InternalPort,
        int? ExposedPort,
        bool IsExternal,
        string EndpointName);

    /// <summary>
    /// Gets the resource that is the target of this Docker Compose service.
    /// </summary>
    internal IResource TargetResource => _targetResource;

    /// <summary>
    /// Gets the collection of environment variables for the Docker Compose service.
    /// </summary>
    internal Dictionary<string, object> EnvironmentVariables { get; } = [];

    /// <summary>
    /// Gets the collection of commands to be executed by the Docker Compose service.
    /// </summary>
    internal List<object> Args { get; } = [];

    /// <summary>
    /// Gets the collection of volumes for the Docker Compose service.
    /// </summary>
    internal List<Volume> Volumes { get; } = [];

    /// <summary>
    /// Gets the mapping of endpoint names to their configurations.
    /// </summary>
    internal Dictionary<string, EndpointMapping> EndpointMappings { get; } = [];

    /// <inheritdoc/>
    public DockerComposeEnvironmentResource Parent => _composeEnvironmentResource;

    internal async Task<Service> BuildComposeServiceAsync()
    {
        var composeService = new Service
        {
            Name = TargetResource.Name.ToLowerInvariant(),
        };

        if (TryGetContainerImageName(TargetResource, out var containerImageName))
        {
            SetContainerImage(containerImageName, composeService);
        }

        SetContainerName(composeService);
        SetEntryPoint(composeService);
        SetPullPolicy(composeService);
        await AddEnvironmentVariablesAndCommandLineArgsAsync(composeService).ConfigureAwait(false);
        AddPorts(composeService);
        AddVolumes(composeService);
        SetDependsOn(composeService);
        return composeService;
    }

    private bool TryGetContainerImageName(IResource resourceInstance, out string? containerImageName)
    {
        // Try to get the container image name from the resource's image configuration
        // (e.g., DockerfileBuildAnnotation image name, ContainerImageAnnotation, etc.)
        if (resourceInstance.TryGetContainerImageName(out containerImageName))
        {
            return true;
        }

        // Use ContainerImageReference as the fallback for resources that will be built
        if (resourceInstance.TryGetLastAnnotation<DockerfileBuildAnnotation>(out _) || resourceInstance is ProjectResource)
        {
            containerImageName = this.AsContainerImagePlaceholder();
            return true;
        }

        return false;
    }

    private void SetContainerName(Service composeService)
    {
        if (TargetResource.TryGetLastAnnotation<ContainerNameAnnotation>(out var containerNameAnnotation))
        {
            composeService.ContainerName = containerNameAnnotation.Name;
        }
    }

    private void SetEntryPoint(Service composeService)
    {
        if (TargetResource is ContainerResource { Entrypoint: { } entrypoint })
        {
            composeService.Entrypoint.Add(entrypoint);

            if (s_shellExecutables.Contains(entrypoint))
            {
                IsShellExec = true;
            }
        }
    }

    private void SetPullPolicy(Service composeService)
    {
        if (TargetResource.TryGetLastAnnotation<ContainerImagePullPolicyAnnotation>(out var pullPolicyAnnotation))
        {
            composeService.PullPolicy = pullPolicyAnnotation.ImagePullPolicy switch
            {
                ImagePullPolicy.Always => "always",
                ImagePullPolicy.Missing => "missing",
                ImagePullPolicy.Never => "never",
                // Default means use the runtime's default, so we don't set it
                _ => null
            };
        }
    }

    private void SetDependsOn(Service composeService)
    {
        if (TargetResource.TryGetAnnotationsOfType<WaitAnnotation>(out var waitAnnotations))
        {
            foreach (var waitAnnotation in waitAnnotations)
            {
                // We can only wait on other compose services
                if (waitAnnotation.Resource is ProjectResource || waitAnnotation.Resource.IsContainer())
                {
                    // https://docs.docker.com/compose/how-tos/startup-order/#control-startup
                    composeService.DependsOn[waitAnnotation.Resource.Name.ToLowerInvariant()] = new()
                    {
                        Condition = waitAnnotation.WaitType switch
                        {
                            // REVIEW: This only works if the target service has health checks,
                            // revisit this when we have a way to add health checks to the compose service
                            // WaitType.WaitUntilHealthy => "service_healthy",
                            WaitType.WaitForCompletion => "service_completed_successfully",
                            _ => "service_started",
                        },
                    };
                }
            }
        }
    }

    private static void SetContainerImage(string? containerImageName, Service composeService)
    {
        if (containerImageName is not null)
        {
            composeService.Image = containerImageName;
        }
    }

    private async Task AddEnvironmentVariablesAndCommandLineArgsAsync(Service composeService)
    {
        var env = new Dictionary<string, string>();

        foreach (var kv in EnvironmentVariables)
        {
            var value = await this.ProcessValueAsync(kv.Value).ConfigureAwait(false);

            env[kv.Key] = value?.ToString() ?? string.Empty;
        }

        if (env.Count > 0)
        {
            foreach (var variable in env)
            {
                composeService.AddEnvironmentalVariable(variable.Key, variable.Value);
            }
        }

        var args = new List<string>();

        foreach (var arg in Args)
        {
            var value = await this.ProcessValueAsync(arg).ConfigureAwait(false);

            if (value is not string str)
            {
                throw new NotSupportedException("Command line args must be strings");
            }

            args.Add(str);
        }

        if (args.Count > 0)
        {
            if (IsShellExec)
            {
                var sb = new StringBuilder();
                foreach (var command in args)
                {
                    // Escape any environment variables expressions in the command
                    // to prevent them from being interpreted by the docker compose CLI
                    EnvVarEscaper.EscapeUnescapedEnvVars(command, sb);
                    composeService.Command.Add(sb.ToString());
                    sb.Clear();
                }
            }
            else
            {
                composeService.Command.AddRange(args);
            }
        }
    }

    private void AddPorts(Service composeService)
    {
        if (EndpointMappings.Count == 0)
        {
            return;
        }

        var ports = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var expose = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (_, mapping) in EndpointMappings)
        {
            // Resolve the internal port for the endpoint mapping
            var internalPort = mapping.InternalPort;

            if (mapping.IsExternal)
            {
                var exposedPort = mapping.ExposedPort?.ToString(CultureInfo.InvariantCulture);

                // No explicit exposed port, let docker compose assign a random port
                if (exposedPort is null)
                {
                    ports.Add(internalPort);
                }
                else
                {
                    // Explicit exposed port, map it to the internal port
                    ports.Add($"{exposedPort}:{internalPort}");
                }
            }
            else
            {
                // Internal endpoints use expose with just internalPort
                expose.Add(internalPort);
            }
        }

        composeService.Ports.AddRange(ports);
        composeService.Expose.AddRange(expose);
    }

    private void AddVolumes(Service composeService)
    {
        if (Volumes.Count == 0)
        {
            return;
        }

        foreach (var volume in Volumes)
        {
            composeService.AddVolume(volume);
        }
    }

    private async Task PrintEndpointsAsync(PipelineStepContext context, DockerComposeEnvironmentResource environment)
    {
        // No external endpoints configured - this is valid for internal-only services
        var externalEndpointMappings = EndpointMappings.Values.Where(m => m.IsExternal).ToList();
        if (externalEndpointMappings.Count == 0)
        {
            context.ReportingStep.Log(LogLevel.Information,
                new MarkdownString($"Successfully deployed **{TargetResource.Name}** to Docker Compose environment **{environment.Name}**. No public endpoints were configured."));
            context.Summary.Add(TargetResource.Name, "No public endpoints");
            return;
        }

        // Query the running containers for published ports
        var runtime = await context.Services.GetRequiredService<IContainerRuntimeResolver>().ResolveAsync(context.CancellationToken).ConfigureAwait(false);
        var composeContext = environment.CreateComposeOperationContext(context);
        var services = await runtime.ComposeListServicesAsync(composeContext, context.CancellationToken).ConfigureAwait(false);

        var endpoints = services is not null
            ? ParseServiceEndpoints(services, externalEndpointMappings, context.Logger)
            : [];

        if (endpoints.Count > 0)
        {
            var endpointList = string.Join(", ", endpoints.Select(e => $"[{e}]({e})"));
            context.ReportingStep.Log(LogLevel.Information, new MarkdownString($"Successfully deployed **{TargetResource.Name}** to {endpointList}."));
            context.Summary.Add(TargetResource.Name, string.Join(", ", endpoints));
        }
        else
        {
            // No published ports found in compose output.
            context.ReportingStep.Log(LogLevel.Information,
                new MarkdownString($"Successfully deployed **{TargetResource.Name}** to Docker Compose environment **{environment.Name}**."));
            context.Summary.Add(TargetResource.Name, "No public endpoints");
        }
    }

    /// <summary>
    /// Extracts endpoint URLs from compose service info, matching against configured external endpoint mappings.
    /// </summary>
    private HashSet<string> ParseServiceEndpoints(
        IReadOnlyList<ComposeServiceInfo> services,
        List<EndpointMapping> externalEndpointMappings,
        ILogger _)
    {
        var endpoints = new HashSet<string>(StringComparers.EndpointAnnotationName);
        var serviceName = TargetResource.Name.ToLowerInvariant();

        foreach (var serviceInfo in services)
        {
            // Skip if not our service
            if (serviceInfo.Service is null ||
                !string.Equals(serviceInfo.Service, serviceName, StringComparisons.ResourceName))
            {
                continue;
            }

            // Skip if no published ports
            if (serviceInfo.Publishers is not { Count: > 0 })
            {
                continue;
            }

            foreach (var publisher in serviceInfo.Publishers)
            {
                // Skip ports that aren't actually published (port 0 or null means not exposed)
                if (publisher.PublishedPort is not > 0)
                {
                    continue;
                }

                // Try to find a matching external endpoint to get the scheme
                var targetPortStr = publisher.TargetPort?.ToString(CultureInfo.InvariantCulture);
                var endpointMapping = externalEndpointMappings
                    .FirstOrDefault(m => m.InternalPort == targetPortStr || m.ExposedPort == publisher.TargetPort);

                var scheme = endpointMapping.Scheme ?? "http";

                if (endpointMapping.IsExternal || scheme is "http" or "https")
                {
                    var endpoint = $"{scheme}://localhost:{publisher.PublishedPort}";
                    endpoints.Add(endpoint);
                }
            }
        }

        return endpoints;
    }

}
