// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using Aspire.Hosting.Utils;
using Semver;

namespace Aspire.Hosting.Tests.Utils;

internal sealed class TestDotnetSdkVersionProvider(string? version) : IDotnetSdkVersionProvider
{
    private readonly SemVersion? _version = version is null
        ? null
        : SemVersion.Parse(version, SemVersionStyles.Strict);
    private readonly ConcurrentQueue<string?> _workingDirectories = [];
    private int _callCount;

    public int CallCount => _callCount;

    public IReadOnlyList<string?> WorkingDirectories => [.. _workingDirectories];

    public Task<SemVersion?> TryGetVersionAsync(
        string? workingDirectory,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RecordCall(workingDirectory);
        return Task.FromResult(_version);
    }

    public Task<bool> SupportsMultiThreadedBuildAsync(
        string? workingDirectory,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RecordCall(workingDirectory);
        return Task.FromResult(DotnetSdkUtils.SupportsMultiThreadedBuild(_version));
    }

    private void RecordCall(string? workingDirectory)
    {
        Interlocked.Increment(ref _callCount);
        _workingDirectories.Enqueue(workingDirectory);
    }
}
