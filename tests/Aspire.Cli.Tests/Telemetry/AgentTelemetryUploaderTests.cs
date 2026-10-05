// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Telemetry;
using Aspire.Shared;
using Microsoft.AspNetCore.InternalTesting;

namespace Aspire.Cli.Tests.Telemetry;

public class AgentTelemetryUploaderTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task DrainAsync_KeepsProcessAliveUntilExporterRemovesPendingData()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var storage = workspace.CreateDirectory("storage").FullName;
        var pending = Path.Combine(storage, "pending");
        var lockPath = Path.Combine(workspace.Path, "uploader.lock");
        await File.WriteAllTextAsync(pending, "opaque exporter data");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var draining = AgentTelemetryUploader.DrainAsync(storage, lockPath, timeout.Token);

        try
        {
            Assert.False(draining.IsCompleted);
            Assert.Null(FileLock.TryAcquire(lockPath));
            File.Delete(pending);
            await draining.DefaultTimeout();
            using var availableLock = FileLock.TryAcquire(lockPath);
            Assert.NotNull(availableLock);
        }
        finally
        {
            await timeout.CancelAsync();
        }
    }

    [Fact]
    public async Task DrainAsync_ConcurrentUploaderReturnsWithoutWaiting()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var lockPath = Path.Combine(workspace.Path, "uploader.lock");
        using var existing = FileLock.Acquire(lockPath);

        await AgentTelemetryUploader.DrainAsync(workspace.Path, lockPath, CancellationToken.None).DefaultTimeout();
    }

    [Fact]
    public async Task DrainAsync_CancellationPreservesBacklogAndReleasesLock()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var storage = workspace.CreateDirectory("storage").FullName;
        var pending = Path.Combine(storage, "pending");
        var lockPath = Path.Combine(workspace.Path, "uploader.lock");
        await File.WriteAllTextAsync(pending, "opaque exporter data");
        using var cancellation = new CancellationTokenSource();
        var draining = AgentTelemetryUploader.DrainAsync(storage, lockPath, cancellation.Token);
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => draining);
        Assert.Equal("opaque exporter data", await File.ReadAllTextAsync(pending));
        using var availableLock = FileLock.TryAcquire(lockPath);
        Assert.NotNull(availableLock);
    }
}
