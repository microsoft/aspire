// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.JavaScript;
using Aspire.Hosting.Orchestrator;
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
    /// via DCP/IDE when started. Registers "Debug in Browser" and "Stop Browser Debug" commands
    /// on the specified command target.
    /// </summary>
    /// <param name="builder">The distributed application builder.</param>
    /// <param name="parentResource">The resource that owns the endpoint to debug (gateway or host).</param>
    /// <param name="commandTarget">The resource on which to register the debug commands.</param>
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

        var debuggerResource = new BrowserDebuggerResource(debuggerResourceName, "msedge", clientProjectDir);

        // Tracks whether a debug browser session is currently active.
        // Toggled by the start/stop command handlers and reset when the resource stops
        // (e.g., user closes the browser).
        var debugSessionActive = false;

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

                    return new BrowserLaunchConfiguration
                    {
                        Mode = mode,
                        Url = appUrl,
                        WebRoot = clientProjectPath,
                        Browser = "msedge"
                    };
                },
                "browser");

        // Register "Debug in Browser" command — shown when no debug session is active.
        commandTarget.WithCommand(
            name: "debug-in-browser",
            displayName: "Debug in Browser",
            executeCommand: async context =>
            {
                // Resolve the DCP instance name from the model resource's DcpInstancesAnnotation.
                // StartResourceAsync expects the DCP metadata name (e.g., "gateway-app-debugger-abc123"),
                // not the model resource name (e.g., "gateway-app-debugger").
                var dcpInstanceName = GetDcpInstanceName(debuggerResource);
                var orchestrator = context.ServiceProvider.GetRequiredService<ApplicationOrchestrator>();
                await orchestrator.StartResourceAsync(dcpInstanceName, context.CancellationToken).ConfigureAwait(false);
                debugSessionActive = true;

                // Publish a no-op update on the command target to force the dashboard to
                // re-evaluate UpdateState callbacks and toggle command visibility.
                var notificationService = context.ServiceProvider.GetRequiredService<ResourceNotificationService>();
                await notificationService.PublishUpdateAsync(commandTarget.Resource, s => s).ConfigureAwait(false);

                // Watch for the debugger resource to stop (e.g., user closes the browser)
                // so we can flip the flag and re-show the "Debug in Browser" command.
                _ = WatchForDebuggerStopAsync(context.ServiceProvider, commandTarget.Resource, debuggerResource, () => debugSessionActive = false);

                return CommandResults.Success();
            },
            commandOptions: new()
            {
                UpdateState = ctx =>
                {
                    if (debugSessionActive)
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

        // Register "Stop Browser Debug" command — shown when a debug session is active.
        commandTarget.WithCommand(
            name: "stop-browser-debug",
            displayName: "Stop Browser Debug",
            executeCommand: async context =>
            {
                var dcpInstanceName = GetDcpInstanceName(debuggerResource);
                var orchestrator = context.ServiceProvider.GetRequiredService<ApplicationOrchestrator>();
                await orchestrator.StopResourceAsync(dcpInstanceName, context.CancellationToken).ConfigureAwait(false);
                debugSessionActive = false;

                // Force dashboard to re-evaluate command visibility.
                var notificationService = context.ServiceProvider.GetRequiredService<ResourceNotificationService>();
                await notificationService.PublishUpdateAsync(commandTarget.Resource, s => s).ConfigureAwait(false);

                return CommandResults.Success();
            },
            commandOptions: new()
            {
                UpdateState = ctx =>
                {
                    if (!debugSessionActive)
                    {
                        return ResourceCommandState.Hidden;
                    }

                    return ResourceCommandState.Enabled;
                },
                IconName = "Stop",
                IconVariant = IconVariant.Filled,
                IsHighlighted = true
            });
    }

    /// <summary>
    /// Watches the debugger resource for a transition to stopped state (e.g., browser closed)
    /// and invokes the callback to reset the active session flag.
    /// Only triggers after the resource has been observed in Running state first,
    /// so that immediate startup failures don't reset the debug session flag.
    /// </summary>
    private static async Task WatchForDebuggerStopAsync(
        IServiceProvider serviceProvider,
        IResource commandTargetResource,
        IResource debuggerResource,
        Action onStopped)
    {
        var resourceNotificationService = serviceProvider.GetRequiredService<ResourceNotificationService>();
        var wasRunning = false;

        await foreach (var evt in resourceNotificationService.WatchAsync().ConfigureAwait(false))
        {
            if (evt.Resource != debuggerResource)
            {
                continue;
            }

            var state = evt.Snapshot.State?.Text;

            if (state == KnownResourceStates.Running)
            {
                wasRunning = true;
                continue;
            }

            // Only reset once the resource has been running and then transitions to a terminal state.
            if (wasRunning
                && (state == KnownResourceStates.Exited
                    || state == KnownResourceStates.Finished
                    || state == KnownResourceStates.FailedToStart))
            {
                onStopped();

                // Force dashboard to re-evaluate command visibility on the command target.
                await resourceNotificationService.PublishUpdateAsync(commandTargetResource, s => s).ConfigureAwait(false);
                break;
            }
        }
    }

    /// <summary>
    /// Resolves the DCP instance name from a resource's <see cref="DcpInstancesAnnotation"/>.
    /// The DCP metadata name (e.g., "gateway-app-debugger-abc123") differs from the model resource
    /// name (e.g., "gateway-app-debugger") because DCP appends a suffix during name generation.
    /// </summary>
    private static string GetDcpInstanceName(IResource resource)
    {
        if (resource.TryGetInstances(out var instances) && instances.Length > 0)
        {
            return instances[0].Name;
        }

        // Fallback to the model resource name if instances haven't been populated yet.
        return resource.Name;
    }
}
