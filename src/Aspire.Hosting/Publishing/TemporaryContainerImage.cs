// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRECONTAINERRUNTIME001

using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Publishing;

/// <summary>
/// Owns cleanup of an invocation-private image reference.
/// </summary>
internal sealed class TemporaryContainerImage(IContainerRuntime runtime, string imageReference, ILogger logger) : IAsyncDisposable
{
    private int _disposed;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // A failed or canceled build may already have written this tag. Cleanup owns only this
        // private reference and must finish independently without replacing the original failure.
        using var cleanupCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await runtime.RemoveImageAsync(imageReference, cleanupCancellation.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to remove temporary container image {ImageName}", imageReference);
        }
    }
}
