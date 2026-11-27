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
        // Validation runs inside the exception boundary because a faulted allocation rethrows when it is inspected;
        // the optional entry is omitted rather than failing the whole snapshot.
        string? command;
        try
        {
            if (!CanResolveWithoutSecrets(annotation.Command, resource, out var reason))
            {
                logger.LogDebug("Connect command for resource '{ResourceName}' is not shown: {Reason}", resource.Name, reason);
                return null;
            }

            command = await annotation.Command.GetValueAsync(
                new ValueProviderContext { Network = KnownNetworkIdentifiers.LocalhostNetwork },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // A cancellation that did not come from this request's token belongs to a value the command depends on,
            // not to this caller, so it only makes the optional entry unavailable.
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
            var runtime = await GetContainerRuntimeNameAsync(resource, getContainerRuntimeName, logger, cancellationToken).ConfigureAwait(false);
            if (runtime is null)
            {
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

    private static async Task<string?> GetContainerRuntimeNameAsync(
        IResource resource,
        Func<CancellationToken, Task<string>> getContainerRuntimeName,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        // Runtime detection is shared: concurrent callers await the probe started with the first caller's token, so
        // the probe can be cancelled while this request is still active. The resolver discards a cancelled probe, so
        // one retry starts detection with this request's token. If that is also cancelled by another caller, omit the
        // optional entry rather than failing the snapshot.
        const int maxAttempts = 2;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await getContainerRuntimeName(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                if (attempt < maxAttempts)
                {
                    continue;
                }

                logger.LogDebug(ex, "Connect command for resource '{ResourceName}' is not shown: container runtime detection was cancelled.", resource.Name);
                return null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The resolver caches a faulted task for a misconfigured ASPIRE_CONTAINER_RUNTIME. Omit the optional
                // entry rather than failing the snapshot, so the resource listing stays available for diagnosis.
                logger.LogDebug(ex, "Connect command for resource '{ResourceName}' is not shown: the container runtime could not be resolved.", resource.Name);
                return null;
            }
        }
    }

    /// <summary>
    /// Wraps an in-container script so that it runs with the container's environment and forwards appended arguments.
    /// </summary>
    /// <remarks>
    /// For the script <c>exec redis-cli -p 6379</c> in container <c>cache-abcdefgh</c> this produces:
    /// <code>docker exec -i cache-abcdefgh sh -c 'exec redis-cli -p 6379 "$@"' sh</code>
    /// <c>sh -c</c> binds the arguments after the script to <c>$0</c>, <c>$1</c>, ..., so the trailing <c>sh</c> fills
    /// <c>$0</c> and arguments appended by the caller reach the client through <c>"$@"</c>. The script is single-quoted
    /// for the host shell, so embedded single quotes are written as <c>'\''</c>.
    /// See https://pubs.opengroup.org/onlinepubs/9799919799/utilities/sh.html.
    /// </remarks>
    internal static string FormatContainerCommand(string runtime, string containerName, string script)
    {
        var quotedScript = EscapeForSingleQuotedShellString(script);

        // -i forwards stdin so interactive clients stay open and piped input (for example a SQL script read with
        // psql -f -) reaches the client. -t is deliberately omitted: a TTY fails when stdin is not a terminal, as in
        // agent pipelines, and translates output line endings to CRLF.
        // See https://docs.docker.com/reference/cli/docker/container/exec/ and
        // https://docs.podman.io/en/latest/markdown/podman-exec.1.html.
        return $"{runtime} exec -i {containerName} sh -c '{quotedScript} \"$@\"' sh";
    }

    /// <summary>
    /// Escapes text for use inside a single-quoted POSIX shell string by writing each <c>'</c> as <c>'\''</c>.
    /// </summary>
    internal static string EscapeForSingleQuotedShellString(string value)
        => value.Replace("'", "'\\''", StringComparison.Ordinal);

    internal static bool CanResolveWithoutSecrets(object? valueProvider, IResource owner, out string? reason)
    {
        switch (valueProvider)
        {
            case ReferenceExpression { IsConditional: true } conditional:
                return CanResolveWithoutSecrets(conditional.Condition, owner, out reason) &&
                    CanResolveWithoutSecrets(conditional.WhenTrue, owner, out reason) &&
                    CanResolveWithoutSecrets(conditional.WhenFalse, owner, out reason);

            case ReferenceExpression expression:
                foreach (var provider in expression.ValueProviders)
                {
                    if (!CanResolveWithoutSecrets(provider, owner, out reason))
                    {
                        return false;
                    }
                }

                reason = null;
                return true;

            case EndpointReference endpoint:
                return IsOwnEndpoint(endpoint, owner, out reason) && IsAllocated(endpoint, out reason);

            case EndpointReferenceExpression endpointExpression:
                return IsOwnEndpoint(endpointExpression.Endpoint, owner, out reason) &&
                    IsResolvableProperty(endpointExpression, out reason) &&
                    IsAllocated(endpointExpression.Endpoint, out reason);

            default:
                reason = $"the command references a value of type '{valueProvider?.GetType().Name}'. Connect commands can reference only endpoint values.";
                return false;
        }

        static bool IsOwnEndpoint(EndpointReference endpoint, IResource owner, out string? reason)
        {
            // The command is re-resolved only when its own resource publishes a snapshot. Another resource's endpoint
            // can be allocated or reallocated without that happening, which would leave the command missing or stale
            // in watch streams. Compare names because surrogate builders (e.g. Azure emulator containers) wrap the
            // model resource with a different object of the same name.
            if (!string.Equals(endpoint.Resource.Name, owner.Name, StringComparisons.ResourceName))
            {
                reason = $"the command references endpoint '{endpoint.EndpointName}' of resource '{endpoint.Resource.Name}'. Connect commands can reference only the resource's own endpoints.";
                return false;
            }

            reason = null;
            return true;
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

            // Commands are resolved for the localhost network, but a reference bound to another network resolves that
            // network's allocation instead, e.g. a container-network address and port that the host cannot reach.
            // Integrations reference the default endpoint, so a reference bound to any other network is rejected.
            if (endpoint.ContextNetworkID is { } network && network != KnownNetworkIdentifiers.LocalhostNetwork)
            {
                reason = $"endpoint '{endpoint.EndpointName}' is referenced on network '{network.Value}'. Connect commands can reference only localhost endpoint values.";
                return false;
            }

            // Resolving an endpoint that has no allocation on the localhost network waits for one, and
            // EndpointReference.IsAllocated caches a negative result.
            if (!endpoint.EndpointAnnotation.AllAllocatedEndpoints.TryGetAllocatedEndpoint(KnownNetworkIdentifiers.LocalhostNetwork, out _))
            {
                reason = $"endpoint '{endpoint.EndpointName}' is not allocated yet.";
                return false;
            }

            reason = null;
            return true;
        }
    }
}
