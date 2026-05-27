// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Serialization;
using Aspire.Cli.Acquisition;
using Aspire.Cli.Resources;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Commands;

/// <summary>
/// Shared helpers consumed by <see cref="InfoOptionAction"/>:
/// <list type="bullet">
///   <item><see cref="DescribeSelfSafely"/> — wraps <see cref="IInstallationDiscovery.DescribeSelf"/>
///   with the failure-tolerant shape consumed by the peer-probe contract.</item>
///   <item><see cref="BuildRowsAsync"/> and friends — classify a discovery walk into
///   the <see cref="InstallationInfo"/> rows surfaced by <c>aspire --info</c>.</item>
/// </list>
/// </summary>
/// <remarks>
/// Behavior of the row-builder and the JSON shape it produces is the
/// cross-version peer-probe contract consumed by
/// <see cref="Aspire.Cli.Acquisition.PeerInstallProbe"/> (and by external
/// tooling reading <c>aspire --info --format json</c>); changes here must
/// stay backwards-compatible with the parser in
/// <c>PeerInstallProbe.TryParseRichProbeResult</c>.
/// </remarks>
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
            logger.LogWarning(ex, "Could not describe the running Aspire CLI installation for `aspire --info --self` output.");
            return CreateFailedDiscoveryRow();
        }
    }

    public static async Task<List<InstallationInfo>> BuildRowsSafelyAsync(HiveEnumerator hiveEnumerator, IInstallationDiscovery installationDiscovery, ILogger logger, CancellationToken cancellationToken)
    {
        try
        {
            return await BuildRowsAsync(hiveEnumerator, installationDiscovery, logger, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Match the tolerant posture of the channel-read and self-version paths:
            // a broken discovery walk (filesystem ACL, IO hiccup on
            // ~/.aspire/hives, ...) shouldn't take the diagnostic command down
            // with it. Surface a single failure row so the user sees that
            // discovery ran but didn't produce data, plus the underlying reason.
            logger.LogWarning(ex, "Install discovery walk failed for `aspire --info`.");
            return
            [
                new InstallationInfo
                {
                    Id = "discovery",
                    Kind = "discovery-failed",
                    Path = null,
                    PathStatus = InstallationPathStatus.NotOnPath,
                    Status = InstallationInfoStatus.Failed,
                    StatusReason = ex.Message,
                }
            ];
        }
    }

    public static async Task<List<InstallationInfo>> BuildRowsAsync(HiveEnumerator hiveEnumerator, IInstallationDiscovery installationDiscovery, ILogger logger, CancellationToken cancellationToken)
    {
        var discoveredInstalls = (await installationDiscovery.DiscoverAllAsync(cancellationToken))
            .Where(i => IsDisplayableInstall(i, logger))
            .ToList();
        var installChannels = discoveredInstalls
            .Select(i => i.Channel)
            .Where(c => !string.IsNullOrEmpty(c))
            .ToHashSet(StringComparer.Ordinal);
        var ids = new Dictionary<string, int>(StringComparer.Ordinal);
        var rows = new List<InstallationInfo>();

        foreach (var install in discoveredInstalls)
        {
            var baseId = GetInstallId(install);
            var id = GetUniqueId(baseId, ids, logger);
            var kind = GetInstallKind(install);
            var hive = install.Channel is { Length: > 0 } && hiveEnumerator.HasHive(install.Channel)
                ? hiveEnumerator.GetHivePath(install.Channel)
                : null;
            var managedBy = GetManagedBy(install);
            logger.LogDebug(
                "Classified install path '{Path}' as id '{Id}', kind '{Kind}', channel '{Channel}', status '{Status}', pathStatus '{PathStatus}', hive '{Hive}', managedBy '{ManagedBy}'. Source='{Source}', reason='{Reason}'.",
                install.Path,
                id,
                kind,
                install.Channel ?? "(none)",
                install.Status,
                install.PathStatus,
                hive ?? "(none)",
                managedBy ?? "(none)",
                install.Source ?? "(none)",
                install.StatusReason ?? "(none)");
            // The aggregate row carries the same field set as InstallationInfo;
            // we keep `Status` and `PathStatus` orthogonal so consumers can
            // switch on each axis independently. Display-side collapsing
            // happens in the text renderer (see InfoOptionAction.WriteFullInfoAsync).
            rows.Add(install with
            {
                Id = id,
                Kind = kind,
                Hive = hive,
                ManagedBy = managedBy,
            });
        }

        foreach (var hive in hiveEnumerator.GetHives().Where(h => !installChannels.Contains(h.Name)))
        {
            var id = GetUniqueId(hive.Name, ids, logger);
            logger.LogDebug(
                "Classified hive '{Hive}' as orphan install row id '{Id}' because no discovered install reports channel '{Channel}'.",
                hive.Path,
                id,
                hive.Name);
            // Orphan-hive rows have no installation binary, so `Path` and most
            // per-binary fields are null. The hive directory itself is in `Hive`.
            rows.Add(new InstallationInfo
            {
                Id = id,
                Kind = "orphan-hive",
                Path = null,
                Channel = hive.Name,
                Hive = hive.Path,
                PathStatus = InstallationPathStatus.NotOnPath,
                Status = InstallationInfoStatus.NoInstallFound,
                StatusReason = "No discovered install reports this hive's channel.",
            });
        }

        return rows
            .OrderBy(GetSortRank)
            .ThenBy(row => row.Id, StringComparer.Ordinal)
            .ToList();
    }

    // Sort order: active installs first, then shadowed, then not-on-PATH ok,
    // then failed / notProbed, then orphan-hives last. Status and PathStatus
    // are orthogonal on the wire, so the rank function combines both axes.
    private static int GetSortRank(InstallationInfo row)
    {
        if (row.Status is InstallationInfoStatus.NoInstallFound)
        {
            return 4;
        }

        if (row.Status is not InstallationInfoStatus.Ok)
        {
            // failed / notProbed / unknown
            return 3;
        }

        return row.PathStatus switch
        {
            InstallationPathStatus.Active => 0,
            InstallationPathStatus.Shadowed => 1,
            InstallationPathStatus.NotOnPath => 2,
            _ => 3,
        };
    }

    private static string GetInstallId(InstallationInfo install)
    {
        if (install.Source is "script")
        {
            return "script";
        }

        if (!string.IsNullOrEmpty(install.Channel))
        {
            return install.Channel;
        }

        return install.Source ?? install.Path ?? "unknown";
    }

    private static bool IsDisplayableInstall(InstallationInfo install, ILogger logger)
    {
        if (!string.IsNullOrEmpty(install.Source))
        {
            logger.LogDebug("Including install path '{Path}' because it has source '{Source}'.", install.Path, install.Source);
            return true;
        }

        var fileName = Path.GetFileName(install.CanonicalPath ?? install.Path ?? string.Empty);
        var isAspireBinary = fileName is "aspire" or "aspire.exe";
        if (!isAspireBinary)
        {
            logger.LogDebug("Ignoring discovery row path '{Path}' because it has no install source and the resolved filename '{FileName}' is not an Aspire CLI binary.", install.Path, fileName);
        }

        return isAspireBinary;
    }

    private static string GetUniqueId(string id, Dictionary<string, int> ids, ILogger logger)
    {
        var originalId = id;
        var uniqueId = GetUniqueIdCore(id, ids);
        if (!string.Equals(originalId, uniqueId, StringComparison.Ordinal))
        {
            logger.LogDebug("Disambiguated duplicate install id '{OriginalId}' as '{UniqueId}'.", originalId, uniqueId);
        }

        return uniqueId;
    }

    private static string GetUniqueIdCore(string id, Dictionary<string, int> ids)
    {
        if (!ids.TryGetValue(id, out var count))
        {
            ids[id] = 1;
            return id;
        }

        count++;
        ids[id] = count;
        return $"{id}-{count}";
    }

    private static string GetInstallKind(InstallationInfo install)
        => install.Source switch
        {
            // The sidecar wire string is "brew" but everywhere we surface this to
            // a human (or tool reading our JSON output) we use the friendlier
            // "homebrew" label so the displayed `kind` and `managedBy` agree.
            "brew" => "homebrew",
            null => "unknown",
            _ => install.Source,
        };

    private static string? GetManagedBy(InstallationInfo install)
        => install.Source switch
        {
            "dotnet-tool" => "dotnet-tool",
            "winget" => "winget",
            "brew" => "homebrew",
            _ => null
        };

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

/// <summary>
/// Combined object emitted by <c>aspire --info --format json</c>. The
/// per-install rows are produced by <see cref="InstallationInfoOutput.BuildRowsAsync"/>.
/// </summary>
internal sealed record InfoOutput(
    [property: JsonPropertyName("version")] string? Version,
    [property: JsonPropertyName("channel")] string? Channel,
    [property: JsonPropertyName("installs")] IReadOnlyList<InstallationInfo> Installs);

/// <summary>
/// Output format for <c>aspire --info</c>. <c>List</c> is the default text
/// rendering; <c>Json</c> is the machine-readable shape.
/// </summary>
internal enum InfoOutputFormat
{
    List,
    Json,
}
