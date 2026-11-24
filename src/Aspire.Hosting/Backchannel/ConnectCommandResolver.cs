// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRECONNECT001 // Snapshot publishing consumes the experimental connect command annotation.

using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Backchannel;

/// <summary>
/// Resolves a resource's <see cref="ConnectCommandAnnotation"/> into the command shown by tooling.
/// </summary>
internal static class ConnectCommandResolver
{
    /// <summary>
    /// Resolves the connect command for a resource, or returns <see langword="null"/> when the resource has none or it
    /// cannot be resolved without blocking (for example, before its endpoints are allocated).
    /// </summary>
    /// <param name="resource">The resource.</param>
    /// <param name="resourceId">
    /// The runtime resource name. For containers this is also the container name, because Aspire names each container
    /// after its DCP instance, e.g. <c>cache-abcdefgh</c>.
    /// </param>
    /// <param name="getContainerRuntimeName">Returns the lowercase container runtime command, such as <c>docker</c> or <c>podman</c>.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    public static async Task<ResourceSnapshotConnect?> ResolveAsync(
        IResource resource,
        string resourceId,
        Func<CancellationToken, Task<string>> getContainerRuntimeName,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (!resource.TryGetLastAnnotation<ConnectCommandAnnotation>(out var annotation))
        {
            return null;
        }

        // A container command needs a container to run in. Resources such as Azure services that only run as a
        // container when emulated have no container otherwise.
        if (annotation.Location == ConnectCommandLocation.Container && !resource.IsContainer())
        {
            return null;
        }

        // The command is displayed by tooling. Only endpoint values and literals are allowed so that a parameter,
        // connection string, or other value provider can never put a secret into it. Endpoint values must already be
        // allocated: resolving an unallocated endpoint waits for allocation, which would stall snapshot streaming.
        if (!CanResolveWithoutSecrets(annotation.Command, out var reason))
        {
            logger.LogDebug("Connect command for resource '{ResourceName}' is not shown: {Reason}", resource.Name, reason);
            return null;
        }

        string? command;
        try
        {
            command = await annotation.Command.GetValueAsync(
                new ValueProviderContext { Network = KnownNetworkIdentifiers.LocalhostNetwork },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Connect command for resource '{ResourceName}' could not be resolved.", resource.Name);
            return null;
        }

        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        string runs;
        if (annotation.Location == ConnectCommandLocation.Container)
        {
            string runtime;
            try
            {
                runtime = await getContainerRuntimeName(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The resolver caches a faulted task for a misconfigured ASPIRE_CONTAINER_RUNTIME. Omit the optional
                // entry rather than failing the snapshot, so the resource listing stays available for diagnosis.
                logger.LogDebug(ex, "Connect command for resource '{ResourceName}' is not shown: the container runtime could not be resolved.", resource.Name);
                return null;
            }

            command = FormatContainerCommand(runtime, resourceId, command);
            runs = ResourceSnapshotConnect.RunsInContainer;
        }
        else
        {
            runs = ResourceSnapshotConnect.RunsOnHost;
        }

        return new ResourceSnapshotConnect
        {
            Command = command,
            Runs = runs,
            Tool = annotation.Tool,
            Append = annotation.Append,
            Note = annotation.Note
        };
    }

    /// <summary>
    /// Wraps an in-container script so that it runs with the container's environment and forwards appended arguments.
    /// </summary>
    /// <remarks>
    /// For the script <c>exec redis-cli -p 6379</c> in container <c>cache-abcdefgh</c> this produces:
    /// <code>docker exec cache-abcdefgh sh -c 'exec redis-cli -p 6379 "$@"' sh</code>
    /// <c>sh -c</c> binds the arguments after the script to <c>$0</c>, <c>$1</c>, ..., so the trailing <c>sh</c> fills
    /// <c>$0</c> and arguments appended by the caller reach the client through <c>"$@"</c>. The script is single-quoted
    /// for the host shell, so embedded single quotes are written as <c>'\''</c>.
    /// See https://pubs.opengroup.org/onlinepubs/9799919799/utilities/sh.html.
    /// </remarks>
    internal static string FormatContainerCommand(string runtime, string containerName, string script)
    {
        var quotedScript = script.Replace("'", "'\\''", StringComparison.Ordinal);
        return $"{runtime} exec {containerName} sh -c '{quotedScript} \"$@\"' sh";
    }

    internal static bool CanResolveWithoutSecrets(object? valueProvider, out string? reason)
    {
        switch (valueProvider)
        {
            case ReferenceExpression { IsConditional: true } conditional:
                return CanResolveWithoutSecrets(conditional.Condition, out reason) &&
                    CanResolveWithoutSecrets(conditional.WhenTrue, out reason) &&
                    CanResolveWithoutSecrets(conditional.WhenFalse, out reason);

            case ReferenceExpression expression:
                foreach (var provider in expression.ValueProviders)
                {
                    if (!CanResolveWithoutSecrets(provider, out reason))
                    {
                        return false;
                    }
                }

                reason = null;
                return true;

            case EndpointReference endpoint:
                return IsAllocated(endpoint, out reason);

            case EndpointReferenceExpression endpointExpression:
                return IsResolvableProperty(endpointExpression, out reason) && IsAllocated(endpointExpression.Endpoint, out reason);

            default:
                reason = $"the command references a value of type '{valueProvider?.GetType().Name}'. Connect commands can reference only endpoint values.";
                return false;
        }

        static bool IsResolvableProperty(EndpointReferenceExpression endpointExpression, out string? reason)
        {
            switch (endpointExpression.Property)
            {
                // A target port that is not fixed resolves to a DCP template such as {{- portForServing "..." -}},
                // which DCP expands only in the specs it renders, never in a command shown by tooling.
                case EndpointProperty.TargetPort when endpointExpression.Endpoint.Exists && endpointExpression.Endpoint.TargetPort is null:
                    reason = $"endpoint '{endpointExpression.Endpoint.EndpointName}' has no fixed target port.";
                    return false;

                // Browser origins wait for URL callbacks to finish even after allocation, which would stall snapshots.
                case EndpointProperty.BrowserOrigins or EndpointProperty.PrimaryBrowserOrigin:
                    reason = $"the command references the '{endpointExpression.Property}' endpoint property, which connect commands do not support.";
                    return false;

                default:
                    reason = null;
                    return true;
            }
        }

        static bool IsAllocated(EndpointReference endpoint, out string? reason)
        {
            // EndpointAnnotation throws for an endpoint the resource does not define.
            if (!endpoint.Exists)
            {
                reason = $"endpoint '{endpoint.EndpointName}' is not defined.";
                return false;
            }

            // Check the network the command is resolved for. Resolving an endpoint that has no allocation on that
            // network waits for one, and EndpointReference.IsAllocated caches a negative result.
            if (!endpoint.EndpointAnnotation.AllAllocatedEndpoints.TryGetAllocatedEndpoint(endpoint.ContextNetworkID ?? KnownNetworkIdentifiers.LocalhostNetwork, out _))
            {
                reason = $"endpoint '{endpoint.EndpointName}' is not allocated yet.";
                return false;
            }

            reason = null;
            return true;
        }
    }
}
