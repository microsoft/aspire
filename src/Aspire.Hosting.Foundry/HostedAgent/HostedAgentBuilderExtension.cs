// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;
using Aspire.Hosting.Foundry;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting;

/// <summary>
/// Extension methods for adding hosted agent applications to the distributed application model.
/// </summary>
public static class HostedAgentResourceBuilderExtensions
{
    /// <summary>
    /// In both run and publish modes, build, deploy, and run the containerized agent as a hosted agent in Microsoft Foundry.
    /// </summary>
    /// <remarks>This overload is not available in polyglot app hosts because run-mode hosted agents are not yet implemented.</remarks>
    [AspireExportIgnore(Reason = "RunAsHostedAgent is not yet implemented, so AsHostedAgent is not available in polyglot hosts.")]
    public static IResourceBuilder<T> AsHostedAgent<T>(
        this IResourceBuilder<T> builder, Action<HostedAgentConfiguration>? configure = null)
        where T : ExecutableResource
    {
        return builder.AsHostedAgent(project: null, configure: configure);
    }

    /// <summary>
    /// In both run and publish modes, build, deploy, and run the containerized agent as a hosted agent in Microsoft Foundry.
    /// </summary>
    /// <remarks>This overload is not available in polyglot app hosts because run-mode hosted agents are not yet implemented.</remarks>
    [AspireExportIgnore(Reason = "RunAsHostedAgent is not yet implemented, so AsHostedAgent is not available in polyglot hosts.")]
    public static IResourceBuilder<T> AsHostedAgent<T>(
        this IResourceBuilder<T> builder, IResourceBuilder<AzureCognitiveServicesProjectResource>? project = null, Action<HostedAgentConfiguration>? configure = null)
        where T : ExecutableResource
    {
        return builder.RunAsHostedAgent(project: project, configure: configure).PublishAsHostedAgent(project: project, configure: configure);
    }

    /// <summary>
    /// In run mode, build, deploy, and run the containerized agent as a hosted agent in Microsoft Foundry.
    /// </summary>
    /// <remarks>This overload is not available in polyglot app hosts because run-mode hosted agents are not yet implemented.</remarks>
    [AspireExportIgnore(Reason = "RunAsHostedAgent is not yet implemented.")]
    public static IResourceBuilder<T> RunAsHostedAgent<T>(
        this IResourceBuilder<T> builder, Action<HostedAgentConfiguration> configure)
        where T : ExecutableResource
    {
        return builder.RunAsHostedAgent(project: null, configure: configure);
    }

    /// <summary>
    /// In run mode, build, deploy, and run the containerized agent as a hosted agent in Microsoft Foundry.
    /// </summary>
    /// <remarks>This overload is not available in polyglot app hosts because run-mode hosted agents are not yet implemented.</remarks>
    [AspireExportIgnore(Reason = "RunAsHostedAgent is not yet implemented.")]
    public static IResourceBuilder<T> RunAsHostedAgent<T>(
        this IResourceBuilder<T> builder, IResourceBuilder<AzureCognitiveServicesProjectResource>? project = null, Action<HostedAgentConfiguration>? configure = null)
        where T : ExecutableResource
    {
        // TODO: Implement this. This will require
        // 1. Ensuring that ACR is provisioned
        // 2. Building and pushing the container image
        // 3. Creating an agent version and returning the name/version of the agent for later use.
        throw new NotImplementedException("RunAsHostedAgent is not yet implemented.");
    }

    /// <summary>
    /// Publish the containerized agent as a hosted agent in Microsoft Foundry.
    ///
    /// If a project resource is not provided, the method will attempt to find an existing
    /// Microsoft Foundry project resource in the application model. If none exists,
    /// a new project resource (and its parent account resource) will be created automatically.
    /// </summary>
    [AspireExportIgnore(Reason = "Subset of the full PublishAsHostedAgent overload which is exported.")]
    public static IResourceBuilder<T> PublishAsHostedAgent<T>(
        this IResourceBuilder<T> builder, Action<HostedAgentConfiguration> configure)
        where T : IResourceWithEndpoints, IResourceWithEnvironment, IComputeResource
    {
        return PublishAsHostedAgent(builder, project: null, configure: configure);
    }

    /// <summary>
    /// Publish the containerized agent as a hosted agent in Microsoft Foundry.
    ///
    /// If a project resource is not provided, the method will attempt to find an existing
    /// Microsoft Foundry project resource in the application model. If none exists,
    /// a new project resource (and its parent account resource) will be created automatically.
    /// </summary>
    [AspireExport("publishAsHostedAgentExecutable", MethodName = "publishAsHostedAgent", Description = "Publishes an executable resource as a hosted agent in Microsoft Foundry.")]
    public static IResourceBuilder<T> PublishAsHostedAgent<T>(
        this IResourceBuilder<T> builder, IResourceBuilder<AzureCognitiveServicesProjectResource>? project = null, Action<HostedAgentConfiguration>? configure = null)
        where T : IResourceWithEndpoints, IResourceWithEnvironment, IComputeResource
    {
        /*
         * Much of the logic here is similar to ExecutableResourceBuilderExtensions.PublishAsDockerFile().
         *
         * That is, in Publish mode, we swap the original resource with a hosted agent resource.
         */
        ArgumentNullException.ThrowIfNull(builder);

        var resource = builder.Resource;

        if (builder.ApplicationBuilder.ExecutionContext.IsRunMode)
        {
            // Preserve any target port already configured on an existing "http" endpoint;
            // fall back to the default MAF agent port (8088) when none is set.
            var existingHttpEndpoint = resource.Annotations.OfType<EndpointAnnotation>().FirstOrDefault(e => e.Name == "http");
            var targetPort = existingHttpEndpoint?.TargetPort ?? 8088;

            builder
                .WithHttpEndpoint(name: "http", env: "DEFAULT_AD_PORT", targetPort: targetPort)
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
                    ctx.Urls.Add(new()
                    {
                        DisplayText = "Liveness probe",
                        Url = new UriBuilder(http.Url)
                        {
                            Path = "/liveness"
                        }.ToString(),
                        Endpoint = http.Endpoint,
                        DisplayLocation = UrlDisplayLocation.DetailsOnly
                    });
                    ctx.Urls.Add(new()
                    {
                        DisplayText = "Readiness probe",
                        Url = new UriBuilder(http.Url)
                        {
                            Path = "/readiness"
                        }.ToString(),
                        Endpoint = http.Endpoint,
                        DisplayLocation = UrlDisplayLocation.DetailsOnly
                    });
                })
                .WithHttpHealthCheck("/liveness")
                .WithHttpCommand(
                    path: "/responses",
                    displayName: "Send Message",
                    endpointName: "http",
                    commandOptions: new()
                    {
                        Method = HttpMethod.Post,
                        IconName = "Agents",
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
                            try
                            {
                                var response = await ctx.Response
                                    .EnsureSuccessStatusCode()
                                    .Content
                                    .ReadFromJsonAsync<JsonObject>(cancellationToken: ctx.CancellationToken)
                                    .ConfigureAwait(true);
                                var formattedResponse = $"```\n{JsonSerializer.Serialize(response, new JsonSerializerOptions { WriteIndented = true })}\n```";
                                var interactionService = ctx.ServiceProvider.GetRequiredService<IInteractionService>();
                                await interactionService.PromptMessageBoxAsync(
                                    title: "Agent Response",
                                    message: formattedResponse,
                                    options: new()
                                    {
                                        Intent = MessageIntent.Success,
                                        EnableMessageMarkdown = true,
                                        PrimaryButtonText = "Thanks!"
                                    },
                                    cancellationToken: ctx.CancellationToken
                                ).ConfigureAwait(true);
                                return new() { Success = true };
                            }
                            catch (Exception ex)
                            {
                                var interactionService = ctx.ServiceProvider.GetRequiredService<IInteractionService>();
                                await interactionService.PromptMessageBoxAsync(
                                    title: "Error",
                                    message: $"An error occurred while processing the agent's response: {ex.Message}",
                                    options: new()
                                    {
                                        Intent = MessageIntent.Error,
                                        PrimaryButtonText = "OK"
                                    },
                                    cancellationToken: ctx.CancellationToken
                                ).ConfigureAwait(true);
                                Console.Error.Write($"Error processing agent response: {ex}");
                                return new() { Success = false };
                            }
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
            return builder;
        }
        AzureCognitiveServicesProjectResource? projResource;
        if (project is not null)
        {
            projResource = project.Resource;
        }
        else
        {
            projResource = builder.ApplicationBuilder.Resources.OfType<AzureCognitiveServicesProjectResource>().FirstOrDefault();
            if (projResource is null)
            {
                project = builder.ApplicationBuilder
                    .AddFoundry($"{resource.Name}-proj-foundry")
                    .AddProject($"{resource.Name}-proj");
                projResource = project.Resource;
            }
            else
            {
                project = builder.ApplicationBuilder.CreateResourceBuilder(projResource);
            }
        }

        builder.WithComputeEnvironment(project);

        // Hosted Agent resource name
        var agentName = $"{resource.Name}-ha";
        if (builder.ApplicationBuilder.TryCreateResourceBuilder<AzureHostedAgentResource>(agentName, out var rb))
        {
            // We already have a hosted agent for this resource
            if (configure is not null)
            {
                rb.Resource.Configure = configure;
            }
            return builder;
        }
        // Get the corresponding ContainerResource for ExecutableResources. Usually this is swapped in at publish time for ExecutableResources.
        IResource target;
        if (resource is ContainerResource containerResource)
        {
            target = containerResource;
        }
        else if (builder.ApplicationBuilder.TryCreateResourceBuilder<ContainerResource>(resource.Name, out var crb))
        {
            target = crb.Resource;
        }
        else
        {
            // Ensure we have a container resource to deploy.
            // ExecutableResource needs PublishAsDockerFile()
            // to convert them into container resources at this stage.
            if (resource is ExecutableResource)
            {
                builder.ApplicationBuilder.CreateResourceBuilder((ExecutableResource)(object)resource).PublishAsDockerFile();

                if (builder.ApplicationBuilder.TryCreateResourceBuilder(resource.Name, out crb))
                {
                    target = crb.Resource;
                }
                else
                {
                    throw new InvalidOperationException($"Unable to create hosted agent for resource '{resource.Name}' because it could not be converted to a container resource.");
                }
            }
            else if (resource is not ProjectResource)
            {
                throw new InvalidOperationException($"Unable to create hosted agent for resource '{resource.Name}' because it is not a container, executable, or project resource.");
            }
            else
            {
                target = resource;
            }
        }

        // Create a separate agent resource to host the deployment
        var agent = new AzureHostedAgentResource(agentName, target, configure);

        // Ensure image gets pushed properly
        target.Annotations.Add(new DeploymentTargetAnnotation(agent)
        {
            ComputeEnvironment = projResource,
            ContainerRegistry = projResource.ContainerRegistry
        });

        builder.ApplicationBuilder.CreateResourceBuilder(target)
            .WithReferenceRelationship(projResource);

        // Hosted agents deploy from the target compute resource, so the target needs the project role assignment.
        if ((!target.TryGetAnnotationsOfType<RoleAssignmentAnnotation>(out var roleAssignments) ||
            !roleAssignments.Any(a => ReferenceEquals(a.Target, projResource))) &&
            projResource.TryGetLastAnnotation<DefaultRoleAssignmentsAnnotation>(out var defaultRoleAssignments))
        {
            target.Annotations.Add(new RoleAssignmentAnnotation(projResource, defaultRoleAssignments.Roles));
        }

        builder.ApplicationBuilder.AddResource(agent)
            .WithReferenceRelationship(target)
            .WithReference(project);

        return builder;
    }
}
