// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;

namespace Aspire.Cli;

internal sealed class CliExecutionContext(DirectoryInfo workingDirectory, DirectoryInfo hivesDirectory, DirectoryInfo cacheDirectory, DirectoryInfo sdksDirectory, DirectoryInfo logsDirectory, string logFilePath, bool debugMode = false, IReadOnlyDictionary<string, string?>? environmentVariables = null, DirectoryInfo? homeDirectory = null)
{
    public DirectoryInfo WorkingDirectory { get; } = workingDirectory;
    public DirectoryInfo HivesDirectory { get; } = hivesDirectory;
    public DirectoryInfo CacheDirectory { get; } = cacheDirectory;
    public DirectoryInfo SdksDirectory { get; } = sdksDirectory;

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