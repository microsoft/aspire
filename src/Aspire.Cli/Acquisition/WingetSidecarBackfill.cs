// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Acquisition;

/// <summary>
/// Back-fills the install-source sidecar (<c>.aspire-install.json</c>) next to
/// the running CLI binary. The winget portable manifest has no post-install
/// hook, so unlike the other install routes (which stamp the sidecar from the
/// installer script), winget installs rely on the CLI itself to write the
/// sidecar on a subsequent startup. See <c>docs/specs/install-sources.md</c>.
/// </summary>
/// <remarks>
/// <para>
/// Called from the <see cref="CliExecutionContext"/> DI factory on every CLI
/// startup (see <c>Program.TryEnsureWingetSidecar</c>). Two write paths:
/// </para>
/// <list type="bullet">
///   <item>
///     <description>
///     <c>{"source":"winget"}</c> when the Windows Uninstall registry hive
///     reports the running binary as a winget portable install. Promotes the
///     row to a recognized winget install in <c>aspire --info</c>.
///     </description>
///   </item>
///   <item>
///     <description>
///     <c>{"backfilled":true}</c> when the running binary is on Windows but
///     not a winget install. This sentinel has no <c>source</c> field, so
///     <see cref="InstallSidecarReader"/> parses it as <see cref="InstallSource.Unknown"/>
///     and downstream consumers see the row exactly as they would for a
///     missing sidecar — except the next startup hits the <c>File.Exists</c>
///     fast path at the top of <see cref="EnsureSidecar(string)"/> and skips
///     the registry walk. Without this negative marker, every non-winget
///     Windows startup paid two full Uninstall-hive enumerations forever.
///     </description>
///   </item>
/// </list>
/// <para>
/// Idempotent and atomic: an existing sidecar (real or backfilled sentinel)
/// is never overwritten, so a later upgrade to a real install route that
/// stamps its own sidecar wins on first run and the backfill never touches it.
/// Concurrent invocations race the temp+move; the loser cleans up its temp
/// file. Failures are logged but never thrown — back-fill is best-effort
/// startup hygiene, not a CLI prerequisite.
/// </para>
/// </remarks>
internal sealed class WingetSidecarBackfill
{
    // Positive write: the running binary IS a winget portable install. Promotes
    // the row to a recognized winget install in `aspire --info`.
    private static readonly byte[] s_wingetSidecarContent = Encoding.UTF8.GetBytes("{\"source\":\"winget\"}");

    // Negative-result sentinel: the running binary is NOT a winget portable
    // install on this host. Wire-identical to "no sidecar" because
    // InstallSidecarReader.TryRead reports Unknown / null source when the
    // `source` field is absent. Its only job is to make the `File.Exists`
    // fast path at the top of EnsureSidecar fire on subsequent startups, so we
    // walk the HKCU + HKLM Uninstall hives at most once per install location.
    private static readonly byte[] s_backfilledSentinelContent = Encoding.UTF8.GetBytes("{\"backfilled\":true}");

    private readonly IWindowsRegistryReader _registry;
    private readonly ILogger<WingetSidecarBackfill> _logger;

    public WingetSidecarBackfill(IWindowsRegistryReader registry, ILogger<WingetSidecarBackfill> logger)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(logger);
        _registry = registry;
        _logger = logger;
    }

    /// <summary>
    /// Ensures that <c>&lt;binaryDir&gt;/.aspire-install.json</c> exists. On a
    /// winget portable install writes <c>{"source":"winget"}</c>; on any other
    /// Windows install writes a <c>{"backfilled":true}</c> sentinel so this
    /// method's registry walk runs at most once per install location.
    /// Idempotent: an existing sidecar is never overwritten.
    /// </summary>
    public void EnsureSidecar(string binaryDir)
        => EnsureSidecar(binaryDir, Environment.ProcessPath);

    /// <summary>
    /// Test seam for <see cref="EnsureSidecar(string)"/>: takes the running
    /// binary path as an explicit parameter rather than reading
    /// <see cref="Environment.ProcessPath"/>, so tests can pin both the write
    /// target and the registry-probe argument to a controlled value. Production
    /// callers go through the single-arg overload.
    /// </summary>
    internal void EnsureSidecar(string binaryDir, string? processPath)
    {
        // The back-fill exists solely to amortize a HKCU + HKLM Uninstall hive walk
        // that only makes sense on Windows; on Linux/macOS there is no registry to
        // probe and IWindowsRegistryReader resolves to a no-op stub. Writing a
        // negative-result sentinel on non-Windows would still drop a stray
        // .aspire-install.json next to the running binary (e.g. under the dotnet
        // global-tools store) with no corresponding benefit, so exit before any IO.
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        if (string.IsNullOrEmpty(binaryDir))
        {
            return;
        }

        var sidecarPath = Path.Combine(binaryDir, InstallSidecarReader.SidecarFileName);
        if (File.Exists(sidecarPath))
        {
            return;
        }

        if (string.IsNullOrEmpty(processPath))
        {
            return;
        }

        // Pick the payload up front so the atomic-write helper is content-agnostic
        // and both positive and negative paths share the same temp+move semantics.
        var content = _registry.HasWingetAspireUninstallEntry(processPath)
            ? s_wingetSidecarContent
            : s_backfilledSentinelContent;

        TryWriteSidecarAtomically(binaryDir, sidecarPath, content);
    }

    private void TryWriteSidecarAtomically(string binaryDir, string sidecarPath, byte[] content)
    {
        var tempPath = Path.Combine(binaryDir, $"{InstallSidecarReader.SidecarFileName}.{Guid.NewGuid():N}.tmp");

        try
        {
            File.WriteAllBytes(tempPath, content);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Winget sidecar back-fill could not write temp sidecar at {Path}.", tempPath);
            return;
        }

        try
        {
            File.Move(tempPath, sidecarPath, overwrite: false);
            _logger.LogDebug("Winget sidecar back-fill wrote sidecar at {Path}.", sidecarPath);
        }
        catch (Exception ex)
        {
            // Either a concurrent winner already stamped the same literal bytes
            // (IOException from overwrite:false), a permission failure, or any
            // other unexpected error. Back-fill is best-effort startup code, so
            // swallow the failure and always clean up the temp file we just
            // created so a partial install doesn't leave litter behind.
            _logger.LogDebug(ex, "Winget sidecar back-fill could not rename temp sidecar to {Path}.", sidecarPath);
            TryDeleteTemp(tempPath);
        }
    }

    private void TryDeleteTemp(string tempPath)
    {
        try
        {
            File.Delete(tempPath);
        }
        catch (IOException ex)
        {
            _logger.LogDebug(ex, "Winget sidecar back-fill could not delete temp sidecar at {Path}.", tempPath);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogDebug(ex, "Winget sidecar back-fill could not delete temp sidecar at {Path}.", tempPath);
        }
    }
}
