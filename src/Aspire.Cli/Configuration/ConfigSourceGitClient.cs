// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Cli.Resources;
using Aspire.Cli.Utils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Configuration;

internal interface IConfigSourceGitClient
{
    Task<(string Commit, string Version, JsonObject Configuration)> AcquireAsync(ConfigSource source, string directory, CancellationToken cancellationToken);
    Task<(string Commit, string Version)> CheckAsync(ConfigSource source, string directory, CancellationToken cancellationToken);
}

internal sealed class ConfigSourceGitClient(ICliHostEnvironment hostEnvironment) : IConfigSourceGitClient
{
    private static readonly TimeSpan s_gitTimeout = TimeSpan.FromSeconds(30);

    public async Task<(string Commit, string Version, JsonObject Configuration)> AcquireAsync(ConfigSource source, string directory, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(s_gitTimeout);
        try
        {
            var (repositoryDirectory, commit, version) = await FetchVersionAsync(source, directory, allowInteractive: true, timeout.Token);
            var length = await RunAsync(repositoryDirectory, ["cat-file", "-s", version], timeout.Token);
            if (!long.TryParse(length.Trim(), CultureInfo.InvariantCulture, out var size) || size > 4 * 1024 * 1024)
            {
                throw new ConfigSourceException(ConfigSourceStrings.InvalidConfiguration);
            }

            var json = await RunAsync(repositoryDirectory, ["cat-file", "blob", version], timeout.Token);
            try
            {
                var configuration = JsonNode.Parse(json, documentOptions: ConfigurationHelper.ParseOptions) as JsonObject
                    ?? throw new ConfigSourceException(ConfigSourceStrings.InvalidConfiguration);
                // Validate the known settings as well as JSON syntax. Unknown content settings
                // are retained for future schemas, but a source cannot enable another source.
                if (configuration.Any(p => p.Key.Equals(ConfigSource.SectionName, StringComparison.OrdinalIgnoreCase)
                    || p.Key.StartsWith(ConfigSource.SectionName + ":", StringComparison.OrdinalIgnoreCase)
                    || p.Key.StartsWith(ConfigSource.SectionName + ".", StringComparison.OrdinalIgnoreCase)))
                {
                    throw new ConfigSourceException(ConfigSourceStrings.NestedSource);
                }
                // Use the same root-level flat-key compatibility as local settings before
                // validating duplicate paths and typed values such as "features:enabled".
                ConfigurationHelper.NormalizeSettings(configuration);
                using var parsed = new ConfigurationManager();
                using var stream = new MemoryStream(Encoding.UTF8.GetBytes(configuration.ToJsonString()));
                parsed.AddJsonStream(stream);
                _ = configuration.Deserialize(JsonSourceGenerationContext.Default.AspireConfigFile);
                return (commit, version, configuration);
            }
            catch (Exception ex) when (ex is JsonException or FormatException or ArgumentException)
            {
                throw new ConfigSourceException(ConfigSourceStrings.InvalidConfiguration);
            }
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ConfigSourceException(ConfigSourceStrings.GitTimeout)
            {
                GitOutput = (ex as GitProcessCanceledException)?.Output
            };
        }
    }

    public async Task<(string Commit, string Version)> CheckAsync(ConfigSource source, string directory, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(s_gitTimeout);
        try
        {
            var (_, commit, version) = await FetchVersionAsync(source, directory, allowInteractive: false, timeout.Token);
            return (commit, version);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ConfigSourceException(ConfigSourceStrings.GitTimeout)
            {
                GitOutput = (ex as GitProcessCanceledException)?.Output
            };
        }
    }

    private async Task<(string Directory, string Commit, string Version)> FetchVersionAsync(
        ConfigSource source, string directory, bool allowInteractive, CancellationToken cancellationToken)
    {
        var repositoryDirectory = Path.Combine(directory, "repository.git");
        await RunAsync(directory, ["init", "--bare", "--", repositoryDirectory], cancellationToken);
        // Fetch accepts both a fully qualified branch ref and a full commit ID. Unlike
        // clone --branch, this also acquires commits outside the default branch's shallow history.
        // https://git-scm.com/docs/git-fetch
        await RunAsync(repositoryDirectory, ["fetch", "--depth", "1", "--no-tags", "--", source.Repository, source.GetGitRef()], cancellationToken, allowInteractive);
        var commit = (await RunAsync(repositoryDirectory, ["rev-parse", "FETCH_HEAD^{commit}"], cancellationToken)).Trim();
        var treeEntry = await RunAsync(repositoryDirectory, ["ls-tree", "-z", commit, "--", source.Path], cancellationToken);
        // git ls-tree -z emits "100644 blob <object-id>\tpath\0". Inspecting the tree
        // identifies this file's version without reading or materializing its JSON.
        var tab = treeEntry.IndexOf('\t');
        var header = tab < 0 ? [] : treeEntry[..tab].Split(' ');
        if (header.Length != 3 || header[0] is not ("100644" or "100755") || header[1] != "blob"
            || treeEntry.IndexOf('\0') != treeEntry.Length - 1)
        {
            throw new ConfigSourceException(ConfigSourceStrings.MissingFile);
        }

        return (repositoryDirectory, commit, header[2]);
    }

    internal ProcessStartInfo CreateStartInfo(string directory, string[] arguments, bool allowInteractive = true)
    {
        // Explicitly identify our bare repository. Discovery inside it is intentionally
        // disabled when a user sets safe.bareRepository=explicit.
        // https://git-scm.com/docs/git-config#Documentation/git-config.txt-safebareRepository
        var gitArguments = arguments[0] is "clone" or "init" ? arguments : [$"--git-dir={directory}", .. arguments];
        // Advisory checks share the terminal with the command, so even interactive hosts
        // must capture their output and disable Git/credential-manager prompts.
        var interactive = allowInteractive && hostEnvironment.SupportsInteractiveInput;
        var interactiveFetch = arguments[0] == "fetch" && interactive;
        var startInfo = new ProcessStartInfo("git", ["--literal-pathspecs", .. gitArguments])
        {
            WorkingDirectory = directory,
            // Let Git and credential helpers own the terminal during interactive fetches.
            // Prompts need not end in a newline, and Git must control password echo.
            RedirectStandardOutput = !interactiveFetch,
            RedirectStandardError = !interactiveFetch,
            StandardOutputEncoding = interactiveFetch ? null : System.Text.Encoding.UTF8,
            StandardErrorEncoding = interactiveFetch ? null : System.Text.Encoding.UTF8,
            CreateNoWindow = !interactive
        };
        startInfo.Environment["GIT_TERMINAL_PROMPT"] = interactive ? "1" : "0";
        if (!interactive)
        {
            // Git's terminal-prompt setting does not disable Git Credential Manager UI.
            // https://github.com/git-ecosystem/git-credential-manager/blob/release/docs/environment.md#gcm_interactive
            startInfo.Environment["GCM_INTERACTIVE"] = "never";
            // These settings do not govern SSH launchers, which may still prompt.
            // Preserve the user's launcher instead of rewriting its command/options.
            // A blocked fetch is bounded by the two-minute timeout, and its captured
            // output is displayed on timeout so the user can correct authentication.
        }
        // Keep Git diagnostics stable for the local failure classification below.
        startInfo.Environment["LC_ALL"] = "C";
        foreach (var key in new[] { "GIT_DIR", "GIT_WORK_TREE", "GIT_INDEX_FILE", "GIT_OBJECT_DIRECTORY", "GIT_ALTERNATE_OBJECT_DIRECTORIES" })
        {
            startInfo.Environment.Remove(key);
        }
        return startInfo;
    }

    private Task<string> RunAsync(string directory, string[] arguments, CancellationToken cancellationToken, bool allowInteractive = true)
    {
        var startInfo = CreateStartInfo(directory, arguments, allowInteractive);
        return RunProcessAsync(startInfo, captureTimeoutOutput: arguments[0] == "fetch" && startInfo.RedirectStandardOutput, cancellationToken);
    }

    internal static async Task<string> RunProcessAsync(ProcessStartInfo startInfo, bool captureTimeoutOutput, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // The caller's token includes the overall Git timeout. Reuse tree-aware termination
        // and bounded draining for both captured queries and terminal-owned fetches.
        // Suppress runner diagnostics because capture exceptions can include private Git output.
        var result = await ProcessCaptureRunner.RunAsync<GitOutput?>(
            startInfo, Timeout.InfiniteTimeSpan,
            async (process, token) =>
            {
                if (!startInfo.RedirectStandardOutput)
                {
                    return new GitOutput(string.Empty, string.Empty);
                }
                // Read both pipes concurrently so a full stderr pipe cannot block stdout.
                var standardOutput = ReadOutputAsync(process.StandardOutput, token);
                var standardError = ReadOutputAsync(process.StandardError, token);
                await Task.WhenAll(standardOutput, standardError);
                return new GitOutput(await standardOutput, await standardError);
            },
            () => null, NullLogger.Instance, cancellationToken);
        if (cancellationToken.IsCancellationRequested && captureTimeoutOutput && result.Capture is { } interrupted)
        {
            // Keep diagnostics separate from exception messages, logs and telemetry.
            // The caller displays them only for timeout, never user/shutdown cancellation.
            throw new GitProcessCanceledException(interrupted.StandardOutput + interrupted.StandardError, cancellationToken);
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (result.FailureKind is ProcessCaptureFailureKind.StartFailed)
        {
            throw new ConfigSourceException(ConfigSourceStrings.GitNotFound);
        }
        if (result.Capture is not { } capture || result.FailureKind is not null)
        {
            // An incomplete drain is a failure, not successful empty Git output.
            throw new ConfigSourceException(ConfigSourceStrings.RepositoryUnavailable);
        }
        if (result.ExitCode != 0)
        {
            // Git stderr can contain private repository URLs and credentials. Classify it
            // locally instead of copying it into exceptions, logs, or telemetry.
            var authenticationFailure = capture.StandardError.Contains("authentication", StringComparison.OrdinalIgnoreCase)
                || capture.StandardError.Contains("permission denied", StringComparison.OrdinalIgnoreCase)
                || capture.StandardError.Contains("could not read Username", StringComparison.OrdinalIgnoreCase);
            throw new ConfigSourceException(authenticationFailure ? ConfigSourceStrings.AuthenticationFailed : ConfigSourceStrings.RepositoryUnavailable);
        }
        return capture.StandardOutput;
    }

    private static async Task<string> ReadOutputAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var output = new StringBuilder();
        var buffer = new char[4096];
        try
        {
            // Prompts such as "Enter passphrase for key '/home/user/.ssh/id_ed25519': "
            // need not end in a newline. Retain chunks, including partial lines.
            // Local query results must remain complete; truncate only for display.
            while (true)
            {
                var count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
                if (count == 0)
                {
                    break;
                }
                output.Append(buffer, 0, count);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // ProcessCaptureRunner terminates the process tree and bounds draining.
            // Return what was already read instead of losing it with ReadToEndAsync.
        }

        return output.ToString();
    }

    internal sealed class GitProcessCanceledException(string output, CancellationToken cancellationToken) : OperationCanceledException(cancellationToken)
    {
        public string Output { get; } = output;
    }

    private sealed record GitOutput(string StandardOutput, string StandardError);
}
