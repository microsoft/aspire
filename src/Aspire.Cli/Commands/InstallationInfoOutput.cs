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
///   the <see cref="InstallListItem"/> rows surfaced by <c>aspire --info</c>.</item>
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
    /// <summary>
    /// Wire value for the <c>status</c> field of a hive-only row that has no
    /// matching discovered install. Single source of truth for both the row
    /// constructor in <see cref="BuildRowsAsync"/> and the sort-key switch in
    /// <see cref="GetSortRank"/> so a future rename can't silently split the
    /// two and demote orphan rows out of their last-sorted bucket.
    /// </summary>
    internal const string OrphanHiveStatus = "no install found";

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

    public static async Task<List<InstallListItem>> BuildRowsSafelyAsync(HiveEnumerator hiveEnumerator, IInstallationDiscovery installationDiscovery, ILogger logger, CancellationToken cancellationToken)
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
                new InstallListItem(
                    Id: "discovery",
                    Kind: "discovery-failed",
                    Channel: null,
                    Path: null,
                    Hive: null,
                    Status: InstallationInfoStatus.Failed,
                    StatusReason: ex.Message,
                    ManagedBy: null)
            ];
        }
    }

    public static async Task<List<InstallListItem>> BuildRowsAsync(HiveEnumerator hiveEnumerator, IInstallationDiscovery installationDiscovery, ILogger logger, CancellationToken cancellationToken)
    {
        var discoveredInstalls = (await installationDiscovery.DiscoverAllAsync(cancellationToken))
            .Where(i => IsDisplayableInstall(i, logger))
            .ToList();
        var installChannels = discoveredInstalls
            .Select(i => i.Channel)
            .Where(c => !string.IsNullOrEmpty(c))
            .ToHashSet(StringComparer.Ordinal);
        var ids = new Dictionary<string, int>(StringComparer.Ordinal);
        var rows = new List<InstallListItem>();

        foreach (var install in discoveredInstalls)
        {
            var baseId = GetInstallId(install);
            var id = GetUniqueId(baseId, ids, logger);
            var kind = GetInstallKind(install);
            var status = GetInstallStatus(install);
            var hive = install.Channel is { Length: > 0 } && hiveEnumerator.HasHive(install.Channel)
                ? hiveEnumerator.GetHivePath(install.Channel)
                : null;
            var managedBy = GetManagedBy(install);
            logger.LogDebug(
                "Classified install path '{Path}' as id '{Id}', kind '{Kind}', channel '{Channel}', status '{Status}', hive '{Hive}', managedBy '{ManagedBy}'. Source='{Source}', pathStatus='{PathStatus}', discoveryStatus='{DiscoveryStatus}', reason='{Reason}'.",
                install.Path,
                id,
                kind,
                install.Channel ?? "(none)",
                status,
                hive ?? "(none)",
                managedBy ?? "(none)",
                install.Source ?? "(none)",
                install.PathStatus,
                install.Status,
                install.StatusReason ?? "(none)");
            rows.Add(new InstallListItem(id, kind, install.Channel, install.Path, hive, status, install.StatusReason, managedBy));
        }

        foreach (var hive in hiveEnumerator.GetHives().Where(h => !installChannels.Contains(h.Name)))
        {
            var id = GetUniqueId(hive.Name, ids, logger);
            logger.LogDebug(
                "Classified hive '{Hive}' as orphan install row id '{Id}' because no discovered install reports channel '{Channel}'.",
                hive.Path,
                id,
                hive.Name);
            rows.Add(new InstallListItem(id, "orphan-hive", hive.Name, null, hive.Path, OrphanHiveStatus, "No discovered install reports this hive's channel.", null));
        }

        return rows
            .OrderBy(GetSortRank)
            .ThenBy(row => row.Id, StringComparer.Ordinal)
            .ToList();
    }

    private static int GetSortRank(InstallListItem row)
        => row.Status switch
        {
            InstallationPathStatus.Active => 0,
            InstallationPathStatus.Shadowed => 1,
            InstallationPathStatus.NotOnPath => 2,
            OrphanHiveStatus => 4,
            _ => 3
        };

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

        return install.Source ?? install.Path;
    }

    private static bool IsDisplayableInstall(InstallationInfo install, ILogger logger)
    {
        if (!string.IsNullOrEmpty(install.Source))
        {
            logger.LogDebug("Including install path '{Path}' because it has source '{Source}'.", install.Path, install.Source);
            return true;
        }

        var fileName = Path.GetFileName(install.CanonicalPath ?? install.Path);
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

    private static string GetInstallStatus(InstallationInfo install)
    {
        // Keep `status` enum-shaped so programmatic consumers can switch on it.
        // For non-Ok rows the discovery status ("failed" / "notProbed") is
        // surfaced here; the human-readable reason rides on the separate
        // `statusReason` field (see `docs/specs/cli-output-formats.md`). For Ok
        // rows the path-status axis is what users actually want to act on, so
        // we project that into `status` instead of always emitting "ok".
        if (install.Status != InstallationInfoStatus.Ok)
        {
            return install.Status;
        }

        return install.PathStatus;
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

/// <summary>
/// One row in the <c>aspire --info</c> install table. Surfaced as JSON when
/// <c>--format json</c> is set.
/// </summary>
internal sealed record InstallListItem(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("channel")] string? Channel,
    [property: JsonPropertyName("path")] string? Path,
    [property: JsonPropertyName("hive")] string? Hive,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("statusReason")] string? StatusReason,
    [property: JsonPropertyName("managedBy")] string? ManagedBy);

/// <summary>
/// Combined object emitted by <c>aspire --info --format json</c>. The
/// per-install rows are produced by <see cref="InstallationInfoOutput.BuildRowsAsync"/>.
/// </summary>
internal sealed record InfoOutput(
    [property: JsonPropertyName("version")] string? Version,
    [property: JsonPropertyName("channel")] string? Channel,
    [property: JsonPropertyName("installs")] IReadOnlyList<InstallListItem> Installs);

/// <summary>
/// Output format for <c>aspire --info</c>. <c>List</c> is the default text
/// rendering; <c>Json</c> is the machine-readable shape.
/// </summary>
internal enum InfoOutputFormat
{
    List,
    Json,
}
