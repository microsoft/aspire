// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;

namespace Aspire.Cli;

internal sealed class CliExecutionContext(DirectoryInfo workingDirectory, DirectoryInfo hivesDirectory, DirectoryInfo cacheDirectory, DirectoryInfo sdksDirectory, DirectoryInfo logsDirectory, string logFilePath, bool debugMode = false, IReadOnlyDictionary<string, string?>? environmentVariables = null, DirectoryInfo? homeDirectory = null, DirectoryInfo? packagesDirectory = null, string channel = "daily", int? prNumber = null)
{
    public DirectoryInfo WorkingDirectory { get; } = workingDirectory;
    public DirectoryInfo HivesDirectory { get; } = hivesDirectory;
    public DirectoryInfo CacheDirectory { get; } = cacheDirectory;
    public DirectoryInfo SdksDirectory { get; } = sdksDirectory;

    /// <summary>
    /// Gets the resolved hive label for the running CLI. For non-PR builds this is the
    /// identity channel verbatim — one of <c>stable</c>, <c>staging</c>, or <c>daily</c>.
    /// For PR builds (identity channel <c>pr</c> with a non-null <see cref="PrNumber"/>)
    /// this is the per-PR hive label <c>pr-&lt;N&gt;</c> (for example <c>pr-16820</c>),
    /// matching the directory layout the packaging service creates under the hives root.
    /// </summary>
    /// <remarks>
    /// This is the value reseed call sites (template factories, scaffolding, guest apphost
    /// project) write into a project's <c>aspire.config.json#channel</c>: it is the consumer-
    /// facing label that subsequent CLI runs use to select the right hive. The raw build-time
    /// identity value (the literal <c>pr</c> for PR builds) is exposed separately via
    /// <see cref="IdentityChannel"/> for callers that need the build-time taxonomy.
    /// </remarks>
    public string Channel => _channel == "pr" && PrNumber.HasValue
        ? $"pr-{PrNumber.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
        : _channel;

    /// <summary>
    /// Gets the raw build-time identity channel value for the running CLI — one of
    /// <c>stable</c>, <c>staging</c>, <c>daily</c>, or <c>pr</c>. Unlike <see cref="Channel"/>,
    /// this never resolves to a per-PR hive label; the literal <c>pr</c> is returned for
    /// every PR build regardless of <see cref="PrNumber"/>.
    /// </summary>
    public string IdentityChannel => _channel;

    private readonly string _channel = channel;

    /// <summary>
    /// Gets the pull-request number associated with this invocation, when
    /// <see cref="IdentityChannel"/> is <c>pr</c>. <see langword="null"/> for any
    /// non-PR channel.
    /// </summary>
    public int? PrNumber { get; } = prNumber;

    /// <summary>
    /// Gets the directory where restored NuGet packages are cached for apphost server sessions.
    /// </summary>
    public DirectoryInfo? PackagesDirectory { get; } = packagesDirectory;

    /// <summary>
    /// Gets the directory where CLI log files are stored.
    /// Used by cache clear command to clean up old log files.
    /// </summary>
    public DirectoryInfo LogsDirectory { get; } = logsDirectory;

    /// <summary>
    /// Gets the path to the current session's log file.
    /// </summary>
    public string LogFilePath { get; } = logFilePath;

    public DirectoryInfo HomeDirectory { get; } = homeDirectory ?? new DirectoryInfo(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
    public bool DebugMode { get; } = debugMode;

    /// <summary>
    /// Gets the environment variables for the CLI execution context.
    /// If null, the process environment variables should be used.
    /// </summary>
    public IReadOnlyDictionary<string, string?>? EnvironmentVariables { get; } = environmentVariables;

    /// <summary>
    /// Gets an environment variable value. Checks the context's environment variables first,
    /// then falls back to the process environment if no custom environment was provided.
    /// When a custom environment dictionary is provided (even if empty), only that dictionary is used
    /// and no fallback to the process environment occurs.
    /// </summary>
    /// <param name="variable">The environment variable name.</param>
    /// <returns>The value of the environment variable, or null if not found.</returns>
    public string? GetEnvironmentVariable(string variable)
    {
        if (EnvironmentVariables is not null)
        {
            // If a custom environment dictionary was provided, only use it (don't fall back)
            return EnvironmentVariables.TryGetValue(variable, out var value) ? value : null;
        }

        return Environment.GetEnvironmentVariable(variable);
    }

    private Command? _command;

    /// <summary>
    /// Gets or sets the currently executing command. Setting this property also signals the CommandSelected task.
    /// </summary>
    public Command? Command
    {
        get => _command;
        set
        {
            _command = value;
            if (value is not null)
            {
                CommandSelected.TrySetResult(value);
            }
        }
    }

    /// <summary>
    /// TaskCompletionSource that is completed when a command is selected and set on this context.
    /// </summary>
    public TaskCompletionSource<Command> CommandSelected { get; } = new();

    /// <summary>
    /// Gets the count of PR hives (PR build directories) on the developer machine.
    /// Hives are detected as subdirectories in the hives directory.
    /// This method accesses the file system.
    /// </summary>
    /// <returns>The number of PR hive subdirectories, or 0 if the hives directory does not exist.</returns>
    public int GetPrHiveCount()
    {
        if (!HivesDirectory.Exists)
        {
            return 0;
        }

        return HivesDirectory.GetDirectories().Length;
    }
}