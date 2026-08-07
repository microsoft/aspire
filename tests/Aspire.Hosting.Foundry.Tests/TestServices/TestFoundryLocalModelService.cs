// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Foundry.Tests;

internal sealed class TestFoundryLocalModelService : IFoundryLocalModelService
{
    private int _downloadCallCount;
    private int _isModelLoadedCallCount;
    private int _loadCallCount;
    private int _isLoaded;

    public TaskCompletionSource DownloadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource AllowDownload { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int DownloadCallCount => Volatile.Read(ref _downloadCallCount);

    public int IsModelLoadedCallCount => Volatile.Read(ref _isModelLoadedCallCount);

    public int LoadCallCount => Volatile.Read(ref _loadCallCount);

    public async Task<string> DownloadModelAsync(string modelName, Action<float> downloadProgress, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _downloadCallCount);
        DownloadStarted.TrySetResult();
        await AllowDownload.Task.WaitAsync(cancellationToken);
        downloadProgress(100);
        return $"{modelName}-id";
    }

    public Task LoadModelAsync(string modelId, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _loadCallCount);
        Volatile.Write(ref _isLoaded, 1);
        return Task.CompletedTask;
    }

    public Task<bool> IsModelLoadedAsync(string modelId, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _isModelLoadedCallCount);
        return Task.FromResult(Volatile.Read(ref _isLoaded) != 0);
    }

    public void Unload() => Volatile.Write(ref _isLoaded, 0);
}
