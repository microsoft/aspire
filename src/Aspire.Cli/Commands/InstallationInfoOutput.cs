// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text.Json.Serialization;
using Aspire.Cli.Acquisition;
using Aspire.Cli.Interaction;
using Aspire.Cli.Resources;
using Aspire.Cli.Utils;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Aspire.Cli.Commands;

// ---------------------------------------------------------------------------
// Output contract DTOs for aspire --info
// ---------------------------------------------------------------------------

/// <summary>
/// Top-level JSON envelope emitted by <c>aspire --info --format json</c>.
/// </summary>
internal sealed class InfoOutput
{
    /// <summary>The version of the running CLI (e.g. <c>13.0.0</c>).</summary>
    [JsonPropertyName("version")]
    public string? Version { get; init; }

    /// <summary>The identity channel of the running CLI (e.g. <c>stable</c>).</summary>
    [JsonPropertyName("channel")]
    public string? Channel { get; init; }

    /// <summary>One row per discovered installation, orphan hive, or aggregate failure.</summary>
    [JsonPropertyName("installs")]
    public required InfoInstallation[] Installs { get; init; }
}

/// <summary>
/// One row in the <c>installs</c> array of <see cref="InfoOutput"/>.
/// </summary>
/// <remarks>
/// <para>
/// <c>kind</c> distinguishes three cases:
/// <list type="bullet">
///   <item><see cref="InfoInstallationKind.Installation"/> — a discovered CLI binary.</item>
///   <item><see cref="InfoInstallationKind.OrphanHive"/> — a hive directory with no correlated installation.</item>
///   <item><see cref="InfoInstallationKind.DiscoveryFailed"/> — aggregate discovery failure (timeout or unexpected exception).</item>
/// </list>
/// </para>
/// <para>
/// Nullable fields are omitted from JSON when <see langword="null"/>
/// (via <c>DefaultIgnoreCondition = WhenWritingNull</c> on <see cref="JsonSourceGenerationContext"/>).
/// The wire name for the install route is <c>source</c>, not the internal
/// <see cref="InstallationInfo.Route"/> field name <c>route</c>.
/// </para>
/// </remarks>
internal sealed class InfoInstallation
{
    /// <summary>Row discriminator. See <see cref="InfoInstallationKind"/>.</summary>
    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    /// <summary>Absolute path of the CLI binary as discovered.</summary>
    [JsonPropertyName("path")]
    public string? Path { get; init; }

    /// <summary>Symlink-resolved absolute path, used for identity / deduplication.</summary>
    [JsonPropertyName("canonicalPath")]
    public string? CanonicalPath { get; init; }

    /// <summary>CLI version string (e.g. <c>13.0.0-preview.1.25366.3</c>).</summary>
    [JsonPropertyName("version")]
    public string? Version { get; init; }

    /// <summary>Identity channel baked into the CLI binary (e.g. <c>stable</c>).</summary>
    [JsonPropertyName("channel")]
    public string? Channel { get; init; }

    /// <summary>
    /// Install route as recorded by the install sidecar. Wire name is <c>source</c>;
    /// maps from internal <see cref="InstallationInfo.Route"/>.
    /// </summary>
    [JsonPropertyName("source")]
    public string? Source { get; init; }

    /// <summary>
    /// Path to the hive directory correlated with this installation's channel,
    /// or the orphan hive path for <see cref="InfoInstallationKind.OrphanHive"/> rows.
    /// </summary>
    [JsonPropertyName("hive")]
    public string? Hive { get; init; }

    /// <summary>Relationship between this binary and <c>$PATH</c>. See <see cref="InstallationPathStatus"/>.</summary>
    [JsonPropertyName("pathStatus")]
    public required string PathStatus { get; init; }

    /// <summary>Lifecycle status for the row. Valid values are the constants on <see cref="InstallationInfoStatus"/>
    /// (<c>ok</c>, <c>notProbed</c>, <c>failed</c>) and <see cref="InstallationInfoStatus.NoInstallFound"/>
    /// (<c>noInstallFound</c>) for <see cref="InfoInstallationKind.OrphanHive"/> rows.</summary>
    [JsonPropertyName("status")]
    public required string Status { get; init; }

    /// <summary>Human-readable reason for a non-<c>ok</c> status; omitted when absent.</summary>
    [JsonPropertyName("statusReason")]
    public string? StatusReason { get; init; }

    /// <summary>Whether this row describes the running Aspire CLI process.</summary>
    [JsonPropertyName("isCurrent")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsCurrent { get; init; }
}

/// <summary>Wire constants for <see cref="InfoInstallation.Kind"/>.</summary>
internal static class InfoInstallationKind
{
    /// <summary>A discovered CLI binary (healthy, degraded, or failed at the peer level).</summary>
    public const string Installation = "installation";

    /// <summary>A hive directory with no matching installation channel.</summary>
    public const string OrphanHive = "orphan-hive";

    /// <summary>Aggregate discovery failure — timeout or unexpected exception during <c>DiscoverAll</c>.</summary>
    public const string DiscoveryFailed = "discovery-failed";
}

// ---------------------------------------------------------------------------
// Discovery result: separates aggregate failure from individual peer failures
// ---------------------------------------------------------------------------

/// <summary>
/// Outcome of a bounded <c>DiscoverAll</c> call. Separates aggregate failure
/// (timeout, unexpected exception) from individual peer-level failures, which
/// are already encoded as <see cref="InstallationInfoStatus.Failed"/> rows
/// inside <see cref="Installations"/>.
/// </summary>
/// <param name="Installations">
/// All installations that were probed (including peer-level failures with
/// <see cref="InstallationInfoStatus.Failed"/>). Empty on aggregate failure.
/// </param>
/// <param name="AggregateFailureReason">
/// Non-<see langword="null"/> when installation or hive discovery timed out
/// or threw an unexpected exception. Maps to a
/// <see cref="InfoInstallationKind.DiscoveryFailed"/> row in the info output
/// without discarding any rows completed before the failure.
/// </param>
internal sealed record InstallationDiscoveryResult(
    IReadOnlyList<InstallationInfo> Installations,
    string? AggregateFailureReason)
{
    public IReadOnlyList<HiveInfo> Hives { get; init; } = [];
}

// ---------------------------------------------------------------------------
// Main static class
// ---------------------------------------------------------------------------

internal static class InstallationInfoOutput
{
    internal static readonly TimeSpan s_defaultDiscoveryTimeout = TimeSpan.FromSeconds(30);

    // -------------------------------------------------------------------------
    // Info-output entry points
    // -------------------------------------------------------------------------

    public static Task<InstallationDiscoveryResult> DiscoverAllToResultSafelyAsync(
        IInstallationDiscovery discovery,
        WingetFirstRunProbe wingetFirstRunProbe,
        ILogger logger,
        CancellationToken cancellationToken)
        => DiscoverAllToResultSafelyAsync(discovery, wingetFirstRunProbe, logger, s_defaultDiscoveryTimeout, cancellationToken);

    public static Task<InstallationDiscoveryResult> DiscoverAllToResultSafelyAsync(
        IInstallationDiscovery discovery,
        IHiveEnumerator hiveEnumerator,
        WingetFirstRunProbe wingetFirstRunProbe,
        ILogger logger,
        CancellationToken cancellationToken)
        => DiscoverAllToResultSafelyAsync(discovery, hiveEnumerator, wingetFirstRunProbe, logger, s_defaultDiscoveryTimeout, cancellationToken);

    internal static async Task<InstallationDiscoveryResult> DiscoverAllToResultSafelyAsync(
        IInstallationDiscovery discovery,
        WingetFirstRunProbe wingetFirstRunProbe,
        ILogger logger,
        TimeSpan timeout,
        CancellationToken cancellationToken)
        => await DiscoverAllToResultWithTimeoutAsync(
            discovery,
            hiveEnumerator: null,
            wingetFirstRunProbe,
            logger,
            timeout,
            cancellationToken).ConfigureAwait(false);

    internal static async Task<InstallationDiscoveryResult> DiscoverAllToResultSafelyAsync(
        IInstallationDiscovery discovery,
        IHiveEnumerator hiveEnumerator,
        WingetFirstRunProbe wingetFirstRunProbe,
        ILogger logger,
        TimeSpan timeout,
        CancellationToken cancellationToken)
        => await DiscoverAllToResultWithTimeoutAsync(
            discovery,
            hiveEnumerator,
            wingetFirstRunProbe,
            logger,
            timeout,
            cancellationToken).ConfigureAwait(false);

    private static async Task<InstallationDiscoveryResult> DiscoverAllToResultWithTimeoutAsync(
        IInstallationDiscovery discovery,
        IHiveEnumerator? hiveEnumerator,
        WingetFirstRunProbe wingetFirstRunProbe,
        ILogger logger,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        // DiscoverInstallationsCoreToResultAsync catches and logs non-cancellation exceptions, so
        // a task that continues after WaitAsync times out cannot later produce an
        // unobserved fault.
        var discoveryTask = Task.Run(
            () => DiscoverInstallationsCoreToResultAsync(discovery, wingetFirstRunProbe, logger, timeoutCts.Token),
            CancellationToken.None);

        InstallationDiscoveryResult discoveryResult;
        try
        {
            discoveryResult = await discoveryTask.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            var reason = string.Format(
                CultureInfo.CurrentCulture,
                InfoOptionStrings.InstallationDiscoveryTimedOutReasonFormat,
                timeout.TotalSeconds);
            logger.LogWarning("Aspire CLI installation discovery timed out after {TimeoutSeconds} seconds.", timeout.TotalSeconds);
            return new InstallationDiscoveryResult([], AggregateFailureReason: reason);
        }

        if (discoveryResult.AggregateFailureReason is not null || hiveEnumerator is null)
        {
            return discoveryResult;
        }

        // Hive enumeration is secondary diagnostic data. Run it separately so
        // a slow or broken hive source cannot erase installation rows that the
        // first phase already completed.
        var hiveTask = Task.Run(
            () => EnumerateHivesToResult(hiveEnumerator, logger, timeoutCts.Token),
            CancellationToken.None);

        try
        {
            var (hives, failureReason) = await hiveTask.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
            return discoveryResult with
            {
                AggregateFailureReason = failureReason,
                Hives = hives,
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            var reason = string.Format(
                CultureInfo.CurrentCulture,
                InfoOptionStrings.InstallationDiscoveryTimedOutReasonFormat,
                timeout.TotalSeconds);
            logger.LogWarning("Aspire CLI hive discovery timed out after {TimeoutSeconds} seconds.", timeout.TotalSeconds);
            return discoveryResult with { AggregateFailureReason = reason };
        }
    }

    private static async Task<InstallationDiscoveryResult> DiscoverInstallationsCoreToResultAsync(
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
            logger.LogWarning(ex, "Could not run the winget first-run install sidecar probe before installation discovery.");
        }

        try
        {
            logger.LogDebug("Discovering Aspire CLI installations for info output.");
            var installations = await discovery.DiscoverAllAsync(cancellationToken).ConfigureAwait(false);
            logger.LogDebug("Discovered {InstallationCount} Aspire CLI installation(s) for info output.", installations.Count);
            return new InstallationDiscoveryResult(installations, AggregateFailureReason: null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not discover Aspire CLI installations for info output.");
            return new InstallationDiscoveryResult([], AggregateFailureReason: InfoOptionStrings.InstallationDiscoveryFailedReason);
        }
    }

    private static (IReadOnlyList<HiveInfo> Hives, string? FailureReason) EnumerateHivesToResult(
        IHiveEnumerator hiveEnumerator,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        try
        {
            var hives = hiveEnumerator.EnumerateHives(cancellationToken).ToList();
            logger.LogDebug("Discovered {HiveCount} Aspire CLI hive(s) for info output.", hives.Count);
            return (hives, FailureReason: null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not discover Aspire CLI hives for info output.");
            return ([], InfoOptionStrings.InstallationDiscoveryFailedReason);
        }
    }

    /// <summary>
    /// Maps one <see cref="InstallationInfo"/> to an <see cref="InfoInstallation"/> row,
    /// correlating a hive directory when the installation's channel is a valid identity
    /// channel and appears in <paramref name="hivesByChannel"/>.
    /// </summary>
    /// <param name="install">The source installation record.</param>
    /// <param name="hivesByChannel">
    /// Pre-built dictionary of valid-channel-name → hive-path (OrdinalIgnoreCase).
    /// Build this once per <see cref="BuildInfoRows"/> call for efficiency.
    /// </param>
    /// <param name="isCurrent">Whether the row describes the running Aspire CLI process.</param>
    internal static InfoInstallation MapToInfoInstallation(
        InstallationInfo install,
        IReadOnlyDictionary<string, string> hivesByChannel,
        bool isCurrent = false)
    {
        // Correlate a hive only when the channel is a well-known identity string.
        // An invalid or missing channel cannot be a reliable hive key.
        string? hive = null;
        if (install.Channel is { Length: > 0 } channel &&
            IdentityChannelReader.IsValidChannel(channel) &&
            hivesByChannel.TryGetValue(channel, out var hivePath))
        {
            hive = hivePath;
        }

        return new InfoInstallation
        {
            Kind = InfoInstallationKind.Installation,
            Path = install.Path,
            CanonicalPath = install.CanonicalPath,
            Version = install.Version,
            Channel = install.Channel,
            Source = install.Route,   // internal Route → wire source
            Hive = hive,
            PathStatus = install.PathStatus,
            Status = install.Status,
            StatusReason = install.StatusReason,
            IsCurrent = isCurrent,
        };
    }

    /// <summary>
    /// Builds the complete <c>installs</c> array for an <see cref="InfoOutput"/> from
    /// a bounded discovery result and the hive enumeration.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    ///   <item>Each installation becomes a <see cref="InfoInstallationKind.Installation"/> row.</item>
    ///   <item>Hives not correlated with any installation become <see cref="InfoInstallationKind.OrphanHive"/> rows.</item>
    ///   <item>An aggregate failure is appended as a single <see cref="InfoInstallationKind.DiscoveryFailed"/> row.</item>
    /// </list>
    /// </remarks>
    internal static InfoInstallation[] BuildInfoRows(
        InstallationDiscoveryResult discoveryResult,
        IEnumerable<HiveInfo> hives)
    {
        if (discoveryResult is { Installations.Count: 0, AggregateFailureReason: { } failureReason })
        {
            return [CreateInfoDiscoveryFailedRow(failureReason)];
        }

        var hiveList = hives.ToList();

        // Build a lookup of channel → hive path, restricted to valid channel names so that
        // stale or manually-created directories with arbitrary names don't trigger false matches.
        var hivesByChannel = BuildHivesByChannel(hiveList);

        // Track which hive channels were successfully correlated with an installation so
        // the remainder can be emitted as orphan-hive rows.
        var matchedHiveChannels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var rows = new List<InfoInstallation>(discoveryResult.Installations.Count + hiveList.Count + 1);

        for (var index = 0; index < discoveryResult.Installations.Count; index++)
        {
            var install = discoveryResult.Installations[index];
            rows.Add(MapToInfoInstallation(install, hivesByChannel, isCurrent: index == 0));

            if (install.Channel is { Length: > 0 } ch &&
                IdentityChannelReader.IsValidChannel(ch) &&
                hivesByChannel.ContainsKey(ch))
            {
                matchedHiveChannels.Add(ch);
            }
        }

        // Remaining hives (either invalid-channel names or valid channels that no
        // installation claimed) surface as orphan-hive rows so the user can see
        // that the directory exists without a corresponding installation.
        foreach (var hive in hiveList)
        {
            if (!matchedHiveChannels.Contains(hive.Name))
            {
                rows.Add(new InfoInstallation
                {
                    Kind = InfoInstallationKind.OrphanHive,
                    Hive = hive.Path,
                    // No binary exists for an orphan hive, so NotOnPath is the
                    // most accurate value: the hive directory is on disk, but
                    // the binary it was supposed to contain was never installed
                    // or has since been removed.
                    PathStatus = InstallationPathStatus.NotOnPath,
                    Status = InstallationInfoStatus.NoInstallFound,
                    // Orphan rows intentionally carry no synthetic channel field —
                    // the hive name is surfaced via Hive, not Channel.
                });
            }
        }

        if (discoveryResult.AggregateFailureReason is { } aggregateFailureReason)
        {
            rows.Add(CreateInfoDiscoveryFailedRow(aggregateFailureReason));
        }

        return [.. rows];
    }

    /// <summary>
    /// Describes the running CLI as an <see cref="InfoInstallation"/> row. On success,
    /// maps through the same DTO path as <see cref="MapToInfoInstallation"/>. On a
    /// non-cancellation exception, returns a single
    /// <see cref="InfoInstallationKind.DiscoveryFailed"/> row. Cancellation propagates.
    /// </summary>
    internal static InfoInstallation DescribeSelfAsInfoInstallation(
        IInstallationDiscovery discovery,
        ILogger logger)
    {
        try
        {
            return MapToInfoInstallation(
                discovery.DescribeSelf(),
                hivesByChannel: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                isCurrent: true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not describe the running Aspire CLI installation for info self-probe output.");
            return CreateInfoDiscoveryFailedRow(InfoOptionStrings.InstallationDiscoveryFailedReason);
        }
    }

    internal static IReadOnlyList<InstallationInfo> DescribeSelfForLegacyDoctor(
        IInstallationDiscovery discovery,
        ILogger logger)
    {
        try
        {
            return [discovery.DescribeSelf()];
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not describe the running Aspire CLI installation for the legacy doctor self-probe.");
            return
            [
                new InstallationInfo
                {
                    Path = string.Empty,
                    PathStatus = InstallationPathStatus.NotOnPath,
                    Status = InstallationInfoStatus.Failed,
                    StatusReason = InfoOptionStrings.InstallationDiscoveryFailedReason,
                },
            ];
        }
    }

    /// <summary>
    /// Builds a lookup of valid-identity-channel-name → hive path (OrdinalIgnoreCase),
    /// used by <see cref="BuildInfoRows"/> to correlate full-discovery rows.
    /// </summary>
    internal static IReadOnlyDictionary<string, string> BuildHivesByChannel(IEnumerable<HiveInfo> hives)
    {
        return hives
            .Where(h => IdentityChannelReader.IsValidChannel(h.Name))
            .ToDictionary(h => h.Name, h => h.Path, StringComparer.OrdinalIgnoreCase);
    }

    // -------------------------------------------------------------------------
    // Human rendering (aspire --info)
    // -------------------------------------------------------------------------

    // Fixed-width label column: computed from every label the renderer can emit
    // (not just the ones present in a given row) so alignment is guaranteed
    // document-wide, regardless of which fields happen to populate a particular
    // installation.
    private static readonly string[] s_infoLabels =
    [
        InfoOptionStrings.SourceLabel,
        InfoOptionStrings.PathLabel,
        InfoOptionStrings.CanonicalPathLabel,
        InfoOptionStrings.VersionLabel,
        InfoOptionStrings.ChannelLabel,
        InfoOptionStrings.HiveLabel,
        InfoOptionStrings.PathStatusLabel,
        InfoOptionStrings.StatusLabel,
        InfoOptionStrings.ReasonLabel,
    ];

    /// <summary>
    /// Renders the <c>aspire --info</c> human-readable output — the running CLI's
    /// version/channel followed by one subsection per row in <paramref name="rows"/> —
    /// through <see cref="IInteractionService.DisplayRenderable"/>.
    /// </summary>
    internal static void DisplayHumanReadable(
        IInteractionService interactionService,
        CliExecutionContext executionContext,
        IReadOnlyList<InfoInstallation> rows)
    {
        interactionService.DisplayRenderable(BuildHumanRenderable(interactionService.SupportsLinks, executionContext, rows));
    }

    /// <summary>
    /// Builds the unbordered <see cref="Rows"/> renderable for <c>aspire --info</c>'s
    /// human output. A top-level <see cref="Rows"/> holds bold <see cref="Markup"/>
    /// section headings interleaved with per-section field <see cref="Grid"/>s (a
    /// 2-character indent column, a fixed-width label column, and a wrapping value
    /// column) — headings cannot share a grid with field rows because a fixed-width
    /// first column would truncate/wrap them.
    /// </summary>
    internal static IRenderable BuildHumanRenderable(
        bool supportsLinks,
        CliExecutionContext executionContext,
        IReadOnlyList<InfoInstallation> rows)
    {
        var labelWidth = s_infoLabels.Max(label => label.Length);

        var renderables = new List<IRenderable>
        {
            new Markup($"[bold]{InfoOptionStrings.InfoHeading.EscapeMarkup()}[/]"),
        };

        var identityGrid = CreateInfoFieldGrid(labelWidth);
        AddInfoFieldIfPresent(identityGrid, InfoOptionStrings.VersionLabel, executionContext.IdentityVersion);
        AddInfoFieldIfPresent(identityGrid, InfoOptionStrings.ChannelLabel, executionContext.IdentityChannel);
        renderables.Add(identityGrid);

        renderables.Add(Text.Empty);
        renderables.Add(new Markup($"[bold]{InfoOptionStrings.InstallationsHeading.EscapeMarkup()}[/]"));

        var installationIndex = 0;
        foreach (var row in rows)
        {
            renderables.Add(Text.Empty);

            var heading = row.Kind switch
            {
                InfoInstallationKind.OrphanHive => InfoOptionStrings.OrphanHiveHeading,
                InfoInstallationKind.DiscoveryFailed => InfoOptionStrings.DiscoveryFailureHeading,
                _ when row.IsCurrent => string.Format(CultureInfo.CurrentCulture, InfoOptionStrings.CurrentInstallationHeadingFormat, ++installationIndex),
                _ => string.Format(CultureInfo.CurrentCulture, InfoOptionStrings.InstallationHeadingFormat, ++installationIndex),
            };
            renderables.Add(new Markup($"[bold]{heading.EscapeMarkup()}[/]"));

            var grid = CreateInfoFieldGrid(labelWidth);
            AddInfoFieldIfPresent(grid, InfoOptionStrings.SourceLabel, row.Source);

            var hasPath = row.Path is { Length: > 0 };
            if (hasPath)
            {
                AddInfoField(grid, InfoOptionStrings.PathLabel, MarkupHelpers.SafeFileLink(supportsLinks, row.Path!));
            }

            // Only show CanonicalPath when it meaningfully differs from Path — otherwise
            // it's pure duplication of the row directly above it.
            if (row.CanonicalPath is { Length: > 0 } canonicalPath &&
                (!hasPath || !string.Equals(row.Path, canonicalPath, StringComparison.Ordinal)))
            {
                AddInfoField(grid, InfoOptionStrings.CanonicalPathLabel, MarkupHelpers.SafeFileLink(supportsLinks, canonicalPath));
            }

            AddInfoFieldIfPresent(grid, InfoOptionStrings.VersionLabel, row.Version);
            AddInfoFieldIfPresent(grid, InfoOptionStrings.ChannelLabel, row.Channel);

            if (row.Hive is { Length: > 0 } hive)
            {
                AddInfoField(grid, InfoOptionStrings.HiveLabel, MarkupHelpers.SafeFileLink(supportsLinks, hive));
            }

            AddInfoField(grid, InfoOptionStrings.PathStatusLabel, MapPathStatusDisplay(row.PathStatus));
            AddInfoField(grid, InfoOptionStrings.StatusLabel, MapStatusDisplay(row.Status));
            AddInfoFieldIfPresent(grid, InfoOptionStrings.ReasonLabel, row.StatusReason);

            renderables.Add(grid);
        }

        return new Rows(renderables);
    }

    private static Grid CreateInfoFieldGrid(int labelWidth)
    {
        var grid = new Grid();
        grid.AddColumn(new GridColumn { Width = 2, NoWrap = true, Padding = new Padding(0) });
        grid.AddColumn(new GridColumn { Width = labelWidth, NoWrap = true, Padding = new Padding(0, 0, 1, 0) });
        grid.AddColumn(new GridColumn { Padding = new Padding(0) });
        return grid;
    }

    // Values are already-escaped markup fragments (via EscapeMarkup() or
    // MarkupHelpers.SafeFileLink, both of which always escape peer/environment-derived
    // text) by the time they reach this helper.
    private static void AddInfoField(Grid grid, string label, string value)
    {
        grid.AddRow(
            new Markup(string.Empty),
            new Markup(label.EscapeMarkup()),
            new Markup(value));
    }

    private static void AddInfoFieldIfPresent(Grid grid, string label, string? rawValue)
    {
        if (rawValue is { Length: > 0 })
        {
            AddInfoField(grid, label, rawValue.EscapeMarkup());
        }
    }

    /// <summary>
    /// Maps an <see cref="InstallationInfoStatus"/> wire value to escaped, localized
    /// human text. Unknown values fall back to escaped raw text rather than throwing,
    /// since the value may originate from a peer CLI of a different version.
    /// </summary>
    private static string MapStatusDisplay(string status)
    {
        return status switch
        {
            InstallationInfoStatus.Ok => InfoOptionStrings.StatusOk.EscapeMarkup(),
            InstallationInfoStatus.NotProbed => InfoOptionStrings.StatusNotProbed.EscapeMarkup(),
            InstallationInfoStatus.Failed => InfoOptionStrings.StatusFailed.EscapeMarkup(),
            InstallationInfoStatus.NoInstallFound => InfoOptionStrings.StatusNoInstallFound.EscapeMarkup(),
            _ => status.EscapeMarkup(),
        };
    }

    /// <summary>
    /// Maps an <see cref="InstallationPathStatus"/> wire value to escaped, localized
    /// human text. Unknown values fall back to escaped raw text rather than throwing,
    /// since the value may originate from a peer CLI of a different version.
    /// </summary>
    private static string MapPathStatusDisplay(string pathStatus)
    {
        return pathStatus switch
        {
            InstallationPathStatus.Active => InfoOptionStrings.PathStatusActive.EscapeMarkup(),
            InstallationPathStatus.Shadowed => InfoOptionStrings.PathStatusShadowed.EscapeMarkup(),
            InstallationPathStatus.NotOnPath => InfoOptionStrings.PathStatusNotOnPath.EscapeMarkup(),
            _ => pathStatus.EscapeMarkup(),
        };
    }

    // -------------------------------------------------------------------------
    // Winget probe
    // -------------------------------------------------------------------------

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

    // -------------------------------------------------------------------------
    // Private factory helpers
    // -------------------------------------------------------------------------

    private static InfoInstallation CreateInfoDiscoveryFailedRow(string reason)
    {
        return new InfoInstallation
        {
            Kind = InfoInstallationKind.DiscoveryFailed,
            // Discovery-failed rows have no binary to place on PATH, so
            // NotOnPath is the accurate value even though the row represents
            // a global failure rather than a per-binary status.
            PathStatus = InstallationPathStatus.NotOnPath,
            Status = InstallationInfoStatus.Failed,
            StatusReason = reason,
        };
    }
}
