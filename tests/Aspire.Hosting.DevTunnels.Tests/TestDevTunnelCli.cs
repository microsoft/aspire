// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.DevTunnels.Tests;

internal sealed class TestDevTunnelCli : DevTunnelCli
{
    private readonly ConcurrentQueue<TestDevTunnelCliResult> _createResults = new();
    private readonly ConcurrentQueue<TestDevTunnelCliResult> _updateResults = new();
    private readonly ConcurrentQueue<TestDevTunnelCliResult> _resetAccessResults = new();

    public TestDevTunnelCli()
        : base("test-devtunnel")
    {
    }

    public ConcurrentQueue<TestDevTunnelCliCall> Calls { get; } = new();

    public void EnqueueCreateResult(int exitCode, string? output = null, string? error = null)
        => _createResults.Enqueue(new(exitCode, output, error));

    public void EnqueueUpdateResult(int exitCode, string? output = null, string? error = null)
        => _updateResults.Enqueue(new(exitCode, output, error));

    public void EnqueueResetAccessResult(int exitCode, string? output = null, string? error = null)
        => _resetAccessResults.Enqueue(new(exitCode, output, error));

    protected override Task<int> RunAsync(
        string[] args,
        TextWriter? outputWriter = null,
        TextWriter? errorWriter = null,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        var (method, tunnelId, results) = args switch
        {
            ["create", ..] => (nameof(CreateTunnelAsync), args.Length > 1 && !args[1].StartsWith("--", StringComparison.Ordinal) ? args[1] : null, _createResults),
            ["update", var id, ..] => (nameof(UpdateTunnelAsync), id, _updateResults),
            ["access", "reset", var id, ..] => (nameof(ResetAccessAsync), id, _resetAccessResults),
            _ => throw new InvalidOperationException($"Unexpected test devtunnel command: {string.Join(" ", args)}")
        };

        Calls.Enqueue(new(method, tunnelId, args));
        return CompleteAsync(results, outputWriter, errorWriter, cancellationToken);
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

internal sealed record TestDevTunnelCliCall(string Method, string? TunnelId, string[] Arguments);

internal sealed record TestDevTunnelCliResult(int ExitCode, string? Output, string? Error);
