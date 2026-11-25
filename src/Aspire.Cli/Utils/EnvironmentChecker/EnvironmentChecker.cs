// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using Aspire.Cli.Resources;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Utils.EnvironmentChecker;

/// <summary>
/// Orchestrates environment checks using injected IEnvironmentCheck instances.
/// </summary>
internal sealed class EnvironmentChecker : IEnvironmentChecker
{
    internal static readonly TimeSpan s_defaultCheckTimeout = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan s_defaultTotalTimeout = TimeSpan.FromMinutes(2);

    // When the caller cancels, a check may still be releasing external resources (for example the DCP
    // connection check killing the DCP process tree it started). Exiting before that finishes can leave
    // those processes orphaned, so give the cancelled check a short, bounded window to clean up.
    internal static readonly TimeSpan s_cancelledCheckCleanupTimeout = TimeSpan.FromSeconds(5);

    private readonly IEnvironmentCheck[] _checks;
    private readonly ILogger<EnvironmentChecker> _logger;
    private readonly TimeSpan _checkTimeout;
    private readonly TimeSpan _totalTimeout;
    private readonly TimeProvider _timeProvider;

    public EnvironmentChecker(IEnumerable<IEnvironmentCheck> checks, ILogger<EnvironmentChecker> logger)
        : this(checks, logger, s_defaultCheckTimeout, s_defaultTotalTimeout)
    {
    }

    internal EnvironmentChecker(IEnumerable<IEnvironmentCheck> checks, ILogger<EnvironmentChecker> logger, TimeSpan checkTimeout, TimeSpan totalTimeout, TimeProvider? timeProvider = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(checkTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(totalTimeout, TimeSpan.Zero);

        _checks = checks.OrderBy(c => c.Order).ToArray();
        _logger = logger;
        _checkTimeout = checkTimeout;
        _totalTimeout = totalTimeout;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<EnvironmentCheckResult>> CheckAllAsync(CancellationToken cancellationToken = default)
    {
        var results = new List<EnvironmentCheckResult>();
        using var totalTimerCts = new CancellationTokenSource(_totalTimeout, _timeProvider);
        using var totalTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, totalTimerCts.Token);

        // Run all checks in order (by Order property)
        // Continue running remaining checks even if one fails
        foreach (var check in _checks)
        {
            var checkType = check.GetType();
            var checkName = GetCheckName(checkType);
            using var checkTimerCts = new CancellationTokenSource(_checkTimeout, _timeProvider);
            using var checkTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(totalTimeoutCts.Token, checkTimerCts.Token);
            var stopwatch = Stopwatch.StartNew();
            Task<IReadOnlyList<EnvironmentCheckResult>>? checkTask = null;

            _logger.LogDebug("Starting environment check {CheckType}.", checkType.Name);

            try
            {
                // Some checks call synchronous platform APIs before returning their task. Scheduling the
                // invocation keeps those APIs from preventing the timeout token from being observed here.
                checkTask = Task.Run(() => check.CheckAsync(checkTimeoutCts.Token), CancellationToken.None);
                // WaitAsync can time out while a non-cooperative check keeps running. Observe any
                // exception it produces later because it can no longer reach the catch block below.
                _ = checkTask.ContinueWith(
                    static task => _ = task.Exception,
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);

                var checkResults = await checkTask.WaitAsync(checkTimeoutCts.Token).ConfigureAwait(false);
                results.AddRange(checkResults);

                _logger.LogDebug(
                    "Environment check {CheckType} completed in {ElapsedMilliseconds} ms.",
                    checkType.Name,
                    stopwatch.ElapsedMilliseconds);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // User requested cancellation, stop all checks once the cancelled check has released its resources.
                if (checkTask is not null)
                {
                    await WaitForCancelledCheckCleanupAsync(checkTask, checkType).ConfigureAwait(false);
                }

                throw;
            }
            catch (OperationCanceledException) when (totalTimeoutCts.IsCancellationRequested)
            {
                _logger.LogWarning(
                    "Environment checks timed out after {TimeoutSeconds} seconds while running {CheckType}.",
                    _totalTimeout.TotalSeconds,
                    checkType.Name);

                results.Add(CreateTimeoutResult(
                    "environment-checks",
                    string.Format(CultureInfo.CurrentCulture, DoctorCommandStrings.EnvironmentChecksTimedOutMessageFormat, _totalTimeout.TotalSeconds),
                    checkType,
                    _totalTimeout));
                break;
            }
            catch (OperationCanceledException) when (checkTimeoutCts.IsCancellationRequested)
            {
                _logger.LogWarning(
                    "Environment check {CheckType} timed out after {TimeoutSeconds} seconds.",
                    checkType.Name,
                    _checkTimeout.TotalSeconds);

                results.Add(CreateTimeoutResult(
                    checkName,
                    string.Format(CultureInfo.CurrentCulture, DoctorCommandStrings.EnvironmentCheckTimedOutMessageFormat, checkName, _checkTimeout.TotalSeconds),
                    checkType,
                    _checkTimeout));
            }
            catch (Exception ex)
            {
                // Log the error but continue with other checks
                _logger.LogDebug(ex, "Environment check {CheckType} failed with exception", checkType.Name);
            }
        }

        return results;
    }

    private async Task WaitForCancelledCheckCleanupAsync(Task checkTask, Type checkType)
    {
        try
        {
            await checkTask.WaitAsync(s_cancelledCheckCleanupTimeout, _timeProvider).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _logger.LogDebug(
                "Environment check {CheckType} did not finish cleaning up within {TimeoutSeconds} seconds of cancellation.",
                checkType.Name,
                s_cancelledCheckCleanupTimeout.TotalSeconds);
        }
        catch (Exception ex)
        {
            // The check is expected to end with cancellation; any other failure is already superseded by it.
            _logger.LogDebug(ex, "Environment check {CheckType} ended after cancellation.", checkType.Name);
        }
    }

    private static EnvironmentCheckResult CreateTimeoutResult(string name, string message, Type checkType, TimeSpan timeout)
    {
        return new EnvironmentCheckResult
        {
            Category = EnvironmentCheckCategories.Environment,
            Name = name,
            Status = EnvironmentCheckStatus.Warning,
            Message = message,
            Metadata = new JsonObject
            {
                ["checkType"] = checkType.Name,
                ["timeoutSeconds"] = timeout.TotalSeconds,
            }
        };
    }

    private static string GetCheckName(Type checkType)
    {
        var typeName = checkType.Name.EndsWith("Check", StringComparison.Ordinal)
            ? checkType.Name[..^"Check".Length]
            : checkType.Name;
        var name = new System.Text.StringBuilder(typeName.Length + 4);

        for (var i = 0; i < typeName.Length; i++)
        {
            var character = typeName[i];
            if (i > 0 && char.IsUpper(character) && !char.IsUpper(typeName[i - 1]))
            {
                name.Append('-');
            }

            name.Append(char.ToLowerInvariant(character));
        }

        return name.ToString();
    }
}
