// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Cli.Configuration;
using Aspire.Cli.Utils;

namespace Aspire.Cli.Projects;

/// <summary>
/// Result of preparing an AppHost server for running.
/// </summary>
/// <param name="Success">Whether preparation succeeded.</param>
/// <param name="Output">Build/preparation output for display on failure.</param>
/// <param name="ChannelName">The NuGet channel used (SDK mode only, null for bundle mode).</param>
/// <param name="NeedsCodeGeneration">Whether code generation is needed for the guest language.</param>
internal sealed record AppHostServerPrepareResult(
    bool Success,
    OutputCollector? Output,
    string? ChannelName = null,
    bool NeedsCodeGeneration = false);

/// <summary>
/// Represents an AppHost server that can be prepared and run.
/// This abstraction allows for different implementations:
/// - SDK mode: dynamically generates and builds a .NET project
/// - Bundle mode: uses a pre-built server from the Aspire bundle
/// </summary>
internal interface IAppHostServerProject
{
    /// <summary>
    /// Gets the path to the user's app (the polyglot apphost directory).
    /// </summary>
    string AppPath { get; }

    /// <summary>
    /// Prepares the AppHost server for running.
    /// For SDK mode: creates project files and builds the project.
    /// For bundle mode: restores integration packages from NuGet.
    /// </summary>
    /// <param name="sdkVersion">The Aspire SDK version to use.</param>
    /// <param name="integrations">The integration references (NuGet packages and/or project references) required by the app host.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The preparation result indicating success/failure and any output.</returns>
    Task<AppHostServerPrepareResult> PrepareAsync(
        string sdkVersion,
        IEnumerable<IntegrationReference> integrations,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs the AppHost server process.
    /// </summary>
    /// <param name="hostPid">The host process ID (CLI) for orphan detection.</param>
    /// <param name="environmentVariables">Environment variables to pass to the server.</param>
    /// <param name="additionalArgs">Additional command-line arguments.</param>
    /// <param name="debug">Whether to enable debug logging.</param>
    /// <returns>The socket path, server process, and an output collector for stdout/stderr.</returns>
    (string SocketPath, Process Process, OutputCollector OutputCollector) Run(
        int hostPid,
        IReadOnlyDictionary<string, string>? environmentVariables = null,
        string[]? additionalArgs = null,
        bool debug = false);

    /// <summary>
    /// Gets a unique identifier path for this AppHost, used for running instance detection.
    /// For SDK mode: returns the generated project file path.
    /// For prebuilt mode: returns the app path.
    /// </summary>
    /// <returns>A path that uniquely identifies this AppHost.</returns>
    string GetInstanceIdentifier();
}
