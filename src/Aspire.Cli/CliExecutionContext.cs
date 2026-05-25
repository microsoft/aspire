// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;

namespace Aspire.Cli;

internal sealed class CliExecutionContext(DirectoryInfo workingDirectory, DirectoryInfo hivesDirectory, DirectoryInfo cacheDirectory, DirectoryInfo sdksDirectory, DirectoryInfo logsDirectory, string logFilePath, bool debugMode = false, IReadOnlyDictionary<string, string?>? environmentVariables = null, DirectoryInfo? homeDirectory = null, DirectoryInfo? packagesDirectory = null, string identityChannel = "local", DirectoryInfo? aspireHomeDirectory = null)
{
    public DirectoryInfo WorkingDirectory { get; } = workingDirectory;
    public DirectoryInfo HivesDirectory { get; } = hivesDirectory;
    public DirectoryInfo CacheDirectory { get; } = cacheDirectory;
    public DirectoryInfo SdksDirectory { get; } = sdksDirectory;

    /// <summary>
    /// Gets the hive label baked into the <strong>running CLI binary</strong>:
    /// one of <c>local</c>, <c>stable</c>, <c>staging</c>, <c>daily</c>, or the
    /// per-PR label <c>pr-&lt;N&gt;</c> (for example <c>pr-16820</c>). The value
    /// is sourced from <c>[AssemblyMetadata("AspireCliChannel", "...")]</c> and
    /// is consumed verbatim by the packaging service to select the matching
    /// hive directory for this CLI process.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the <em>CLI's identity</em>, not the channel a project is asking
    /// restore to use. Restore/packaging decisions that depend on what the
    /// project requested (for example PSM emission for an apphost) must key on
    /// the project's <c>aspire.config.json#channel</c>, not this property.
    /// </para>
    /// <para>
    /// Reseed call sites (template factories, scaffolding, guest apphost
    /// project) write this value into a project's
    /// <c>aspire.config.json#channel</c> as the default — that is the
    /// consumer-facing label subsequent CLI runs use to select the right hive.
    /// CI bakes <c>pr-&lt;PR_NUMBER&gt;</c> directly for PR builds, so no
    /// runtime "<c>pr</c> + parsed PrNumber" join is required.
    /// </para>
    /// </remarks>
    public string IdentityChannel { get; } = identityChannel;

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

    /// <summary>
    /// Gets or sets the log file path of the CLI process managing the connected app host.
    /// This is populated after connecting to a running app host via <see cref="Backchannel.AppHostConnectionResolver"/>.
    /// </summary>
    public string? AppHostCliLogFilePath { get; set; }

    public DirectoryInfo HomeDirectory { get; } = homeDirectory ?? new DirectoryInfo(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    /// <summary>
    /// Gets the Aspire state root used for route-specific install layouts.
    /// </summary>
    /// <remarks>
    /// Production wires this explicitly via <see cref="Program.BuildCliExecutionContext"/>
    /// using <see cref="Utils.CliPathHelper.GetAspireHomeDirectory(string?, Microsoft.Extensions.Logging.ILogger?)"/>
    /// so the install-route sidecar lookup runs. When neither <c>aspireHomeDirectory</c>
    /// nor <c>homeDirectory</c> are supplied (direct construction in tests), the same
    /// install-route lookup is performed here so route-aware code paths see a
    /// consistent value. When <c>homeDirectory</c> is supplied without
    /// <c>aspireHomeDirectory</c>, the home stays contained within the test directory
    /// at <c>&lt;homeDirectory&gt;/.aspire</c> — the install-route lookup is
    /// intentionally skipped because tests passing an explicit <c>homeDirectory</c>
    /// are declaring their own filesystem sandbox.
    /// </remarks>
    public DirectoryInfo AspireHomeDirectory { get; } = aspireHomeDirectory ?? new DirectoryInfo(
        homeDirectory is not null
            ? Path.Combine(homeDirectory.FullName, ".aspire")
            : Utils.CliPathHelper.GetAspireHomeDirectory());

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
    /// Gets the count of hives (per-channel CLI build directories) on the developer machine,
    /// including the <c>local</c> hive and any <c>pr-*</c> hives.
    /// Hives are detected as subdirectories in the hives directory.
    /// This method accesses the file system.
    /// </summary>
    /// <returns>The number of hive subdirectories, or 0 if the hives directory does not exist.</returns>
    public int GetHiveCount()
    {
        if (!HivesDirectory.Exists)
        {
            return 0;
        }

        return HivesDirectory.GetDirectories().Length;
    }
}
