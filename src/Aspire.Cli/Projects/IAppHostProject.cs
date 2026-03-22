// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Backchannel;
using Aspire.Cli.Utils;

namespace Aspire.Cli.Projects;

/// <summary>
/// Result of validating an AppHost file.
/// </summary>
internal record AppHostValidationResult(
    bool IsValid,
    bool IsPossiblyUnbuildable = false,
    bool IsUnsupported = false,
    string? Message = null);

/// <summary>
/// Context for updating packages in an AppHost project.
/// </summary>
internal sealed class UpdatePackagesContext
{
    /// <summary>
    /// Gets or sets the AppHost file.
    /// </summary>
    public required FileInfo AppHostFile { get; init; }

    /// <summary>
    /// Gets or sets the package channel to update to.
    /// </summary>
    public required Packaging.PackageChannel Channel { get; init; }
}

/// <summary>
/// Result of updating packages in an AppHost project.
/// </summary>
internal sealed class UpdatePackagesResult
{
    /// <summary>
    /// Gets or sets whether any updates were applied.
    /// </summary>
    public bool UpdatesApplied { get; init; }
}

/// <summary>
/// Context for adding a package to an AppHost project.
/// </summary>
internal sealed class AddPackageContext
{
    /// <summary>
    /// Gets or sets the AppHost file.
    /// </summary>
    public required FileInfo AppHostFile { get; init; }

    /// <summary>
    /// Gets or sets the package ID to add.
    /// </summary>
    public required string PackageId { get; init; }

    /// <summary>
    /// Gets or sets the package version to add.
    /// </summary>
    public required string PackageVersion { get; init; }

    /// <summary>
    /// Gets or sets the optional NuGet source.
    /// </summary>
    public string? Source { get; init; }

    /// <summary>
    /// Gets or sets the output collector for capturing stdout/stderr.
    /// Project implementations populate this during execution.
    /// Commands can access it for error display.
    /// </summary>
    public OutputCollector? OutputCollector { get; set; }
}

/// <summary>
/// Context for publishing an AppHost project.
/// </summary>
internal sealed class PublishContext
{
    /// <summary>
    /// Gets the AppHost file to publish.
    /// </summary>
    public required FileInfo AppHostFile { get; init; }

    /// <summary>
    /// Gets the output path for publish artifacts.
    /// </summary>
    public string? OutputPath { get; init; }

    /// <summary>
    /// Gets additional environment variables to pass to the AppHost.
    /// </summary>
    public IDictionary<string, string> EnvironmentVariables { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// Gets the arguments to pass to the AppHost for publishing.
    /// </summary>
    public string[] Arguments { get; init; } = [];

    /// <summary>
    /// Gets the task completion source for the backchannel connection.
    /// </summary>
    public TaskCompletionSource<IAppHostCliBackchannel>? BackchannelCompletionSource { get; init; }

    /// <summary>
    /// Gets the working directory for the command.
    /// </summary>
    public required DirectoryInfo WorkingDirectory { get; init; }

    /// <summary>
    /// Gets or sets the output collector for capturing stdout/stderr.
    /// Project implementations populate this during execution.
    /// Commands can access it for error display.
    /// </summary>
    public OutputCollector? OutputCollector { get; set; }

    /// <summary>
    /// Gets whether debug logging is enabled.
    /// </summary>
    public bool Debug { get; init; }

    /// <summary>
    /// Gets whether to start a debug session in the extension for the AppHost.
    /// </summary>
    public bool StartDebugSession { get; init; }

    /// <summary>
    /// Gets whether to skip building before running.
    /// </summary>
    public bool NoBuild { get; init; }
}

/// <summary>
/// Interface for AppHost projects of various types.
/// This is the single extension point for adding new language support.
/// </summary>
internal interface IAppHostProject
{
    /// <summary>
    /// Gets or sets whether this project type is unsupported in the current environment.
    /// </summary>
    bool IsUnsupported { get; set; }

    /// <summary>
    /// Gets the unique identifier for this language (e.g., "csharp", "typescript").
    /// Used for configuration storage and CLI arguments.
    /// </summary>
    string LanguageId { get; }

    /// <summary>
    /// Gets the human-readable display name (e.g., "C# (.NET)", "TypeScript (Node.js)").
    /// </summary>
    string DisplayName { get; }

    /// <summary>
    /// Gets the file patterns to search for when detecting apphosts.
    /// Examples: ["*.csproj", "*.fsproj", "apphost.cs"] or ["apphost.ts"]
    /// </summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The detection patterns for this project type.</returns>
    Task<string[]> GetDetectionPatternsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Determines if this handler can process the given file.
    /// Called after GetDetectionPatternsAsync patterns match to do deeper validation.
    /// </summary>
    /// <param name="appHostFile">The candidate apphost file.</param>
    /// <returns>True if this handler can process the file; otherwise, false.</returns>
    bool CanHandle(FileInfo appHostFile);

    /// <summary>
    /// Gets the default apphost filename for this language (e.g., "apphost.cs", "apphost.ts").
    /// Returns null if the language has not been resolved yet.
    /// </summary>
    string? AppHostFileName { get; }

    /// <summary>
    /// Determines whether this AppHost should use project references instead of package references.
    /// </summary>
    /// <param name="appHostFile">The AppHost file being operated on.</param>
    /// <returns><see langword="true"/> when project-reference mode should be used; otherwise <see langword="false"/>.</returns>
    bool IsUsingProjectReferences(FileInfo appHostFile);

    /// <summary>
    /// Runs the AppHost project.
    /// </summary>
    /// <param name="context">The context containing all information needed to run the AppHost.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The exit code from running the AppHost.</returns>
    Task<int> RunAsync(AppHostProjectContext context, CancellationToken cancellationToken);

    /// <summary>
    /// Publishes the AppHost project.
    /// </summary>
    /// <param name="context">The context containing all information needed to publish the AppHost.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The exit code from publishing the AppHost.</returns>
    Task<int> PublishAsync(PublishContext context, CancellationToken cancellationToken);

    /// <summary>
    /// Validates that a candidate file is a valid AppHost for this project type.
    /// This does deeper validation beyond just file pattern matching.
    /// </summary>
    /// <param name="appHostFile">The candidate AppHost file to validate.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A validation result indicating if the file is valid and any additional status.</returns>
    Task<AppHostValidationResult> ValidateAppHostAsync(FileInfo appHostFile, CancellationToken cancellationToken);

    /// <summary>
    /// Adds a package to the AppHost project.
    /// </summary>
    /// <param name="context">The context containing package information.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>True if the package was added successfully; otherwise, false.</returns>
    Task<bool> AddPackageAsync(AddPackageContext context, CancellationToken cancellationToken);

    /// <summary>
    /// Updates packages in the AppHost project to the latest versions.
    /// </summary>
    /// <param name="context">The context containing update information.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The result of the update operation.</returns>
    Task<UpdatePackagesResult> UpdatePackagesAsync(UpdatePackagesContext context, CancellationToken cancellationToken);

    /// <summary>
    /// Finds any running instance of this AppHost and stops it.
    /// </summary>
    /// <param name="appHostFile">The AppHost file to check for running instances.</param>
    /// <param name="homeDirectory">The user's home directory for computing socket paths.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The result indicating what happened with the running instance check.</returns>
    Task<RunningInstanceResult> FindAndStopRunningInstanceAsync(FileInfo appHostFile, DirectoryInfo homeDirectory, CancellationToken cancellationToken);

    /// <summary>
    /// Gets the UserSecretsId for the specified AppHost file.
    /// </summary>
    /// <param name="appHostFile">The AppHost file.</param>
    /// <param name="autoInit">If true, initializes user secrets if not configured.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task<string?> GetUserSecretsIdAsync(FileInfo appHostFile, bool autoInit, CancellationToken cancellationToken);
}

/// <summary>
/// Result of finding and stopping a running instance.
/// </summary>
internal enum RunningInstanceResult
{
    /// <summary>
    /// No running instance was found.
    /// </summary>
    NoRunningInstance,

    /// <summary>
    /// A running instance was found and successfully stopped.
    /// </summary>
    InstanceStopped,

    /// <summary>
    /// A running instance was found but failed to stop.
    /// </summary>
    StopFailed
}
