// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;
using Aspire.Cli.Projects;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Utils.EnvironmentChecker;

internal sealed class TypeScriptAppHostToolingCheck : IEnvironmentCheck
{
    internal const string YarnClassicCheckName = "typescript-apphost-yarn-classic";
    internal const string ToolsCheckName = "typescript-apphost-tools";

    private readonly IProjectLocator _projectLocator;
    private readonly ILanguageDiscovery _languageDiscovery;
    private readonly CliExecutionContext _executionContext;
    private readonly IEnvironment _environment;
    private readonly ILogger<TypeScriptAppHostToolingCheck> _logger;
    private readonly Func<string, string?> _commandResolver;

    public TypeScriptAppHostToolingCheck(
        IProjectLocator projectLocator,
        ILanguageDiscovery languageDiscovery,
        CliExecutionContext executionContext,
        IEnvironment environment,
        ILogger<TypeScriptAppHostToolingCheck> logger)
        : this(projectLocator, languageDiscovery, executionContext, environment, logger, PathLookupHelper.FindFullPathFromPath)
    {
    }

    internal TypeScriptAppHostToolingCheck(
        IProjectLocator projectLocator,
        ILanguageDiscovery languageDiscovery,
        CliExecutionContext executionContext,
        IEnvironment environment,
        ILogger<TypeScriptAppHostToolingCheck> logger,
        Func<string, string?> commandResolver)
    {
        _projectLocator = projectLocator;
        _languageDiscovery = languageDiscovery;
        _executionContext = executionContext;
        _environment = environment;
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

        TypeScriptAppHostToolchain toolchain;
        try
        {
            toolchain = TypeScriptAppHostToolchainResolver.Resolve(appHostDirectory, _environment, _logger);
        }
        catch (YarnClassicNotSupportedException ex)
        {
            return
            [
                new EnvironmentCheckResult
                {
                    Category = EnvironmentCheckCategories.Environment,
                    Name = YarnClassicCheckName,
                    Status = EnvironmentCheckStatus.Fail,
                    Message = "TypeScript AppHost does not support Yarn Classic.",
                    Details = ex.Message,
                    Fix = "Upgrade to Yarn 4 or later, or switch to npm, pnpm, or Bun, then rerun 'aspire doctor'.",
                    Link = "https://yarnpkg.com/getting-started/install",
                    Metadata = new JsonObject
                    {
                        ["language"] = KnownLanguageId.TypeScript,
                        ["appHostPath"] = appHostFile.FullName
                    }
                }
            ];
        }

        var missingResults = new List<EnvironmentCheckResult>();

        foreach (var command in TypeScriptAppHostToolchainResolver.GetRequiredCommands(toolchain))
        {
            if (CommandPathResolver.TryResolveCommand(command, _commandResolver, out _, out var errorMessage))
            {
                continue;
            }

            missingResults.Add(new EnvironmentCheckResult
            {
                Category = EnvironmentCheckCategories.Environment,
                Name = GetMissingCommandCheckName(command),
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
                Category = EnvironmentCheckCategories.Environment,
                Name = ToolsCheckName,
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

    // Delegates to the shared resolver so the doctor tooling check and `aspire update --migrate` stay in
    // lockstep on how the TypeScript AppHost entry point is located.
    private Task<FileInfo?> ResolveTypeScriptAppHostAsync(CancellationToken cancellationToken)
        => LegacyTypeScriptAppHost.ResolveTypeScriptAppHostAsync(
            _projectLocator,
            _languageDiscovery,
            _executionContext.WorkingDirectory,
            _logger,
            cancellationToken);

    internal static string GetMissingCommandCheckName(string command) => $"typescript-apphost-{command}";
}
