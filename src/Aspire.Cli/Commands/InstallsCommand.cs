// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aspire.Cli.Acquisition;
using Aspire.Cli.Configuration;
using Aspire.Cli.Interaction;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Uninstall;
using Aspire.Cli.Utils;
using Microsoft.Extensions.Logging;
using Spectre.Console;

namespace Aspire.Cli.Commands;

internal sealed class InstallsCommand : BaseCommand
{
    internal override HelpGroup HelpGroup => HelpGroup.ToolsAndConfiguration;

    private static readonly Option<InstallsOutputFormat> s_formatOption = new("--format")
    {
        Description = "The output format.",
        Hidden = true
    };

    private static readonly Option<bool> s_selfOption = new("--self")
    {
        Hidden = true
    };

    private readonly IInstallationDiscovery _installationDiscovery;
    private readonly ILogger _logger;

    public InstallsCommand(HiveEnumerator hiveEnumerator, CliCleanupService cleanupService, IInstallationDiscovery installationDiscovery, IInteractionService interactionService, IFeatures features, ICliUpdateNotifier updateNotifier, CliExecutionContext executionContext, AspireCliTelemetry telemetry, ILogger<InstallsCommand> logger)
        : base("installs", "Manage Aspire CLI installs", features, updateNotifier, executionContext, interactionService, telemetry)
    {
        _installationDiscovery = installationDiscovery;
        _logger = logger;
        Options.Add(s_formatOption);
        Options.Add(s_selfOption);
        Subcommands.Add(new ListCommand(hiveEnumerator, installationDiscovery, interactionService, features, updateNotifier, executionContext, telemetry, logger));
        Subcommands.Add(new UninstallSubCommand(hiveEnumerator, cleanupService, installationDiscovery, interactionService, features, updateNotifier, executionContext, telemetry, logger));
    }

    protected override Task<CommandResult> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        if (!parseResult.GetValue(s_selfOption))
        {
            return Task.FromResult(CommandResult.DisplayHelp());
        }

        var self = InstallationInfoOutput.DescribeSelfSafely(_installationDiscovery, _logger);
        if (parseResult.GetValue(s_formatOption) == InstallsOutputFormat.Json)
        {
            var json = JsonSerializer.Serialize(self.ToArray(), JsonSourceGenerationContext.RelaxedEscaping.InstallationInfoArray);
            InteractionService.DisplayRawText(json, ConsoleOutput.Standard);
        }
        else
        {
            foreach (var install in self)
            {
                InteractionService.DisplayMarkdown("**self**");
                DisplaySelfField("Status", install.Status);
                DisplaySelfField("Channel", install.Channel);
                DisplaySelfField("Source", install.Source);
                DisplaySelfField("Version", install.Version);
                DisplaySelfField("Path", install.Path);
                DisplaySelfField("On PATH", install.PathStatus);
            }
        }

        return Task.FromResult(CommandResult.Success());
    }

    private void DisplaySelfField(string name, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        InteractionService.DisplayPlainText($"  {name,-8} {value}");
    }

    private sealed class ListCommand : BaseCommand
    {
        private static readonly Option<InstallsOutputFormat> s_formatOption = new("--format")
        {
            Description = "The output format."
        };

        private readonly HiveEnumerator _hiveEnumerator;
        private readonly IInstallationDiscovery _installationDiscovery;
        private readonly ILogger _logger;

        public ListCommand(HiveEnumerator hiveEnumerator, IInstallationDiscovery installationDiscovery, IInteractionService interactionService, IFeatures features, ICliUpdateNotifier updateNotifier, CliExecutionContext executionContext, AspireCliTelemetry telemetry, ILogger logger)
            : base("list", "List Aspire CLI installs and orphan hives", features, updateNotifier, executionContext, interactionService, telemetry)
        {
            _hiveEnumerator = hiveEnumerator;
            _installationDiscovery = installationDiscovery;
            _logger = logger;
            Options.Add(s_formatOption);
        }

        protected override async Task<CommandResult> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
        {
            var rows = await BuildRowsAsync(_hiveEnumerator, _installationDiscovery, _logger, cancellationToken);
            if (parseResult.GetValue(s_formatOption) == InstallsOutputFormat.Json)
            {
                var json = JsonSerializer.Serialize(rows, JsonSourceGenerationContext.RelaxedEscaping.ListInstallListItem);
                InteractionService.DisplayRawText(json, ConsoleOutput.Standard);
                return CommandResult.Success();
            }

            foreach (var row in rows)
            {
                InteractionService.DisplayMarkdown($"**{row.Id}**  {row.Kind}");
                DisplayField("Status", row.Status);
                DisplayField("Channel", row.Channel);
                DisplayField("Path", row.Path);
                DisplayField("Hive", row.Hive);
                if (row.StatusReason is { Length: > 0 })
                {
                    DisplayField("Reason", row.StatusReason);
                }
                DisplayField("Cleanup", row.CleanupHint);
                InteractionService.DisplayEmptyLine();
            }

            return CommandResult.Success();
        }

        private void DisplayField(string name, string? value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return;
            }

            InteractionService.DisplayPlainText($"  {name,-8} {value}");
        }
    }

    private static async Task<List<InstallListItem>> BuildRowsAsync(HiveEnumerator hiveEnumerator, IInstallationDiscovery installationDiscovery, ILogger logger, CancellationToken cancellationToken)
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
            var cleanupHint = GetCleanupHint(install, id);
            var managedBy = GetManagedBy(install);
            logger.LogDebug(
                "Classified install path '{Path}' as id '{Id}', kind '{Kind}', channel '{Channel}', status '{Status}', hive '{Hive}', managedBy '{ManagedBy}', cleanup '{CleanupHint}'. Source='{Source}', pathStatus='{PathStatus}', discoveryStatus='{DiscoveryStatus}', reason='{Reason}'.",
                install.Path,
                id,
                kind,
                install.Channel ?? "(none)",
                status,
                hive ?? "(none)",
                managedBy ?? "(none)",
                cleanupHint,
                install.Source ?? "(none)",
                install.PathStatus,
                install.Status,
                install.StatusReason ?? "(none)");
            rows.Add(new InstallListItem(id, kind, install.Channel, install.Path, hive, status, install.StatusReason, managedBy, cleanupHint));
        }

        foreach (var hive in hiveEnumerator.GetHives().Where(h => !installChannels.Contains(h.Name)))
        {
            var id = GetUniqueId(hive.Name, ids, logger);
            logger.LogDebug(
                "Classified hive '{Hive}' as orphan install row id '{Id}' because no discovered install reported channel '{Channel}'.",
                hive.Path,
                id,
                hive.Name);
            rows.Add(new InstallListItem(id, "orphan-hive", hive.Name, null, hive.Path, "no install found", "No discovered install reports this hive's channel.", null, $"Use: aspire installs uninstall {id}"));
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
            "no install found" => 4,
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

    private static string GetCleanupHint(InstallationInfo install, string id)
        => install.Source switch
        {
            "dotnet-tool" => "Managed by dotnet tool; use: dotnet tool uninstall",
            "winget" => "Managed by WinGet; use: winget uninstall",
            "brew" => "Managed by Homebrew; use: brew uninstall",
            _ => $"Use: aspire installs uninstall {id}"
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
        if (install.Status != InstallationInfoStatus.Ok)
        {
            return install.StatusReason is { Length: > 0 }
                ? $"{install.Status}: {install.StatusReason}"
                : install.Status;
        }

        return install.PathStatus;
    }

    private static async Task<Dictionary<string, InstallListItem>> BuildIdToChannelMapAsync(HiveEnumerator hiveEnumerator, IInstallationDiscovery installationDiscovery, ILogger logger, CancellationToken cancellationToken)
    {
        var rows = await BuildRowsAsync(hiveEnumerator, installationDiscovery, logger, cancellationToken);
        var map = new Dictionary<string, InstallListItem>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            map[row.Id] = row;
        }

        return map;
    }

    private sealed class UninstallSubCommand : BaseCommand
    {
        private static readonly Argument<string> s_idArgument = new("id")
        {
            Description = "The install ID or hive label to uninstall."
        };

        private static readonly Option<bool> s_yesOption = new("--yes", "-y")
        {
            Description = "Confirm uninstall without prompting."
        };

        private static readonly Option<bool> s_dryRunOption = new("--dry-run")
        {
            Description = "Show what would be removed without deleting it."
        };

        private static readonly Option<bool> s_removeSharedInstallOption = new("--remove-shared-install")
        {
            Description = "Also remove the shared script install under ~/.aspire/bin and its bundle layout."
        };

        private readonly HiveEnumerator _hiveEnumerator;
        private readonly CliCleanupService _cleanupService;
        private readonly IInstallationDiscovery _installationDiscovery;
        private readonly ILogger _logger;

        public UninstallSubCommand(HiveEnumerator hiveEnumerator, CliCleanupService cleanupService, IInstallationDiscovery installationDiscovery, IInteractionService interactionService, IFeatures features, ICliUpdateNotifier updateNotifier, CliExecutionContext executionContext, AspireCliTelemetry telemetry, ILogger logger)
            : base("uninstall", "Uninstall an Aspire CLI install or orphan hive", features, updateNotifier, executionContext, interactionService, telemetry)
        {
            _hiveEnumerator = hiveEnumerator;
            _cleanupService = cleanupService;
            _installationDiscovery = installationDiscovery;
            _logger = logger;
            Arguments.Add(s_idArgument);
            Options.Add(s_yesOption);
            Options.Add(s_dryRunOption);
            Options.Add(s_removeSharedInstallOption);
            AddNonInteractiveRequiresYesValidator(this, s_yesOption);
        }

        protected override async Task<CommandResult> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
        {
            var id = parseResult.GetValue(s_idArgument);
            if (string.IsNullOrWhiteSpace(id))
            {
                return CommandResult.Failure(CliExitCodes.InvalidCommand, "An install ID is required.");
            }

            var dryRun = parseResult.GetValue(s_dryRunOption);
            var yes = parseResult.GetValue(s_yesOption);
            var idToRow = await BuildIdToChannelMapAsync(_hiveEnumerator, _installationDiscovery, _logger, cancellationToken);
            if (idToRow.TryGetValue(id, out var row) && row.ManagedBy is not null)
            {
                InteractionService.DisplayError($"Install '{id}' cannot be uninstalled by Aspire. {row.CleanupHint}");
                return CommandResult.Failure(CliExitCodes.InvalidCommand);
            }

            if (!TryResolveChannelFromId(id, _hiveEnumerator, idToRow, _logger, out var channel))
            {
                InteractionService.DisplayError($"No Aspire CLI install or hive named '{id}' was found. Run 'aspire installs list' to see uninstallable IDs.");
                return CommandResult.Failure(CliExitCodes.InvalidCommand);
            }

            if (!dryRun && !yes && !await InteractionService.PromptConfirmAsync($"Uninstall Aspire CLI install '{id}'?", cancellationToken: cancellationToken))
            {
                return CommandResult.Cancelled();
            }

            var result = await _cleanupService.UninstallAsync([channel], parseResult.GetValue(s_removeSharedInstallOption), dryRun, cancellationToken);
            HivesCommand.DisplayOperations(InteractionService, result.Operations);

            return result.HasFailures ? CommandResult.Failure(CliExitCodes.InvalidCommand) : CommandResult.Success();
        }

        private static bool TryResolveChannelFromId(string id, HiveEnumerator hiveEnumerator, IReadOnlyDictionary<string, InstallListItem> idToRow, ILogger logger, out string channel)
        {
            if (idToRow.TryGetValue(id, out var mappedRow) && mappedRow.Channel is { Length: > 0 } mappedChannel)
            {
                logger.LogDebug("Resolved install id '{Id}' to channel '{Channel}' from installs list mapping.", id, mappedChannel);
                channel = mappedChannel;
                return true;
            }

            if (hiveEnumerator.HasHive(id))
            {
                logger.LogDebug("Resolved install id '{Id}' to exact matching hive channel.", id);
                channel = id;
                return true;
            }

            // Require an exact match: disambiguated ids produced by
            // BuildRowsAsync are already in `idToRow` (first branch above).
            // Accepting a typoed-suffix id like `pr-17416-2` and stripping it
            // to `pr-17416` would let a user typo silently delete a different
            // hive than they named, so anything that doesn't match exactly
            // is rejected.
            logger.LogDebug("Could not resolve install id '{Id}' because no install mapping or hive match was found.", id);
            channel = string.Empty;
            return false;
        }
    }
}

internal sealed record InstallListItem(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("channel")] string? Channel,
    [property: JsonPropertyName("path")] string? Path,
    [property: JsonPropertyName("hive")] string? Hive,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("statusReason")] string? StatusReason,
    [property: JsonPropertyName("managedBy")] string? ManagedBy,
    [property: JsonPropertyName("cleanupHint")] string CleanupHint);

internal enum InstallsOutputFormat
{
    List,
    Json
}
