// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Migrations;
using Aspire.Cli.Resources;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Utils.EnvironmentChecker;

/// <summary>
/// Surfaces a non-blocking <c>aspire doctor</c> warning for each <see cref="IMigration"/> that
/// applies to the current project, nudging the user toward running <c>aspire update --migrate</c>.
/// </summary>
/// <remarks>
/// This is the read-only half of the migration system: it shares the exact same
/// <see cref="IMigration.DetectAsync(MigrationContext, CancellationToken)"/> detection that <c>aspire update --migrate</c> uses to apply changes,
/// so the two can never drift. Any new migration registered in DI automatically shows up here with
/// no changes to this check.
/// See: https://github.com/microsoft/aspire/issues/17842
/// </remarks>
internal sealed class PendingMigrationsCheck : IEnvironmentCheck
{
    private readonly IEnumerable<IMigration> _migrations;
    private readonly ILogger<PendingMigrationsCheck> _logger;

    public PendingMigrationsCheck(
        IEnumerable<IMigration> migrations,
        ILogger<PendingMigrationsCheck> logger)
    {
        _migrations = migrations;
        _logger = logger;
    }

    /// <inheritdoc />
    // Run last, after the deprecated agent config (100) and legacy settings (101) checks, so the
    // migration nudges appear together at the end of the deprecated/legacy section of the report.
    public int Order => 102;

    /// <inheritdoc />
    public async Task<IReadOnlyList<EnvironmentCheckResult>> CheckAsync(CancellationToken cancellationToken = default)
    {
        var results = new List<EnvironmentCheckResult>();

        foreach (var migration in _migrations.OrderBy(m => m.Order))
        {
            MigrationDescriptor? descriptor;
            try
            {
                descriptor = await migration.DetectAsync(MigrationContext.CurrentDirectory, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // A broken migration provider must not fail the whole doctor run.
                _logger.LogDebug(ex, "Migration '{MigrationId}' detection failed", migration.Id);
                continue;
            }

            if (descriptor is null)
            {
                continue;
            }

            results.Add(new EnvironmentCheckResult
            {
                Category = EnvironmentCheckCategories.AppHost,
                Name = migration.Id,
                Status = EnvironmentCheckStatus.Warning,
                Message = descriptor.Detail,
                Fix = DoctorCommandStrings.PendingMigrationFix,
                Metadata = descriptor.Metadata
            });
        }

        return results;
    }
}
