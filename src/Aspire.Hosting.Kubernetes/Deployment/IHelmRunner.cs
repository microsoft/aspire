// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Kubernetes;

/// <summary>
/// Abstraction for running Helm CLI commands, enabling testability of Helm operations.
/// </summary>
internal interface IHelmRunner
{
    /// <summary>
    /// Runs a Helm command with the specified arguments.
    /// </summary>
    /// <param name="arguments">The arguments to pass to the helm command.</param>
    /// <param name="workingDirectory">The working directory for the process, or null to use the current directory.</param>
    /// <param name="onOutputData">Callback for stdout lines.</param>
    /// <param name="onErrorData">Callback for stderr lines.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The process exit code.</returns>
    Task<int> RunAsync(
        string arguments,
        string? workingDirectory = null,
        Action<string>? onOutputData = null,
        Action<string>? onErrorData = null,
        CancellationToken cancellationToken = default);
}
