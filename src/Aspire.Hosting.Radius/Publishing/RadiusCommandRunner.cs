// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Radius.Publishing;

/// <summary>
/// The result of one external command run on behalf of a Radius deploy-time step.
/// </summary>
internal readonly record struct ProcessRunResult(int ExitCode, string StandardOutput);

/// <summary>
/// Runs one external command on behalf of a Radius deploy-time step. Returns
/// <see langword="null"/> when the executable is not on PATH, which callers treat as "unknown"
/// rather than as a failure. A <see langword="null"/> value in <paramref name="environment"/>
/// removes that variable from the child's environment.
/// </summary>
/// <remarks>
/// Exists so the steps that must talk to a real cluster can be exercised without one. The
/// production composition roots pass the process-based implementation in
/// <see cref="RadiusDeploymentPipelineStep"/>.
/// </remarks>
internal delegate Task<ProcessRunResult?> RadiusCommandRunner(
    string fileName,
    string[] arguments,
    IReadOnlyDictionary<string, string?>? environment,
    CancellationToken cancellationToken);
