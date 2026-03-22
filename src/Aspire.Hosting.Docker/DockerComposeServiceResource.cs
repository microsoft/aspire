// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES001

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Dcp.Process;
using Aspire.Hosting.Docker.Resources.ComposeNodes;
using Aspire.Hosting.Docker.Resources.ServiceNodes;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Utils;
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
        // If the resource has a Dockerfile build annotation, we don't have the image name
        // it will come as a parameter
        if (resourceInstance.TryGetLastAnnotation<DockerfileBuildAnnotation>(out _) || resourceInstance is ProjectResource)
        {
            containerImageName = this.AsContainerImagePlaceholder();
            return true;
        }

        return resourceInstance.TryGetContainerImageName(out containerImageName);
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
        var outputPath = PublishingContextUtils.GetEnvironmentOutputPath(context, environment);

        // No external endpoints configured - this is valid for internal-only services
        var externalEndpointMappings = EndpointMappings.Values.Where(m => m.IsExternal).ToList();
        if (externalEndpointMappings.Count == 0)
        {
            context.ReportingStep.Log(LogLevel.Information,
                new MarkdownString($"Successfully deployed **{TargetResource.Name}** to Docker Compose environment **{environment.Name}**. No public endpoints were configured."));
            context.Summary.Add(TargetResource.Name, "No public endpoints");
            return;
        }

        // Query the running container for its published ports
        var outputLines = await RunDockerComposePsAsync(context, environment, outputPath).ConfigureAwait(false);
        var endpoints = outputLines is not null
            ? ParseServiceEndpoints(outputLines, externalEndpointMappings, context.Logger)
            : [];

        if (endpoints.Count > 0)
        {
            var endpointList = string.Join(", ", endpoints.Select(e => $"[{e}]({e})"));
            context.ReportingStep.Log(LogLevel.Information, new MarkdownString($"Successfully deployed **{TargetResource.Name}** to {endpointList}."));
            context.Summary.Add(TargetResource.Name, string.Join(", ", endpoints));
        }
        else
        {
            // No published ports found in docker compose ps output.
            context.ReportingStep.Log(LogLevel.Information,
                new MarkdownString($"Successfully deployed **{TargetResource.Name}** to Docker Compose environment **{environment.Name}**."));
            context.Summary.Add(TargetResource.Name, "No public endpoints");
        }
    }

    /// <summary>
    /// Runs 'docker compose ps --format json' to get container status and port mappings.
    /// </summary>
    /// <returns>List of JSON output lines, or null if the command failed.</returns>
    private static async Task<List<string>?> RunDockerComposePsAsync(
        PipelineStepContext context,
        DockerComposeEnvironmentResource environment,
        string outputPath)
    {
        var arguments = DockerComposeEnvironmentResource.GetDockerComposeArguments(context, environment);
        arguments += " ps --format json";

        var outputLines = new List<string>();

        var spec = new ProcessSpec("docker")
        {
            Arguments = arguments,
            WorkingDirectory = outputPath,
            ThrowOnNonZeroReturnCode = false,
            InheritEnv = true,
            OnOutputData = output =>
            {
                if (!string.IsNullOrWhiteSpace(output))
                {
                    outputLines.Add(output);
                }
            },
            OnErrorData = error =>
            {
                if (!string.IsNullOrWhiteSpace(error))
                {
                    context.Logger.LogDebug("docker compose ps (stderr): {Error}", error);
                }
            }
        };

        var (pendingProcessResult, processDisposable) = ProcessUtil.Run(spec);

        await using (processDisposable)
        {
            var processResult = await pendingProcessResult
                .WaitAsync(context.CancellationToken)
                .ConfigureAwait(false);

            if (processResult.ExitCode != 0)
            {
                context.Logger.LogDebug("docker compose ps failed with exit code {ExitCode}", processResult.ExitCode);
                return null;
            }
        }

        return outputLines;
    }

    /// <summary>
    /// Parses the JSON output from 'docker compose ps' to extract endpoint URLs for this service.
    /// </summary>
    /// <example>
    /// Example JSON line from 'docker compose ps --format json':
    /// <code>
    /// {"Service":"myservice","State":"running","Publishers":[{"URL":"","TargetPort":80,"PublishedPort":8080,"Protocol":"tcp"}]}
    /// </code>
    /// Note: PublishedPort is 0 when the port is exposed but not mapped to the host.
    /// </example>
    private HashSet<string> ParseServiceEndpoints(
        List<string> outputLines,
        List<EndpointMapping> externalEndpointMappings,
        ILogger logger)
    {
        var endpoints = new HashSet<string>(StringComparers.EndpointAnnotationName);
        var serviceName = TargetResource.Name.ToLowerInvariant();

        foreach (var line in outputLines)
        {
            try
            {
                var serviceInfo = JsonSerializer.Deserialize(line, DockerComposeJsonContext.Default.DockerComposeServiceInfo);

                // Skip if not our service
                if (serviceInfo is null ||
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
                    // Match by internal port (numeric) or by exposed port
                    // InternalPort may be a placeholder like ${API_PORT} for projects, so also check ExposedPort
                    var targetPortStr = publisher.TargetPort?.ToString(CultureInfo.InvariantCulture);
                    var endpointMapping = externalEndpointMappings
                        .FirstOrDefault(m => m.InternalPort == targetPortStr || m.ExposedPort == publisher.TargetPort);

                    // If we found a matching endpoint, use its scheme; otherwise default to http for external ports
                    var scheme = endpointMapping.Scheme ?? "http";

                    // Only add if we found a matching external endpoint OR if scheme is http/https
                    // (published ports are external by definition in docker compose)
                    if (endpointMapping.IsExternal || scheme is "http" or "https")
                    {
                        var endpoint = $"{scheme}://localhost:{publisher.PublishedPort}";
                        endpoints.Add(endpoint);
                    }
                }
            }
            catch (JsonException ex)
            {
                logger.LogDebug(ex, "Failed to parse docker compose ps output line: {Line}", line);
            }
        }

        return endpoints;
    }

    /// <summary>
    /// Represents the JSON output from docker compose ps --format json.
    /// </summary>
    internal sealed class DockerComposeServiceInfo
    {
        public string? Service { get; set; }
        public List<DockerComposePublisher>? Publishers { get; set; }
    }

    /// <summary>
    /// Represents a port publisher in docker compose ps output.
    /// </summary>
    internal sealed class DockerComposePublisher
    {
        public int? PublishedPort { get; set; }
        public int? TargetPort { get; set; }
    }
}

[JsonSerializable(typeof(DockerComposeServiceResource.DockerComposeServiceInfo))]
internal sealed partial class DockerComposeJsonContext : JsonSerializerContext
{
}
