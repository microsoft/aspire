// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.DevTunnels.Tests;

internal sealed class TestDevTunnelCli : DevTunnelCli
{
    private readonly ConcurrentQueue<TestDevTunnelCliResult> _createResults = new();
    private readonly ConcurrentQueue<TestDevTunnelCliResult> _updateResults = new();

    public TestDevTunnelCli()
        : base("test-devtunnel")
    {
    }

    public ConcurrentQueue<TestDevTunnelCliCall> Calls { get; } = new();

    public void EnqueueCreateResult(int exitCode, string? output = null, string? error = null)
        => _createResults.Enqueue(new(exitCode, output, error));

    public void EnqueueUpdateResult(int exitCode, string? output = null, string? error = null)
        => _updateResults.Enqueue(new(exitCode, output, error));

    public override Task<int> CreateTunnelAsync(
        string? tunnelId = null,
        DevTunnelOptions? options = null,
        TextWriter? outputWriter = null,
        TextWriter? errorWriter = null,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        Calls.Enqueue(new(nameof(CreateTunnelAsync), tunnelId));
        return CompleteAsync(_createResults, outputWriter, errorWriter, cancellationToken);
    }

    public override Task<int> UpdateTunnelAsync(
        string tunnelId,
        DevTunnelOptions? options = null,
        TextWriter? outputWriter = null,
        TextWriter? errorWriter = null,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        Calls.Enqueue(new(nameof(UpdateTunnelAsync), tunnelId));
        return CompleteAsync(_updateResults, outputWriter, errorWriter, cancellationToken);
    }

    private static Task<int> CompleteAsync(
        ConcurrentQueue<TestDevTunnelCliResult> results,
        TextWriter? outputWriter,
        TextWriter? errorWriter,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!results.TryDequeue(out var result))
        {
            throw new InvalidOperationException("No test devtunnel CLI result was configured.");
        }

        if (result.Output is not null)
        {
            outputWriter?.WriteLine(result.Output);
        }

        if (result.Error is not null)
        {
            errorWriter?.WriteLine(result.Error);
        }

        return Task.FromResult(result.ExitCode);
    }
}

internal sealed record TestDevTunnelCliCall(string Method, string? TunnelId);

internal sealed record TestDevTunnelCliResult(int ExitCode, string? Output, string? Error);
