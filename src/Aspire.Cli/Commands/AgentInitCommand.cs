// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Aspire.Cli.Agents;
using Aspire.Cli.Agents.AspireSkills;
using Aspire.Cli.Agents.Playwright;
using Aspire.Cli.Git;
using Aspire.Cli.Interaction;
using Aspire.Cli.NuGet;
using Aspire.Cli.Projects;
using Aspire.Cli.Resources;
using Spectre.Console;

namespace Aspire.Cli.Commands;

/// <summary>
/// Command that initializes agent environment configuration for detected agents.
/// This is the new command under 'aspire agent init'.
/// </summary>
internal sealed class AgentInitCommand : BaseCommand, IPackageMetaPrefetchingCommand
{
    private readonly IAgentEnvironmentDetector _agentEnvironmentDetector;
    private readonly IAspireSkillsInstaller _aspireSkillsInstaller;
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
        IAgentEnvironmentDetector agentEnvironmentDetector,
        IAspireSkillsInstaller aspireSkillsInstaller,
        PlaywrightCliInstaller playwrightCliInstaller,
        IGitRepository gitRepository,
        ILanguageDiscovery languageDiscovery,
        CommonCommandServices services)
        : base("init", AgentCommandStrings.InitCommand_Description, services)
    {
        _agentEnvironmentDetector = agentEnvironmentDetector;
        _aspireSkillsInstaller = aspireSkillsInstaller;
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

    internal static readonly Option<string?> s_skillLocationsOption = new("--skill-locations")
    {
        Description = string.Format(CultureInfo.InvariantCulture, AgentCommandStrings.InitCommand_SkillLocationsOptionDescription,
            string.Join(",", SkillLocation.All.Select(l => l.Id)),
            ConsoleInteractionService.AllChoice,
            ConsoleInteractionService.NoneChoice),
        Recursive = true
    };

    internal static readonly Option<string?> s_skillsOption = new("--skills")
    {
        Description = string.Format(CultureInfo.InvariantCulture, AgentCommandStrings.InitCommand_SkillsOptionDescription,
            string.Join(",", SkillDefinition.CliDefined.Select(s => s.Name)),
            ConsoleInteractionService.AllChoice,
            ConsoleInteractionService.NoneChoice),
        Recursive = true
    };

    /// <summary>
    /// Public entry point for executing the init command.
    /// This allows McpInitCommand to delegate to this implementation.
    /// </summary>
    internal Task<CommandResult> ExecuteCommandAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        return ExecuteAsync(parseResult, cancellationToken);
    }

    /// <summary>
    /// Prompts the user to run agent init after a successful command, then chains into agent init if accepted.
    /// Used by commands (e.g. <c>aspire init</c>, <c>aspire new</c>) to offer agent init as a follow-up step.
    /// When <paramref name="selectByDefault"/> is <see langword="null"/> every bundle-sourced skill is
    /// pre-selected, which is what <c>aspire init</c> wants because aspireify is the natural follow-up.
    /// Other callers (e.g. <c>aspire new</c>) can pass a predicate to additionally filter out skills that
    /// don't fit their context (such as one-time setup skills after a template has already produced the AppHost).
    /// Callers that expose <c>--skill-locations</c> and <c>--skills</c> can pass
    /// <paramref name="skillLocationsBinding"/> and <paramref name="skillsBinding"/> so the chained
    /// execution reuses the same non-interactive selection semantics as standalone <c>aspire agent init</c>.
    /// </summary>
    internal async Task<AgentInitExecutionResult> PromptAndChainAsync(
        IInteractionService interactionService,
        int previousResultExitCode,
        DirectoryInfo workspaceRoot,
        PromptBinding<bool> agentInitBinding,
        PromptBinding<string?> skillLocationsBinding,
        PromptBinding<string?> skillsBinding,
        Func<SkillDefinition, bool>? selectByDefault,
        CancellationToken cancellationToken)
    {
        if (previousResultExitCode != CliExitCodes.Success)
        {
            return new(previousResultExitCode, [], []);
        }

        // Add a separating line between prompt and previous work in aspire new and aspire init.
        interactionService.DisplayEmptyLine();

        var runAgentInit = await interactionService.PromptConfirmAsync(
            SharedCommandStrings.PromptRunAgentInit,
            binding: agentInitBinding,
            cancellationToken: cancellationToken);

        if (runAgentInit)
        {
            return await ExecuteAgentInitAsync(workspaceRoot, selectByDefault, skillLocationsBinding, skillsBinding, cancellationToken);
        }

        return new(CliExitCodes.Success, [], []);
    }

    protected override async Task<CommandResult> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        var workspaceRoot = await PromptForWorkspaceRootAsync(parseResult, cancellationToken);
        // Standalone `aspire agent init` is typically run against an existing project, so don't
        // pre-select the one-time aspireify wiring skill even though every other bundle skill
        // is default-on. Users can still opt into it from the prompt or via --skills.
        var skillLocationsBinding = PromptBinding.Create(parseResult, s_skillLocationsOption);
        var skillsBinding = PromptBinding.Create(parseResult, s_skillsOption);
        var result = await ExecuteAgentInitAsync(workspaceRoot, ExcludeOneTimeSetupSkillsFromDefaults, skillLocationsBinding, skillsBinding, cancellationToken);
        return CommandResult.FromExitCode(result.ExitCode);
    }

    /// <summary>
    /// Names of bundle skills that perform one-time workspace setup and should NOT be
    /// pre-selected after a workspace was just produced by a template flow such as
    /// <c>aspire new</c> or after standalone <c>aspire agent init</c> (typically run
    /// against an existing project).
    /// </summary>
    /// <remarks>
    /// This is the single source of truth the CLI consults when filtering bundle skills out
    /// of the auto-preselection set. All bundle skills are default-on, so if the bundle ships
    /// a new wiring or bootstrap-style skill that should NOT auto-run in an already-bootstrapped
    /// workspace, add its name here.
    /// </remarks>
    internal static readonly IReadOnlySet<string> s_oneTimeSetupSkillNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        CommonAgentApplicators.AspireifySkillName,
    };

    /// <summary>
    /// Default-skill predicate used by flows that do not want one-time setup skills
    /// pre-selected — namely <c>aspire new</c> (template already created the AppHost) and
    /// standalone <c>aspire agent init</c> (typically run against an existing project).
    /// Skills filtered here remain available to opt into from the prompt or via <c>--skills</c>.
    /// </summary>
    internal static bool ExcludeOneTimeSetupSkillsFromDefaults(SkillDefinition skill)
        => skill.IsDefault && !s_oneTimeSetupSkillNames.Contains(skill.Name);

    private async Task<DirectoryInfo> PromptForWorkspaceRootAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        // Try to discover the git repository root to use as the default workspace root
        var gitRoot = await _gitRepository.GetRootAsync(cancellationToken);
        var defaultWorkspaceRoot = gitRoot ?? ExecutionContext.WorkingDirectory;

        // Prompt the user for the workspace root
        var workspaceRootPath = await InteractionService.PromptForFilePathAsync(
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

    private async Task<AgentInitExecutionResult> ExecuteAgentInitAsync(
        DirectoryInfo workspaceRoot,
        Func<SkillDefinition, bool>? selectByDefault,
        PromptBinding<string?> skillLocationsBinding,
        PromptBinding<string?> skillsBinding,
        CancellationToken cancellationToken)
    {
        var context = new AgentEnvironmentScanContext
        {
            WorkingDirectory = ExecutionContext.WorkingDirectory,
            RepositoryRoot = workspaceRoot
        };

        var applicators = await InteractionService.ShowStatusAsync(
            McpCommandStrings.InitCommand_DetectingAgentEnvironments,
            async () => await _agentEnvironmentDetector.DetectAsync(context, cancellationToken),
            emoji: KnownEmojis.Robot);

        // Detect the AppHost language to determine which skills to offer.
        // When no language is detected (e.g., standalone `aspire agent init`), language-restricted skills are excluded.
        var detectedLanguage = await _languageDiscovery.DetectLanguageRecursiveAsync(workspaceRoot, cancellationToken);

        // Apply deprecated config migrations silently (these are fixes, not choices)
        var configUpdates = applicators.Where(a => a.PromptGroup == McpInitPromptGroup.ConfigUpdates).ToList();
        var userChoices = applicators.Where(a => a.PromptGroup != McpInitPromptGroup.ConfigUpdates).ToList();

        foreach (var update in configUpdates)
        {
            try
            {
                await update.ApplyAsync(cancellationToken);
                InteractionService.DisplayMessage(KnownEmojis.Wrench, update.Description);
            }
            catch (InvalidOperationException ex)
            {
                InteractionService.DisplayError(ex.Message);
            }
        }

        // --- Phase 1: Skill location selection ---
        var defaultLocationIds = string.Join(",", SkillLocation.All.Where(l => l.IsDefault).Select(l => l.Id));
        var skillLocationsBindingWithDefault = skillLocationsBinding.WithDefault(defaultLocationIds);

        var selectedLocations = await InteractionService.PromptForSelectionsAsync(
            AgentCommandStrings.InitCommand_SelectSkillLocations,
            SkillLocation.All,
            loc => $"{loc.DisplayName} — {loc.Description}",
            preSelected: SkillLocation.All.Where(l => l.IsDefault),
            optional: true,
            binding: skillLocationsBindingWithDefault,
            echoSelected: false,
            cancellationToken: cancellationToken);

        // --- Phase 2: Skill and MCP server selection (only if locations were selected) ---
        IReadOnlyList<SkillDefinition> selectedSkills = [];
        AspireSkillsBundle? aspireSkillsBundle = null;
        string? bundleInstallFailureMessage = null;
        AgentEnvironmentApplicator? combinedMcpApplicator = null;
        var mcpApplicators = userChoices.Where(a => a.PromptGroup == McpInitPromptGroup.AgentEnvironments).ToList();

        if (selectedLocations.Count > 0)
        {
            IReadOnlyList<SkillDefinition> availableSkills;
            if (ShouldSkipBundleCatalogResolution(skillsBinding))
            {
                availableSkills = SkillDefinition.CliDefined
                    .Where(s => s.IsApplicableToLanguage(detectedLanguage))
                    .ToList();
            }
            else
            {
                (availableSkills, aspireSkillsBundle, bundleInstallFailureMessage) = await ResolveAvailableSkillsAsync(detectedLanguage, cancellationToken);
            }

            // Order the merged catalog deterministically by name so the prompt is stable
            // regardless of manifest order. OrdinalIgnoreCase matches the case-insensitive
            // --skills parsing used elsewhere.
            availableSkills = [.. availableSkills.OrderBy(static s => s.Name, StringComparer.OrdinalIgnoreCase)];

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
                            InteractionService.DisplayMessage(KnownEmojis.CheckMarkButton, mcp.Description);
                        }
                    },
                    promptGroup: McpInitPromptGroup.AdditionalOptions);
                skillChoices.Add(combinedMcpApplicator);
            }

            var preSelectedItems = new List<object>();
            var defaultSkills = GetDefaultSkills(availableSkills, selectByDefault);
            preSelectedItems.AddRange(defaultSkills);
            // MCP is intentionally NOT pre-selected

            var defaultSkillNames = string.Join(",", defaultSkills.Select(s => s.Name));
            var skillsBindingWithDefault = skillsBinding.WithDefault(defaultSkillNames);

            // When the bundle failed to install and the caller passed an explicit --skills value
            // that names a bundle-only skill, the upcoming MatchChoicesOrThrow will reject the
            // value as "not a valid choice" with no hint that the underlying cause was the
            // bundle. Surface the install failure first so users can see why the catalog is short.
            // We only do this when the value contains a name that is not in the available catalog
            // and not a CLI-defined skill, so happy-path runs stay silent.
            if (bundleInstallFailureMessage is not null)
            {
                var (wasProvided, requestedSkills, _) = PromptBinding.Resolve(skillsBindingWithDefault);
                if (wasProvided && requestedSkills is not null && HasUnknownBundleSkillCandidate(requestedSkills, availableSkills))
                {
                    InteractionService.DisplayError(bundleInstallFailureMessage);
                }
            }

            var selectedItems = await InteractionService.PromptForSelectionsAsync(
                AgentCommandStrings.InitCommand_SelectSkills,
                skillChoices,
                item => item switch
                {
                    SkillDefinition skill => $"{skill.Name.EscapeMarkup()} — {SimplifyDescription(skill.Description).EscapeMarkup()}",
                    AgentEnvironmentApplicator app => $"[bold]{app.Description}[/] [dim]{AgentCommandStrings.InitCommand_ConfiguresDetectedAgentEnvironments}[/]",
                    _ => item.ToString()!
                },
                preSelected: preSelectedItems,
                optional: true,
                binding: skillsBindingWithDefault,
                // The MCP applicator participates in the interactive multi-select prompt for UX,
                // but it is not a skill and must not be addressable via `--skills`. Restrict
                // non-interactive validation to the actual SkillDefinition catalog.
                bindingChoices: availableSkills.Cast<object>(),
                echoSelected: false,
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

        var installedSkills = new List<InstalledSkillSummaryItem>();

        foreach (var location in selectedLocations)
        {
            context.AddSkillBaseDirectory(location.RelativeSkillDirectory);

            foreach (var skill in selectedSkills)
            {
                // Playwright CLI is installed via PlaywrightCliInstaller, not as a static skill file
                if (!skill.HasInstallableFiles)
                {
                    continue;
                }

                if (skill.SourceKind is SkillSourceKind.AspireSkillsBundle && aspireSkillsBundle is null)
                {
                    continue;
                }

                var installResult = await InstallSkillAsync(
                    workspaceRoot,
                    location.RelativeSkillDirectory,
                    skill,
                    aspireSkillsBundle,
                    isUserLevel: false,
                    cancellationToken);
                hasErrors |= !installResult.Succeeded;
                if (installResult.UpdatedSkill is not null)
                {
                    installedSkills.Add(installResult.UpdatedSkill);
                }

                if (location.IncludeUserLevel)
                {
                    installResult = await InstallSkillAsync(
                        ExecutionContext.HomeDirectory,
                        location.RelativeSkillDirectory,
                        skill,
                        aspireSkillsBundle,
                        isUserLevel: true,
                        cancellationToken);
                    hasErrors |= !installResult.Succeeded;
                    if (installResult.UpdatedSkill is not null)
                    {
                        installedSkills.Add(installResult.UpdatedSkill);
                    }
                }
            }
        }

        DisplayInstalledSkillsSummary(installedSkills);

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
                        InteractionService.DisplayMessage(KnownEmojis.CheckMarkButton, AgentCommandStrings.InitCommand_InstalledPlaywrightCli);
                        break;
                    case PlaywrightInstallStatus.InstalledWithWarnings:
                        InteractionService.DisplayMessage(KnownEmojis.Warning, message!);
                        break;
                    case PlaywrightInstallStatus.Failed:
                        InteractionService.DisplayError(message!);
                        hasErrors = true;
                        break;
                    case PlaywrightInstallStatus.Skipped:
                        // npm is not available — not an error, just informational.
                        InteractionService.DisplaySubtleMessage(AgentCommandStrings.InitCommand_PlaywrightCliSkipped);
                        break;
                    default:
                        throw new UnreachableException($"Unexpected PlaywrightInstallStatus: {status}");
                }
            }
            catch (InvalidOperationException ex)
            {
                InteractionService.DisplayError(ex.Message);
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
                InteractionService.DisplayError(ex.Message);
                if (ex.InnerException is JsonException)
                {
                    InteractionService.DisplaySubtleMessage(
                        string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.SkippedMalformedConfigFile, combinedMcpApplicator.Description));
                }
                hasErrors = true;
            }
        }

        if (hasErrors)
        {
            InteractionService.DisplayMessage(KnownEmojis.Warning, AgentCommandStrings.ConfigurationCompletedWithErrors);
        }
        else
        {
            InteractionService.DisplaySuccess(McpCommandStrings.InitCommand_ConfigurationComplete);
        }

        return new(
            hasErrors ? CliExitCodes.InvalidCommand : CliExitCodes.Success,
            selectedLocations,
            selectedSkills);
    }

    private async Task<(IReadOnlyList<SkillDefinition> Skills, AspireSkillsBundle? Bundle, string? FailureMessage)> ResolveAvailableSkillsAsync(LanguageId? detectedLanguage, CancellationToken cancellationToken)
    {
        var skills = new List<SkillDefinition>();
        AspireSkillsBundle? bundle = null;
        string? failureMessage = null;

        var result = await _aspireSkillsInstaller.InstallAsync(cancellationToken);
        if (result.Status is AspireSkillsInstallStatus.Installed)
        {
            bundle = result.Bundle ?? throw new InvalidOperationException("Aspire skills installer returned an installed result without a bundle.");
            skills.AddRange(bundle.GetSkillDefinitions().Where(static skill => !IsCliDefinedSkillName(skill.Name)));
        }
        else
        {
            // Preserve the install failure so the caller can surface it only when the user
            // passed an explicit --skills value that names a bundle-only skill. Happy-path
            // (interactive prompt with the embedded fallback) stays silent.
            failureMessage = result.Message;
        }

        // When the bundle is unavailable (network failure, version mismatch, etc.), fall back
        // silently to the CLI-defined skills. The installer already logs the underlying cause
        // at debug level, so the user is not interrupted with a warning they cannot act on.
        skills.AddRange(SkillDefinition.CliDefined);

        return (skills
            .Where(s => s.IsApplicableToLanguage(detectedLanguage))
            .ToList(), bundle, failureMessage);
    }

    private static bool HasUnknownBundleSkillCandidate(string requestedSkills, IReadOnlyList<SkillDefinition> availableSkills)
    {
        // Tokens like "all" / "none" don't name skills, so the "looks like a bundle skill but missing"
        // diagnostic doesn't apply — let the normal validation path handle them.
        if (string.IsNullOrWhiteSpace(requestedSkills) ||
            string.Equals(requestedSkills, ConsoleInteractionService.AllChoice, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(requestedSkills, ConsoleInteractionService.NoneChoice, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var requested = requestedSkills.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var name in requested)
        {
            if (IsCliDefinedSkillName(name))
            {
                continue;
            }

            if (!availableSkills.Any(s => s.HasName(name, StringComparison.OrdinalIgnoreCase)))
            {
                // A non-CLI name that isn't in the catalog is exactly the case the bundle would have provided.
                return true;
            }
        }

        return false;
    }

    private static bool ShouldSkipBundleCatalogResolution(PromptBinding<string?> skillsBinding)
    {
        var (wasProvided, optionValue, _) = PromptBinding.Resolve(skillsBinding);
        if (!wasProvided)
        {
            return false;
        }

        return ShouldSkipBundleCatalogResolution(optionValue);
    }

    private static bool ShouldSkipBundleCatalogResolution(string? value)
    {
        if (string.Equals(value, ConsoleInteractionService.NoneChoice, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(value) ||
            string.Equals(value, ConsoleInteractionService.AllChoice, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var selectedSkillNames = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return selectedSkillNames.Length > 0 &&
               selectedSkillNames.All(static name => IsCliDefinedSkillName(name));
    }

    private static bool IsCliDefinedSkillName(string name)
    {
        return SkillDefinition.CliDefined.Any(skill => skill.HasName(name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Extracts the single short sentence from a skill description so the selection prompt
    /// stays readable.
    /// </summary>
    /// <remarks>
    /// Bundle manifest descriptions can include a bold skill-type prefix followed by a
    /// short tagline and additional usage guidance, for example:
    ///   "**WORKFLOW SKILL** - Top-level router for Aspire 13.4 distributed apps. Detects the AppHost. USE FOR: ..."
    /// This trims the prefix and returns only the first sentence. Inputs without the prefix
    /// or sentence terminator are returned trimmed-but-otherwise-unchanged so CLI-defined
    /// short descriptions are preserved as-is.
    /// </remarks>
    internal static string SimplifyDescription(string description)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return description;
        }

        var simplified = description.Trim();

        // Strip the leading bold "TYPE SKILL" prefix when present, and only then strip the
        // separator characters that typically follow it. Gating the separator strip on the
        // prefix match avoids silently mutating descriptions that legitimately start with
        // a dash, em-dash, or colon (e.g. "-mode flag explained" or ":memo notes").
        var strippedBoldPrefix = false;
        if (simplified.StartsWith("**", StringComparison.Ordinal))
        {
            var endBold = simplified.IndexOf("**", 2, StringComparison.Ordinal);
            if (endBold > 0)
            {
                simplified = simplified[(endBold + 2)..].TrimStart();
                strippedBoldPrefix = true;
            }
        }

        if (strippedBoldPrefix)
        {
            // Separators that typically follow the bold prefix (" - ", " — ", " – ", ": ").
            while (simplified.Length > 0 && simplified[0] is '-' or '\u2013' or '\u2014' or ':')
            {
                simplified = simplified[1..].TrimStart();
            }
        }

        // Return up to and including the first sentence-ending punctuation followed by
        // whitespace or end-of-string. This avoids splitting on inline punctuation such
        // as "13.4" or "github.com" inside the first sentence.
        for (var i = 0; i < simplified.Length; i++)
        {
            if (simplified[i] is '.' or '!' or '?'
                && (i + 1 >= simplified.Length || char.IsWhiteSpace(simplified[i + 1])))
            {
                return simplified[..(i + 1)];
            }
        }

        return simplified;
    }

    private static IReadOnlyList<SkillDefinition> GetDefaultSkills(IEnumerable<SkillDefinition> availableSkills, Func<SkillDefinition, bool>? selectByDefault)
    {
        // When the caller doesn't customize default selection, fall back to SkillDefinition.IsDefault.
        // Bundle-sourced skills are uniformly IsDefault=true; CLI-defined skills (playwright-cli,
        // dotnet-inspect) are IsDefault=false so they stay opt-in. Callers like `aspire new` pass
        // a predicate to additionally filter out skills that don't fit their flow.
        var predicate = selectByDefault ?? (static skill => skill.IsDefault);
        return availableSkills.Where(predicate).ToList();
    }

    /// <summary>
    /// Installs the files for a skill at the specified location, creating or updating them as needed.
    /// </summary>
    /// <returns>The install result, including the skill/location pair when files were updated.</returns>
    private async Task<SkillInstallResult> InstallSkillAsync(
        DirectoryInfo rootDirectory,
        string relativeSkillDirectory,
        SkillDefinition skill,
        AspireSkillsBundle? aspireSkillsBundle,
        bool isUserLevel,
        CancellationToken cancellationToken)
    {
        var relativeSkillPath = Path.Combine(relativeSkillDirectory, skill.Name);
        var fullSkillDirectoryPath = Path.Combine(rootDirectory.FullName, relativeSkillPath);

        try
        {
            var skillFiles = await GetSkillFilesAsync(skill, aspireSkillsBundle, cancellationToken);
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
                return new(Succeeded: true, UpdatedSkill: null);
            }

            var displayLocation = GetDisplaySkillDirectory(relativeSkillDirectory, isUserLevel);
            return new(Succeeded: true, new InstalledSkillSummaryItem(skill.Name, displayLocation));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            InteractionService.DisplayError(
                string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.InitCommand_FailedToInstallSkill, skill.Name, fullSkillDirectoryPath, ex.Message));
            return new(Succeeded: false, UpdatedSkill: null);
        }
    }

    private void DisplayInstalledSkillsSummary(IReadOnlyList<InstalledSkillSummaryItem> installedSkills)
    {
        if (installedSkills.Count == 0)
        {
            return;
        }

        var skillNames = string.Join(", ", GetUniqueValues(installedSkills.Select(static installedSkill => installedSkill.SkillName)));
        var locations = string.Join(", ", GetUniqueValues(installedSkills.Select(static installedSkill => installedSkill.DisplayLocation)));
        var message = string.Join(Environment.NewLine,
            AgentCommandStrings.InitCommand_InstalledSkillsSummary,
            $"  {string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.InitCommand_InstalledSkillsSummarySkills, skillNames)}",
            $"  {string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.InitCommand_InstalledSkillsSummaryLocations, locations)}");

        InteractionService.DisplayMessage(KnownEmojis.Robot, message);
    }

    private static IReadOnlyList<string> GetUniqueValues(IEnumerable<string> values)
    {
        var uniqueValues = new List<string>();
        var seenValues = new HashSet<string>(StringComparer.Ordinal);

        foreach (var value in values)
        {
            if (seenValues.Add(value))
            {
                uniqueValues.Add(value);
            }
        }

        return uniqueValues;
    }

    private static string GetDisplaySkillDirectory(string relativeSkillDirectory, bool isUserLevel)
    {
        var displayRelativeSkillDirectory = relativeSkillDirectory
            .Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/');

        return isUserLevel ? $"~/{displayRelativeSkillDirectory}" : displayRelativeSkillDirectory;
    }

    private static async Task<IReadOnlyList<SkillAssetFile>> GetSkillFilesAsync(SkillDefinition skill, AspireSkillsBundle? aspireSkillsBundle, CancellationToken cancellationToken)
    {
        if (skill.SkillContent is not null)
        {
            return [new SkillAssetFile("SKILL.md", skill.SkillContent)];
        }

        if (skill.SourceKind is SkillSourceKind.AspireSkillsBundle)
        {
            if (aspireSkillsBundle is null)
            {
                throw new InvalidOperationException($"Aspire skills bundle was not resolved for skill '{skill.Name}'.");
            }

            return await aspireSkillsBundle.GetSkillFilesAsync(skill, cancellationToken);
        }

        throw new InvalidOperationException($"Skill '{skill.Name}' does not define installable files.");
    }

    private sealed record InstalledSkillSummaryItem(string SkillName, string DisplayLocation);

    private readonly record struct SkillInstallResult(bool Succeeded, InstalledSkillSummaryItem? UpdatedSkill);
}

internal readonly record struct AgentInitExecutionResult(
    int ExitCode,
    IReadOnlyList<SkillLocation> SelectedLocations,
    IReadOnlyList<SkillDefinition> SelectedSkills);
