// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.Globalization;
using Aspire.Cli.Agents;
using Aspire.Cli.Interaction;
using Aspire.Cli.Resources;

namespace Aspire.Cli.Commands;

/// <summary>
/// Defines the command-line options and prompt metadata for a file-backed agent asset kind.
/// </summary>
internal sealed record FileAssetCommandSpec(
    AgentAssetKind AssetKind,
    Option<string?> LocationOption,
    Option<string?> AssetOption,
    string LocationSelectionPrompt,
    string AssetSelectionPrompt,
    string InstallFailureMessage,
    string InstalledSummary,
    string InstalledAssetsSummary,
    string InstalledLocationsSummary,
    string NoDetectedClientWarning) : AgentAssetCommandSpec(AssetKind, AssetOption)
{
    public override IReadOnlyList<Option> Options => [LocationOption, AssetOption];
}

/// <summary>
/// Defines the command-line option and prompt metadata for an action-backed agent asset kind.
/// </summary>
internal sealed record ActionAssetCommandSpec(
    AgentAssetKind AssetKind,
    Option<string?> AssetOption,
    string AssetSelectionPrompt,
    string TargetDescription,
    string NoDetectedClientWarning,
    string NoCompatibleClientError) : AgentAssetCommandSpec(AssetKind, AssetOption)
{
    public override IReadOnlyList<Option> Options => [AssetOption];
}

internal abstract record AgentAssetCommandSpec(
    AgentAssetKind AssetKind,
    Option<string?> AssetOption)
{
    public abstract IReadOnlyList<Option> Options { get; }
}

internal sealed record FileAssetCommandBindings(
    PromptBinding<string?> Locations,
    PromptBinding<string?> Assets);

internal sealed record ActionAssetCommandBindings(PromptBinding<string?> Assets);

/// <summary>
/// Contains prompt bindings for every registered agent asset command spec.
/// </summary>
internal sealed class AgentAssetCommandBindings
{
    private readonly IReadOnlyDictionary<AgentAssetKind, FileAssetCommandBindings> _fileBindings;
    private readonly IReadOnlyDictionary<AgentAssetKind, ActionAssetCommandBindings> _actionBindings;

    internal AgentAssetCommandBindings(
        IReadOnlyDictionary<AgentAssetKind, FileAssetCommandBindings> fileBindings,
        IReadOnlyDictionary<AgentAssetKind, ActionAssetCommandBindings> actionBindings)
    {
        _fileBindings = fileBindings;
        _actionBindings = actionBindings;
    }

    public FileAssetCommandBindings GetFile(AgentAssetKind assetKind)
        => _fileBindings.TryGetValue(assetKind, out var bindings)
            ? bindings
            : throw new InvalidOperationException($"No file asset command bindings are registered for '{assetKind}'.");

    public ActionAssetCommandBindings GetAction(AgentAssetKind assetKind)
        => _actionBindings.TryGetValue(assetKind, out var bindings)
            ? bindings
            : throw new InvalidOperationException($"No action asset command bindings are registered for '{assetKind}'.");
}

/// <summary>
/// Owns the shared command-line options for agent asset selection.
/// </summary>
internal static class AgentAssetCommandOptions
{
    public static readonly FileAssetCommandSpec Skills = new(
        AgentAssetKind.Skill,
        LocationOption: CreateOption(
            "--skill-locations",
            string.Format(
                CultureInfo.InvariantCulture,
                AgentCommandStrings.InitCommand_SkillLocationsOptionDescription,
                string.Join(",", AgentAssetLocation.GetLocations(AgentAssetKind.Skill).Select(static location => location.Id)),
                ConsoleInteractionService.AllChoice,
                ConsoleInteractionService.NoneChoice)),
        AssetOption: CreateOption(
            "--skills",
            string.Format(
                CultureInfo.InvariantCulture,
                AgentCommandStrings.InitCommand_SkillsOptionDescription,
                string.Join(",", AgentAssetCatalog.GetFileAssets(AgentAssetKind.Skill).Select(static asset => asset.Name)),
                ConsoleInteractionService.AllChoice,
                ConsoleInteractionService.NoneChoice)),
        AgentCommandStrings.InitCommand_SelectSkillLocations,
        AgentCommandStrings.InitCommand_SelectSkills,
        AgentCommandStrings.InitCommand_FailedToInstallSkill,
        AgentCommandStrings.InitCommand_InstalledSkillsSummary,
        AgentCommandStrings.InitCommand_InstalledSkillsSummarySkills,
        AgentCommandStrings.InitCommand_InstalledSkillsSummaryLocations,
        AgentCommandStrings.InitCommand_NoDetectedClientForSkills);

    public static readonly ActionAssetCommandSpec Mcp = new(
        AgentAssetKind.Mcp,
        AssetOption: CreateOption(
            "--mcps",
            string.Format(
                CultureInfo.InvariantCulture,
                AgentCommandStrings.InitCommand_McpsOptionDescription,
                string.Join(",", AgentAssetCatalog.GetActionAssets(AgentAssetKind.Mcp).Select(static asset => asset.Name)),
                ConsoleInteractionService.AllChoice,
                ConsoleInteractionService.NoneChoice)),
        AgentCommandStrings.InitCommand_SelectMcpServers,
        AgentCommandStrings.InitCommand_ConfiguresDetectedAgentEnvironments,
        AgentCommandStrings.InitCommand_NoDetectedClientForMcp,
        AgentCommandStrings.InitCommand_NoCompatibleClientForSelectedMcp);

    public static IReadOnlyList<FileAssetCommandSpec> FileSpecs { get; } = [Skills];

    public static IReadOnlyList<ActionAssetCommandSpec> ActionSpecs { get; } = [Mcp];

    public static IReadOnlyList<AgentAssetCommandSpec> All { get; } =
        [.. FileSpecs, .. ActionSpecs];

    static AgentAssetCommandOptions()
    {
        ValidateSpecs(All);
    }

    public static void AddTo(Command command)
    {
        ArgumentNullException.ThrowIfNull(command);

        foreach (var option in All.SelectMany(static spec => spec.Options))
        {
            if (!command.Options.Contains(option))
            {
                command.Options.Add(option);
            }
        }
    }

    public static AgentAssetCommandBindings Bind(ParseResult parseResult)
    {
        ArgumentNullException.ThrowIfNull(parseResult);

        return new(
            FileSpecs.ToDictionary(
                static spec => spec.AssetKind,
                spec => new FileAssetCommandBindings(
                    PromptBinding.Create(parseResult, spec.LocationOption),
                    PromptBinding.Create(parseResult, spec.AssetOption))),
            ActionSpecs.ToDictionary(
                static spec => spec.AssetKind,
                spec => new ActionAssetCommandBindings(
                    PromptBinding.Create(parseResult, spec.AssetOption))));
    }

    public static AgentAssetCommandBindings CreateDefaultBindings()
    {
        return new(
            FileSpecs.ToDictionary(
                static spec => spec.AssetKind,
                static _ => new FileAssetCommandBindings(
                    PromptBinding.CreateDefault<string?>(null),
                    PromptBinding.CreateDefault<string?>(null))),
            ActionSpecs.ToDictionary(
                static spec => spec.AssetKind,
                static _ => new ActionAssetCommandBindings(
                    PromptBinding.CreateDefault<string?>(null))));
    }

    internal static void ValidateSpecs(IReadOnlyList<AgentAssetCommandSpec> specs)
    {
        ArgumentNullException.ThrowIfNull(specs);

        var kinds = new HashSet<AgentAssetKind>();
        var optionNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var spec in specs)
        {
            if (!kinds.Add(spec.AssetKind))
            {
                throw new InvalidOperationException($"Multiple command specs are registered for agent asset kind '{spec.AssetKind}'.");
            }

            var catalogAssets = AgentAssetCatalog.All.Where(asset => asset.AssetKind == spec.AssetKind);
            if (spec is FileAssetCommandSpec && catalogAssets.Any(static asset => asset is not AgentFileAssetDefinition) ||
                spec is ActionAssetCommandSpec && catalogAssets.Any(static asset => asset is not AgentActionAssetDefinition))
            {
                throw new InvalidOperationException($"Agent asset catalog entries for '{spec.AssetKind}' do not match the registered command spec.");
            }

            foreach (var option in spec.Options)
            {
                foreach (var name in option.Aliases.Append(option.Name).Distinct(StringComparer.Ordinal))
                {
                    if (!optionNames.Add(name))
                    {
                        throw new InvalidOperationException($"Multiple agent asset command options use name or alias '{name}'.");
                    }
                }
            }
        }

        var missingKinds = Enum.GetValues<AgentAssetKind>().Where(kind => !kinds.Contains(kind)).ToList();
        if (missingKinds.Count > 0)
        {
            throw new InvalidOperationException($"No command spec is registered for agent asset kind '{missingKinds[0]}'.");
        }
    }

    private static Option<string?> CreateOption(string name, string description)
    {
        return new(name)
        {
            Description = description,
            Recursive = true,
        };
    }
}
