// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Utils.EnvironmentChecker;

/// <summary>
/// Interface for checking Aspire environment setup.
/// </summary>
internal interface IEnvironmentChecker
{
    /// <summary>
    /// Runs all environment checks in order (fast checks first, expensive checks later).
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that returns all check results.</returns>
    Task<IReadOnlyList<EnvironmentCheckResult>> CheckAllAsync(CancellationToken cancellationToken = default);
}
