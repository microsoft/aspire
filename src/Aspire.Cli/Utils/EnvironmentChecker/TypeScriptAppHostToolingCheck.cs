// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Text.Json.Nodes;
using Aspire.Cli.Projects;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Utils.EnvironmentChecker;

internal sealed class TypeScriptAppHostToolingCheck : IEnvironmentCheck
{
    internal const string YarnClassicCheckName = "typescript-apphost-yarn-classic";
    internal const string DenoVersionCheckName = "typescript-apphost-deno-version";
    internal const string ToolsCheckName = "typescript-apphost-tools";
    private static readonly TimeSpan s_versionCheckTimeout = TimeSpan.FromSeconds(5);

    private readonly IProjectLocator _projectLocator;
    private readonly ILanguageDiscovery _languageDiscovery;
    private readonly CliExecutionContext _executionContext;
    private readonly IEnvironment _environment;
    private readonly ILogger<TypeScriptAppHostToolingCheck> _logger;
    private readonly Func<string, string?> _commandResolver;
    private readonly Func<string, CancellationToken, Task<string?>> _denoVersionResolver;

    public TypeScriptAppHostToolingCheck(
        IProjectLocator projectLocator,
        ILanguageDiscovery languageDiscovery,
        CliExecutionContext executionContext,
        IEnvironment environment,
        ILogger<TypeScriptAppHostToolingCheck> logger)
        : this(
            projectLocator,
            languageDiscovery,
            executionContext,
            environment,
            logger,
            PathLookupHelper.FindFullPathFromPath,
            (path, cancellationToken) => GetDenoVersionOutputAsync(path, logger, cancellationToken))
    {
    }

    internal TypeScriptAppHostToolingCheck(
        IProjectLocator projectLocator,
        ILanguageDiscovery languageDiscovery,
        CliExecutionContext executionContext,
        IEnvironment environment,
        ILogger<TypeScriptAppHostToolingCheck> logger,
        Func<string, string?> commandResolver,
        Func<string, CancellationToken, Task<string?>> denoVersionResolver)
    {
        _projectLocator = projectLocator;
        _languageDiscovery = languageDiscovery;
        _executionContext = executionContext;
        _environment = environment;
        _logger = logger;
        _commandResolver = commandResolver;
        _denoVersionResolver = denoVersionResolver;
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
                    Fix = "Upgrade to Yarn 4 or later, or switch to npm, pnpm, Bun, or Deno, then rerun 'aspire doctor'.",
                    Link = "https://yarnpkg.com/getting-started/install",
                    Metadata = new JsonObject
                    {
                        ["language"] = KnownLanguageId.TypeScript,
                        ["appHostPath"] = appHostFile.FullName
                    }
                }
            ];
        }
        catch (DenoVersionNotSupportedException ex)
        {
            return [CreateDenoVersionFailure(appHostFile, ex.Message)];
        }

        var missingResults = new List<EnvironmentCheckResult>();
        string? denoPath = null;

        foreach (var command in TypeScriptAppHostToolchainResolver.GetRequiredCommands(toolchain))
        {
            if (CommandPathResolver.TryResolveCommand(command, _commandResolver, out var commandPath, out var errorMessage))
            {
                if (toolchain == TypeScriptAppHostToolchain.Deno)
                {
                    denoPath = commandPath;
                }
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

        if (toolchain == TypeScriptAppHostToolchain.Deno)
        {
            var versionOutput = await _denoVersionResolver(denoPath!, cancellationToken);
            if (!TryParseDenoMajorVersion(versionOutput, out var majorVersion) || majorVersion < 2)
            {
                var details = majorVersion > 0
                    ? $"Deno {majorVersion} is installed, but TypeScript AppHosts require Deno 2 or later."
                    : "The installed Deno version could not be determined. TypeScript AppHosts require Deno 2 or later.";
                return [CreateDenoVersionFailure(appHostFile, details)];
            }
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

    private static EnvironmentCheckResult CreateDenoVersionFailure(FileInfo appHostFile, string details)
    {
        return new EnvironmentCheckResult
        {
            Category = EnvironmentCheckCategories.Environment,
            Name = DenoVersionCheckName,
            Status = EnvironmentCheckStatus.Fail,
            Message = "TypeScript AppHost requires Deno 2 or later.",
            Details = details,
            Fix = "Upgrade to Deno 2 or later and rerun 'aspire doctor'.",
            Link = CommandPathResolver.GetInstallationLink("deno"),
            Metadata = new JsonObject
            {
                ["language"] = KnownLanguageId.TypeScript,
                ["toolchain"] = "deno",
                ["appHostPath"] = appHostFile.FullName
            }
        };
    }

    internal static bool TryParseDenoMajorVersion(string? output, out int majorVersion)
    {
        majorVersion = 0;
        if (string.IsNullOrWhiteSpace(output))
        {
            return false;
        }

        // `deno --version` starts with `deno 2.9.0 (stable, release, ...)`, followed by
        // separate V8 and TypeScript version lines. Only the first version token is relevant.
        var firstLine = output.AsSpan().TrimStart();
        var lineEnd = firstLine.IndexOfAny('\r', '\n');
        if (lineEnd >= 0)
        {
            firstLine = firstLine[..lineEnd];
        }

        const string prefix = "deno ";
        if (!firstLine.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var version = firstLine[prefix.Length..].TrimStart();
        var majorEnd = version.IndexOf('.');
        return majorEnd > 0 && int.TryParse(version[..majorEnd], out majorVersion);
    }

    private static async Task<string?> GetDenoVersionOutputAsync(
        string executablePath,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("--version");

        var result = await ProcessCaptureRunner.RunAsync(
            startInfo,
            s_versionCheckTimeout,
            static async (process, captureToken) =>
            {
                var stdoutTask = process.StandardOutput.ReadToEndAsync(captureToken);
                var stderrTask = process.StandardError.ReadToEndAsync(captureToken);
                await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
                return (Stdout: await stdoutTask.ConfigureAwait(false), Stderr: await stderrTask.ConfigureAwait(false));
            },
            static () => (Stdout: string.Empty, Stderr: string.Empty),
            logger,
            cancellationToken);

        if (result.Cancelled)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }

        if (result.ExitCode != 0 || result.FailureKind is not null)
        {
            logger.LogDebug(
                "Deno version check failed with exit code {ExitCode}: {Error}",
                result.ExitCode,
                result.Capture.Stderr.Trim());
            return null;
        }

        return result.Capture.Stdout;
    }
}
