// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Aspire.Cli.DotNet;
using Aspire.Cli.Projects;
using Aspire.Shared;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Utils.EnvironmentChecker;

internal sealed partial class GoAppHostToolingCheck : IEnvironmentCheck
{
    internal const string GoCommandCheckName = "go-apphost-go";
    internal const string VersionCheckName = "go-apphost-version";
    internal const string ToolsCheckName = "go-apphost-tools";

    private const string GoCommand = "go";
    private const string ToolchainVariable = "GOTOOLCHAIN";

    // The generated Go SDK and the scaffolded Go AppHost both declare `go 1.26` in their go.mod. The SDK's
    // Windows named-pipe transport relies on the overlapped I/O support that os.OpenFile gained in Go 1.26
    // (https://go.dev/doc/go1.26#os). The CLI cannot reference Aspire.Hosting.CodeGeneration.Go, so
    // GoAppHostToolingCheckTests fails when the go.mod templates in AtsGoCodeGenerator and GoLanguageSupport
    // stop matching this value.
    internal static Version MinimumGoVersion { get; } = new(1, 26);

    // Go 1.21 added toolchain switching (https://go.dev/doc/toolchain). Earlier releases cannot
    // download a newer toolchain, so they can never satisfy a go.mod that requires a later Go.
    private static readonly Version s_toolchainSwitchingVersion = new(1, 21);

    private static readonly TimeSpan s_probeTimeout = TimeSpan.FromSeconds(5);

    private readonly IProjectLocator _projectLocator;
    private readonly ILanguageDiscovery _languageDiscovery;
    private readonly CliExecutionContext _executionContext;
    private readonly IEnvironment _environment;
    private readonly Func<string, string?> _commandResolver;
    private readonly Func<string, CancellationToken, Task<string?>> _versionResolver;
    private readonly Func<string, string, CancellationToken, Task<string?>> _toolchainResolver;

    public GoAppHostToolingCheck(
        IProjectLocator projectLocator,
        ILanguageDiscovery languageDiscovery,
        CliExecutionContext executionContext,
        IEnvironment environment,
        IProcessExecutionFactory processExecutionFactory,
        ILogger<GoAppHostToolingCheck> logger)
        : this(
            projectLocator,
            languageDiscovery,
            executionContext,
            environment,
            PathLookupHelper.FindFullPathFromPath,
            (path, cancellationToken) => RunProbeAsync(processExecutionFactory, CreateVersionProbeStartInfo(path), s_probeTimeout, logger, cancellationToken),
            (path, workingDirectory, cancellationToken) => RunProbeAsync(processExecutionFactory, CreateToolchainProbeStartInfo(path, workingDirectory), s_probeTimeout, logger, cancellationToken))
    {
    }

    internal GoAppHostToolingCheck(
        IProjectLocator projectLocator,
        ILanguageDiscovery languageDiscovery,
        CliExecutionContext executionContext,
        IEnvironment environment,
        Func<string, string?> commandResolver,
        Func<string, CancellationToken, Task<string?>> versionResolver,
        Func<string, string, CancellationToken, Task<string?>> toolchainResolver)
    {
        _projectLocator = projectLocator;
        _languageDiscovery = languageDiscovery;
        _executionContext = executionContext;
        _environment = environment;
        _commandResolver = commandResolver;
        _versionResolver = versionResolver;
        _toolchainResolver = toolchainResolver;
    }

    public int Order => 33;

    public async Task<IReadOnlyList<EnvironmentCheckResult>> CheckAsync(CancellationToken cancellationToken = default)
    {
        var appHostFile = await ResolveGoAppHostAsync(cancellationToken);
        if (appHostFile is null)
        {
            return [];
        }

        if (!CommandPathResolver.TryResolveCommand(GoCommand, _commandResolver, out var goPath, out var errorMessage))
        {
            return
            [
                new EnvironmentCheckResult
                {
                    Category = EnvironmentCheckCategories.Environment,
                    Name = GoCommandCheckName,
                    Status = EnvironmentCheckStatus.Fail,
                    Message = $"Go AppHost requires '{GoCommand}'.",
                    Details = errorMessage,
                    Fix = "Install Go and rerun 'aspire doctor'.",
                    Link = CommandPathResolver.GetInstallationLink(GoCommand),
                    Metadata = CreateMetadata(appHostFile, version: null)
                }
            ];
        }

        var versionOutput = await _versionResolver(goPath!, cancellationToken);
        if (!TryParseGoVersion(versionOutput, out var version))
        {
            // Inconclusive rather than proven broken: a slow first run or an unusual build can leave
            // `go version` without a readable version while Go itself works.
            return
            [
                CreateVersionResult(
                    appHostFile,
                    version: null,
                    EnvironmentCheckStatus.Warning,
                    "The installed Go version could not be determined.",
                    "Check that 'go version' prints a version and rerun 'aspire doctor'.")
            ];
        }

        if (version < MinimumGoVersion)
        {
            return [await CreateOutdatedGoResultAsync(appHostFile, goPath!, version, cancellationToken)];
        }

        return
        [
            new EnvironmentCheckResult
            {
                Category = EnvironmentCheckCategories.Environment,
                Name = ToolsCheckName,
                Status = EnvironmentCheckStatus.Pass,
                Message = $"Go AppHost tooling found ({GoCommand} {version}).",
                Metadata = CreateMetadata(appHostFile, version)
            }
        ];
    }

    private async Task<FileInfo?> ResolveGoAppHostAsync(CancellationToken cancellationToken)
    {
        // A configured AppHost is authoritative: when settings select another language's AppHost, that one
        // runs, so an unrelated apphost.go in the working directory must not require Go.
        var configuredAppHost = await _projectLocator.GetAppHostFromSettingsAsync(cancellationToken);
        if (configuredAppHost is not null)
        {
            return IsGoLanguage(_languageDiscovery.GetLanguageByFile(configuredAppHost)) ? configuredAppHost : null;
        }

        // Look for apphost.go directly. DetectLanguageRecursiveAsync returns only the first language that
        // matches, and C# matches any *.csproj, so a Go AppHost beside an ordinary service project would be
        // reported as C#. GetLanguageById returns null while experimentalPolyglotGo is off, which keeps the
        // lookup gated on the flag.
        var goLanguage = _languageDiscovery.GetLanguageById(new LanguageId(KnownLanguageId.Go));
        var discoveredPath = goLanguage?.FindInDirectory(_executionContext.WorkingDirectory.FullName);
        return discoveredPath is not null ? new FileInfo(discoveredPath) : null;
    }

    private static bool IsGoLanguage(LanguageInfo? language)
    {
        return language is not null &&
            language.LanguageId.Value.Equals(KnownLanguageId.Go, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<EnvironmentCheckResult> CreateOutdatedGoResultAsync(
        FileInfo appHostFile,
        string goPath,
        Version version,
        CancellationToken cancellationToken)
    {
        if (version < s_toolchainSwitchingVersion)
        {
            return CreateVersionResult(
                appHostFile,
                version,
                EnvironmentCheckStatus.Fail,
                $"Go {version} is installed, and Go versions before {s_toolchainSwitchingVersion} cannot download a newer toolchain.",
                $"Install Go {MinimumGoVersion} or later and rerun 'aspire doctor'.");
        }

        var toolchain = await GetEffectiveToolchainAsync(goPath, cancellationToken);

        // `local` always runs the Go on PATH and never switches, so an older Go cannot satisfy the AppHost's
        // go.mod.
        if (toolchain is "local")
        {
            return CreateVersionResult(
                appHostFile,
                version,
                EnvironmentCheckStatus.Fail,
                $"Go {version} is installed, and GOTOOLCHAIN=local prevents Go from switching to a newer toolchain.",
                $"Install Go {MinimumGoVersion} or later, or change GOTOOLCHAIN to auto, then rerun 'aspire doctor'.");
        }

        if (OnlySwitchesToPathToolchain(toolchain))
        {
            var requiredToolchain = $"go{MinimumGoVersion}.0";
            return CreateVersionResult(
                appHostFile,
                version,
                EnvironmentCheckStatus.Warning,
                $"Go {version} is installed, and GOTOOLCHAIN={toolchain} only lets Go switch to a newer toolchain that is already on PATH (for example {requiredToolchain}).",
                $"Install Go {MinimumGoVersion} or later, or put the {requiredToolchain} toolchain on PATH, then rerun 'aspire doctor'.");
        }

        // With the default GOTOOLCHAIN=auto, `go run` downloads the toolchain that go.mod requires.
        return CreateVersionResult(
            appHostFile,
            version,
            EnvironmentCheckStatus.Warning,
            $"Go {version} is installed. Go downloads the Go {MinimumGoVersion} toolchain the first time the AppHost runs, which needs network access.",
            $"Install Go {MinimumGoVersion} or later to avoid the download, then rerun 'aspire doctor'.");
    }

    private async Task<string> GetEffectiveToolchainAsync(string goPath, CancellationToken cancellationToken)
    {
        if (_environment.GetEnvironmentVariable(ToolchainVariable) is { Length: > 0 } environmentToolchain)
        {
            return environmentToolchain;
        }

        // The go command takes GOTOOLCHAIN from the process environment, then from the user config that
        // `go env -w` writes, then from $GOROOT/go.env (https://go.dev/doc/toolchain). Fedora's Go package
        // ships `local` in go.env (https://developer.fedoraproject.org/tech/languages/go/go-installation.html),
        // so ask go for the effective value. Run it in a fresh empty directory, never the AppHost's: go
        // commands that read a go.mod or go.work can switch toolchains and download one, which doctor must
        // not do. A pinned GOTOOLCHAIN=<name> is not modelled; the check validates the Go on PATH.
        var probeDirectory = Directory.CreateTempSubdirectory("aspire-go-doctor-");

        try
        {
            var output = await _toolchainResolver(goPath, probeDirectory.FullName, cancellationToken);

            // An unreadable answer means the default, which keeps the download warning.
            return string.IsNullOrWhiteSpace(output) ? "auto" : output.Trim();
        }
        finally
        {
            try
            {
                probeDirectory.Delete(recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best effort: an empty leftover directory in the temp folder is harmless.
            }
        }
    }

    // `path` is shorthand for `local+path`. The path modes never download, but they can switch to a
    // toolchain that is already installed: the go command looks in PATH for the exact toolchain that go.mod
    // names (the go line `go 1.26` becomes go1.26.0) and fails only when that executable is missing
    // (cmd/go/internal/toolchain/select.go, https://go.dev/doc/toolchain). Doctor does not read go.mod, so
    // these settings are inconclusive. The values are case-sensitive in the go command, so match them exactly.
    private static bool OnlySwitchesToPathToolchain(string toolchain)
    {
        return toolchain is "path" || toolchain.EndsWith("+path", StringComparison.Ordinal);
    }

    private static EnvironmentCheckResult CreateVersionResult(
        FileInfo appHostFile,
        Version? version,
        EnvironmentCheckStatus status,
        string details,
        string fix)
    {
        return new EnvironmentCheckResult
        {
            Category = EnvironmentCheckCategories.Environment,
            Name = VersionCheckName,
            Status = status,
            Message = $"Go AppHost requires Go {MinimumGoVersion} or later.",
            Details = details,
            Fix = fix,
            Link = CommandPathResolver.GetInstallationLink(GoCommand),
            Metadata = CreateMetadata(appHostFile, version)
        };
    }

    private static JsonObject CreateMetadata(FileInfo appHostFile, Version? version)
    {
        var metadata = new JsonObject
        {
            ["language"] = KnownLanguageId.Go,
            ["appHostPath"] = appHostFile.FullName
        };

        if (version is not null)
        {
            metadata["version"] = version.ToString();
        }

        return metadata;
    }

    // `go version` prints a single line such as:
    //   go version go1.26.1 windows/amd64
    //   go version go1.26rc1 linux/amd64
    //   go version devel go1.27-4d2f8e1a5b Tue Jan 6 10:00:00 2026 +0000 linux/amd64
    // Release candidates and betas have no patch number and development builds follow the
    // major.minor with a commit hash, so the first go<major>.<minor>[.<patch>] token is the version.
    // Output without such a token is treated as unreadable.
    internal static bool TryParseGoVersion(string? output, [NotNullWhen(true)] out Version? version)
    {
        version = null;
        return !string.IsNullOrWhiteSpace(output) &&
            GoVersionRegex().Match(output) is { Success: true } match &&
            Version.TryParse(match.Groups["version"].Value, out version);
    }

    [GeneratedRegex(@"\bgo(?<version>\d+\.\d+(?:\.\d+)?)", RegexOptions.CultureInvariant)]
    private static partial Regex GoVersionRegex();

    // The version probe reports the installed toolchain: every go command, including `go version`, runs
    // toolchain selection first. With the default GOTOOLCHAIN=auto, an older Go would download the
    // toolchain that a go.mod in the current directory requires and report that version instead of the
    // installed one, and the download can outlast the timeout. `local` makes it report the installed
    // toolchain without touching the network.
    internal static ProcessStartInfo CreateVersionProbeStartInfo(string executablePath)
    {
        var startInfo = new ProcessStartInfo(executablePath, ["version"]);

        startInfo.Environment[ToolchainVariable] = "local";

        return startInfo;
    }

    // Unlike the version probe, this one must not override GOTOOLCHAIN: it reads the effective setting.
    internal static ProcessStartInfo CreateToolchainProbeStartInfo(string executablePath, string workingDirectory)
    {
        return new ProcessStartInfo(executablePath, ["env", ToolchainVariable])
        {
            WorkingDirectory = workingDirectory
        };
    }

    // The factory launches go like every other CLI child process (pinned output encoding, ASPIRE_CLI_*
    // identity variables stripped, debug logging) and force-kills it when the timeout cancels the wait.
    // Only the command line, environment and working directory of the start info are used. The output
    // streams get separate buffers: go reports notices such as a toolchain download on standard error,
    // and they must not end up in the answer that the callers parse.
    internal static async Task<string?> RunProbeAsync(
        IProcessExecutionFactory processExecutionFactory,
        ProcessStartInfo startInfo,
        TimeSpan timeout,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        var arguments = string.Join(' ', startInfo.ArgumentList);
        var standardOutput = new StringBuilder();
        var standardError = new StringBuilder();

        try
        {
            await using var process = processExecutionFactory.CreateExecution(
                startInfo,
                new ProcessInvocationOptions
                {
                    StandardOutputCallback = line => standardOutput.AppendLine(line),
                    StandardErrorCallback = line => standardError.AppendLine(line)
                });

            if (!await process.StartAsync(timeoutCts.Token).ConfigureAwait(false))
            {
                return null;
            }

            var exitCode = await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);

            if (exitCode == 0)
            {
                return standardOutput.ToString();
            }

            logger.LogDebug(
                "'go {Arguments}' failed with exit code {ExitCode}: {Error}",
                arguments,
                exitCode,
                standardError.ToString().Trim());
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogDebug("'go {Arguments}' timed out after {Timeout}s.", arguments, timeout.TotalSeconds);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            logger.LogDebug(ex, "Could not start 'go {Arguments}' using '{GoPath}'.", arguments, startInfo.FileName);
        }

        return null;
    }
}
