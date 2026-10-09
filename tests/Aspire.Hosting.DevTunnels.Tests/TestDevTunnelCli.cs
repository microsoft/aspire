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
    private readonly ConcurrentQueue<TestDevTunnelCliResult> _showResults = new();
    private readonly ConcurrentQueue<TestDevTunnelCliResult> _showPortResults = new();
    private readonly ConcurrentQueue<TestDevTunnelCliResult> _createPortResults = new();
    private readonly ConcurrentQueue<TestDevTunnelCliResult> _deletePortResults = new();
    private readonly ConcurrentQueue<TestDevTunnelCliResult> _createAccessResults = new();
    private readonly ConcurrentQueue<TestDevTunnelCliResult> _listAccessResults = new();

    public TestDevTunnelCli()
        : base("test-devtunnel")
    {
    }

    public ConcurrentQueue<TestDevTunnelCliCall> Calls { get; } = new();

    public Action<TestDevTunnelCliCall>? OnCall { get; set; }

    public void EnqueueCreateResult(int exitCode, string? output = null, string? error = null)
        => _createResults.Enqueue(new(exitCode, output, error));

    public void EnqueueUpdateResult(int exitCode, string? output = null, string? error = null)
        => _updateResults.Enqueue(new(exitCode, output, error));

    public void EnqueueResetAccessResult(int exitCode, string? output = null, string? error = null)
        => _resetAccessResults.Enqueue(new(exitCode, output, error));

    public void EnqueueShowResult(int exitCode, string? output = null, string? error = null)
        => _showResults.Enqueue(new(exitCode, output, error));

    public void EnqueueShowPortResult(int exitCode, string? output = null, string? error = null)
        => _showPortResults.Enqueue(new(exitCode, output, error));

    public void EnqueueCreatePortResult(int exitCode, string? output = null, string? error = null)
        => _createPortResults.Enqueue(new(exitCode, output, error));

    public void EnqueueDeletePortResult(int exitCode, string? output = null, string? error = null)
        => _deletePortResults.Enqueue(new(exitCode, output, error));

    public void EnqueueCreateAccessResult(int exitCode, string? output = null, string? error = null)
        => _createAccessResults.Enqueue(new(exitCode, output, error));

    public void EnqueueListAccessResult(int exitCode, string? output = null, string? error = null)
        => _listAccessResults.Enqueue(new(exitCode, output, error));

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
            ["show", var id, ..] => (nameof(ShowTunnelAsync), id, _showResults),
            ["port", "show", var id, ..] => (nameof(ShowPortAsync), id, _showPortResults),
            ["port", "create", var id, ..] => (nameof(CreatePortAsync), id, _createPortResults),
            ["port", "delete", var id, ..] => (nameof(DeletePortAsync), id, _deletePortResults),
            ["access", "create", var id, ..] => (nameof(CreateAccessAsync), id, _createAccessResults),
            ["access", "list", var id, ..] => (nameof(ListAccessAsync), id, _listAccessResults),
            _ => throw new InvalidOperationException($"Unexpected test devtunnel command: {string.Join(" ", args)}")
        };

        var call = new TestDevTunnelCliCall(method, tunnelId, args);
        Calls.Enqueue(call);
        OnCall?.Invoke(call);
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
