// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Dcp;
using Aspire.Hosting.Orchestrator;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting;

/// <summary>
/// Extension methods for configuring a Blazor Web App (hosted model) to proxy
/// service calls and telemetry from its WebAssembly client.
/// </summary>
[Experimental("ASPIREBLAZOR001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public static class BlazorHostedExtensions
{
    /// <summary>
    /// Configures the host to proxy requests from the WebAssembly client to the specified service.
    /// The WASM client can reach this service via <c>/{apiPrefix}/{serviceName}/{path}</c>.
    /// YARP routes and clusters are emitted as environment variables.
    /// A <c>/_blazor/_configuration</c> response is built so the WASM client gets the proxy URL.
    /// This is an explicit opt-in — <c>WithReference</c> makes the service available to the server,
    /// while <c>ProxyBlazorService</c> additionally makes it available to the WASM client.
    /// </summary>
    /// <param name="host">The host resource builder.</param>
    /// <param name="service">The service to proxy.</param>
    /// <param name="apiPrefix">The URL path prefix for API proxy routes. Defaults to <c>"_api"</c>.</param>
    [AspireExportIgnore(Reason = "Blazor hosted APIs are not yet stable for ATS export.")]
    public static IResourceBuilder<ProjectResource> ProxyBlazorService(
        this IResourceBuilder<ProjectResource> host,
        IResourceBuilder<IResourceWithServiceDiscovery> service,
        string apiPrefix = GatewayConfigurationBuilder.DefaultApiPrefix)
    {
        var annotation = GetOrAddHostedClientAnnotation(host.Resource);
        annotation.Services.Add(new HostedClientService(service.Resource.Name, apiPrefix));

        // Forward the service reference to the host so YARP can resolve it via service discovery.
        var existingRefs = GetReferencedResourceNames(host.Resource);
        if (!existingRefs.Contains(service.Resource.Name))
        {
            host.WithReference(service);
        }

        EnsureEnvironmentCallback(host, annotation);

        return host;
    }

    /// <summary>
    /// Configures the host to proxy OpenTelemetry data from the WebAssembly client to the Aspire dashboard.
    /// The WASM client sends OTLP data to <c>/{otlpPrefix}/{path}</c> which gets forwarded to the dashboard.
    /// Also sets the <c>OTEL_SERVICE_NAME</c> in the client configuration so telemetry from the
    /// WASM client appears with the correct service name in the dashboard.
    /// </summary>
    /// <param name="host">The host resource builder.</param>
    /// <param name="otlpPrefix">The URL path prefix for OTLP proxy routes. Defaults to <c>"_otlp"</c>.</param>
    [AspireExportIgnore(Reason = "Blazor hosted APIs are not yet stable for ATS export.")]
    public static IResourceBuilder<ProjectResource> ProxyBlazorTelemetry(
        this IResourceBuilder<ProjectResource> host,
        string otlpPrefix = GatewayConfigurationBuilder.DefaultOtlpPrefix)
    {
        var annotation = GetOrAddHostedClientAnnotation(host.Resource);
        annotation.ProxyBlazorTelemetry = true;
        annotation.OtlpPrefix = otlpPrefix;

        EnsureEnvironmentCallback(host, annotation);

        return host;
    }

    /// <summary>
    /// Enables WebAssembly debugging for a hosted Blazor client project. Adds a
    /// <see cref="BrowserDebugAnnotation"/> so DCP creates an <c>IdeSession</c> resource
    /// for the WASM client, and registers a "Debug in Browser" command on the host resource.
    /// </summary>
    /// <typeparam name="TClientProject">The client project metadata type (from the .Client project).</typeparam>
    /// <param name="host">The host resource builder.</param>
    [AspireExportIgnore(Reason = "Blazor hosted APIs are not yet stable for ATS export.")]
    public static IResourceBuilder<ProjectResource> ProxyWasmDebugging<TClientProject>(
        this IResourceBuilder<ProjectResource> host)
        where TClientProject : IProjectMetadata, new()
    {
        if (host.ApplicationBuilder.ExecutionContext.IsPublishMode)
        {
            return host;
        }

        var clientMetadata = new TClientProject();
        return host.ProxyWasmDebugging(clientMetadata.ProjectPath);
    }

    /// <summary>
    /// Enables WebAssembly debugging for a hosted Blazor client project. Adds a
    /// <see cref="BrowserDebugAnnotation"/> so DCP creates an <c>IdeSession</c> resource
    /// for the WASM client, and registers a "Debug in Browser" command on the host resource.
    /// </summary>
    /// <param name="host">The host resource builder.</param>
    /// <param name="clientProjectPath">Path to the WASM client .csproj file (absolute or relative to AppHost directory).</param>
    [AspireExportIgnore(Reason = "Blazor hosted APIs are not yet stable for ATS export.")]
    public static IResourceBuilder<ProjectResource> ProxyWasmDebugging(
        this IResourceBuilder<ProjectResource> host,
        string clientProjectPath)
    {
        if (host.ApplicationBuilder.ExecutionContext.IsPublishMode)
        {
            return host;
        }

        var resolvedPath = Path.IsPathRooted(clientProjectPath)
            ? clientProjectPath
            : Path.GetFullPath(Path.Combine(host.ApplicationBuilder.AppHostDirectory, clientProjectPath));

        // The app URL for a hosted scenario is the host's own endpoint (no path prefix needed).
        var debugAnnotation = new BrowserDebugAnnotation(resolvedPath);
        host.WithAnnotation(debugAnnotation);

        // Register "Debug in Browser" command on the host resource.
        host.WithCommand(
            name: "debug-in-browser",
            displayName: "Debug in Browser (WASM)",
            executeCommand: async context =>
            {
                var sessionName = debugAnnotation.IdeSessionName;
                if (sessionName is null)
                {
                    return new ExecuteCommandResult { Success = false, Message = "Debug session has not been initialized yet." };
                }

                var orchestrator = context.ServiceProvider.GetRequiredService<ApplicationOrchestrator>();
                await orchestrator.LaunchBrowserDebugSessionAsync(sessionName, context.CancellationToken).ConfigureAwait(false);
                return CommandResults.Success();
            },
            commandOptions: new()
            {
                UpdateState = ctx =>
                {
                    var configuration = ctx.ServiceProvider.GetRequiredService<IConfiguration>();
                    if (string.IsNullOrEmpty(configuration[DcpExecutor.DebugSessionPortVar]))
                    {
                        return ResourceCommandState.Hidden;
                    }

                    return ctx.ResourceSnapshot.State?.Text == KnownResourceStates.Running
                        ? ResourceCommandState.Enabled
                        : ResourceCommandState.Disabled;
                },
                IconName = "BugArrowCounterclockwise",
                IsHighlighted = true
            });

        return host;
    }

    private static void EnsureEnvironmentCallback(
        IResourceBuilder<ProjectResource> host,
        HostedClientAnnotation annotation)
    {
        if (annotation.IsInitialized)
        {
            return;
        }

        annotation.IsInitialized = true;

        // Register "Debug in Browser" command for the hosted WASM client automatically,
        // unless ProxyWasmDebugging was already called explicitly (which adds its own annotation).
        // In the hosted model, the server project hosts the WASM client — use the server's
        // project path so the IDE can resolve the client assembly from its references.
        if (!host.ApplicationBuilder.ExecutionContext.IsPublishMode
            && !host.Resource.TryGetAnnotationsOfType<BrowserDebugAnnotation>(out _))
        {
            var projectMetadata = host.Resource.GetProjectMetadata();
            var debugAnnotation = new BrowserDebugAnnotation(projectMetadata.ProjectPath);
            host.WithAnnotation(debugAnnotation);

            host.WithCommand(
                name: "debug-in-browser",
                displayName: "Debug in Browser (WASM)",
                executeCommand: async context =>
                {
                    var sessionName = debugAnnotation.IdeSessionName;
                    if (sessionName is null)
                    {
                        return new ExecuteCommandResult { Success = false, Message = "Debug session has not been initialized yet." };
                    }

                    var orch = context.ServiceProvider.GetRequiredService<ApplicationOrchestrator>();
                    await orch.LaunchBrowserDebugSessionAsync(sessionName, context.CancellationToken).ConfigureAwait(false);
                    return CommandResults.Success();
                },
                commandOptions: new()
                {
                    UpdateState = ctx =>
                    {
                        var configuration = ctx.ServiceProvider.GetRequiredService<IConfiguration>();
                        if (string.IsNullOrEmpty(configuration[DcpExecutor.DebugSessionPortVar]))
                        {
                            return ResourceCommandState.Hidden;
                        }

                        return ctx.ResourceSnapshot.State?.Text == KnownResourceStates.Running
                            ? ResourceCommandState.Enabled
                            : ResourceCommandState.Disabled;
                    },
                    IconName = "BugArrowCounterclockwise",
                    IsHighlighted = true
                });
        }

        host.WithEnvironment(context =>
        {
            var httpsHostEndpoint = GetEndpointIfDefined(host.Resource, "https");
            var httpHostEndpoint = GetEndpointIfDefined(host.Resource, "http");
            var hostEndpoint = httpsHostEndpoint ?? httpHostEndpoint
                ?? throw new InvalidOperationException($"The host '{host.Resource.Name}' must define an HTTP or HTTPS endpoint.");

            // Resolve the HTTP OTLP endpoint for WASM client proxying.
            // WASM clients use HTTP/protobuf (not gRPC), so we need the HTTP endpoint.
            var httpOtlpEndpointUrl = BlazorGatewayExtensions.ResolveHttpOtlpEndpointUrl(context, host.ApplicationBuilder.Configuration);

            if (httpOtlpEndpointUrl is null && annotation.ProxyBlazorTelemetry)
            {
                context.Logger.LogWarning(
                    "OTLP telemetry proxying was requested but no dashboard HTTP endpoint could be resolved. " +
                    "WASM client telemetry will not be forwarded.");
            }

            GatewayConfigurationBuilder.EmitHostedProxyConfiguration(
                context.EnvironmentVariables,
                hostEndpoint,
                httpHostEndpoint,
                $"{host.Resource.Name} (client)",
                annotation.Services,
                annotation.ProxyBlazorTelemetry,
                httpOtlpEndpointUrl,
                annotation.OtlpPrefix);
        });
    }

    private static HostedClientAnnotation GetOrAddHostedClientAnnotation(IResource resource)
    {
        if (resource.TryGetLastAnnotation<HostedClientAnnotation>(out var existing))
        {
            return existing;
        }

        var newAnnotation = new HostedClientAnnotation();
        resource.Annotations.Add(newAnnotation);
        return newAnnotation;
    }

    private static HashSet<string> GetReferencedResourceNames(IResource resource)
    {
        return resource.Annotations
            .OfType<EndpointReferenceAnnotation>()
            .Select(a => a.Resource.Name)
            .ToHashSet(StringComparers.ResourceName);
    }

    private static EndpointReference? GetEndpointIfDefined(IResourceWithEndpoints resource, string endpointName)
    {
        var endpoint = resource.GetEndpoint(endpointName);
        return endpoint.Exists ? endpoint : null;
    }
}

/// <summary>
/// Annotation stored on a host resource that tracks proxied services and telemetry configuration.
/// </summary>
internal sealed class HostedClientAnnotation : IResourceAnnotation
{
    public List<HostedClientService> Services { get; } = [];
    public bool ProxyBlazorTelemetry { get; set; }
    public bool IsInitialized { get; set; }
    public string OtlpPrefix { get; set; } = GatewayConfigurationBuilder.DefaultOtlpPrefix;
}

/// <summary>
/// A service proxied from the hosted Blazor WebAssembly client through the host.
/// </summary>
internal readonly struct HostedClientService(string serviceName, string apiPrefix = GatewayConfigurationBuilder.DefaultApiPrefix, string? endpointName = null)
{
    public string ServiceName { get; } = serviceName;
    public string ApiPrefix { get; } = apiPrefix;

    /// <summary>
    /// The specific endpoint name to target on the service, or <see langword="null"/> to resolve by scheme.
    /// When set, YARP uses the .NET service discovery named endpoint format
    /// (<c>https+http://_endpointName.serviceName</c>) instead of scheme-based resolution.
    /// </summary>
    public string? EndpointName { get; } = endpointName;

    /// <summary>
    /// Gets the service discovery destination address for YARP.
    /// Uses named endpoint format (<c>_endpointName.serviceName</c>) when a specific endpoint
    /// is targeted; otherwise resolves by scheme.
    /// </summary>
    public string DestinationAddress => EndpointName is not null
        ? $"https+http://_{EndpointName}.{ServiceName}"
        : $"https+http://{ServiceName}";
}
