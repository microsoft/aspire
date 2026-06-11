// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Utils.EnvironmentChecker;

/// <summary>
/// Orchestrates environment checks using injected IEnvironmentCheck instances.
/// </summary>
internal sealed class EnvironmentChecker(IEnumerable<IEnvironmentCheck> checks, ILogger<EnvironmentChecker> logger) : IEnvironmentChecker
{
    private readonly IEnvironmentCheck[] _checks = checks.OrderBy(c => c.Order).ToArray();

    /// <inheritdoc />
    public async Task<IReadOnlyList<EnvironmentCheckResult>> CheckAllAsync(CancellationToken cancellationToken = default)
    {
        var results = new List<EnvironmentCheckResult>();

        // Run all checks in order (by Order property)
        // Continue running remaining checks even if one fails
        foreach (var check in _checks)
        {
            try
            {
                var checkResults = await check.CheckAsync(cancellationToken);
                results.AddRange(checkResults);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // User requested cancellation, stop all checks
                throw;
            }
            catch (Exception ex)
            {
                // Log the error but continue with other checks
                logger.LogDebug(ex, "Environment check {CheckType} failed with exception", check.GetType().Name);
            }
        }

        return results;
    }
}
