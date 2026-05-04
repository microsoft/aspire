// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Diagnostics;
using Aspire.Cli.Utils;
using Aspire.TypeSystem;
using Microsoft.Extensions.Logging;

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
    private readonly Func<string, string?> _commandResolver;

    /// <summary>
    /// Creates a new GuestRuntime for the given runtime specification.
    /// </summary>
    /// <param name="spec">The runtime specification describing how to execute the guest language.</param>
    /// <param name="logger">Logger for debugging output.</param>
    /// <param name="fileLoggerProvider">Optional file logger for writing output to disk.</param>
    /// <param name="commandResolver">Optional command resolver used to locate executables on PATH.</param>
    public GuestRuntime(RuntimeSpec spec, ILogger logger, FileLoggerProvider? fileLoggerProvider = null, Func<string, string?>? commandResolver = null)
    {
        _spec = spec;
        _logger = logger;
        _fileLoggerProvider = fileLoggerProvider;
        _commandResolver = commandResolver ?? PathLookupHelper.FindFullPathFromPath;
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
            var (exitCode, output) = await launcher.LaunchAsync(
                commandSpec.Command,
                args,
                directory,
                environmentVariables,
                cancellationToken);
            if (exitCode != 0)
            {
                return (exitCode, output ?? outputCollector);
            }
        }

        return (0, outputCollector);
    }

    /// <summary>
    /// Installs dependencies for the guest language project.
    /// </summary>
    /// <param name="directory">The project directory.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A tuple containing the exit code and captured output from the dependency installation command.</returns>
    public async Task<(int ExitCode, OutputCollector Output)> InstallDependenciesAsync(DirectoryInfo directory, CancellationToken cancellationToken)
    {
        var outputCollector = new OutputCollector();

        if (_spec.InstallDependencies is null)
        {
            _logger.LogDebug("No dependency installation configured for {Language}", _spec.Language);
            return (0, outputCollector);
        }

        var args = ReplacePlaceholders(_spec.InstallDependencies.Args, null, directory, null);
        var environmentVariables = _spec.InstallDependencies.EnvironmentVariables ?? new Dictionary<string, string>();

        var launcher = CreateDefaultLauncher();
        var (exitCode, output) = await launcher.LaunchAsync(
            _spec.InstallDependencies.Command,
            args,
            directory,
            environmentVariables,
            cancellationToken);

        return (exitCode, output ?? outputCollector);
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
    /// <returns>A tuple of the exit code and captured output (null when launched via extension).</returns>
    public async Task<(int ExitCode, OutputCollector? Output)> RunAsync(
        FileInfo appHostFile,
        DirectoryInfo directory,
        IDictionary<string, string> environmentVariables,
        bool watchMode,
        IGuestProcessLauncher launcher,
        CancellationToken cancellationToken)
    {
        return await RunAsync(appHostFile, directory, environmentVariables, watchMode, launcher, runArgs: null, cancellationToken);
    }

    /// <summary>
    /// Runs the AppHost guest process.
    /// </summary>
    /// <param name="appHostFile">The AppHost file to execute.</param>
    /// <param name="directory">The project directory.</param>
    /// <param name="environmentVariables">Environment variables to set for the process.</param>
    /// <param name="watchMode">Whether to run in watch mode for hot reload.</param>
    /// <param name="launcher">Strategy for launching the process.</param>
    /// <param name="runArgs">Additional arguments for the AppHost process.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A tuple of the exit code and captured output (null when launched via extension).</returns>
    public async Task<(int ExitCode, OutputCollector? Output)> RunAsync(
        FileInfo appHostFile,
        DirectoryInfo directory,
        IDictionary<string, string> environmentVariables,
        bool watchMode,
        IGuestProcessLauncher launcher,
        string[]? runArgs,
        CancellationToken cancellationToken)
    {
        var commandSpec = watchMode && _spec.WatchExecute is not null
            ? _spec.WatchExecute
            : _spec.Execute;

        return await ExecuteCommandAsync(commandSpec, appHostFile, directory, environmentVariables, runArgs, launcher, cancellationToken);
    }

    /// <summary>
    /// Runs the AppHost guest process for publishing.
    /// </summary>
    /// <param name="appHostFile">The AppHost file to execute.</param>
    /// <param name="directory">The project directory.</param>
    /// <param name="environmentVariables">Environment variables to set for the process.</param>
    /// <param name="publishArgs">Additional arguments for publishing.</param>
    /// <param name="launcher">Strategy for launching the process.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A tuple of the exit code and captured output.</returns>
    public async Task<(int ExitCode, OutputCollector? Output)> PublishAsync(
        FileInfo appHostFile,
        DirectoryInfo directory,
        IDictionary<string, string> environmentVariables,
        string[]? publishArgs,
        IGuestProcessLauncher launcher,
        CancellationToken cancellationToken)
    {
        var commandSpec = _spec.PublishExecute ?? _spec.Execute;

        return await ExecuteCommandAsync(commandSpec, appHostFile, directory, environmentVariables, publishArgs, launcher, cancellationToken);
    }

    private async Task<(int ExitCode, OutputCollector? Output)> ExecuteCommandAsync(
        CommandSpec commandSpec,
        FileInfo appHostFile,
        DirectoryInfo directory,
        IDictionary<string, string> environmentVariables,
        string[]? additionalArgs,
        IGuestProcessLauncher launcher,
        CancellationToken cancellationToken)
    {
        var args = ReplacePlaceholders(commandSpec.Args, appHostFile, directory, additionalArgs);

        await EnsureMigrationFilesExistAsync(directory, cancellationToken);

        var mergedEnvironment = new Dictionary<string, string>(environmentVariables);
        if (commandSpec.EnvironmentVariables is not null)
        {
            foreach (var (key, value) in commandSpec.EnvironmentVariables)
            {
                mergedEnvironment[key] = value;
            }
        }

        _logger.LogDebug("Launching: {Command} {Args}", commandSpec.Command, string.Join(" ", args));
        return await launcher.LaunchAsync(commandSpec.Command, args, directory, mergedEnvironment, cancellationToken);
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
    public ProcessGuestLauncher CreateDefaultLauncher() => new(_spec.Language, _logger, _fileLoggerProvider, _commandResolver);

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
            if (arg == "{args}")
            {
                if (additionalArgs is { Length: > 0 })
                {
                    result.AddRange(additionalArgs);
                }

                continue;
            }

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
