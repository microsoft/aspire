// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Foundry;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting;

/// <summary>
/// Extension methods for adding hosted agent applications to the distributed application model.
/// </summary>
public static class HostedAgentResourceBuilderExtensions
{
    private static readonly JsonSerializerOptions s_indentedJsonOptions = new() { WriteIndented = true };

    /// <summary>
    /// Configures the resource to run locally as a Microsoft Foundry hosted agent.
    /// </summary>
    /// <ats-summary>Configures the resource to run locally as a Microsoft Foundry hosted agent.</ats-summary>
    /// <typeparam name="T">The type of resource being configured.</typeparam>
    /// <param name="builder">The resource builder for the compute resource.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for chaining.</returns>
    /// <remarks>
    /// This method applies in run mode. It configures the resource with the hosted agent responses endpoint,
    /// a dashboard command for sending messages to the agent, and OpenTelemetry environment variables expected
    /// by the Microsoft Foundry agent server SDK.
    /// </remarks>
    /// <example>
    /// <code lang="csharp">
    /// var agent = builder.AddProject&lt;Projects.AgentService&gt;("agent")
    ///     .AsHostedAgent();
    /// </code>
    /// </example>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExportIgnore(Reason = "Subset of the full AsHostedAgent(project) overload which is exported.")]
    public static IResourceBuilder<T> AsHostedAgent<T>(this IResourceBuilder<T> builder)
        where T : IResourceWithEndpoints, IResourceWithEnvironment, IComputeResource
    {
        return AsHostedAgent(builder, project: null, configure: null);
    }

    // The internal AsHostedAgentForExport overload below is the polyglot-exported version of AsHostedAgent.
    // The CLR method name differs from AsHostedAgent to avoid C# overload ambiguity with the Action-based
    // overload, but the ATS capability name must stay "asHostedAgent" for compatibility.
    // .NET callers should keep using the Action<HostedAgentConfiguration> overload above, which exposes
    // the full HostedAgentConfiguration surface (tools, content filters, container protocol versions, etc.).

    /// <summary>
    /// Configures the resource to run and publish as a hosted agent in Microsoft Foundry, targeting the specified Foundry project.
    /// </summary>
    /// <typeparam name="T">The type of resource being configured.</typeparam>
    /// <param name="builder">The resource builder for the compute resource.</param>
    /// <param name="project">The Microsoft Foundry project the hosted agent is deployed into.</param>
    /// <param name="options">Optional hosted agent deployment options (description, CPU, memory, metadata, environment variables) applied in publish mode.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for chaining.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="builder"/> or <paramref name="project"/> is <see langword="null"/>.</exception>
    [AspireExport("asHostedAgent", MethodName = "asHostedAgent")]
    internal static IResourceBuilder<T> AsHostedAgentForExport<T>(
        this IResourceBuilder<T> builder,
        IResourceBuilder<AzureCognitiveServicesProjectResource> project,
        HostedAgentOptions? options = null)
        where T : IResourceWithEndpoints, IResourceWithEnvironment, IComputeResource
    {
        ArgumentNullException.ThrowIfNull(project);

        Action<HostedAgentConfiguration>? configure = options is null ? null : options.ApplyTo;
        return AsHostedAgent(builder, project: project, configure: configure);
    }

    /// <summary>
    /// Configures the resource to run and publish as a hosted agent in Microsoft Foundry, with full programmatic
    /// access to the underlying <see cref="HostedAgentConfiguration"/> (including Azure SDK-specific options
    /// such as tools and content filters).
    /// </summary>
    /// <typeparam name="T">The type of resource being configured.</typeparam>
    /// <param name="builder">The resource builder for the compute resource.</param>
    /// <param name="project">Optional Microsoft Foundry project resource used for both run and publish mode configuration. When <see langword="null"/>, an existing Foundry project in the model is reused or a new project is created in publish mode.</param>
    /// <param name="configure">A callback to configure hosted agent deployment options in publish mode.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for chaining.</returns>
    [AspireExportIgnore(Reason = "Action callback shape is awkward for polyglot hosts; the HostedAgentOptions overload is exported instead.")]
    public static IResourceBuilder<T> AsHostedAgent<T>(
        this IResourceBuilder<T> builder,
        IResourceBuilder<AzureCognitiveServicesProjectResource>? project,
        Action<HostedAgentConfiguration>? configure = null)
        where T : IResourceWithEndpoints, IResourceWithEnvironment, IComputeResource
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (builder.ApplicationBuilder.ExecutionContext.IsRunMode)
        {
            ConfigureRunMode(builder);

            if (project is not null)
            {
                AddProjectReferenceForRunMode(builder, project);
            }

            return builder;
        }

        var publishProject = project ?? ResolveProjectBuilderForPublish(builder);
        ConfigurePublishMode(builder, publishProject, configure);

        return builder;
    }

    /// <summary>
    /// Configures the resource to run and publish as a hosted agent in Microsoft Foundry, with full programmatic
    /// access to the underlying <see cref="HostedAgentConfiguration"/>. The Foundry project is resolved automatically
    /// in publish mode.
    /// </summary>
    /// <typeparam name="T">The type of resource being configured.</typeparam>
    /// <param name="builder">The resource builder for the compute resource.</param>
    /// <param name="configure">A callback to configure hosted agent deployment options in publish mode.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for chaining.</returns>
    [AspireExportIgnore(Reason = "Subset of the full AsHostedAgent overload.")]
    public static IResourceBuilder<T> AsHostedAgent<T>(
        this IResourceBuilder<T> builder,
        Action<HostedAgentConfiguration> configure)
        where T : IResourceWithEndpoints, IResourceWithEnvironment, IComputeResource
    {
        ArgumentNullException.ThrowIfNull(configure);
        return AsHostedAgent(builder, project: null, configure: configure);
    }

    private static void AddProjectReferenceForRunMode<T>(
        IResourceBuilder<T> builder,
        IResourceBuilder<AzureCognitiveServicesProjectResource> project)
        where T : IResourceWithEndpoints, IResourceWithEnvironment, IComputeResource
    {
        builder.WithReference(project);

        // The default ACR is required for publish-time image push, but in run mode it adds noise to the dashboard.
        // When a hosted agent references a Foundry project for local execution, remove the default registry resource.
        if (project.Resource.DefaultContainerRegistry is { } defaultRegistry)
        {
            builder.ApplicationBuilder.Resources.Remove(defaultRegistry);
            project.Resource.DefaultContainerRegistry = null;
        }
    }

    private static IResourceBuilder<AzureCognitiveServicesProjectResource> ResolveProjectBuilderForPublish<T>(IResourceBuilder<T> builder)
        where T : IResourceWithEndpoints, IResourceWithEnvironment, IComputeResource
    {
        if (builder.ApplicationBuilder.Resources.OfType<AzureCognitiveServicesProjectResource>().FirstOrDefault() is { } existingProject)
        {
            return builder.ApplicationBuilder.CreateResourceBuilder(existingProject);
        }

        return builder.ApplicationBuilder
            .AddFoundry($"{builder.Resource.Name}-proj-foundry")
            .AddProject($"{builder.Resource.Name}-proj");
    }

    private static void ConfigureRunMode<T>(IResourceBuilder<T> builder)
        where T : IResourceWithEndpoints, IResourceWithEnvironment, IComputeResource
    {
        // Preserve any target port already configured on an existing "http" endpoint;
        // fall back to the default MAF agent port (8088) when none is set.
        var existingHttpEndpoint = builder.Resource.Annotations.OfType<EndpointAnnotation>().FirstOrDefault(e => e.Name == "http");
        var targetPort = existingHttpEndpoint?.TargetPort ?? 8088;

        builder
            .WithIconName("Agents")
            .WithHttpEndpoint(name: "http", env: "DEFAULT_AD_PORT", targetPort: targetPort, isProxied: true)
            .WithUrls((ctx) =>
            {
                var http = ctx.Urls.FirstOrDefault(u => u.Endpoint?.EndpointName == "http" || u.Endpoint?.EndpointName == "https");
                if (http is null)
                {
                    return;
                }
                http.DisplayText = "Responses Endpoint";
                http.Url = new UriBuilder(http.Url)
                {
                    Path = "/responses"
                }.ToString();
            })
            .WithHttpCommand(
                path: "/responses",
                displayName: "Send Message",
                endpointName: "http",
                commandOptions: new()
                {
                    Method = HttpMethod.Post,
                    IconName = "ChatSparkle",
                    IconVariant = IconVariant.Regular,
                    IsHighlighted = true,
                    PrepareRequest = async ctx =>
                    {
                        var interactionService = ctx.ServiceProvider.GetRequiredService<IInteractionService>();
                        var result = await interactionService.PromptInputAsync(
                            title: "Responses API",
                            message: "Enter a message to send to the agent.",
                            inputLabel: "Message",
                            placeHolder: "I would like to know the weather today.",
                            cancellationToken: ctx.CancellationToken
                        ).ConfigureAwait(true);
                        if (result.Canceled || string.IsNullOrWhiteSpace(result.Data.Value))
                        {
                            ctx.HttpClient.CancelPendingRequests();
                            throw new OperationCanceledException("User canceled the input prompt.");
                        }
                        var request = ctx.Request;
                        var input = result.Data.Value;
                        request.Content = new StringContent(new JsonObject() { ["input"] = input }.ToString(), System.Text.Encoding.UTF8, "application/json");
                    },
                    GetCommandResult = async ctx =>
                    {
                        ctx.CancellationToken.ThrowIfCancellationRequested();

                        var response = ctx.Response;
                        if (!response.IsSuccessStatusCode)
                        {
                            var errorPayload = await response.Content.ReadAsStringAsync(ctx.CancellationToken).ConfigureAwait(true);
                            return CommandResults.Failure(
                                $"Agent request failed with status code {(int)response.StatusCode} ({response.StatusCode}).",
                                errorPayload,
                                CommandResultFormat.Text);
                        }

                        var responseJson = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: ctx.CancellationToken).ConfigureAwait(true);
                        if (responseJson is null)
                        {
                            return CommandResults.Failure("Agent returned an empty response.");
                        }

                        var formattedResponse = JsonSerializer.Serialize(responseJson, s_indentedJsonOptions);
                        return CommandResults.Success(
                            message: "Agent response received.",
                            result: formattedResponse,
                            resultFormat: CommandResultFormat.Json,
                            displayImmediately: true);
                    },
                }
            )
            .WithOtlpExporter()
            .WithEnvironment((ctx) =>
            {
                ctx.EnvironmentVariables.Add("OTEL_INSTRUMENTATION_OPENAI_AGENTS_ENABLED", "true");
                ctx.EnvironmentVariables.Add("OTEL_INSTRUMENTATION_OPENAI_AGENTS_CAPTURE_CONTENT", "true");
                ctx.EnvironmentVariables.Add("OTEL_INSTRUMENTATION_OPENAI_AGENTS_CAPTURE_METRICS", "true");
                ctx.EnvironmentVariables.Add("OTEL_GENAI_CAPTURE_MESSAGES", "true");
                ctx.EnvironmentVariables.Add("OTEL_GENAI_CAPTURE_SYSTEM_INSTRUCTIONS", "true");
                ctx.EnvironmentVariables.Add("OTEL_GENAI_CAPTURE_TOOL_DEFINITIONS", "true");
                ctx.EnvironmentVariables.Add("OTEL_GENAI_EMIT_OPERATION_DETAILS", "true");
                ctx.EnvironmentVariables.Add("OTEL_GENAI_AGENT_NAME", ctx.Resource.Name);
                ctx.EnvironmentVariables.Add("OTEL_GENAI_AGENT_ID", ctx.Resource.Name);
                var endpointVar = ctx.EnvironmentVariables.FirstOrDefault((item) => item.Key == "OTEL_EXPORTER_OTLP_ENDPOINT");
                if (endpointVar.Equals(default(KeyValuePair<string, string>)))
                {
                    return;
                }
                // The Microsoft Foundry agentserver SDK expects the exporter to be at OTEL_EXPORTER_ENDPOINT instead.
                ctx.EnvironmentVariables["OTEL_EXPORTER_ENDPOINT"] = endpointVar.Value;
            });
    }

    private static void ConfigurePublishMode<T>(
        IResourceBuilder<T> builder,
        IResourceBuilder<AzureCognitiveServicesProjectResource> project,
        Action<HostedAgentConfiguration>? configure)
        where T : IResourceWithEndpoints, IResourceWithEnvironment, IComputeResource
    {
        /*
         * Much of the logic here is similar to ExecutableResourceBuilderExtensions.PublishAsDockerFile().
         *
         * That is, in Publish mode, we swap the original resource with a hosted agent resource.
         */
        var resource = builder.Resource;
        var projectResource = project.Resource;

        if (!projectResource.HasAnnotationOfType<RequiresHostedAgentRegistryAnnotation>())
        {
            projectResource.Annotations.Add(new RequiresHostedAgentRegistryAnnotation());
        }

        ResourceBuilderExtensions.WithComputeEnvironment(builder, project);

        // Hosted Agent resource name
        var agentName = $"{resource.Name}-ha";
        if (builder.ApplicationBuilder.TryCreateResourceBuilder<AzureHostedAgentResource>(agentName, out var existingHostedAgent))
        {
            // We already have a hosted agent for this resource
            if (configure is not null)
            {
                existingHostedAgent.Resource.Configure = configure;
            }
            return;
        }

        // Get the corresponding ContainerResource for ExecutableResources. Usually this is swapped in at publish time for ExecutableResources.
        IResourceWithEnvironment target;
        if (resource is ContainerResource containerResource)
        {
            target = containerResource;
        }
        else if (builder.ApplicationBuilder.TryCreateResourceBuilder<ContainerResource>(resource.Name, out var containerResourceBuilder))
        {
            target = containerResourceBuilder.Resource;
        }
        else if (resource is ExecutableResource executableResource)
        {
            // Ensure we have a container resource to deploy.
            // ExecutableResource needs PublishAsDockerFile() to convert it into a container resource at this stage.
            builder.ApplicationBuilder.CreateResourceBuilder(executableResource)
                .PublishAsDockerFile();

            if (builder.ApplicationBuilder.TryCreateResourceBuilder(resource.Name, out containerResourceBuilder))
            {
                target = containerResourceBuilder.Resource;
            }
            else
            {
                throw new InvalidOperationException($"Unable to create hosted agent for resource '{resource.Name}' because it could not be converted to a container resource.");
            }
        }
        else if (resource is ProjectResource)
        {
            target = resource;
        }
        else
        {
            throw new InvalidOperationException($"Unable to create hosted agent for resource '{resource.Name}' because it is not a container, executable, or project resource.");
        }

        // The hosted agent wrapper is not the deployed workload. Apply the Foundry
        // reference to the target so its connection annotations flow into the deployment.
        builder.ApplicationBuilder.CreateResourceBuilder(target)
            .WithReference(project);

        // Create a separate agent resource to host the deployment.
        var hostedAgent = new AzureHostedAgentResource(agentName, target, configure);

        // Ensure image gets pushed properly.
        target.Annotations.Add(new DeploymentTargetAnnotation(hostedAgent)
        {
            ComputeEnvironment = projectResource,
            ContainerRegistry = projectResource.ContainerRegistry
        });

        builder.ApplicationBuilder.AddResource(hostedAgent)
            .WithIconName("Agents")
            .WithReferenceRelationship(target);
    }
}
