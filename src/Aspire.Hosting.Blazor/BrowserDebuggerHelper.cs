// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Dcp;
using Aspire.Hosting.Orchestrator;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

#pragma warning disable ASPIREEXTENSION001 // WithDebugSupport is experimental

namespace Aspire.Hosting;

/// <summary>
/// Shared helper for creating browser debugger resources.
/// Both hosted Blazor (BlazorHostedExtensions) and gateway (BlazorGatewayExtensions)
/// use this to avoid duplicating the child-resource + command registration pattern.
/// </summary>
internal static class BrowserDebuggerHelper
{
    /// <summary>
    /// Creates a hidden child ExecutableResource with WithExplicitStart that launches a debug browser
    /// via DCP/IDE when started. Registers a "Debug in Browser" command on the specified command target.
    /// </summary>
    /// <param name="builder">The distributed application builder.</param>
    /// <param name="parentResource">The resource that owns the endpoint to debug (gateway or host).</param>
    /// <param name="commandTarget">The resource on which to register the "Debug in Browser" command.</param>
    /// <param name="clientProjectPath">Absolute path to the WASM client .csproj.</param>
    /// <param name="relativePath">Optional path prefix appended to the endpoint URL.</param>
    internal static void AddBrowserDebuggerResource(
        IDistributedApplicationBuilder builder,
        IResourceWithEndpoints parentResource,
        IResourceBuilder<IResource> commandTarget,
        string clientProjectPath,
        string? relativePath)
    {
        var debuggerResourceName = relativePath is not null
            ? $"{parentResource.Name}-{commandTarget.Resource.Name}-debugger"
            : $"{parentResource.Name}-wasm-debugger";

        var clientProjectDir = Path.GetDirectoryName(clientProjectPath) ?? clientProjectPath;

        var debugAnnotation = new BrowserDebugAnnotation(clientProjectPath, relativePath);
        debugAnnotation.DebuggerResourceName = debuggerResourceName;
        parentResource.Annotations.Add(debugAnnotation);

        var debuggerResource = new ExecutableResource(debuggerResourceName, "browser-debug", clientProjectDir);

        builder.AddResource(debuggerResource)
            .WithParentRelationship(parentResource)
            .ExcludeFromManifest()
            .WithExplicitStart()
            .WithInitialState(new()
            {
                ResourceType = "BrowserDebugger",
                Properties = [],
                IsHidden = true
            })
            .WithDebugSupport(
                mode =>
                {
                    // Resolve the parent's endpoint at runtime to get the actual allocated URL.
                    EndpointAnnotation? endpointAnnotation = null;
                    if (parentResource.TryGetAnnotationsOfType<EndpointAnnotation>(out var endpoints))
                    {
                        endpointAnnotation = endpoints.FirstOrDefault(e => e.UriScheme == "https")
                            ?? endpoints.FirstOrDefault(e => e.UriScheme == "http");
                    }

                    if (endpointAnnotation is null)
                    {
                        throw new InvalidOperationException(
                            $"Resource '{parentResource.Name}' does not have an HTTP or HTTPS endpoint. " +
                            "Browser debugging requires an endpoint to navigate to.");
                    }

                    var endpointReference = parentResource.GetEndpoint(endpointAnnotation.Name);
                    var appUrl = relativePath is not null
                        ? $"{endpointReference.Url}/{relativePath}/"
                        : endpointReference.Url;

                    return new Dcp.Model.BrowserLaunchConfiguration
                    {
                        Mode = mode,
                        Url = appUrl,
                        WebRoot = clientProjectPath,
                        Browser = "msedge"
                    };
                },
                "browser");

        // Register "Debug in Browser" command on the command target resource.
        commandTarget.WithCommand(
            name: "debug-in-browser",
            displayName: "Debug in Browser",
            executeCommand: async context =>
            {
                var orchestrator = context.ServiceProvider.GetRequiredService<ApplicationOrchestrator>();
                await orchestrator.StartResourceAsync(debuggerResourceName, context.CancellationToken).ConfigureAwait(false);
                return CommandResults.Success();
            },
            commandOptions: new()
            {
                UpdateState = ctx =>
                {
                    // Hide command when no IDE is connected (DEBUG_SESSION_PORT is set by DCP
                    // when an IDE protocol session is active).
                    var configuration = ctx.ServiceProvider.GetRequiredService<IConfiguration>();
                    if (string.IsNullOrEmpty(configuration[DcpExecutor.DebugSessionPortVar]))
                    {
                        return ResourceCommandState.Hidden;
                    }

                    // Disable when the parent isn't running yet.
                    return ctx.ResourceSnapshot.State?.Text == KnownResourceStates.Running
                        ? ResourceCommandState.Enabled
                        : ResourceCommandState.Disabled;
                },
                IconName = "BugArrowCounterclockwise",
                IsHighlighted = true
            });
    }
}
