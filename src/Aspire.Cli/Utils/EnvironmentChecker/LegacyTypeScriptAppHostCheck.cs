// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text.Json.Nodes;
using Aspire.Cli.Projects;
using Aspire.Cli.Resources;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Utils.EnvironmentChecker;

/// <summary>
/// Surfaces a non-blocking warning when a TypeScript AppHost still uses the legacy
/// <c>apphost.ts</c> layout (importing generated SDK files from <c>./.modules/aspire.js</c>)
/// instead of the modern <c>apphost.mts</c> layout (<c>./.aspire/modules/aspire.mjs</c>).
/// </summary>
/// <remarks>
/// The legacy layout continues to work, but read-only commands (<c>aspire ls</c>, <c>ps</c>,
/// <c>doctor</c>) would otherwise never signal that a newer format — and an automatic
/// <c>aspire migrate</c> — is available.
/// See: https://github.com/microsoft/aspire/issues/17842
/// </remarks>
internal sealed class LegacyTypeScriptAppHostCheck : IEnvironmentCheck
{
    internal const string CheckName = "legacy-typescript-apphost";

    private readonly IProjectLocator _projectLocator;
    private readonly ILanguageDiscovery _languageDiscovery;
    private readonly CliExecutionContext _executionContext;
    private readonly ILogger<LegacyTypeScriptAppHostCheck> _logger;

    public LegacyTypeScriptAppHostCheck(
        IProjectLocator projectLocator,
        ILanguageDiscovery languageDiscovery,
        CliExecutionContext executionContext,
        ILogger<LegacyTypeScriptAppHostCheck> logger)
    {
        _projectLocator = projectLocator;
        _languageDiscovery = languageDiscovery;
        _executionContext = executionContext;
        _logger = logger;
    }

    /// <inheritdoc />
    public int Order => 102; // Run after the TypeScript tooling check (31) and legacy settings check (101).

    /// <inheritdoc />
    public async Task<IReadOnlyList<EnvironmentCheckResult>> CheckAsync(CancellationToken cancellationToken = default)
    {
        var appHostFile = await ResolveTypeScriptAppHostAsync(cancellationToken);
        if (appHostFile?.Directory is not { Exists: true } appHostDirectory)
        {
            return [];
        }

        if (!LegacyTypeScriptAppHost.IsLegacyAppHostFile(appHostFile) ||
            !LegacyTypeScriptAppHost.IsLegacyLayout(appHostDirectory.FullName))
        {
            return [];
        }

        var result = new EnvironmentCheckResult
        {
            Category = EnvironmentCheckCategories.AppHost,
            Name = CheckName,
            Status = EnvironmentCheckStatus.Warning,
            Message = string.Format(CultureInfo.CurrentCulture, DoctorCommandStrings.LegacyTypeScriptAppHostMessageFormat, appHostFile.FullName),
            Fix = DoctorCommandStrings.LegacyTypeScriptAppHostFix,
            Metadata = new JsonObject
            {
                ["language"] = KnownLanguageId.TypeScript,
                ["appHostPath"] = appHostFile.FullName
            }
        };

        return [result];
    }

    private async Task<FileInfo?> ResolveTypeScriptAppHostAsync(CancellationToken cancellationToken)
    {
        try
        {
            var configuredAppHost = await _projectLocator.GetAppHostFromSettingsAsync(cancellationToken);
            if (configuredAppHost is not null &&
                TypeScriptAppHostToolchainResolver.IsTypeScriptLanguage(_languageDiscovery.GetLanguageByFile(configuredAppHost)))
            {
                return configuredAppHost;
            }

            var detectedLanguageId = await _languageDiscovery.DetectLanguageRecursiveAsync(_executionContext.WorkingDirectory, cancellationToken);
            if (detectedLanguageId is null)
            {
                return null;
            }

            var detectedLanguage = _languageDiscovery.GetLanguageById(detectedLanguageId.Value);
            if (!TypeScriptAppHostToolchainResolver.IsTypeScriptLanguage(detectedLanguage))
            {
                return null;
            }

            var discoveredPath = detectedLanguage?.FindInDirectory(_executionContext.WorkingDirectory.FullName);
            return discoveredPath is not null ? new FileInfo(discoveredPath) : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to resolve TypeScript AppHost for legacy layout environment check");
            return null;
        }
    }
}
