// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

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
    string? State,
    bool ResourceNotFound,
    string? ErrorMessage);

/// <summary>
/// Applies the shared Aspire backchannel wait semantics for a resource.
/// </summary>
internal sealed class ResourceWaitService
{
#pragma warning disable CA1822 // Keep the instance shape used by existing call sites.
    public async Task<ResourceWaitResult> WaitAsync(
        IAppHostAuxiliaryBackchannel connection,
        string resourceName,
        ResourceWaitTarget target,
        int timeoutSeconds,
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
            response.State,
            response.ResourceNotFound,
            response.ErrorMessage);
    }
#pragma warning restore CA1822

    internal static bool IsTerminalFailureState(string? state)
    {
        return string.Equals(
            state,
            KnownResourceStates.FailedToStart,
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
