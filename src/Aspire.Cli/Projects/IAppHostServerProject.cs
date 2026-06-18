// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Cli.Configuration;
using Aspire.Cli.Processes;
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
/// Result of <see cref="IAppHostServerProject.Run"/> — a launched AppHost server process plus
/// the cleanup handle that owns it.
/// </summary>
/// <param name="SocketPath">RPC socket the server is publishing on.</param>
/// <param name="Process">
/// The underlying <see cref="System.Diagnostics.Process"/>. Callers may observe state
/// (<see cref="Process.HasExited"/>, <see cref="Process.Id"/>, etc.) and drive lifecycle APIs
/// (<see cref="Process.Kill(bool)"/>, <see cref="Process.WaitForExitAsync(CancellationToken)"/>),
/// but must dispose via <see cref="ProcessLifetime"/> instead of the <see cref="Process"/>
/// directly so the isolated-spawn path can release its anonymous pipes and stdin handle.
/// Callers should prefer the <see cref="ReadExitCode"/> / <see cref="ReadHasExited"/> accessors
/// over <see cref="Process.ExitCode"/> / <see cref="Process.HasExited"/> for status checks,
/// because the managed Process returned for the isolated Windows path is obtained via
/// <see cref="Process.GetProcessById(int)"/> and cannot reliably surface ExitCode/HasExited
/// (see https://github.com/dotnet/runtime/issues/45003 and <see cref="IsolatedProcess"/>).
/// </param>
/// <param name="OutputCollector">Captured stdout/stderr for failure display.</param>
/// <param name="FileName">
/// The launched executable, captured at spawn time. Reading <see cref="ProcessStartInfo.FileName"/>
/// off <see cref="Process"/> is unreliable on the isolated Windows path (the Process is obtained
/// via <see cref="Process.GetProcessById(int)"/>, which returns an empty <see cref="ProcessStartInfo"/>),
/// so telemetry should read identity from this field instead.
/// </param>
/// <param name="Arguments">The argument list, captured at spawn time. Same rationale as <paramref name="FileName"/>.</param>
/// <param name="ProcessLifetime">
/// Disposes the process and any associated isolated-spawn resources. Always non-null; for the
/// non-isolated path this is a thin adapter that just disposes <paramref name="Process"/>, for
/// the isolated path it is the <see cref="IsolatedProcess"/> wrapper that also drains the
/// stdout/stderr pumps and closes the anonymous pipes + NUL stdin handle on Windows.
/// </param>
/// <param name="ExitCodeOverride">
/// Optional override for <see cref="ReadExitCode"/>. The isolated Windows spawn path supplies
/// one because <see cref="Process.ExitCode"/> on a <see cref="Process.GetProcessById(int)"/>
/// instance throws <see cref="InvalidOperationException"/>. Non-isolated callers leave this
/// <see langword="null"/> and the accessor reads from <see cref="Process"/> directly.
/// </param>
/// <param name="HasExitedOverride">Optional override for <see cref="ReadHasExited"/>. Same rationale as <paramref name="ExitCodeOverride"/>.</param>
internal sealed record AppHostServerRunResult(
    string SocketPath,
    Process Process,
    OutputCollector OutputCollector,
    string FileName,
    IReadOnlyList<string> Arguments,
    IAsyncDisposable ProcessLifetime,
    Func<int>? ExitCodeOverride = null,
    Func<bool>? HasExitedOverride = null)
{
    /// <summary>
    /// Reads the child's exit code. Use this instead of <c>Process.ExitCode</c> — on the
    /// isolated Windows path that property throws because the managed Process came from
    /// <see cref="Process.GetProcessById(int)"/>; the override consults the kept CreateProcess
    /// handle directly.
    /// </summary>
    public int ReadExitCode() => ExitCodeOverride is { } reader ? reader() : Process.ExitCode;

    /// <summary>
    /// Reads whether the child has exited. Same rationale as <see cref="ReadExitCode"/> —
    /// prefer this over <c>Process.HasExited</c> for status checks on the isolated Windows path.
    /// </summary>
    public bool ReadHasExited() => HasExitedOverride is { } reader ? reader() : Process.HasExited;
}

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
    string AppDirectoryPath { get; }

    /// <summary>
    /// Prepares the AppHost server for running.
    /// For SDK mode: creates project files and builds the project.
    /// For bundle mode: restores integration packages from NuGet.
    /// </summary>
    /// <param name="sdkVersion">The Aspire SDK version to use.</param>
    /// <param name="integrations">The integration references (NuGet packages and/or project references) required by the app host.</param>
    /// <param name="requestedChannel">The package channel to use for this prepare operation, or <see langword="null" /> to use the project configuration.</param>
    /// <param name="packageSourceOverride">Optional package source to prefer for Aspire package restore.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The preparation result indicating success/failure and any output.</returns>
    Task<AppHostServerPrepareResult> PrepareAsync(
        string sdkVersion,
        IEnumerable<IntegrationReference> integrations,
        string? requestedChannel = null,
        string? packageSourceOverride = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs the AppHost server process.
    /// </summary>
    /// <param name="hostPid">The host process ID (CLI) for orphan detection.</param>
    /// <param name="environmentVariables">Environment variables to pass to the server.</param>
    /// <param name="additionalArgs">Additional command-line arguments.</param>
    /// <param name="debug">Whether to enable debug logging.</param>
    /// <param name="isolateConsole">
    /// When <see langword="true"/>, on Windows the server is spawned via
    /// <see cref="IsolatedProcess"/> into its own hidden console (CREATE_NEW_CONSOLE | SW_HIDE)
    /// so DCP's <c>stop-process-tree</c> can <c>AttachConsole</c> + post <c>CTRL_C_EVENT</c>
    /// against the server without also signalling the CLI. On Unix the flag is observed but the
    /// resulting spawn is effectively the same as today's path (a thin <see cref="Process.Start(ProcessStartInfo)"/>
    /// wrapper) because SIGTERM via the process group is enough. On Windows the server is bound to
    /// the process-wide <see cref="WindowsConsoleProcessJob"/> kill-on-close safety net.
    /// </param>
    /// <returns>The launched server process and its associated cleanup handle.</returns>
    AppHostServerRunResult Run(
        int hostPid,
        IReadOnlyDictionary<string, string>? environmentVariables = null,
        string[]? additionalArgs = null,
        bool debug = false,
        bool isolateConsole = false);

    /// <summary>
    /// Gets a unique identifier path for this AppHost, used for running instance detection.
    /// For SDK mode: returns the generated project file path.
    /// For prebuilt mode: returns the app path.
    /// </summary>
    /// <returns>A path that uniquely identifies this AppHost.</returns>
    string GetInstanceIdentifier();
}
