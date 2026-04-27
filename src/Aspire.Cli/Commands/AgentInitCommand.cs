// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Aspire.Cli.Agents;
using Aspire.Cli.Agents.Playwright;
using Aspire.Cli.Configuration;
using Aspire.Cli.Git;
using Aspire.Cli.Interaction;
using Aspire.Cli.NuGet;
using Aspire.Cli.Projects;
using Aspire.Cli.Resources;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Utils;
using Spectre.Console;

namespace Aspire.Cli.Commands;

/// <summary>
/// Command that initializes agent environment configuration for detected agents.
/// This is the new command under 'aspire agent init'.
/// </summary>
internal sealed class AgentInitCommand : BaseCommand, IPackageMetaPrefetchingCommand
{
    private readonly IInteractionService _interactionService;
    private readonly IAgentEnvironmentDetector _agentEnvironmentDetector;
    private readonly PlaywrightCliInstaller _playwrightCliInstaller;
    private readonly IGitRepository _gitRepository;
    private readonly ILanguageDiscovery _languageDiscovery;

    /// <summary>
    /// AgentInitCommand does not need template package metadata prefetching.
    /// </summary>
    public bool PrefetchesTemplatePackageMetadata => false;

    /// <summary>
    /// AgentInitCommand does not need CLI package metadata prefetching.
    /// </summary>
    public bool PrefetchesCliPackageMetadata => false;

    public AgentInitCommand(
        IInteractionService interactionService,
        IFeatures features,
        ICliUpdateNotifier updateNotifier,
        CliExecutionContext executionContext,
        IAgentEnvironmentDetector agentEnvironmentDetector,
        PlaywrightCliInstaller playwrightCliInstaller,
        IGitRepository gitRepository,
        ILanguageDiscovery languageDiscovery,
        AspireCliTelemetry telemetry)
        : base("init", AgentCommandStrings.InitCommand_Description, features, updateNotifier, executionContext, interactionService, telemetry)
    {
        _interactionService = interactionService;
        _agentEnvironmentDetector = agentEnvironmentDetector;
        _playwrightCliInstaller = playwrightCliInstaller;
        _gitRepository = gitRepository;
        _languageDiscovery = languageDiscovery;

        Options.Add(s_workspaceRootOption);
        Options.Add(s_skillLocationsOption);
        Options.Add(s_skillsOption);
    }

    private static readonly Option<string?> s_workspaceRootOption = new("--workspace-root")
    {
        Description = AgentCommandStrings.InitCommand_WorkspaceRootOptionDescription
    };

    private static readonly Option<string?> s_skillLocationsOption = new("--skill-locations")
    {
        Description = string.Format(CultureInfo.InvariantCulture, AgentCommandStrings.InitCommand_SkillLocationsOptionDescription,
            string.Join(",", SkillLocation.All.Select(l => l.Id)),
            ConsoleInteractionService.AllChoice,
            ConsoleInteractionService.NoneChoice)
    };

    private static readonly Option<string?> s_skillsOption = new("--skills")
    {
        Description = string.Format(CultureInfo.InvariantCulture, AgentCommandStrings.InitCommand_SkillsOptionDescription,
            string.Join(",", SkillDefinition.All.Select(s => s.Name)),
            ConsoleInteractionService.AllChoice,
            ConsoleInteractionService.NoneChoice)
    };

    protected override bool UpdateNotificationsEnabled => false;

    /// <summary>
    /// Public entry point for executing the init command.
    /// This allows McpInitCommand to delegate to this implementation.
    /// </summary>
    internal Task<int> ExecuteCommandAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        return ExecuteAsync(parseResult, cancellationToken);
    }

    /// <summary>
    /// Prompts the user to run agent init after a successful command, then chains into agent init if accepted.
    /// Used by commands (e.g. <c>aspire init</c>, <c>aspire new</c>) to offer agent init as a follow-up step.
    /// </summary>
    internal async Task<int> PromptAndChainAsync(
        IInteractionService interactionService,
        int previousResultExitCode,
        DirectoryInfo workspaceRoot,
        PromptBinding<bool> agentInitBinding,
        CancellationToken cancellationToken)
    {
        if (previousResultExitCode != ExitCodeConstants.Success)
        {
            return previousResultExitCode;
        }

        var runAgentInit = await interactionService.PromptConfirmAsync(
            SharedCommandStrings.PromptRunAgentInit,
            binding: agentInitBinding,
            cancellationToken: cancellationToken);

        if (runAgentInit)
        {
            return await ExecuteAgentInitAsync(workspaceRoot, parseResult: null, cancellationToken);
        }

        return ExitCodeConstants.Success;
    }

    protected override async Task<int> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        var workspaceRoot = await PromptForWorkspaceRootAsync(parseResult, cancellationToken);
        return await ExecuteAgentInitAsync(workspaceRoot, parseResult, cancellationToken);
    }

    private async Task<DirectoryInfo> PromptForWorkspaceRootAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        // Try to discover the git repository root to use as the default workspace root
        var gitRoot = await _gitRepository.GetRootAsync(cancellationToken);
        var defaultWorkspaceRoot = gitRoot ?? ExecutionContext.WorkingDirectory;

        // Prompt the user for the workspace root
        var workspaceRootPath = await _interactionService.PromptForFilePathAsync(
            McpCommandStrings.InitCommand_WorkspaceRootPrompt,
            binding: PromptBinding.Create(parseResult, s_workspaceRootOption, defaultWorkspaceRoot.FullName),
            validator: path =>
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    return ValidationResult.Error(McpCommandStrings.InitCommand_WorkspaceRootRequired);
                }

                if (!Directory.Exists(path))
                {
                    return ValidationResult.Error(string.Format(CultureInfo.InvariantCulture, McpCommandStrings.InitCommand_WorkspaceRootNotFound, path));
                }

                return ValidationResult.Success();
            },
            directory: true,
            cancellationToken: cancellationToken);

        return new DirectoryInfo(workspaceRootPath);
    }

    private async Task<int> ExecuteAgentInitAsync(DirectoryInfo workspaceRoot, ParseResult? parseResult, CancellationToken cancellationToken)
    {
        var context = new AgentEnvironmentScanContext
        {
            WorkingDirectory = ExecutionContext.WorkingDirectory,
            RepositoryRoot = workspaceRoot
        };

        var applicators = await _interactionService.ShowStatusAsync(
            McpCommandStrings.InitCommand_DetectingAgentEnvironments,
            async () => await _agentEnvironmentDetector.DetectAsync(context, cancellationToken));

        // Detect the AppHost language to determine which skills to offer.
        // When no language is detected (e.g., standalone `aspire agent init`), language-restricted skills are excluded.
        var detectedLanguage = await _languageDiscovery.DetectLanguageRecursiveAsync(workspaceRoot, cancellationToken);

        // Filter skills based on language applicability
        var availableSkills = SkillDefinition.All
            .Where(s => s.IsApplicableToLanguage(detectedLanguage))
            .ToList();

        // Apply deprecated config migrations silently (these are fixes, not choices)
        var configUpdates = applicators.Where(a => a.PromptGroup == McpInitPromptGroup.ConfigUpdates).ToList();
        var userChoices = applicators.Where(a => a.PromptGroup != McpInitPromptGroup.ConfigUpdates).ToList();

        foreach (var update in configUpdates)
        {
            try
            {
                await update.ApplyAsync(cancellationToken);
                _interactionService.DisplayMessage(KnownEmojis.Wrench, update.Description);
            }
            catch (InvalidOperationException ex)
            {
                _interactionService.DisplayError(ex.Message);
            }
        }

        // --- Phase 1: Skill location selection ---
        var defaultLocationIds = string.Join(",", SkillLocation.All.Where(l => l.IsDefault).Select(l => l.Id));
        var skillLocationsBinding = parseResult is not null
            ? PromptBinding.Create(parseResult, s_skillLocationsOption, defaultLocationIds)
            : PromptBinding.CreateDefault<string?>(defaultLocationIds);

        var selectedLocations = await _interactionService.PromptForSelectionsAsync(
            AgentCommandStrings.InitCommand_SelectSkillLocations,
            SkillLocation.All,
            loc => $"{loc.DisplayName} — {loc.Description}",
            preSelected: SkillLocation.All.Where(l => l.IsDefault),
            optional: true,
            binding: skillLocationsBinding,
            cancellationToken: cancellationToken);

        // --- Phase 2: Skill and MCP server selection (only if locations were selected) ---
        IReadOnlyList<SkillDefinition> selectedSkills = [];
        AgentEnvironmentApplicator? combinedMcpApplicator = null;
        var mcpApplicators = userChoices.Where(a => a.PromptGroup == McpInitPromptGroup.AgentEnvironments).ToList();

        if (selectedLocations.Count > 0)
        {
            // Build prompt items: skills first, then MCP as a separate non-default item
            var skillChoices = new List<object>();
            skillChoices.AddRange(availableSkills);

            if (mcpApplicators.Count > 0)
            {
                combinedMcpApplicator = new AgentEnvironmentApplicator(
                    AgentCommandStrings.InitCommand_ConfigureMcpServer,
                    async ct =>
                    {
                        foreach (var mcp in mcpApplicators)
                        {
                            await mcp.ApplyAsync(ct);
                            _interactionService.DisplayMessage(KnownEmojis.CheckMarkButton, mcp.Description);
                        }
                    },
                    promptGroup: McpInitPromptGroup.AdditionalOptions);
                skillChoices.Add(combinedMcpApplicator);
            }

            var preSelectedItems = new List<object>();
            preSelectedItems.AddRange(availableSkills.Where(s => s.IsDefault));
            // MCP is intentionally NOT pre-selected

            var defaultSkillNames = string.Join(",", availableSkills.Where(s => s.IsDefault).Select(s => s.Name));
            var skillsBinding = parseResult is not null
                ? PromptBinding.Create(parseResult, s_skillsOption, defaultSkillNames)
                : PromptBinding.CreateDefault<string?>(defaultSkillNames);

            var selectedItems = await _interactionService.PromptForSelectionsAsync(
                AgentCommandStrings.InitCommand_SelectSkills,
                skillChoices,
                item => item switch
                {
                    SkillDefinition skill => $"{skill.Name} — {skill.Description}",
                    AgentEnvironmentApplicator app => $"[bold]{app.Description}[/] [dim]{AgentCommandStrings.InitCommand_ConfiguresDetectedAgentEnvironments}[/]",
                    _ => item.ToString()!
                },
                preSelected: preSelectedItems,
                optional: true,
                binding: skillsBinding,
                cancellationToken: cancellationToken);

            selectedSkills = selectedItems.OfType<SkillDefinition>().ToList();

            // Clear MCP applicator if it was not selected by the user.
            if (combinedMcpApplicator is not null && !selectedItems.Contains(combinedMcpApplicator))
            {
                combinedMcpApplicator = null;
            }
        }

        // --- Phase 3: Apply skill files for selected locations × skills ---
        // Each skill file write is fast (small markdown files), so sequential execution
        // is fine — parallelizing would complicate error handling for no meaningful gain.
        var hasErrors = false;
        foreach (var location in selectedLocations)
        {
            context.AddSkillBaseDirectory(location.RelativeSkillDirectory);

            foreach (var skill in selectedSkills)
            {
                // Playwright CLI is installed via PlaywrightCliInstaller, not as a static skill file
                if (skill.SkillContent is null && skill.EmbeddedResourceRoot is null)
                {
                    continue;
                }

                hasErrors |= !await InstallSkillAsync(
                    workspaceRoot,
                    location.RelativeSkillDirectory,
                    skill,
                    isUserLevel: false,
                    cancellationToken);

                if (location.IncludeUserLevel)
                {
                    hasErrors |= !await InstallSkillAsync(
                        ExecutionContext.HomeDirectory,
                        location.RelativeSkillDirectory,
                        skill,
                        isUserLevel: true,
                        cancellationToken);
                }
            }
        }

        // --- Phase 4: Handle Playwright CLI (installs binary + mirrors skill files to registered directories) ---
        var selectedSkillDirs = selectedLocations.Select(l => l.RelativeSkillDirectory).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (selectedSkills.Contains(SkillDefinition.PlaywrightCli) && selectedLocations.Count > 0)
        {
            try
            {
                var (status, message) = await _playwrightCliInstaller.InstallAsync(workspaceRoot.FullName, selectedSkillDirs, cancellationToken);
                switch (status)
                {
                    case PlaywrightInstallStatus.Installed:
                        _interactionService.DisplayMessage(KnownEmojis.CheckMarkButton, AgentCommandStrings.InitCommand_InstalledPlaywrightCli);
                        break;
                    case PlaywrightInstallStatus.InstalledWithWarnings:
                        _interactionService.DisplayMessage(KnownEmojis.Warning, message!);
                        break;
                    case PlaywrightInstallStatus.Failed:
                        _interactionService.DisplayError(message!);
                        hasErrors = true;
                        break;
                    case PlaywrightInstallStatus.Skipped:
                        // npm is not available — not an error, just informational.
                        _interactionService.DisplaySubtleMessage(AgentCommandStrings.InitCommand_PlaywrightCliSkipped);
                        break;
                    default:
                        throw new UnreachableException($"Unexpected PlaywrightInstallStatus: {status}");
                }
            }
            catch (InvalidOperationException ex)
            {
                _interactionService.DisplayError(ex.Message);
                hasErrors = true;
            }
        }

        // --- Phase 5: Apply MCP server configuration if selected ---
        if (combinedMcpApplicator is not null)
        {
            try
            {
                await combinedMcpApplicator.ApplyAsync(cancellationToken);
            }
            // InvalidOperationException is thrown by scanner-generated applicators
            // (e.g., MCP config writers) when the underlying operation fails.
            // JsonException as InnerException indicates a malformed config file
            // (e.g., invalid JSON in .copilot/mcp-config.json or .vscode/mcp.json).
            catch (InvalidOperationException ex)
            {
                _interactionService.DisplayError(ex.Message);
                if (ex.InnerException is JsonException)
                {
                    _interactionService.DisplaySubtleMessage(
                        string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.SkippedMalformedConfigFile, combinedMcpApplicator.Description));
                }
                hasErrors = true;
            }
        }

        if (hasErrors)
        {
            _interactionService.DisplayMessage(KnownEmojis.Warning, AgentCommandStrings.ConfigurationCompletedWithErrors);
            _interactionService.DisplayMessage(KnownEmojis.PageFacingUp, string.Format(CultureInfo.CurrentCulture, InteractionServiceStrings.SeeLogsAt, ExecutionContext.LogFilePath));
        }
        else
        {
            _interactionService.DisplaySuccess(McpCommandStrings.InitCommand_ConfigurationComplete);
        }

        return hasErrors ? ExitCodeConstants.InvalidCommand : ExitCodeConstants.Success;
    }

    /// <summary>
    /// Installs the files for a skill at the specified location, creating or updating them as needed.
    /// </summary>
    /// <returns><c>true</c> if successful, <c>false</c> if an error occurred.</returns>
    private async Task<bool> InstallSkillAsync(
        DirectoryInfo rootDirectory,
        string relativeSkillDirectory,
        SkillDefinition skill,
        bool isUserLevel,
        CancellationToken cancellationToken)
    {
        var relativeSkillPath = Path.Combine(relativeSkillDirectory, skill.Name);
        var fullSkillDirectoryPath = Path.Combine(rootDirectory.FullName, relativeSkillPath);

        try
        {
            var skillFiles = await GetSkillFilesAsync(skill, cancellationToken);
            var anyFileUpdated = false;

            foreach (var skillFile in skillFiles)
            {
                var fullPath = Path.Combine(rootDirectory.FullName, relativeSkillPath, skillFile.RelativePath);
                var directory = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                if (File.Exists(fullPath))
                {
                    var existingContent = await File.ReadAllTextAsync(fullPath, cancellationToken);
                    if (string.Equals(existingContent.ReplaceLineEndings("\n"), skillFile.Content.ReplaceLineEndings("\n"), StringComparison.Ordinal))
                    {
                        continue;
                    }
                }

                await File.WriteAllTextAsync(fullPath, skillFile.Content, cancellationToken);
                anyFileUpdated = true;
            }

            if (!anyFileUpdated)
            {
                return true;
            }

            var displayRelativeSkillPath = relativeSkillPath
                .Replace(Path.DirectorySeparatorChar, '/')
                .Replace(Path.AltDirectorySeparatorChar, '/');
            var displayPath = isUserLevel ? $"~/{displayRelativeSkillPath}" : displayRelativeSkillPath;
            _interactionService.DisplayMessage(KnownEmojis.CheckMarkButton,
                string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.InitCommand_InstalledSkill, skill.Name, displayPath));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _interactionService.DisplayError(
                string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.InitCommand_FailedToInstallSkill, skill.Name, fullSkillDirectoryPath, ex.Message));
            return false;
        }
    }

    private static async Task<IReadOnlyList<SkillAssetFile>> GetSkillFilesAsync(SkillDefinition skill, CancellationToken cancellationToken)
    {
        if (skill.SkillContent is not null)
        {
            return [new SkillAssetFile("SKILL.md", skill.SkillContent)];
        }

        if (skill.EmbeddedResourceRoot is not null)
        {
            return await EmbeddedSkillResourceLoader.LoadTextFilesAsync(skill.EmbeddedResourceRoot, skill.ShouldInstallFile, cancellationToken);
        }

        throw new InvalidOperationException($"Skill '{skill.Name}' does not define installable files.");
    }
}
