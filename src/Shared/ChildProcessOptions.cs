// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;

namespace Aspire.Shared;

/// <summary>
/// Output and cancellation behavior shared by redirected process executions.
/// </summary>
internal sealed class ChildProcessOptions
{
    /// <summary>
    /// Gets the clock used for output-drain and termination deadlines.
    /// </summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
    public TimeSpan OutputDrainIdleTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan TerminationTimeout { get; init; } = TimeSpan.FromSeconds(5);
    /// <summary>
    /// Begins the caller's graceful window, or returns null to terminate immediately.
    /// </summary>
    public Func<CancellationToken?>? BeginGracefulShutdown { get; init; }
    public Func<int, CancellationToken, Task>? RequestGracefulShutdownAsync { get; init; }

    public Action<string>? StandardOutputCallback { get; init; }
    public Action<string>? StandardErrorCallback { get; init; }
    public ChildProcessLifetime Lifetime { get; init; }
    public string? CompletionPath { get; init; }
    public Func<ProcessStartInfo, string?, TimeSpan, ProcessStartInfo> CreateSupervisorStartInfo { get; init; } = ProcessSupervisor.CreateStartInfo;
    public bool Detached { get; init; }
    public bool KillEntireProcessTreeOnCancel { get; init; } = true;
}

/// <summary>
/// Separates caller-managed roots, contained worker trees, and DCP-owned AppHost descendants.
/// </summary>
internal enum ChildProcessLifetime
{
    CallerManaged,
    OwnedTree,
    AppHost
}
