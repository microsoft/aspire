// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Cli.Configuration;
using Aspire.Cli.Resources;

namespace Aspire.Cli.Utils.EnvironmentChecker;

/// <summary>
/// Checks for the presence of a legacy <c>.aspire/settings.json</c> file without a sibling
/// <c>aspire.config.json</c>, and surfaces a warning hint to migrate.
/// </summary>
/// <remarks>
/// The legacy file continues to work — this check emits a non-blocking warning. Migration is
/// triggered automatically by any write command (aspire run/add/init/update/pipeline),
/// but users who only run read-only commands (aspire ls, ps, doctor) would otherwise never
/// see a signal that a newer format exists.
/// See: https://github.com/microsoft/aspire/issues/17632
/// </remarks>
internal sealed class LegacySettingsFileCheck(CliExecutionContext executionContext) : IEnvironmentCheck
{
    internal const string CheckName = "legacy-settings-file";

    /// <inheritdoc />
    public int Order => 101; // Run after core checks and deprecated agent config check (100)

    /// <inheritdoc />
    public Task<IReadOnlyList<EnvironmentCheckResult>> CheckAsync(CancellationToken cancellationToken = default)
    {
        var workingDirectory = executionContext.WorkingDirectory;

        // Walk up the directory tree looking for a legacy .aspire/settings.json
        // that doesn't have a sibling aspire.config.json at the same root.
        var searchDir = workingDirectory;

        while (searchDir is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var modernConfigPath = Path.Combine(searchDir.FullName, AspireConfigFile.FileName);
            if (File.Exists(modernConfigPath))
            {
                // Found the modern config file — no legacy concern at this level or above.
                break;
            }

            var legacySettingsPath = ConfigurationHelper.BuildPathToSettingsJsonFile(searchDir.FullName);
            if (File.Exists(legacySettingsPath))
            {
                // Found a legacy file without a sibling modern config — surface the hint.
                var result = new EnvironmentCheckResult
                {
                    Category = EnvironmentCheckCategories.Environment,
                    Name = CheckName,
                    Status = EnvironmentCheckStatus.Warning,
                    Message = string.Format(CultureInfo.CurrentCulture, DoctorCommandStrings.LegacySettingsDetectedMessageFormat, legacySettingsPath),
                    Fix = DoctorCommandStrings.LegacySettingsDetectedFix
                };

                return Task.FromResult<IReadOnlyList<EnvironmentCheckResult>>([result]);
            }

            searchDir = searchDir.Parent;
        }

        return Task.FromResult<IReadOnlyList<EnvironmentCheckResult>>([]);
    }
}
