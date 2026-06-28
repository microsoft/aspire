// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace Aspire.Hosting.Postgres;

/// <summary>
/// Health check for a PostgreSQL server resource that gates the resource's Healthy state behind a short
/// stability window instead of a single <c>SELECT 1</c>.
/// </summary>
/// <remarks>
/// On a fresh data volume the official PostgreSQL image runs <c>initdb</c> against a temporary server on
/// the unix socket (with TCP closed) and then <b>restarts</b> to the real listener. A single probe can
/// pass before that restart, which would flip the resource Healthy, release every <c>WaitFor</c>
/// dependent, and trigger native database creation — all straight into the restart's connection reset.
/// <para>
/// To avoid that, this check requires a single connection to survive several consecutive <c>SELECT 1</c>
/// probes (the restart resets the connection mid-loop) before reporting Healthy. Once the server has
/// proven durably ready, the check latches and falls back to a single cheap probe per call, so the gate
/// only adds latency during first start. It only ever <i>delays</i> Healthy within a bounded window, so it
/// cannot deadlock dependents.
/// </para>
/// </remarks>
internal sealed class PostgresServerHealthCheck : IHealthCheck
{
    // ~6 probes spaced ~500ms apart (~3s) comfortably outlasts the initdb restart window.
    private const int ConsecutiveProbes = 6;
    private const int ProbeIntervalMilliseconds = 500;

    private readonly Func<string?> _connectionStringAccessor;
    private readonly Func<int, CancellationToken, Task> _runProbes;

    // Once the server has been observed durably ready we latch and stop running the full probe window.
    private volatile bool _stable;

    public PostgresServerHealthCheck(Func<string?> connectionStringAccessor)
    {
        ArgumentNullException.ThrowIfNull(connectionStringAccessor);

        _connectionStringAccessor = connectionStringAccessor;
        _runProbes = RunProbesAsync;
    }

    /// <summary>
    /// Test seam: lets unit tests drive the consecutive/latch control flow without a live server by
    /// supplying the routine that runs the requested number of consecutive probes.
    /// </summary>
    internal PostgresServerHealthCheck(Func<string?> connectionStringAccessor, Func<int, CancellationToken, Task> runProbes)
    {
        ArgumentNullException.ThrowIfNull(connectionStringAccessor);
        ArgumentNullException.ThrowIfNull(runProbes);

        _connectionStringAccessor = connectionStringAccessor;
        _runProbes = runProbes;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(_connectionStringAccessor()))
        {
            return HealthCheckResult.Unhealthy("The connection string is unavailable.");
        }

        // Until proven stable, require the full consecutive-probe window; afterwards a single probe.
        var probeCount = _stable ? 1 : ConsecutiveProbes;

        try
        {
            await _runProbes(probeCount, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A probe failed (e.g. the connection was reset by the initdb restart). Stay Unhealthy; the
            // next poll retries the full window because we have not latched.
            return HealthCheckResult.Unhealthy(ex.Message, ex);
        }

        _stable = true;
        return HealthCheckResult.Healthy();
    }

    private async Task RunProbesAsync(int count, CancellationToken cancellationToken)
    {
        var connectionString = _connectionStringAccessor()!;

        // HACK: The Npgsql client defaults to using the username in the connection string if the database
        //       is not specified. We work with a non-database-scoped connection string, so pin it to the
        //       always-present 'postgres' database. Matches the behavior of the previous AddNpgSql check.
        using var connection = new NpgsqlConnection(connectionString + ";Database=postgres;");
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        // Run the probes on this single connection: if the initdb restart happens during the window it
        // resets this connection and one of the probes throws, keeping the gate closed.
        for (var i = 0; i < count; i++)
        {
            if (i > 0)
            {
                await Task.Delay(ProbeIntervalMilliseconds, cancellationToken).ConfigureAwait(false);
            }

            using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1;";
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
