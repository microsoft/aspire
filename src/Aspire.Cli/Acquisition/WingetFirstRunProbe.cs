// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Acquisition;

/// <summary>
/// First-run self-stamp for winget-installed CLI binaries. Writes the
/// install-route sidecar when the OS reports the running binary as a winget
/// portable install. See <c>docs/specs/install-routes.md</c>.
/// </summary>
internal sealed class WingetFirstRunProbe
{
    private static readonly byte[] s_wingetSidecarContent = Encoding.UTF8.GetBytes("{\"source\":\"winget\"}");

    private readonly IWindowsRegistryReader _registry;
    private readonly ILogger<WingetFirstRunProbe> _logger;

    public WingetFirstRunProbe(IWindowsRegistryReader registry, ILogger<WingetFirstRunProbe> logger)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(logger);
        _registry = registry;
        _logger = logger;
    }

    /// <summary>
    /// Writes <c>&lt;binaryDir&gt;/.aspire-install.json</c> next to
    /// <paramref name="realProcessPath"/> when that path identifies a winget
    /// portable install AND no sidecar exists yet. Idempotent: any second
    /// call is a no-op.
    /// </summary>
    /// <param name="realProcessPath">
    /// The fully-resolved path to the running CLI binary, after symlink
    /// resolution via
    /// <see cref="Utils.CliPathHelper.ResolveSymlinkOrOriginalPath(string, ILogger?)"/>.
    /// Callers must pass the resolved path, not the raw
    /// <see cref="Environment.ProcessPath"/>: winget portable installs expose the
    /// CLI through a command-alias symlink under
    /// <c>%LOCALAPPDATA%\Microsoft\WinGet\Links\aspire.exe</c>, and the
    /// registry matcher's <c>InstallLocation</c> containment check requires
    /// the resolved path so that the link-path location does not falsely
    /// look outside the package's install directory. Both call sites that
    /// previously read <see cref="Environment.ProcessPath"/> directly inside
    /// this method instead resolve once at the caller and pass the result
    /// through, which also ensures the sidecar is stamped next to the real
    /// binary rather than next to the link.
    /// </param>
    public void Run(string realProcessPath)
    {
        if (string.IsNullOrEmpty(realProcessPath))
        {
            return;
        }

        var binaryDir = Path.GetDirectoryName(realProcessPath);
        if (string.IsNullOrEmpty(binaryDir))
        {
            return;
        }

        var sidecarPath = Path.Combine(binaryDir, InstallSidecarReader.SidecarFileName);
        // Self-heal: skip only when the sidecar exists AND its `source` field
        // parses cleanly. A malformed/truncated/oversized/empty sidecar (mid-write
        // crash, manual edit, source string missing, etc.) counts as "needs
        // overwrite" — fall through so the probe can re-stamp the canonical
        // {"source":"winget"} payload below if the registry still claims this
        // binary as winget. Sidecars whose `source` is a non-winget string
        // (e.g. {"source":"script"} from a different install route) parse as
        // non-null and are still skipped — the probe never clobbers a foreign
        // route's sidecar, only a corrupt one.
        var sidecarExists = File.Exists(sidecarPath);
        if (sidecarExists && InstallSidecarReader.ReadSourceField(sidecarPath) is not null)
        {
            return;
        }

        if (!_registry.HasWingetAspireUninstallEntry(realProcessPath))
        {
            return;
        }

        // overwriteIfCorrupt: only true on the self-heal branch (the corrupt
        // sidecar already exists). For the cold-start branch (no sidecar yet)
        // we keep overwrite:false so two racing probes don't clobber a sidecar
        // that just got written by a different install-route's atomic writer.
        TryWriteSidecarAtomically(binaryDir, sidecarPath, overwriteIfCorrupt: sidecarExists);
    }

    private void TryWriteSidecarAtomically(string binaryDir, string sidecarPath, bool overwriteIfCorrupt)
    {
        var tempPath = Path.Combine(binaryDir, $"{InstallSidecarReader.SidecarFileName}.{Guid.NewGuid():N}.tmp");

        try
        {
            File.WriteAllBytes(tempPath, s_wingetSidecarContent);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Winget first-run probe could not write temp sidecar at {Path}.", tempPath);
            return;
        }

        try
        {
            File.Move(tempPath, sidecarPath, overwrite: overwriteIfCorrupt);
            _logger.LogDebug("Winget first-run probe wrote sidecar at {Path}.", sidecarPath);
        }
        catch (Exception ex)
        {
            // Either a concurrent winner already stamped the same literal bytes
            // (IOException from overwrite:false), a permission failure, or any
            // other unexpected error. The probe is best-effort startup code, so
            // swallow the failure and always clean up the temp file we just
            // created so a partial install doesn't leave litter behind.
            _logger.LogDebug(ex, "Winget first-run probe could not rename temp sidecar to {Path}.", sidecarPath);
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
            _logger.LogDebug(ex, "Winget first-run probe could not delete temp sidecar at {Path}.", tempPath);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogDebug(ex, "Winget first-run probe could not delete temp sidecar at {Path}.", tempPath);
        }
    }
}
