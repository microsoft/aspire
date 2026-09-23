// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using Aspire.Hosting.Utils;
using Semver;

namespace Aspire.Hosting.Tests.Utils;

internal sealed class TestDotnetSdkVersionProvider(string? version) : IDotnetSdkVersionProvider
{
    private static readonly IReadOnlyDictionary<string, string> s_emptyEnvironment =
        new Dictionary<string, string>();
    private readonly SemVersion? _version = version is null
        ? null
        : SemVersion.Parse(version, SemVersionStyles.Strict);
    private readonly ConcurrentQueue<string?> _workingDirectories = [];
    private readonly ConcurrentQueue<IReadOnlyDictionary<string, string>> _probeEnvironments = [];
    private int _callCount;

    public int CallCount => _callCount;

    public IReadOnlyList<string?> WorkingDirectories => [.. _workingDirectories];

    public IReadOnlyList<IReadOnlyDictionary<string, string>> ProbeEnvironments => [.. _probeEnvironments];

    public Task<SemVersion?> TryGetVersionAsync(
        string? workingDirectory,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RecordCall(workingDirectory, s_emptyEnvironment);
        return Task.FromResult(_version);
    }

    public Task<bool> SupportsMultiThreadedBuildAsync(
        string? workingDirectory,
        IReadOnlyDictionary<string, string> environmentVariables,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RecordCall(workingDirectory, environmentVariables);
        return Task.FromResult(DotnetSdkUtils.SupportsMultiThreadedBuild(_version));
    }

    private void RecordCall(
        string? workingDirectory,
        IReadOnlyDictionary<string, string> environmentVariables)
    {
        Interlocked.Increment(ref _callCount);
        _workingDirectories.Enqueue(workingDirectory);
        _probeEnvironments.Enqueue(environmentVariables.ToDictionary());
    }
}
