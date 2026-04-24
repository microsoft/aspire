// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;
using Aspire.Cli.Projects;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Utils.EnvironmentChecker;

internal sealed class TypeScriptAppHostToolingCheck : IEnvironmentCheck
{
    private readonly IProjectLocator _projectLocator;
    private readonly ILanguageDiscovery _languageDiscovery;
    private readonly CliExecutionContext _executionContext;
    private readonly ILogger<TypeScriptAppHostToolingCheck> _logger;
    private readonly Func<string, string?> _commandResolver;

    public TypeScriptAppHostToolingCheck(
        IProjectLocator projectLocator,
        ILanguageDiscovery languageDiscovery,
        CliExecutionContext executionContext,
        ILogger<TypeScriptAppHostToolingCheck> logger)
        : this(projectLocator, languageDiscovery, executionContext, logger, PathLookupHelper.FindFullPathFromPath)
    {
    }

    internal TypeScriptAppHostToolingCheck(
        IProjectLocator projectLocator,
        ILanguageDiscovery languageDiscovery,
        CliExecutionContext executionContext,
        ILogger<TypeScriptAppHostToolingCheck> logger,
        Func<string, string?> commandResolver)
    {
        _projectLocator = projectLocator;
        _languageDiscovery = languageDiscovery;
        _executionContext = executionContext;
        _logger = logger;
        _commandResolver = commandResolver;
    }

    public int Order => 31;

    public async Task<IReadOnlyList<EnvironmentCheckResult>> CheckAsync(CancellationToken cancellationToken = default)
    {
        var appHostFile = await ResolveTypeScriptAppHostAsync(cancellationToken);
        if (appHostFile?.Directory is not { Exists: true } appHostDirectory)
        {
            return [];
        }

        var toolchain = TypeScriptAppHostToolchainResolver.Resolve(appHostDirectory);
        var missingResults = new List<EnvironmentCheckResult>();

        foreach (var command in TypeScriptAppHostToolchainResolver.GetRequiredCommands(toolchain))
        {
            if (CommandPathResolver.TryResolveCommand(command, _commandResolver, out _, out var errorMessage))
            {
                continue;
            }

            missingResults.Add(new EnvironmentCheckResult
            {
                Category = "environment",
                Name = $"typescript-apphost-{command}",
                Status = EnvironmentCheckStatus.Fail,
                Message = $"TypeScript AppHost requires '{command}'.",
                Details = errorMessage,
                Fix = $"Install {TypeScriptAppHostToolchainResolver.GetDisplayName(toolchain)} tooling and rerun 'aspire doctor'.",
                Link = CommandPathResolver.GetInstallationLink(command),
                Metadata = new JsonObject
                {
                    ["language"] = KnownLanguageId.TypeScript,
                    ["toolchain"] = TypeScriptAppHostToolchainResolver.GetCommandName(toolchain),
                    ["command"] = command
                }
            });
        }

        if (missingResults.Count > 0)
        {
            return missingResults;
        }

        return
        [
            new EnvironmentCheckResult
            {
                Category = "environment",
                Name = "typescript-apphost-tools",
                Status = EnvironmentCheckStatus.Pass,
                Message = $"TypeScript AppHost tooling found ({string.Join(", ", TypeScriptAppHostToolchainResolver.GetRequiredCommands(toolchain))}).",
                Metadata = new JsonObject
                {
                    ["language"] = KnownLanguageId.TypeScript,
                    ["toolchain"] = TypeScriptAppHostToolchainResolver.GetCommandName(toolchain),
                    ["appHostPath"] = appHostFile.FullName
                }
            }
        ];
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
            _logger.LogDebug(ex, "Failed to resolve TypeScript AppHost for environment check");
            return null;
        }
    }
}
