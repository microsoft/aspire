// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.Text.Json;
using Aspire.Cli.Acquisition;
using Aspire.Cli.Configuration;
using Aspire.Cli.Interaction;
using Aspire.Cli.Resources;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Utils;
using Spectre.Console;

namespace Aspire.Cli.Commands;

/// <summary>
/// <c>aspire info</c> — describes the running CLI by default, or all
/// discoverable CLI installations with <c>--all</c>. JSON output is always
/// an array so consumers see a stable schema regardless of which mode the
/// user invoked.
/// </summary>
internal sealed class InfoCommand : BaseCommand
{
    internal override HelpGroup HelpGroup => HelpGroup.ToolsAndConfiguration;

    private static readonly Option<bool> s_selfOption = new("--self")
    {
        Description = InfoCommandStrings.SelfOptionDescription,
    };

    // `--all` is kept as an explicit-opt-in alias for back-compat with
    // earlier docs and scripts. Discovery is now the default behavior, so
    // this option is a no-op when present and is hidden from help.
    private static readonly Option<bool> s_allOption = new("--all")
    {
        Description = InfoCommandStrings.AllOptionDescription,
        Hidden = true,
    };

    private static readonly Option<OutputFormat> s_formatOption = new("--format")
    {
        Description = InfoCommandStrings.FormatOptionDescription,
    };

    private readonly IInstallationDiscovery _discovery;
    private readonly WingetFirstRunProbe _wingetFirstRunProbe;
    private readonly IAnsiConsole _ansiConsole;

    public InfoCommand(
        IInstallationDiscovery discovery,
        WingetFirstRunProbe wingetFirstRunProbe,
        IFeatures features,
        ICliUpdateNotifier updateNotifier,
        CliExecutionContext executionContext,
        IInteractionService interactionService,
        IAnsiConsole ansiConsole,
        AspireCliTelemetry telemetry)
        : base("info", InfoCommandStrings.Description, features, updateNotifier, executionContext, interactionService, telemetry)
    {
        _discovery = discovery;
        _wingetFirstRunProbe = wingetFirstRunProbe;
        _ansiConsole = ansiConsole;

        Options.Add(s_selfOption);
        Options.Add(s_allOption);
        Options.Add(s_formatOption);
    }

    protected override async Task<CommandResult> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        var selfOnly = parseResult.GetValue(s_selfOption);
        var format = parseResult.GetValue(s_formatOption);

        // Give a never-run winget install a chance to stamp its sidecar
        // before we read it. The probe writes nothing on non-Windows hosts
        // or when the running binary isn't a winget portable install, so
        // this is a cheap no-op in the common case.
        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(processPath))
        {
            var binaryDir = Path.GetDirectoryName(processPath);
            if (!string.IsNullOrEmpty(binaryDir))
            {
                _wingetFirstRunProbe.Run(binaryDir);
            }
        }

        // Default is full discovery so `aspire info` shows the
        // user-expected complete picture (running CLI plus every other
        // Aspire install on the system). `--self` opts into the cheap,
        // single-row path for scripts / programmatic consumers.
        var installs = selfOnly
            ? (IReadOnlyList<InstallationInfo>)[_discovery.DescribeSelf()]
            : await _discovery.DiscoverAllAsync(cancellationToken);

        if (format == OutputFormat.Json)
        {
            OutputJson(installs);
        }
        else
        {
            OutputTable(installs);
        }

        return CommandResult.Success();
    }

    private void OutputJson(IReadOnlyList<InstallationInfo> installs)
    {
        // Materialize to a concrete array for AOT-safe source-gen
        // (InstallationInfo[] is registered in JsonSourceGenerationContext,
        // IReadOnlyList<InstallationInfo> is not).
        var array = installs.ToArray();
        var json = JsonSerializer.Serialize(array, JsonSourceGenerationContext.RelaxedEscaping.InstallationInfoArray);
        InteractionService.DisplayRawText(json, ConsoleOutput.Standard);
    }

    private void OutputTable(IReadOnlyList<InstallationInfo> installs)
    {
        _ansiConsole.WriteLine();
        _ansiConsole.MarkupLine($"[bold]{InfoCommandStrings.HeaderInstallations.EscapeMarkup()}[/]");
        _ansiConsole.WriteLine(new string('=', InfoCommandStrings.HeaderInstallations.Length));
        _ansiConsole.WriteLine();

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn(InfoCommandStrings.ColumnPath)
            .AddColumn(InfoCommandStrings.ColumnVersion)
            .AddColumn(InfoCommandStrings.ColumnChannel)
            .AddColumn(InfoCommandStrings.ColumnRoute)
            .AddColumn(InfoCommandStrings.ColumnOnPath);

        // The first row is, by contract, the running CLI. Tag it visibly so
        // users running `aspire info --all` can tell themselves apart from
        // peers. The contract is enforced by InstallationDiscovery, not by
        // ordering inside this method.
        var selfCanonical = Environment.ProcessPath is { Length: > 0 } p
            ? TryResolveSymlink(p)
            : null;
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

        foreach (var install in installs)
        {
            var isSelf = !string.IsNullOrEmpty(selfCanonical) &&
                         !string.IsNullOrEmpty(install.CanonicalPath) &&
                         comparer.Equals(install.CanonicalPath, selfCanonical);
            var pathDisplay = string.IsNullOrEmpty(install.Path)
                ? InfoCommandStrings.ValueUnknown
                : install.Path;
            if (isSelf)
            {
                pathDisplay = $"{pathDisplay} [grey]{InfoCommandStrings.ValueCurrentMarker.EscapeMarkup()}[/]";
            }
            else
            {
                pathDisplay = pathDisplay.EscapeMarkup();
            }

            table.AddRow(
                pathDisplay,
                ValueOrPlaceholder(install.Version, install.Status),
                ValueOrPlaceholder(install.Channel, install.Status),
                ValueOrPlaceholder(install.Route, install.Status),
                install.IsOnPath ? InfoCommandStrings.ValueYes : InfoCommandStrings.ValueNo);
        }

        _ansiConsole.Write(table);
    }

    private static string ValueOrPlaceholder(string? value, string status)
    {
        if (!string.IsNullOrEmpty(value))
        {
            return value.EscapeMarkup();
        }

        // Distinguish "we asked but the peer didn't tell us" from "we didn't
        // ask" so users understand why version/channel are missing.
        return status == InstallationInfoStatus.NotProbed
            ? InfoCommandStrings.ValueNotProbed
            : InfoCommandStrings.ValueUnknown;
    }

    private static string? TryResolveSymlink(string path)
    {
        try
        {
            var resolved = File.ResolveLinkTarget(path, returnFinalTarget: true);
            return resolved?.FullName ?? Path.GetFullPath(path);
        }
        catch (IOException)
        {
            return Path.GetFullPath(path);
        }
    }
}
