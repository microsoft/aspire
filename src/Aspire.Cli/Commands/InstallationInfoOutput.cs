// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Acquisition;
using Aspire.Cli.Resources;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Commands;

internal static class InstallationInfoOutput
{
    public static IReadOnlyList<InstallationInfo> DescribeSelfSafely(IInstallationDiscovery discovery, ILogger logger)
    {
        try
        {
            return [discovery.DescribeSelf()];
        }
        catch (OperationCanceledException)
        {
            // Cancellation must propagate so the caller can honor the cancellation token
            // even if DescribeSelf ever becomes cancellable.
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not describe the running Aspire CLI installation for `aspire installs --self` output.");
            return CreateFailedDiscoveryRow();
        }
    }

    public static void RunWingetFirstRunProbe(WingetFirstRunProbe wingetFirstRunProbe)
    {
        // Give a never-run winget install a chance to stamp its sidecar before
        // we read it. The probe writes nothing on non-Windows hosts or when the
        // running binary isn't a winget portable install, so this is a cheap
        // no-op in the common case.
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(processPath))
        {
            return;
        }

        var binaryDir = Path.GetDirectoryName(processPath);
        if (!string.IsNullOrEmpty(binaryDir))
        {
            wingetFirstRunProbe.Run(binaryDir);
        }
    }

    private static IReadOnlyList<InstallationInfo> CreateFailedDiscoveryRow()
    {
        return
        [
            new InstallationInfo
            {
                Path = Environment.ProcessPath ?? string.Empty,
                CanonicalPath = null,
                PathStatus = InstallationPathStatus.NotOnPath,
                Status = InstallationInfoStatus.Failed,
                StatusReason = DoctorCommandStrings.InstallationDiscoveryFailedReason,
            }
        ];
    }
}
