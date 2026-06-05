// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Utils.EnvironmentChecker;

/// <summary>
/// Interface for a single environment check.
/// </summary>
internal interface IEnvironmentCheck
{
    /// <summary>
    /// Gets the execution order for this check. Lower values execute first.
    /// </summary>
    int Order { get; }

    /// <summary>
    /// Executes the environment check.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that returns the check results. May return an empty list if the check should be skipped.</returns>
    Task<IReadOnlyList<EnvironmentCheckResult>> CheckAsync(CancellationToken cancellationToken = default);
}
