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
    internal const string SidecarFileName = ".aspire-install.json";

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
    /// Writes <c>&lt;binaryDir&gt;/.aspire-install.json</c> when the running
    /// process is a winget portable install AND no sidecar exists yet.
    /// Idempotent: any second call is a no-op.
    /// </summary>
    public void Run(string binaryDir)
    {
        if (string.IsNullOrEmpty(binaryDir))
        {
            return;
        }

        var sidecarPath = Path.Combine(binaryDir, SidecarFileName);
        if (File.Exists(sidecarPath))
        {
            return;
        }

        var processPath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(processPath))
        {
            return;
        }

        if (!_registry.HasWingetAspireUninstallEntry(processPath))
        {
            return;
        }

        TryWriteSidecarAtomically(binaryDir, sidecarPath);
    }

    private void TryWriteSidecarAtomically(string binaryDir, string sidecarPath)
    {
        var tempPath = Path.Combine(binaryDir, $"{SidecarFileName}.{Guid.NewGuid():N}.tmp");

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
            File.Move(tempPath, sidecarPath, overwrite: false);
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
