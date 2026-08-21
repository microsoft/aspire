// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Backchannel;

/// <summary>
/// Identifies the resource condition requested by a wait operation.
/// </summary>
internal enum ResourceWaitTarget
{
    Healthy,
    Up,
    Down
}

/// <summary>
/// Identifies the outcome of a resource wait operation.
/// </summary>
internal enum ResourceWaitOutcome
{
    Success,
    Timeout,
    Failure
}

/// <summary>
/// Contains the interpreted result of one resource wait operation.
/// </summary>
internal sealed record ResourceWaitResult(
    ResourceWaitOutcome Outcome,
    string ResourceName,
    string? State,
    string? Health,
    bool ResourceNotFound,
    string? ErrorMessage,
    TimeSpan Elapsed);

/// <summary>
/// Applies the shared Aspire backchannel wait semantics for a resource.
/// </summary>
internal sealed class ResourceWaitService(
    TimeProvider timeProvider,
    ILogger<ResourceWaitService> logger)
{
    private const string FailedToStartState = "FailedToStart";

    public async Task<ResourceWaitResult> WaitAsync(
        IAppHostAuxiliaryBackchannel connection,
        string resourceName,
        ResourceWaitTarget target,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        var startTimestamp = timeProvider.GetTimestamp();
        return await WaitCoreAsync(
            connection,
            resourceName,
            target,
            timeoutSeconds,
            startTimestamp,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ResourceWaitResult>> WaitForResourcesAsync(
        IAppHostAuxiliaryBackchannel connection,
        IReadOnlyList<string> resourceNames,
        ResourceWaitTarget target,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        // Capture one deadline before dispatch so every concurrent backchannel call consumes
        // the same timeout budget instead of receiving a fresh timeout.
        var deadline = timeProvider.GetUtcNow().AddSeconds(timeoutSeconds);
        var waitTasks = resourceNames
            .Select(resourceName => WaitUntilDeadlineAsync(
                connection,
                resourceName,
                target,
                deadline,
                cancellationToken))
            .ToArray();

        return await Task.WhenAll(waitTasks).ConfigureAwait(false);
    }

    private async Task<ResourceWaitResult> WaitUntilDeadlineAsync(
        IAppHostAuxiliaryBackchannel connection,
        string resourceName,
        ResourceWaitTarget target,
        DateTimeOffset deadline,
        CancellationToken cancellationToken)
    {
        var startTimestamp = timeProvider.GetTimestamp();
        var remaining = deadline - timeProvider.GetUtcNow();
        if (remaining <= TimeSpan.Zero)
        {
            return new ResourceWaitResult(
                ResourceWaitOutcome.Timeout,
                resourceName,
                State: null,
                Health: null,
                ResourceNotFound: false,
                ErrorMessage: null,
                timeProvider.GetElapsedTime(startTimestamp));
        }

        // The backchannel accepts whole seconds. Round up so a fractional second remaining on
        // the shared deadline is not truncated into an early timeout.
        var remainingSeconds = (int)Math.Ceiling(remaining.TotalSeconds);
        try
        {
            return await WaitCoreAsync(
                connection,
                resourceName,
                target,
                remainingSeconds,
                startTimestamp,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(
                "Wait for resource '{ResourceName}' failed with {ExceptionType}",
                resourceName,
                ex.GetType().Name);
            return new ResourceWaitResult(
                ResourceWaitOutcome.Failure,
                resourceName,
                State: null,
                Health: null,
                ResourceNotFound: false,
                ErrorMessage: null,
                timeProvider.GetElapsedTime(startTimestamp));
        }
    }

    private async Task<ResourceWaitResult> WaitCoreAsync(
        IAppHostAuxiliaryBackchannel connection,
        string resourceName,
        ResourceWaitTarget target,
        int timeoutSeconds,
        long startTimestamp,
        CancellationToken cancellationToken)
    {
        var response = await connection.WaitForResourceAsync(
            resourceName,
            GetProtocolValue(target),
            timeoutSeconds,
            cancellationToken).ConfigureAwait(false);

        // The AppHost's "down" predicate treats FailedToStart as terminal. It still represents
        // a failed resource and must not become a successful wait result.
        var outcome = response switch
        {
            _ when IsTerminalFailureState(response.State) => ResourceWaitOutcome.Failure,
            { Success: true } => ResourceWaitOutcome.Success,
            { ResourceNotFound: true } => ResourceWaitOutcome.Failure,
            { TimedOut: true } => ResourceWaitOutcome.Timeout,
            _ => ResourceWaitOutcome.Failure
        };

        return new ResourceWaitResult(
            outcome,
            resourceName,
            response.State,
            response.HealthStatus,
            response.ResourceNotFound,
            response.ErrorMessage,
            timeProvider.GetElapsedTime(startTimestamp));
    }

    internal static bool IsTerminalFailureState(string? state)
    {
        return string.Equals(
            state,
            FailedToStartState,
            StringComparisons.ResourceState);
    }

    internal static string GetProtocolValue(ResourceWaitTarget target)
    {
        return target switch
        {
            ResourceWaitTarget.Healthy => "healthy",
            ResourceWaitTarget.Up => "up",
            ResourceWaitTarget.Down => "down",
            _ => throw new ArgumentOutOfRangeException(nameof(target))
        };
    }
}
