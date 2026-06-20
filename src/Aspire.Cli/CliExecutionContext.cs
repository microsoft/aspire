// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using Aspire.Cli.Resources;
using Aspire.Shared;

namespace Aspire.Cli;

internal sealed class CliExecutionContext(DirectoryInfo workingDirectory, DirectoryInfo hivesDirectory, DirectoryInfo cacheDirectory, DirectoryInfo sdksDirectory, DirectoryInfo logsDirectory, string logFilePath, string identityChannel, bool debugMode = false, IReadOnlyDictionary<string, string?>? environmentVariables = null, DirectoryInfo? homeDirectory = null, DirectoryInfo? packagesDirectory = null, DirectoryInfo? aspireHomeDirectory = null, string? identityVersion = null, string? identityCommit = null, string? nugetServiceIndexOverride = null, bool identityOverridden = false, DirectoryInfo? identityPackagesDirectory = null)
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
    /// Gets the running CLI's informational version string (for example
    /// <c>13.4.0-preview.1.25366.3</c>), as resolved by
    /// <see cref="Acquisition.IIdentityResolver"/>. This is the value every
    /// identity-sensitive version decision must read instead of going to the
    /// assembly directly, so that <c>ASPIRE_CLI_VERSION</c> / the
    /// <c>version</c> field of <c>.aspire-install.json</c> are honored.
    /// </summary>
    /// <remarks>
    /// In production the resolver always supplies a value (env → sidecar →
    /// assembly fallback), so this is non-null. When the context is constructed
    /// directly in tests without an explicit <c>identityVersion</c>, it falls
    /// back to the assembly's
    /// <see cref="System.Reflection.AssemblyInformationalVersionAttribute"/> —
    /// matching the legacy <c>VersionHelper.GetDefaultTemplateVersion()</c>
    /// behavior so existing tests continue to observe the assembly version.
    /// </remarks>
    public string IdentityVersion { get; } = identityVersion
        ?? PackageUpdateHelpers.GetCurrentAssemblyVersion()
        ?? throw new InvalidOperationException(ErrorStrings.UnableToRetrieveAssemblyVersion);

    /// <summary>
    /// Gets the running CLI's identity version with any build-metadata suffix
    /// (the <c>+&lt;sha&gt;</c> portion) stripped, e.g.
    /// <c>13.4.0-preview.1.25366.3+abc123</c> → <c>13.4.0-preview.1.25366.3</c>.
    /// This is the SDK / bundled-package version: the CLI version is the SDK
    /// version, so the bundled server and packages must match. Identity-sensitive
    /// SDK-version decisions read this instead of
    /// <c>VersionHelper.GetDefaultSdkVersion()</c>.
    /// </summary>
    /// <remarks>
    /// Computed once and cached: <see cref="IdentityVersion"/> is immutable and
    /// <see cref="StripBuildMetadata"/> is a pure string operation, so there is
    /// no reason to recompute it on every access.
    /// </remarks>
    public string IdentitySdkVersion => field ??= StripBuildMetadata(IdentityVersion);

    /// <summary>
    /// Gets the running CLI's source-revision commit (the <c>+&lt;sha&gt;</c>
    /// suffix of <see cref="System.Reflection.AssemblyInformationalVersionAttribute"/>
    /// when no override is in effect), as resolved by
    /// <see cref="Acquisition.IIdentityResolver"/>. Identity-sensitive commit
    /// decisions (for example the staging darc feed name) read this instead of
    /// parsing the assembly informational version directly. Empty when neither
    /// an override nor the assembly informational version carries a commit
    /// suffix.
    /// </summary>
    public string? IdentityCommit { get; } = identityCommit;

    /// <summary>
    /// Gets a value indicating whether at least one identity field
    /// (<see cref="IdentityChannel"/>, <see cref="IdentityVersion"/>,
    /// <see cref="IdentityCommit"/>, <see cref="NuGetServiceIndexOverride"/>
    /// or <see cref="IdentityPackagesDirectory"/>)
    /// was sourced from an <c>ASPIRE_CLI_*</c> environment variable or the
    /// install sidecar rather than the assembly's build-time stamp. When
    /// <see langword="true"/> the CLI is emulating a build it is not, so a
    /// startup notice is surfaced (see
    /// <c>Program.DisplayFirstTimeUseNoticeIfNeededAsync</c>) and tooling can
    /// flag the run as diagnostic. See <c>docs/specs/cli-identity-sidecar.md</c>.
    /// </summary>
    public bool IdentityOverridden { get; } = identityOverridden;

    /// <summary>
    /// Optional replacement for the canonical
    /// <c>https://api.nuget.org/v3/index.json</c> URL when the CLI emits
    /// <em>new</em> <c>NuGet.config</c> files. <see langword="null"/> means
    /// use the canonical URL. This is a test-bench affordance; it never
    /// rewrites URLs the CLI reads from existing user configs. See
    /// <c>docs/specs/cli-identity-sidecar.md</c>.
    /// </summary>
    public string? NuGetServiceIndexOverride { get; } = nugetServiceIndexOverride;

    /// <summary>
    /// Optional directory of <c>.nupkg</c> files that the CLI's <c>Aspire*</c>
    /// package feed resolves from directly, sourced from
    /// <c>ASPIRE_CLI_PACKAGES</c> or the <c>packages</c> field of the install
    /// sidecar. <see langword="null"/> means no override is in effect.
    /// </summary>
    /// <remarks>
    /// When set, <c>PackagingService.GetChannelsAsync</c> synthesizes a package
    /// channel named after <see cref="IdentityChannel"/> that maps
    /// <c>Aspire*</c> to this directory (and everything else to nuget.org),
    /// replacing any same-named hive discovered under <c>~/.aspire/hives</c>.
    /// This lets a locally built CLI resolve locally built packages (for
    /// example from <c>artifacts/packages/&lt;Config&gt;/Shipping</c>) without
    /// copying them into a hive. The directory must contain at most one version
    /// of each <c>Aspire*</c> package — the packaging service fails fast on
    /// duplicates so an unintended version cannot be silently selected. This is
    /// distinct from <see cref="PackagesDirectory"/>, which is the CLI's own
    /// restore cache. See <c>docs/specs/cli-identity-sidecar.md</c>.
    /// </remarks>
    public DirectoryInfo? IdentityPackagesDirectory { get; } = identityPackagesDirectory;

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
    /// Production wires this explicitly via <c>Program.BuildCliExecutionContext</c>
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

    /// <summary>
    /// Strips the build-metadata suffix (everything from the first <c>+</c>)
    /// from an informational version string. SemVer build metadata is not part
    /// of version-equality comparisons, so SDK / package version decisions
    /// compare against the metadata-free form.
    /// </summary>
    private static string StripBuildMetadata(string version)
    {
        // AssemblyInformationalVersion shape: "13.4.0-preview.1.25366.3+abc123".
        // The "+<sha>" build metadata is optional; some build configurations omit it.
        var plusIndex = version.IndexOf('+');
        return plusIndex > 0 ? version[..plusIndex] : version;
    }
}
