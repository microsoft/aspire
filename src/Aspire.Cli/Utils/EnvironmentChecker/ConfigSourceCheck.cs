// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Cli.Configuration;
using Aspire.Cli.Projects;
using Aspire.Cli.Resources;
using Aspire.Shared.Telemetry;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Utils.EnvironmentChecker;

internal sealed class ConfigSourceCheck(
    ConfigSourceService configSources,
    IProjectLocator projectLocator,
    CliExecutionContext executionContext,
    ILogger<ConfigSourceCheck> logger) : IEnvironmentCheck
{
    internal const string MachineCheckName = "config-source";
    internal const string AppHostCheckName = "apphost-config-source";

    public int Order => 5;

    public async Task<IReadOnlyList<EnvironmentCheckResult>> CheckAsync(CancellationToken cancellationToken = default)
    {
        var results = new List<EnvironmentCheckResult>();
        ConfigSource? candidate = null;
        try
        {
            var resolution = await configSources.ResolveCandidateAsync(cancellationToken);
            candidate = resolution.Source;
            var inconclusive = resolution.DetectionOutcome is InternalMicrosoftDetectorOutcome.Failed or InternalMicrosoftDetectorOutcome.TimedOut;
            var metadata = candidate is not null ? CreateSourceMetadata(candidate) : new JsonObject();
            if (inconclusive)
            {
                metadata["detectionOutcome"] = resolution.DetectionOutcome;
            }
            else
            {
                metadata["available"] = candidate is not null;
            }
            if (candidate is not null)
            {
                metadata["origin"] = resolution.IsExplicit ? CliConfigNames.InitSourceUrl : "microsoftEmployee";
            }
            results.Add(new EnvironmentCheckResult
            {
                Category = EnvironmentCheckCategories.Environment,
                Name = MachineCheckName,
                Status = inconclusive ? EnvironmentCheckStatus.Warning : EnvironmentCheckStatus.Pass,
                Message = inconclusive ? ConfigSourceStrings.DoctorCandidateFailed
                    : candidate is null ? ConfigSourceStrings.DoctorUnavailable : ConfigSourceStrings.DoctorAvailable,
                Details = candidate is not null ? ConfigSourceService.DescribeCandidate(candidate, resolution.IsExplicit) : null,
                Metadata = metadata
            });
        }
        catch (ConfigSourceException ex)
        {
            logger.LogDebug("Doctor configuration source discovery failed ({FailureType}).", ex.GetType().Name);
            results.Add(new EnvironmentCheckResult
            {
                Category = EnvironmentCheckCategories.Environment,
                Name = MachineCheckName,
                Status = EnvironmentCheckStatus.Warning,
                Message = ConfigSourceStrings.DoctorCandidateFailed,
                Details = ex.Message
            });
        }

        try
        {
            var appHosts = await EnvironmentChecker.ResolveAppHostFilesAsync(
                projectLocator, executionContext.WorkingDirectory, logger, cancellationToken);
            if (appHosts.Count == 0)
            {
                return results;
            }

            var appHost = appHosts[0];
            var path = ConfigurationHelper.FindNearestConfigFilePath(appHost.Directory!)
                ?? Path.Combine(appHost.Directory!.FullName, AspireConfigFile.FileName);
            var binding = await configSources.ReadBindingAsync(path, cancellationToken);
            if (binding is null && candidate is null)
            {
                return results;
            }

            var metadata = binding is { OptOut: false } ? CreateSourceMetadata(binding) : new JsonObject();
            metadata["appHostPath"] = appHost.FullName;
            metadata["settingsFile"] = path;
            metadata["state"] = binding is null ? "unconfigured" : binding.OptOut ? "optedOut" : "bound";
            var details = string.Format(CultureInfo.CurrentCulture, ConfigSourceStrings.DoctorProjectDetails, appHost.FullName, path);
            if (binding is { OptOut: false })
            {
                details += "\n" + string.Format(CultureInfo.CurrentCulture, ConfigSourceStrings.DoctorBindingDetails,
                    binding.Repository, binding.Path, binding.Ref ?? ConfigSourceStrings.DefaultBranch,
                    binding.AppliedVersion ?? DoctorCommandStrings.VersionUnknown);
            }
            results.Add(new EnvironmentCheckResult
            {
                Category = EnvironmentCheckCategories.AppHost,
                Name = AppHostCheckName,
                Status = binding is null ? EnvironmentCheckStatus.Warning : EnvironmentCheckStatus.Pass,
                Message = binding is null ? ConfigSourceStrings.DoctorUnbound
                    : binding.OptOut ? ConfigSourceStrings.DoctorOptOut : ConfigSourceStrings.DoctorBound,
                Details = details,
                Fix = binding is null ? ConfigSourceStrings.DoctorAdoptFix : null,
                Metadata = metadata
            });
        }
        catch (ProjectLocatorException ex) when (ex.FailureReason is ProjectLocatorFailureReason.NoProjectFileFound or ProjectLocatorFailureReason.ProjectFileDoesntExist)
        {
            logger.LogDebug("Skipping doctor project configuration source diagnostics because no AppHost was found.");
        }
        catch (Exception ex) when (ex is ConfigSourceException or JsonException or IOException or UnauthorizedAccessException or ProjectLocatorException)
        {
            logger.LogDebug("Doctor project configuration source check failed ({FailureType}).", ex.GetType().Name);
            results.Add(new EnvironmentCheckResult
            {
                Category = EnvironmentCheckCategories.AppHost,
                Name = AppHostCheckName,
                Status = EnvironmentCheckStatus.Warning,
                Message = ConfigSourceStrings.DoctorProjectFailed,
                Details = ex is ConfigSourceException ? ex.Message : null
            });
        }

        return results;
    }

    private static JsonObject CreateSourceMetadata(ConfigSource source)
    {
        var metadata = new JsonObject
        {
            ["repository"] = source.Repository,
            ["path"] = source.Path
        };
        if (source.Ref is not null)
        {
            metadata["ref"] = source.Ref;
        }
        if (source.AppliedVersion is not null)
        {
            metadata["appliedVersion"] = source.AppliedVersion;
        }

        return metadata;
    }
}
