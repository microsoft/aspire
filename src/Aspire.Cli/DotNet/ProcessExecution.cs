// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Shared;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.DotNet;

/// <summary>
/// Adapts CLI command-wide shutdown policy to shared process execution.
/// </summary>
internal sealed class ProcessExecution : ChildProcess, IProcessExecution
{
    internal ProcessExecution(
        ProcessStartInfo startInfo,
        ILogger logger,
        ProcessInvocationOptions options,
        IEnvironment hostEnvironment)
        : base(startInfo, logger, new ChildProcessOptions
        {
            StandardOutputCallback = line => options.StandardOutputCallback?.Invoke(line),
            StandardErrorCallback = line => options.StandardErrorCallback?.Invoke(line),
            KillEntireProcessTreeOnCancel = options.KillEntireProcessTreeOnCancel,
            Lifetime = options.KillOnParentExit && options.Lifetime != ChildProcessLifetime.AppHost
                ? ChildProcessLifetime.OwnedTree
                : options.Lifetime,
            CompletionPath = options.CompletionPath,
            CreateSupervisorStartInfo = options.CreateSupervisorStartInfo,
            Detached = options.Detached,
            BeginGracefulShutdown = options.GracefulShutdownSignaler is not null && options.ShutdownService is { } shutdownService
                ? () =>
                {
                    if (!shutdownService.IsEnabled)
                    {
                        return null;
                    }
                    shutdownService.BeginGracefulWindow();
                    return shutdownService.GracefulShutdownToken;
                }
                : null,
            RequestGracefulShutdownAsync = options.GracefulShutdownSignaler is { } signaler
                ? (pid, token) => signaler.RequestProcessTreeGracefulShutdownAsync(
                    pid, startTime: null, includeStartTimeForDcp: false, token)
                : null
        }, hostEnvironment.IsWindows())
    {
    }
}
