// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRECONTAINERRUNTIME001

using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Publishing;

/// <summary>
/// Coordinates interactive container runtime readiness checks for pipeline builds.
/// </summary>
internal sealed class ContainerRuntimeReadiness(IServiceProvider services)
{
    private readonly object _lock = new();
    private readonly Dictionary<IContainerRuntime, Task> _checks = new(ReferenceEqualityComparer.Instance);

    public Task EnsureRunningAsync(IContainerRuntime containerRuntime, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_lock)
        {
            // Share an in-flight prompt across concurrent build steps, but do not cache completed
            // checks: the runtime can stop or recover before a later build or retry.
            if (!_checks.TryGetValue(containerRuntime, out var check) || check.IsCompleted)
            {
                check = EnsureRunningCoreAsync(containerRuntime, cancellationToken);
                _checks[containerRuntime] = check;
            }

            return check.WaitAsync(cancellationToken);
        }
    }

    private async Task EnsureRunningCoreAsync(IContainerRuntime containerRuntime, CancellationToken cancellationToken)
    {
        if (await containerRuntime.CheckIfRunningAsync(cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        var interactionService = services.GetService<IInteractionService>();
        if (interactionService?.IsAvailable == true)
        {
            var result = await interactionService.PromptNotificationAsync(
                $"{containerRuntime.Name} is not running",
                $"Start {containerRuntime.Name} and confirm to continue.",
                new NotificationInteractionOptions
                {
                    Intent = MessageIntent.Warning
                },
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (!result.Canceled && result.Data)
            {
                var timeProvider = services.GetRequiredService<TimeProvider>();
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(5), timeProvider);
                using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

                try
                {
                    // Starting Docker can take a while even after the user confirms.
                    while (!await containerRuntime.CheckIfRunningAsync(waitCts.Token).ConfigureAwait(false))
                    {
                        await Task.Delay(TimeSpan.FromSeconds(2), timeProvider, waitCts.Token).ConfigureAwait(false);
                    }

                    return;
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // Timed out waiting for startup; report the same actionable failure below.
                }
            }
        }

        throw new DistributedApplicationException(
            $"{containerRuntime.Name} is not running. Start {containerRuntime.Name} and try again.");
    }
}
