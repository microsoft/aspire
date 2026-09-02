// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Diagnostics;
using Aspire.Cli.DotNet;
using Aspire.Cli.Processes;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Utils;
using Aspire.TypeSystem;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Projects;

/// <summary>
/// A data-driven runtime executor for guest language processes.
/// Interprets <see cref="RuntimeSpec"/> to install dependencies and execute AppHost processes.
/// </summary>
internal sealed class GuestRuntime
{
    private readonly RuntimeSpec _spec;
    private readonly ILogger _logger;
    private readonly FileLoggerProvider? _fileLoggerProvider;
    private readonly IEnvironment _environment;
    private readonly ProfilingTelemetry _profilingTelemetry;
    private readonly CommandSpec[]? _installDependencies;

    /// <summary>
    /// Creates a new GuestRuntime for the given runtime specification.
    /// </summary>
    /// <param name="spec">The runtime specification describing how to execute the guest language.</param>
    /// <param name="logger">Logger for debugging output.</param>
    /// <param name="environment">The environment abstraction for OS detection.</param>
    /// <param name="profilingTelemetry">Profiling telemetry for child-process diagnostics.</param>
    /// <param name="fileLoggerProvider">Optional file logger for writing output to disk.</param>
    /// <param name="installDependencies">
    /// Optional internal command sequence that replaces <see cref="RuntimeSpec.InstallDependencies"/>.
    /// </param>
    public GuestRuntime(
        RuntimeSpec spec,
        ILogger logger,
        IEnvironment environment,
        ProfilingTelemetry profilingTelemetry,
        FileLoggerProvider? fileLoggerProvider = null,
        CommandSpec[]? installDependencies = null)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(profilingTelemetry);

        _spec = spec;
        _logger = logger;
        _fileLoggerProvider = fileLoggerProvider;
        _environment = environment;
        _profilingTelemetry = profilingTelemetry;
        _installDependencies = installDependencies
            ?? (spec.InstallDependencies is null ? null : [spec.InstallDependencies]);
    }

    public GuestRuntime(RuntimeSpec spec, ILogger logger, Func<string, string?> commandResolver, IEnvironment environment, ProfilingTelemetry profilingTelemetry, FileLoggerProvider? fileLoggerProvider = null)
        : this(spec, logger, environment, profilingTelemetry, fileLoggerProvider)
    {
        ArgumentNullException.ThrowIfNull(commandResolver);
    }

    /// <summary>
    /// Gets the language identifier from the runtime specification.
    /// </summary>
    public string Language => _spec.Language;

    /// <summary>
    /// Gets the display name from the runtime specification.
    /// </summary>
    public string DisplayName => _spec.DisplayName;

    /// <summary>
    /// Gets the extension capability required to launch this language via the VS Code extension.
    /// Null if this language does not support extension-based launching.
    /// </summary>
    public string? ExtensionLaunchCapability => _spec.ExtensionLaunchCapability;

    /// <summary>
    /// Gets the environment variable used by the runtime for an additional certificate bundle in run mode.
    /// </summary>
    public string? CertificateBundleEnvironmentVariable => _spec.CertificateBundleEnvironmentVariable;

    /// <summary>
    /// Initializes the project environment (e.g., creates a virtual environment and installs dependencies).
    /// Runs each command in <see cref="RuntimeSpec.Initialize"/> sequentially.
    /// </summary>
    /// <param name="directory">The project directory.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The exit code from the first failing command, or 0 if all succeed.</returns>
    public async Task<(int ExitCode, OutputCollector Output)> InitializeAsync(DirectoryInfo directory, CancellationToken cancellationToken)
    {
        var outputCollector = new OutputCollector();

        if (_spec.Initialize is null or { Length: 0 })
        {
            _logger.LogDebug("No initialization configured for {Language}", _spec.Language);
            return (0, outputCollector);
        }

        foreach (var commandSpec in _spec.Initialize)
        {
            var args = ReplacePlaceholders(commandSpec.Args, null, directory, null);
            var environmentVariables = commandSpec.EnvironmentVariables ?? new Dictionary<string, string>();

            var launcher = CreateDefaultLauncher();
            using var activity = _profilingTelemetry.StartGuestInitializeCommand(_spec.Language, _spec.DisplayName, commandSpec.Command, args, directory);
            var (exitCode, output) = await launcher.LaunchAsync(
                commandSpec.Command,
                args,
                directory,
                environmentVariables,
                afterLaunchAsync: null,
                options: null,
                cancellationToken);
            activity.SetProcessExitCode(exitCode);
            if (exitCode != 0)
            {
                activity.SetError($"{_spec.DisplayName} initialization exited with code {exitCode}.");
                return (exitCode, output ?? outputCollector);
            }
        }

        return (0, outputCollector);
    }

    /// <summary>
    /// Installs dependencies for the guest language project.
    /// </summary>
    /// <param name="directory">The project directory.</param>
    /// <param name="environmentVariables">Environment variables inherited by each dependency installation command.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A tuple containing the exit code and captured output from the dependency installation commands.</returns>
    public async Task<(int ExitCode, OutputCollector Output)> InstallDependenciesAsync(
        DirectoryInfo directory,
        IDictionary<string, string> environmentVariables,
        CancellationToken cancellationToken)
    {
        var outputCollector = new OutputCollector();

        if (_installDependencies is null or { Length: 0 })
        {
            _logger.LogDebug("No dependency installation configured for {Language}", _spec.Language);
            return (0, outputCollector);
        }

        var launcher = CreateDefaultLauncher();
        OutputCollector lastOutput = outputCollector;
        foreach (var command in _installDependencies)
        {
            var args = ReplacePlaceholders(command.Args, null, directory, null);
            var mergedEnvironment = MergeEnvironmentVariables(environmentVariables, command);

            using var activity = _profilingTelemetry.StartGuestInstallDependencies(
                _spec.Language,
                _spec.DisplayName,
                command.Command,
                args,
                directory);
            var (exitCode, output) = await launcher.LaunchAsync(
                command.Command,
                args,
                directory,
                mergedEnvironment,
                afterLaunchAsync: null,
                options: null,
                cancellationToken);
            activity.SetProcessExitCode(exitCode);
            if (exitCode != 0)
            {
                activity.SetError($"{_spec.DisplayName} dependency installation exited with code {exitCode}.");
                return (exitCode, output ?? outputCollector);
            }

            lastOutput = output ?? outputCollector;
        }

        return (0, lastOutput);
    }

    /// <summary>
    /// Runs the AppHost guest process.
    /// </summary>
    /// <param name="appHostFile">The AppHost file to execute.</param>
    /// <param name="directory">The project directory.</param>
    /// <param name="environmentVariables">Environment variables to set for the process.</param>
    /// <param name="watchMode">Whether to run in watch mode for hot reload.</param>
    /// <param name="launcher">Strategy for launching the process.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="noBuild">Whether to skip pre-execution build/check commands.</param>
    /// <param name="afterAppHostLaunchedAsync">Callback invoked after the AppHost execute command has launched.</param>
    /// <param name="appHostLaunchOptions">
    /// Optional launch options forwarded to the launcher for the long-running AppHost execute command only.
    /// Pre-execute commands (e.g. <c>tsc --noEmit</c>) and dependency installation are short-lived and
    /// keep today's force-kill behavior, so this is not passed there.
    /// </param>
    /// <returns>A tuple of the exit code and captured output (null when launched via extension).</returns>
    public async Task<(int ExitCode, OutputCollector? Output)> RunAsync(
        FileInfo appHostFile,
        DirectoryInfo directory,
        IDictionary<string, string> environmentVariables,
        bool watchMode,
        IGuestProcessLauncher launcher,
        CancellationToken cancellationToken,
        bool noBuild = false,
        Func<Task>? afterAppHostLaunchedAsync = null,
        GuestLaunchOptions? appHostLaunchOptions = null)
    {
        var useWatchCommand = watchMode && _spec.WatchExecute is not null;
        var commandSpec = useWatchCommand
            ? _spec.WatchExecute!
            : _spec.Execute;

        await EnsureMigrationFilesExistAsync(directory, cancellationToken);
        if (!useWatchCommand && !noBuild)
        {
            var preExecuteResult = await RunPreExecuteCommandsAsync(appHostFile, directory, environmentVariables, launcher, cancellationToken);
            if (preExecuteResult.ExitCode != 0)
            {
                return preExecuteResult;
            }
        }

        var phase = useWatchCommand
            ? ProfilingTelemetry.Values.GuestCommandPhaseWatchExecute
            : ProfilingTelemetry.Values.GuestCommandPhaseExecute;
        return await ExecuteCommandAsync(commandSpec, appHostFile, directory, environmentVariables, null, phase, launcher, cancellationToken, afterLaunchAsync: afterAppHostLaunchedAsync, launchOptions: appHostLaunchOptions);
    }

    /// <summary>
    /// Runs the AppHost guest process for publishing.
    /// </summary>
    /// <param name="appHostFile">The AppHost file to execute.</param>
    /// <param name="directory">The project directory.</param>
    /// <param name="environmentVariables">Environment variables to set for the process.</param>
    /// <param name="publishArgs">Additional arguments for publishing.</param>
    /// <param name="launcher">Strategy for launching the process.</param>
    /// <param name="noBuild">Whether to skip pre-execution build/check commands.</param>
    /// <param name="afterAppHostLaunchedAsync">Callback invoked after the AppHost execute command has launched.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A tuple of the exit code and captured output.</returns>
    public async Task<(int ExitCode, OutputCollector? Output)> PublishAsync(
        FileInfo appHostFile,
        DirectoryInfo directory,
        IDictionary<string, string> environmentVariables,
        string[]? publishArgs,
        IGuestProcessLauncher launcher,
        bool noBuild = false,
        Func<Task>? afterAppHostLaunchedAsync = null,
        CancellationToken cancellationToken = default)
    {
        var commandSpec = _spec.PublishExecute ?? _spec.Execute;

        await EnsureMigrationFilesExistAsync(directory, cancellationToken);
        if (!noBuild)
        {
            var preExecuteResult = await RunPreExecuteCommandsAsync(appHostFile, directory, environmentVariables, launcher, cancellationToken);
            if (preExecuteResult.ExitCode != 0)
            {
                return preExecuteResult;
            }
        }

        var phase = _spec.PublishExecute is not null
            ? ProfilingTelemetry.Values.GuestCommandPhasePublishExecute
            : ProfilingTelemetry.Values.GuestCommandPhaseExecute;
        return await ExecuteCommandAsync(commandSpec, appHostFile, directory, environmentVariables, publishArgs, phase, launcher, cancellationToken, afterLaunchAsync: afterAppHostLaunchedAsync);
    }

    private async Task<(int ExitCode, OutputCollector? Output)> RunPreExecuteCommandsAsync(
        FileInfo appHostFile,
        DirectoryInfo directory,
        IDictionary<string, string> environmentVariables,
        IGuestProcessLauncher launcher,
        CancellationToken cancellationToken)
    {
        if (_spec.PreExecute is null or { Length: 0 })
        {
            return (0, new OutputCollector());
        }

        var preExecuteLauncher = launcher is ExtensionGuestLauncher ? CreateDefaultLauncher() : launcher;
        foreach (var commandSpec in _spec.PreExecute)
        {
            var args = ReplacePlaceholders(commandSpec.Args, appHostFile, directory, null);
            var mergedEnvironment = MergeEnvironmentVariables(environmentVariables, commandSpec);

            var stampFile = ResolveStampFile(commandSpec.UpToDateCheck, appHostFile, directory);
            if (stampFile is not null && IsUpToDate(commandSpec.UpToDateCheck!, stampFile, appHostFile, directory))
            {
                _logger.LogDebug("Skipping up-to-date pre-execution command: {Command}", commandSpec.Command);
                continue;
            }

            _logger.LogDebug("Launching pre-execution command: {Command} {Args}", commandSpec.Command, string.Join(" ", args));
            using var activity = _profilingTelemetry.StartGuestExecuteCommand(_spec.Language, _spec.DisplayName, commandSpec.Command, args, directory, ProfilingTelemetry.Values.GuestCommandPhasePreExecute);
            var (exitCode, output) = await preExecuteLauncher.LaunchAsync(commandSpec.Command, args, directory, mergedEnvironment, afterLaunchAsync: null, options: null, cancellationToken);
            activity.SetProcessExitCode(exitCode);
            if (exitCode != 0)
            {
                activity.SetError($"{_spec.DisplayName} pre-execution exited with code {exitCode}.");
                return (exitCode, output ?? new OutputCollector());
            }

            if (stampFile is not null)
            {
                WriteStamp(stampFile);
            }
        }

        return (0, new OutputCollector());
    }

    /// <summary>
    /// Resolves the stamp file for a command's up-to-date check, or null when the command has none.
    /// </summary>
    private static FileInfo? ResolveStampFile(CommandUpToDateCheck? check, FileInfo appHostFile, DirectoryInfo directory)
    {
        if (check is null)
        {
            return null;
        }

        var resolved = ReplacePlaceholders([check.StampFile], appHostFile, directory, null)[0];
        return new FileInfo(Path.Combine(directory.FullName, resolved));
    }

    /// <summary>
    /// Determines whether every declared output exists and every declared input is older than the stamp file.
    /// </summary>
    /// <remarks>
    /// Comparison is strictly "no input newer than the stamp". An input written in the same second as
    /// the stamp therefore counts as up to date, which matches how make-style checks behave and is
    /// safe here because the stamp is written after the compile reads its inputs.
    /// </remarks>
    private bool IsUpToDate(CommandUpToDateCheck check, FileInfo stampFile, FileInfo appHostFile, DirectoryInfo directory)
    {
        stampFile.Refresh();
        if (!stampFile.Exists)
        {
            return false;
        }

        var outputs = ReplacePlaceholders(check.Outputs ?? [], appHostFile, directory, null);
        foreach (var output in outputs)
        {
            var path = Path.IsPathRooted(output) ? output : Path.Combine(directory.FullName, output);
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                return false;
            }
        }

        var stampWriteTime = stampFile.LastWriteTimeUtc;
        var stampDirectory = stampFile.Directory?.FullName;
        var inputs = ReplacePlaceholders(check.Inputs, appHostFile, directory, null);

        foreach (var input in inputs)
        {
            // A spec may name a path only some project layouts have (src/main/java, for instance), so a
            // missing input is not a change. A missing *output* is handled by the stamp check above.
            var recursive = input.EndsWith("/**", StringComparison.Ordinal) || input.EndsWith(@"\**", StringComparison.Ordinal);
            var trimmed = recursive ? input[..^3] : input;
            var path = Path.IsPathRooted(trimmed) ? trimmed : Path.Combine(directory.FullName, trimmed);

            if (File.Exists(path))
            {
                // A file the spec names outright is not a scan result, so the extension filter - which
                // exists to keep a directory scan from picking up the command's own outputs - does not
                // apply to it. This is what lets a build descriptor such as pom.xml or build.gradle be
                // declared as an input of a check whose scans are restricted to sources.
                if (File.GetLastWriteTimeUtc(path) > stampWriteTime)
                {
                    return false;
                }

                continue;
            }

            if (!Directory.Exists(path))
            {
                continue;
            }

            try
            {
                // A directory's own timestamp moves when an entry is added, removed, or renamed inside
                // it, and not when an existing entry is rewritten. Both POSIX and NTFS guarantee that,
                // and it is the only signal here that catches a *deleted* input: after a delete every
                // surviving file is older than the stamp, so comparing files alone sees no change at
                // all and the command keeps reusing outputs built from a source that is gone. It also
                // covers a changed set of staged dependency JARs, whose own timestamps are rewritten
                // by the staging step on every launch and so cannot be compared directly.
                //
                // The cost is being occasionally eager: an unrelated file appearing in an input
                // directory - an editor swap file, a .DS_Store - triggers one extra run, after which
                // the new stamp settles it. That is the safe direction for this to be wrong in.
                if (Directory.GetLastWriteTimeUtc(path) > stampWriteTime)
                {
                    return false;
                }

                // EnumerateFiles is lazy: the traversal - and any IOException or
                // UnauthorizedAccessException an unreadable subdirectory raises - happens while the
                // foreach pulls from it, not at the call. Iterating inside the same try is what puts
                // that failure in front of this catch; enumerating outside it let an unreadable tree
                // abort AppHost startup instead of falling back to running the command.
                foreach (var file in Directory.EnumerateFiles(path))
                {
                    if (MatchesExtension(check, file) && File.GetLastWriteTimeUtc(file) > stampWriteTime)
                    {
                        return false;
                    }
                }

                if (!recursive)
                {
                    continue;
                }

                foreach (var subdirectory in EnumerateInputDirectories(path, stampDirectory))
                {
                    // Unlike the declared root above, a subdirectory's own timestamp only counts when
                    // the directory still holds inputs, or holds nothing at all. A recursive input
                    // reaches whatever happens to sit under it - a log directory, a tool's scratch
                    // space - and the timestamp rule ignores the extension filter, so checking every
                    // subdirectory would let any unrelated write force a rebuild, and a command that
                    // writes under its own input root would never settle. An emptied directory is
                    // kept because that is exactly what deleting the last input in a package looks
                    // like, and a delete is the one change no file timestamp can reveal.
                    var holdsInputs = false;
                    var holdsFiles = false;

                    foreach (var file in Directory.EnumerateFiles(subdirectory))
                    {
                        holdsFiles = true;

                        if (!MatchesExtension(check, file))
                        {
                            continue;
                        }

                        holdsInputs = true;

                        if (File.GetLastWriteTimeUtc(file) > stampWriteTime)
                        {
                            return false;
                        }
                    }

                    if ((holdsInputs || !holdsFiles) && Directory.GetLastWriteTimeUtc(subdirectory) > stampWriteTime)
                    {
                        return false;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // An unreadable input tree cannot be proven unchanged, so fall back to running the command.
                _logger.LogDebug(ex, "Unable to scan up-to-date check input {Input}; treating the command as out of date.", path);
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Enumerates the subdirectories of <paramref name="root"/>, skipping trees that cannot hold an
    /// input.
    /// </summary>
    /// <remarks>
    /// Lazy on purpose: an <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/> from
    /// an unreadable subdirectory has to surface while the caller is iterating, inside the caller's
    /// try, rather than at the call.
    /// </remarks>
    private static IEnumerable<string> EnumerateInputDirectories(string root, string? outputDirectory)
    {
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            foreach (var child in Directory.EnumerateDirectories(pending.Pop()))
            {
                if (IsNeverAnInput(child, outputDirectory))
                {
                    continue;
                }

                yield return child;
                pending.Push(child);
            }
        }
    }

    /// <summary>
    /// Returns whether a directory can be skipped entirely while scanning a recursive input.
    /// </summary>
    /// <remarks>
    /// A recursive input has to descend to be correct - a source in a package subdirectory is
    /// compiled, and rewriting it in place moves no ancestor's timestamp - but descending everywhere
    /// costs the launch time the check exists to save, and would let the command's own outputs
    /// invalidate the check that produced them.
    /// <para>
    /// Dot-directories are tooling state (<c>.git</c>, <c>.gradle</c>, <c>.idea</c>) rather than
    /// sources, and no language here has a package or module segment that may begin with a dot. The
    /// generated SDK lives in one and is declared as its own input, so it stays tracked.
    /// </para>
    /// </remarks>
    private static bool IsNeverAnInput(string directory, string? outputDirectory)
    {
        var name = Path.GetFileName(directory.AsSpan());

        if (name.StartsWith("."))
        {
            return true;
        }

        // A dependency tree, never a first-party source root, and by far the largest thing likely to
        // sit beside an AppHost.
        if (name.Equals("node_modules", StringComparison.Ordinal))
        {
            return true;
        }

        return outputDirectory is not null
            && string.Equals(directory, outputDirectory, PathComparison);
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    private static bool MatchesExtension(CommandUpToDateCheck check, string path)
    {
        if (check.FileExtensions is null or { Length: 0 })
        {
            return true;
        }

        var extension = Path.GetExtension(path);
        foreach (var candidate in check.FileExtensions)
        {
            if (string.Equals(extension, candidate, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private void WriteStamp(FileInfo stampFile)
    {
        try
        {
            stampFile.Directory?.Create();
            // The content is never read; only the write time matters. Rewriting rather than touching
            // keeps this working on file systems where setting a time on a missing file would throw.
            File.WriteAllBytes(stampFile.FullName, []);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A stamp that cannot be written only costs the next launch a rebuild, so this must never
            // be the reason a run fails.
            _logger.LogDebug(ex, "Unable to write up-to-date stamp {Stamp}.", stampFile.FullName);
        }
    }

    private async Task<(int ExitCode, OutputCollector? Output)> ExecuteCommandAsync(
        CommandSpec commandSpec,
        FileInfo appHostFile,
        DirectoryInfo directory,
        IDictionary<string, string> environmentVariables,
        string[]? additionalArgs,
        string phase,
        IGuestProcessLauncher launcher,
        CancellationToken cancellationToken,
        Func<Task>? afterLaunchAsync = null,
        GuestLaunchOptions? launchOptions = null)
    {
        var args = ReplacePlaceholders(commandSpec.Args, appHostFile, directory, additionalArgs);

        var mergedEnvironment = MergeEnvironmentVariables(environmentVariables, commandSpec);

        _logger.LogDebug("Launching: {Command} {Args}", commandSpec.Command, string.Join(" ", args));
        using var activity = _profilingTelemetry.StartGuestExecuteCommand(_spec.Language, _spec.DisplayName, commandSpec.Command, args, directory, phase);
        var (exitCode, output) = await launcher.LaunchAsync(commandSpec.Command, args, directory, mergedEnvironment, afterLaunchAsync: afterLaunchAsync, options: launchOptions, cancellationToken);
        activity.SetProcessExitCode(exitCode);
        if (exitCode != 0)
        {
            activity.SetError($"{_spec.DisplayName} execution exited with code {exitCode}.");
        }

        return (exitCode, output);
    }

    private static Dictionary<string, string> MergeEnvironmentVariables(
        IDictionary<string, string> environmentVariables,
        CommandSpec commandSpec)
    {
        var mergedEnvironment = new Dictionary<string, string>(environmentVariables.Count, ProcessEnvironment.Comparer);
        foreach (var (key, value) in environmentVariables)
        {
            mergedEnvironment[key] = value;
        }

        if (commandSpec.EnvironmentVariables is not null)
        {
            foreach (var (key, value) in commandSpec.EnvironmentVariables)
            {
                mergedEnvironment[key] = value;
            }
        }

        return mergedEnvironment;
    }

    /// <summary>
    /// Creates any migration files that are required by the runtime but missing from the project directory.
    /// This handles upgrade scenarios where a newer CLI introduces new required files.
    /// </summary>
    private async Task EnsureMigrationFilesExistAsync(DirectoryInfo directory, CancellationToken cancellationToken)
    {
        if (_spec.MigrationFiles is null or { Count: 0 })
        {
            return;
        }

        foreach (var (fileName, content) in _spec.MigrationFiles)
        {
            var filePath = Path.Combine(directory.FullName, fileName);
            if (!File.Exists(filePath))
            {
                _logger.LogInformation("Creating missing required file: {FileName}", fileName);
                await File.WriteAllTextAsync(filePath, content, cancellationToken);
            }
        }
    }

    /// <summary>
    /// Creates the default process-based launcher for this runtime.
    /// </summary>
    public ProcessGuestLauncher CreateDefaultLauncher() => new(
        _spec.Language,
        _logger,
        fileLoggerProvider: _fileLoggerProvider,
        // The launcher logs each guest stdout/stderr line itself, so the execution factory is given
        // a NullLogger to avoid double-logging those lines.
        processExecutionFactory: new ProcessExecutionFactory(_environment, NullLogger<ProcessExecutionFactory>.Instance));

    /// <summary>
    /// Replaces placeholders in command arguments with actual values.
    /// </summary>
    private static string[] ReplacePlaceholders(
        string[] args,
        FileInfo? appHostFile,
        DirectoryInfo directory,
        string[]? additionalArgs)
    {
        var result = new List<string>();

        foreach (var arg in args)
        {
            var replaced = arg
                .Replace("{appHostFile}", appHostFile?.FullName ?? "")
                .Replace("{appHostDir}", directory.FullName);

            if (replaced.Contains("{args}"))
            {
                if (additionalArgs is { Length: > 0 })
                {
                    replaced = replaced.Replace("{args}", string.Join(" ", additionalArgs));
                }
                else
                {
                    replaced = replaced.Replace("{args}", "");
                }
            }

            if (!string.IsNullOrWhiteSpace(replaced))
            {
                result.Add(replaced);
            }
        }

        if (additionalArgs is { Length: > 0 } && !args.Any(a => a.Contains("{args}")))
        {
            result.AddRange(additionalArgs);
        }

        return result.ToArray();
    }
}
