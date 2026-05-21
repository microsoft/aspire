// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Acquisition;
using Aspire.Cli.Resources;
using Microsoft.Extensions.Logging;
using Spectre.Console;

namespace Aspire.Cli.Commands;

internal static class InstallationInfoOutput
{
    public static async Task<IReadOnlyList<InstallationInfo>> DiscoverAllSafelyAsync(
        IInstallationDiscovery discovery,
        WingetFirstRunProbe wingetFirstRunProbe,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        try
        {
            RunWingetFirstRunProbe(wingetFirstRunProbe);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not run the winget first-run install sidecar probe before doctor installation discovery.");
        }

        try
        {
            logger.LogDebug("Discovering Aspire CLI installations for doctor output.");
            var installations = await discovery.DiscoverAllAsync(cancellationToken).ConfigureAwait(false);
            logger.LogDebug("Discovered {InstallationCount} Aspire CLI installation(s) for doctor output.", installations.Count);
            return installations;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not discover Aspire CLI installations for doctor output.");
            return CreateFailedDiscoveryRow();
        }
    }

    public static IReadOnlyList<InstallationInfo> DescribeSelfSafely(IInstallationDiscovery discovery, ILogger logger)
    {
        try
        {
            return [discovery.DescribeSelf()];
        }
        catch (OperationCanceledException)
        {
            // Symmetric with DiscoverAllSafelyAsync: cancellation must propagate
            // so the caller can honor the cancellation token even if DescribeSelf
            // ever becomes cancellable.
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not describe the running Aspire CLI installation for doctor self-probe output.");
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

    public static void OutputTable(IAnsiConsole ansiConsole, IReadOnlyList<InstallationInfo> installs)
    {
        ansiConsole.WriteLine();
        ansiConsole.MarkupLine($"[bold]{DoctorCommandStrings.HeaderInstallations.EscapeMarkup()}[/]");
        ansiConsole.WriteLine(new string('=', DoctorCommandStrings.HeaderInstallations.Length));
        ansiConsole.WriteLine();

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn(DoctorCommandStrings.ColumnPath)
            .AddColumn(DoctorCommandStrings.ColumnVersion)
            .AddColumn(DoctorCommandStrings.ColumnChannel)
            .AddColumn(DoctorCommandStrings.ColumnRoute)
            .AddColumn(DoctorCommandStrings.ColumnPathStatus);

        // The first row is, by contract, the running CLI (enforced by
        // InstallationDiscovery, not by ordering here). Tag installs[0]
        // directly rather than re-resolving Environment.ProcessPath and
        // matching CanonicalPath: that re-derivation can disagree with
        // the discovery layer's notion of self (e.g. when ProcessPath is
        // unresolvable at render time but was resolvable when DescribeSelf
        // ran, or when a peer happens to share a canonical path with the
        // running CLI).
        for (var i = 0; i < installs.Count; i++)
        {
            var install = installs[i];
            var isSelf = i == 0;
            var pathDisplay = string.IsNullOrEmpty(install.Path)
                ? DoctorCommandStrings.ValueUnknown
                : install.Path;
            pathDisplay = pathDisplay.EscapeMarkup();
            if (isSelf)
            {
                pathDisplay = $"{pathDisplay} [grey]{DoctorCommandStrings.ValueCurrentMarker.EscapeMarkup()}[/]";
            }

            table.AddRow(
                pathDisplay,
                ValueOrPlaceholder(install.Version, install.Status),
                ValueOrPlaceholder(install.Channel, install.Status),
                ValueOrPlaceholder(install.Route, install.Status),
                PathStatusDisplay(install.PathStatus));
        }

        ansiConsole.Write(table);
    }

    private static string PathStatusDisplay(string pathStatus)
    {
        return pathStatus switch
        {
            InstallationPathStatus.Active => DoctorCommandStrings.ValuePathActive,
            InstallationPathStatus.Shadowed => DoctorCommandStrings.ValuePathShadowed,
            InstallationPathStatus.NotOnPath => DoctorCommandStrings.ValuePathNotOnPath,
            _ => pathStatus.EscapeMarkup(),
        };
    }

    private static string ValueOrPlaceholder(string? value, string status)
    {
        if (!string.IsNullOrEmpty(value))
        {
            return value.EscapeMarkup();
        }

        // Missing fields mean different things for skipped rows, failed
        // probes, and rows that responded but did not populate this value.
        return status switch
        {
            InstallationInfoStatus.NotProbed => DoctorCommandStrings.ValueNotProbed,
            InstallationInfoStatus.Failed => DoctorCommandStrings.ValueProbeFailed,
            _ => DoctorCommandStrings.ValueUnknown,
        };
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
