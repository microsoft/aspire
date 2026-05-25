// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aspire.Cli.Acquisition;
using Aspire.Cli.Configuration;
using Aspire.Cli.Interaction;
using Aspire.Cli.Telemetry;
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
    private readonly WingetFirstRunProbe _wingetFirstRunProbe;
    private readonly ILogger _logger;

    public InstallsCommand(HiveEnumerator hiveEnumerator, IInstallationDiscovery installationDiscovery, WingetFirstRunProbe wingetFirstRunProbe, IInteractionService interactionService, IFeatures features, ICliUpdateNotifier updateNotifier, CliExecutionContext executionContext, AspireCliTelemetry telemetry, ILogger<InstallsCommand> logger)
        : base("installs", "Manage Aspire CLI installs", features, updateNotifier, executionContext, interactionService, telemetry)
    {
        _installationDiscovery = installationDiscovery;
        _wingetFirstRunProbe = wingetFirstRunProbe;
        _logger = logger;
        Options.Add(s_formatOption);
        Options.Add(s_selfOption);
        Subcommands.Add(new ListCommand(hiveEnumerator, installationDiscovery, wingetFirstRunProbe, interactionService, features, updateNotifier, executionContext, telemetry, logger));
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
        private readonly WingetFirstRunProbe _wingetFirstRunProbe;
        private readonly ILogger _logger;

        public ListCommand(HiveEnumerator hiveEnumerator, IInstallationDiscovery installationDiscovery, WingetFirstRunProbe wingetFirstRunProbe, IInteractionService interactionService, IFeatures features, ICliUpdateNotifier updateNotifier, CliExecutionContext executionContext, AspireCliTelemetry telemetry, ILogger logger)
            : base("list", "List Aspire CLI installs and orphan hives", features, updateNotifier, executionContext, interactionService, telemetry)
        {
            _hiveEnumerator = hiveEnumerator;
            _installationDiscovery = installationDiscovery;
            _wingetFirstRunProbe = wingetFirstRunProbe;
            _logger = logger;
            Options.Add(s_formatOption);
        }

        protected override async Task<CommandResult> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
        {
            RunWingetFirstRunProbe(_wingetFirstRunProbe, _logger);
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

    private static void RunWingetFirstRunProbe(WingetFirstRunProbe wingetFirstRunProbe, ILogger logger)
    {
        try
        {
            InstallationInfoOutput.RunWingetFirstRunProbe(wingetFirstRunProbe);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not run the winget first-run install sidecar probe before install listing.");
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
                "Classified hive '{Hive}' as orphan install row id '{Id}' because no discovered install reported channel '{Channel}'.",
                hive.Path,
                id,
                hive.Name);
            rows.Add(new InstallListItem(id, "orphan-hive", hive.Name, null, hive.Path, "no install found", "No discovered install reports this hive's channel.", null));
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

}

internal sealed record InstallListItem(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("channel")] string? Channel,
    [property: JsonPropertyName("path")] string? Path,
    [property: JsonPropertyName("hive")] string? Hive,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("statusReason")] string? StatusReason,
    [property: JsonPropertyName("managedBy")] string? ManagedBy);

internal enum InstallsOutputFormat
{
    List,
    Json
}
