// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Cli.Resources;

namespace Aspire.Cli.Utils.EnvironmentChecker;

/// <summary>
/// Checks for the presence of a legacy .aspire/settings.json file without a modern aspire.config.json.
/// </summary>
internal sealed class LegacySettingsCheck : IEnvironmentCheck
{
    private readonly CliExecutionContext _executionContext;

    public LegacySettingsCheck(CliExecutionContext executionContext)
    {
        ArgumentNullException.ThrowIfNull(executionContext);
        _executionContext = executionContext;
    }

    /// <inheritdoc />
    public int Order => 110; // Run after core checks

    /// <inheritdoc />
    public Task<IReadOnlyList<EnvironmentCheckResult>> CheckAsync(CancellationToken cancellationToken = default)
    {
        var results = new List<EnvironmentCheckResult>();

        var configPath = ConfigurationHelper.FindNearestConfigFilePath(_executionContext.WorkingDirectory);
        if (configPath is not null)
        {
            var configFile = new FileInfo(configPath);
            var legacyRoot = ConfigurationHelper.GetLegacySettingsRootDirectory(configFile);

            if (legacyRoot is not null)
            {
                results.Add(new EnvironmentCheckResult
                {
                    Category = "aspire",
                    Name = "legacy-settings-file",
                    Status = EnvironmentCheckStatus.Info,
                    Message = string.Format(CultureInfo.CurrentCulture, DoctorCommandStrings.LegacySettingsDetected, configPath),
                    Fix = DoctorCommandStrings.LegacySettingsFix
                });
            }
        }

        return Task.FromResult<IReadOnlyList<EnvironmentCheckResult>>(results);
    }
}
