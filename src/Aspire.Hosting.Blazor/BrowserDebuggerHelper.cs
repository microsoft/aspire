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
    private const string BrowserCapability = "browser";
    private const string BrowserDebuggingUnavailableMessage =
        "Browser debugging requires an active IDE debug session that supports the 'browser' launch configuration.";
    private const string BrowserDebuggerClientUnavailableMessage =
        "Browser debugging is unavailable because no Blazor WebAssembly client project was discovered.";

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
    /// <param name="browser">The browser to use for debugging.</param>
    internal static void AddBrowserDebuggerResource(
        IDistributedApplicationBuilder builder,
        IResourceWithEndpoints parentResource,
        IResourceBuilder<IResource> commandTarget,
        string clientProjectPath,
        string? relativePath,
        string browser = "msedge")
    {
        var clientProjectDirectory = Path.GetDirectoryName(clientProjectPath) ?? clientProjectPath;

        AddBrowserDebuggerResource(
            builder,
            parentResource,
            commandTarget,
            clientProjectDirectory,
            () => clientProjectPath,
            relativePath,
            browser);
    }

    /// <summary>
    /// Creates a browser debugger whose client project path is resolved before application startup.
    /// </summary>
    internal static void AddBrowserDebuggerResource(
        IDistributedApplicationBuilder builder,
        IResourceWithEndpoints parentResource,
        IResourceBuilder<IResource> commandTarget,
        string workingDirectory,
        Func<string?> clientProjectPathProvider,
        string? relativePath,
        string browser = "msedge")
    {
        var debuggerResourceName = relativePath is not null
            ? $"{parentResource.Name}-{commandTarget.Resource.Name}-debugger"
            : $"{parentResource.Name}-wasm-debugger";

        var debuggerResource = new BrowserDebuggerResource(debuggerResourceName, browser, workingDirectory);
        debuggerResource.Annotations.Add(NameValidationPolicyAnnotation.None);

        // Tracks whether a debug browser session is currently active.
        // Toggled by the start/stop command handlers and reset when the resource stops
        // (e.g., user closes the browser).
        var debugSessionActive = false;
        var debugSessionGeneration = 0;
        var watcherCts = new CancellationTokenSource();
        // Commands can be invoked concurrently by dashboard, CLI, or MCP clients. Serialize the
        // complete state transition so duplicate starts and overlapping start/stop requests cannot
        // race while replacing the watcher cancellation token source.
        var debugSessionLock = new SemaphoreSlim(1, 1);

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
                    // DCP materializes launch configurations during startup. When discovery found
                    // no client, the command stays hidden and this placeholder is never launched.
                    var clientProjectPath = clientProjectPathProvider() ?? workingDirectory;

                    return new BrowserLaunchConfiguration
                    {
                        Mode = mode,
                        Url = appUrl,
                        WebRoot = clientProjectPath,
                        Browser = browser
                    };
                },
                BrowserCapability);

        // Register "Debug in Browser" command — shown when no debug session is active.
        commandTarget.WithCommand(
            name: "debug-in-browser",
            displayName: "Debug in Browser",
            executeCommand: async context =>
            {
                await debugSessionLock.WaitAsync(context.CancellationToken).ConfigureAwait(false);
                try
                {
                    if (clientProjectPathProvider() is null)
                    {
                        return CommandResults.Failure(BrowserDebuggerClientUnavailableMessage);
                    }

                    if (!debuggerResource.SupportsDebugging(builder.Configuration, out _))
                    {
                        return CommandResults.Failure(BrowserDebuggingUnavailableMessage);
                    }

                    if (debugSessionActive)
                    {
                        return CommandResults.Success();
                    }

                    // Resolve the DCP instance name from the model resource's DcpInstancesAnnotation.
                    // StartResourceAsync expects the DCP metadata name (e.g., "gateway-app-debugger-abc123"),
                    // not the model resource name (e.g., "gateway-app-debugger").
                    var dcpInstanceName = GetDcpInstanceName(debuggerResource);
                    var currentGeneration = Interlocked.Increment(ref debugSessionGeneration);

                    // Cancel the previous watcher to signal it to stop, then dispose the old CTS
                    // before creating a new one to avoid leaking CTS registrations and timers
                    // from repeated start/stop cycles.
                    await watcherCts.CancelAsync().ConfigureAwait(false);
                    watcherCts.Dispose();
                    watcherCts = new CancellationTokenSource();

                    var notificationService = context.Services.GetRequiredService<ResourceNotificationService>();
                    var minimumSnapshotVersion = notificationService.TryGetCurrentState(dcpInstanceName, out var currentState)
                        ? currentState.Snapshot.Version
                        : 0;

                    var orchestrator = context.Services.GetRequiredService<ApplicationOrchestrator>();
                    await orchestrator.StartResourceAsync(dcpInstanceName, context.CancellationToken).ConfigureAwait(false);
                    debugSessionActive = true;

                    // Watch for the debugger resource to stop (e.g., user closes the browser)
                    // so we can flip the flag and re-show the "Debug in Browser" command.
                    _ = WatchForDebuggerStopAsync(
                        context.Services,
                        commandTarget.Resource,
                        debuggerResource,
                        minimumSnapshotVersion,
                        watcherCts.Token,
                        () =>
                        {
                            if (Volatile.Read(ref debugSessionGeneration) == currentGeneration)
                            {
                                debugSessionActive = false;
                            }
                        });

                    // Publish a no-op update on the command target to force the dashboard to
                    // re-evaluate UpdateState callbacks and toggle command visibility.
                    await notificationService.PublishUpdateAsync(commandTarget.Resource, s => s).ConfigureAwait(false);

                    return CommandResults.Success();
                }
                finally
                {
                    debugSessionLock.Release();
                }
            },
            commandOptions: new()
            {
                UpdateState = ctx =>
                {
                    if (clientProjectPathProvider() is null)
                    {
                        return ResourceCommandState.Hidden;
                    }

                    if (debugSessionActive)
                    {
                        return ResourceCommandState.Hidden;
                    }

                    return debuggerResource.SupportsDebugging(builder.Configuration, out _)
                        && ctx.ResourceSnapshot.State?.Text == KnownResourceStates.Running
                        ? ResourceCommandState.Enabled
                        : ResourceCommandState.Disabled;
                },
                IconName = "Bug",
                IconVariant = IconVariant.Filled,
                IsHighlighted = true
            });

        // Register "Stop Browser Debug" command — shown when a debug session is active.
        commandTarget.WithCommand(
            name: "stop-browser-debug",
            displayName: "Stop Browser Debug",
            executeCommand: async context =>
            {
                await debugSessionLock.WaitAsync(context.CancellationToken).ConfigureAwait(false);
                try
                {
                    if (!debugSessionActive)
                    {
                        return CommandResults.Success();
                    }

                    var dcpInstanceName = GetDcpInstanceName(debuggerResource);
                    var orchestrator = context.Services.GetRequiredService<ApplicationOrchestrator>();
                    await orchestrator.StopResourceAsync(dcpInstanceName, context.CancellationToken).ConfigureAwait(false);

                    // Invalidate and cancel the watcher after the stop succeeds. If stopping fails,
                    // the existing watcher must remain active so a later terminal state is observed.
                    Interlocked.Increment(ref debugSessionGeneration);
                    await watcherCts.CancelAsync().ConfigureAwait(false);
                    debugSessionActive = false;

                    // Force dashboard to re-evaluate command visibility.
                    var notificationService = context.Services.GetRequiredService<ResourceNotificationService>();
                    await notificationService.PublishUpdateAsync(commandTarget.Resource, s => s).ConfigureAwait(false);

                    return CommandResults.Success();
                }
                finally
                {
                    debugSessionLock.Release();
                }
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
                IconName = "DismissCircle",
                IconVariant = IconVariant.Filled,
                IsHighlighted = true
            });
    }

    /// <summary>
    /// Watches the debugger resource for a transition to stopped state (e.g., browser closed)
    /// and invokes the callback to reset the active session flag.
    /// Handles both the normal case (Running → terminal) and the immediate failure case
    /// (Starting → FailedToStart without ever reaching Running).
    /// </summary>
    internal static async Task WatchForDebuggerStopAsync(
        IServiceProvider serviceProvider,
        IResource commandTargetResource,
        IResource debuggerResource,
        long minimumSnapshotVersion,
        CancellationToken cancellationToken,
        Action onStopped)
    {
        var resourceNotificationService = serviceProvider.GetRequiredService<ResourceNotificationService>();

        try
        {
            await foreach (var evt in resourceNotificationService.WatchAsync(cancellationToken).ConfigureAwait(false))
            {
                if (evt.Resource != debuggerResource || evt.Snapshot.Version <= minimumSnapshotVersion)
                {
                    continue;
                }

                var state = evt.Snapshot.State?.Text;

                // The snapshot version boundary ensures these states belong to the current start,
                // even when the watcher subscribes after a rapid Starting -> terminal transition.
                // DCP executables use "Terminated" (killed by controller) and "Finished" (ran to completion).
                // Explicit-start resources may also transition back to "NotStarted" after stopping.
                var isTerminal = state == KnownResourceStates.Exited
                    || state == KnownResourceStates.Finished
                    || state == KnownResourceStates.FailedToStart
                    || state == "Terminated"
                    || state == KnownResourceStates.NotStarted;

                if (isTerminal)
                {
                    onStopped();

                    // Force dashboard to re-evaluate command visibility on the command target.
                    await resourceNotificationService.PublishUpdateAsync(commandTargetResource, s => s).ConfigureAwait(false);
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when the watcher is cancelled (e.g., stop command or new debug session).
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
