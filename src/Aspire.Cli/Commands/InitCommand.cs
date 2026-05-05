// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Cli.Agents;
using Aspire.Cli.Certificates;
using Aspire.Cli.Configuration;
using Aspire.Cli.DotNet;
using Aspire.Cli.Exceptions;
using Aspire.Cli.Interaction;
using Aspire.Cli.NuGet;
using Aspire.Cli.Projects;
using Aspire.Cli.Resources;
using Aspire.Cli.Scaffolding;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Templating;
using Aspire.Cli.Utils;
using Aspire.Shared;

namespace Aspire.Cli.Commands;

/// <summary>
/// Drops a skeleton AppHost and, when applicable, an <c>aspire.config.json</c>, then
/// installs the appropriate init skill for an agent to complete the wiring. This is a
/// thin launcher — the heavy lifting (project discovery, dependency configuration,
/// validation) is delegated to the <c>aspireify</c> skill.
/// </summary>
internal sealed class InitCommand : BaseCommand
{
    internal override HelpGroup HelpGroup => HelpGroup.AppCommands;

    private readonly CliExecutionContext _executionContext;
    private readonly ILanguageService _languageService;
    private readonly ISolutionLocator _solutionLocator;
    private readonly AgentInitCommand _agentInitCommand;
    private readonly IDotNetCliRunner _runner;
    private readonly ICertificateService _certificateService;
    private readonly IScaffoldingService _scaffoldingService;
    private readonly ILanguageDiscovery _languageDiscovery;
    private readonly TemplateNuGetConfigService _templateNuGetConfigService;

    private static readonly Option<string?> s_sourceOption = new("--source", "-s")
    {
        Description = "Deprecated. Accepted for compatibility but no longer affects `aspire init`; this option will be removed in a future version.",
        Recursive = true,
        Hidden = true
    };

    private static readonly Option<string?> s_versionOption = new("--version")
    {
        Description = "Deprecated. Accepted for compatibility but no longer affects `aspire init`; this option will be removed in a future version.",
        Recursive = true,
        Hidden = true
    };

    private readonly Option<string?> _channelOption;
    private readonly Option<string?> _languageOption;

    public InitCommand(
        ILanguageService languageService,
        ISolutionLocator solutionLocator,
        AspireCliTelemetry telemetry,
        IFeatures features,
        ICliUpdateNotifier updateNotifier,
        CliExecutionContext executionContext,
        IInteractionService interactionService,
        AgentInitCommand agentInitCommand,
        IDotNetCliRunner runner,
        ICertificateService certificateService,
        IScaffoldingService scaffoldingService,
        ILanguageDiscovery languageDiscovery,
        TemplateNuGetConfigService templateNuGetConfigService)
        : base("init", InitCommandStrings.Description, features, updateNotifier, executionContext, interactionService, telemetry)
    {
        _executionContext = executionContext;
        _languageService = languageService;
        _solutionLocator = solutionLocator;
        _agentInitCommand = agentInitCommand;
        _runner = runner;
        _certificateService = certificateService;
        _scaffoldingService = scaffoldingService;
        _languageDiscovery = languageDiscovery;
        _templateNuGetConfigService = templateNuGetConfigService;

        _channelOption = new Option<string?>("--channel")
        {
            Description = "Deprecated. Accepted for compatibility but no longer affects `aspire init`; this option will be removed in a future version.",
            Recursive = true,
            Hidden = true
        };

        _languageOption = new Option<string?>("--language")
        {
            Description = InitCommandStrings.LanguageOptionDescription
        };
        Options.Add(s_sourceOption);
        Options.Add(s_versionOption);
        Options.Add(_channelOption);
        Options.Add(_languageOption);
        Options.Add(NewCommand.s_suppressAgentInitOption);
    }

    protected override async Task<int> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        using var activity = Telemetry.StartDiagnosticActivity(this.Name);

        // Step 1: Get the language selection.
        var explicitLanguage = parseResult.GetValue(_languageOption);
        DisplayDeprecatedOptionWarnings(parseResult);

        var projectSelection = await _languageService.GetOrPromptForProjectSelectionAsync(explicitLanguage, saveLanguageSelection: false, cancellationToken);
        var selectedProject = projectSelection.Project;

        var isCSharp = selectedProject.LanguageId == KnownLanguageId.CSharp;
        var workingDirectory = _executionContext.WorkingDirectory;

        // Step 2: Detect solution (C# only — determines single-file vs full project).
        FileInfo? solutionFile = null;
        if (isCSharp)
        {
            solutionFile = await _solutionLocator.FindSolutionFileAsync(workingDirectory, cancellationToken);
        }

        // Step 3: Drop the skeleton AppHost and any related config files needed for that mode.
        var dropResult = isCSharp
            ? await DropCSharpSkeletonAsync(workingDirectory, solutionFile, cancellationToken)
            : await DropPolyglotSkeletonAsync(selectedProject.LanguageId, workingDirectory, cancellationToken);

        if (dropResult != ExitCodeConstants.Success)
        {
            InteractionService.DisplayError(string.Format(CultureInfo.CurrentCulture, InteractionServiceStrings.ProjectCouldNotBeCreated, ExecutionContext.LogFilePath));
            return dropResult;
        }

        // Persist the prompted language selection now that the skeleton drop succeeded.
        if (projectSelection.ShouldPersistSelection)
        {
            await _languageService.SetLanguageAsync(selectedProject, cancellationToken: cancellationToken);
        }

        // Trust the dev certificate so the first `aspire start` doesn't hit cert errors.
        // The skeleton AppHost / aspire.config.json profiles default to HTTPS, and the
        // aspireify skill guidance prefers HTTPS for service endpoints. Best-effort —
        // ignore failures since `aspire doctor` / `aspire certs trust` provide a fallback.
        if (isCSharp)
        {
            try
            {
                _ = await _certificateService.EnsureCertificatesTrustedAsync(cancellationToken);
            }
            catch (CertificateServiceException)
            {
                // Non-fatal: surface via aspire doctor / aspire certs trust.
            }
        }

        // Step 4: Chain to aspire agent init for MCP server + skill configuration.
        // This prompt lets users choose which skills to install — including aspireify.
        var workspaceRoot = solutionFile?.Directory ?? workingDirectory;
        var agentInitBinding = PromptBinding.CreateInvertedBoolConfirm(parseResult, NewCommand.s_suppressAgentInitOption, defaultValue: true);
        var agentInitResult = await _agentInitCommand.PromptAndChainAsync(InteractionService, ExitCodeConstants.Success, workspaceRoot, agentInitBinding, cancellationToken);

        // Step 5: Print follow-up commands only when the user selected the one-time init skill.
        if (agentInitResult.ExitCode == ExitCodeConstants.Success &&
            agentInitResult.SelectedSkills.Contains(SkillDefinition.Aspireify))
        {
            var commands = GetAspireifyCommands(agentInitResult.SelectedLocations);
            if (commands.Count > 0)
            {
                InteractionService.DisplayEmptyLine();
                InteractionService.DisplayMessage(
                    KnownEmojis.Dizzy,
                    commands.Count == 1
                        ? "Aspire AppHost created! To complete setup, run:"
                        : "Aspire AppHost created! To complete setup, run one of:");
                InteractionService.DisplayEmptyLine();

                foreach (var command in commands)
                {
                    InteractionService.DisplaySubtleMessage($"  {command}");
                }
            }
        }

        return agentInitResult.ExitCode;
    }

    private void DisplayDeprecatedOptionWarnings(ParseResult parseResult)
    {
        DisplayDeprecatedOptionWarningIfProvided(parseResult.GetValue(s_sourceOption), "--source");
        DisplayDeprecatedOptionWarningIfProvided(parseResult.GetValue(s_versionOption), "--version");
        DisplayDeprecatedOptionWarningIfProvided(parseResult.GetValue(_channelOption), "--channel");
    }

    private void DisplayDeprecatedOptionWarningIfProvided(string? value, string optionName)
    {
        if (value is not null)
        {
            InteractionService.DisplayMessage(
                KnownEmojis.Warning,
                $"`aspire init {optionName}` is deprecated and no longer affects generated AppHosts. It is accepted for compatibility and will be removed in a future version.");
        }
    }

    private static IReadOnlyList<string> GetAspireifyCommands(IReadOnlyList<SkillLocation> selectedLocations)
    {
        var commands = new List<string>();

        if (selectedLocations.Contains(SkillLocation.ClaudeCode))
        {
            commands.Add("claude \"run the aspireify skill\"");
        }

        if (selectedLocations.Contains(SkillLocation.OpenCode))
        {
            commands.Add("opencode --prompt \"run the aspireify skill\"");
        }

        return commands;
    }

    private async Task<int> DropCSharpSkeletonAsync(DirectoryInfo workingDirectory, FileInfo? solutionFile, CancellationToken cancellationToken)
    {
        if (solutionFile is not null)
        {
            return await DropCSharpProjectSkeletonAsync(solutionFile, cancellationToken);
        }

        return await DropCSharpSingleFileSkeletonAsync(workingDirectory, cancellationToken);
    }

    private Task<int> DropCSharpSingleFileSkeletonAsync(DirectoryInfo workingDirectory, CancellationToken cancellationToken)
    {
        _ = cancellationToken;

        var appHostPath = Path.Combine(workingDirectory.FullName, "apphost.cs");
        if (File.Exists(appHostPath))
        {
            InteractionService.DisplayMessage(KnownEmojis.CheckMarkButton, "apphost.cs already exists — skipping.");
            return Task.FromResult(ExitCodeConstants.Success);
        }

        // Drop bare single-file apphost. Pin the SDK version so later operations
        // (project updating, version parsing in ProjectUpdater/FallbackProjectParser)
        // can locate and update the directive — they expect the @<version> form.
        var aspireVersion = VersionHelper.GetDefaultTemplateVersion();
        var appHostContent = $$"""
            #:sdk Aspire.AppHost.Sdk@{{aspireVersion}}

            var builder = DistributedApplication.CreateBuilder(args);

            // The aspireify skill will wire up your projects here.

            builder.Build().Run();
            """;
        File.WriteAllText(appHostPath, appHostContent);
        InteractionService.DisplayMessage(KnownEmojis.CheckMarkButton, "Created apphost.cs");

        // Drop aspire.config.json
        var configResult = DropAspireConfig(workingDirectory, "apphost.cs", language: null);

        return Task.FromResult(configResult);
    }

    private async Task<int> DropCSharpProjectSkeletonAsync(FileInfo solutionFile, CancellationToken cancellationToken)
    {
        var solutionDir = solutionFile.Directory!;
        var solutionName = Path.GetFileNameWithoutExtension(solutionFile.Name);
        var appHostDirName = $"{solutionName}.AppHost";
        var appHostDirPath = Path.Combine(solutionDir.FullName, appHostDirName);

        if (Directory.Exists(appHostDirPath))
        {
            InteractionService.DisplayMessage(KnownEmojis.CheckMarkButton, $"{appHostDirName}/ already exists — skipping.");
            return ExitCodeConstants.Success;
        }

        // Resolve the channel-aware template package version + feed mapping. This makes init
        // honor the global `channel` configuration (matching `aspire new`) and ensures the
        // staging/daily/PR feed is queried for non-stable CLI builds. PR hives are intentionally
        // excluded — init should produce the same template on every machine for a given CLI build.
        TemplatePackageSelection selection;
        try
        {
            var query = new TemplatePackageQuery(
                ChannelOverride: null,
                VersionOverride: null,
                SourceOverride: null,
                IncludePrHives: false);

            selection = await _templateNuGetConfigService.ResolveTemplatePackageAsync(query, cancellationToken);
        }
        catch (ChannelNotFoundException ex)
        {
            InteractionService.DisplayError(ex.Message);
            return ExitCodeConstants.FailedToInstallTemplates;
        }
        catch (EmptyChoicesException ex)
        {
            InteractionService.DisplayError(ex.Message);
            return ExitCodeConstants.FailedToInstallTemplates;
        }
        catch (NuGetPackageCacheException ex)
        {
            // Surface NuGet feed search failures (offline, inaccessible feed, etc.) with a friendly error
            // instead of letting them bubble up to the top-level "unexpected error" handler. The pre-extraction
            // init code went straight to `dotnet new install` and never invoked a NuGet search, so this catch
            // restores parity with the prior init failure mode for these scenarios.
            InteractionService.DisplayError(ex.Message);
            return ExitCodeConstants.FailedToInstallTemplates;
        }

        // The aspire-apphost template ships in the Aspire.ProjectTemplates package.
        // `dotnet new` does not install templates implicitly, so on a fresh machine
        // (or after a CLI update) the template will be missing. Install first.
        var installOutcome = await _templateNuGetConfigService.InstallTemplatePackageAsync(
            selection,
            _runner,
            "Installing Aspire project templates...",
            statusEmoji: null,
            cancellationToken);

        if (installOutcome.ExitCode != 0)
        {
            InteractionService.DisplayLines(installOutcome.OutputLines);
            InteractionService.DisplayError(string.Format(CultureInfo.CurrentCulture, TemplatingStrings.TemplateInstallationFailed, installOutcome.ExitCode, _executionContext.LogFilePath));
            return ExitCodeConstants.FailedToInstallTemplates;
        }

        // Use the aspire-apphost template to generate a correct AppHost project
        // with proper launchSettings.json, .csproj, and Program.cs.
        var result = await InteractionService.ShowStatusAsync(
            "Creating AppHost from template...",
            async () =>
            {
                return await _runner.NewProjectAsync(
                    "aspire-apphost",
                    appHostDirName,
                    appHostDirPath,
                    extraArgs: [],
                    options: new ProcessInvocationOptions(),
                    cancellationToken: cancellationToken);
            });

        if (result != 0)
        {
            InteractionService.DisplayError($"Failed to create AppHost from template (exit code {result}).");
            return ExitCodeConstants.FailedToCreateNewProject;
        }

        InteractionService.DisplayMessage(KnownEmojis.CheckMarkButton, $"Created {appHostDirName}/");

        return ExitCodeConstants.Success;
    }

    private async Task<int> DropPolyglotSkeletonAsync(string languageId, DirectoryInfo workingDirectory, CancellationToken cancellationToken)
    {
        var language = _languageDiscovery.GetLanguageById(languageId)
            ?? throw new NotSupportedException($"Polyglot skeleton not yet supported for language: {languageId}");

        var appHostFileName = language.AppHostFileName
            ?? throw new NotSupportedException($"Polyglot skeleton not yet supported for language: {language.LanguageId}");

        var appHostPath = Path.Combine(workingDirectory.FullName, appHostFileName);
        if (File.Exists(appHostPath))
        {
            InteractionService.DisplayMessage(KnownEmojis.CheckMarkButton, $"{appHostFileName} already exists — skipping.");
            return ExitCodeConstants.Success;
        }

        var context = new ScaffoldContext(language, workingDirectory, workingDirectory.Name);
        var scaffolded = await _scaffoldingService.ScaffoldAsync(context, cancellationToken);
        if (!scaffolded)
        {
            return ExitCodeConstants.FailedToCreateNewProject;
        }

        InteractionService.DisplayMessage(KnownEmojis.CheckMarkButton, $"Created {appHostFileName}");
        return ExitCodeConstants.Success;
    }

    private int DropAspireConfig(DirectoryInfo directory, string appHostPath, string? language)
    {
        var configPath = Path.Combine(directory.FullName, AspireConfigFile.FileName);

        JsonObject settings;

        if (File.Exists(configPath))
        {
            // Merge into existing file (e.g. language selection already wrote it)
            var existingContent = File.ReadAllText(configPath);
            if (string.IsNullOrWhiteSpace(existingContent))
            {
                settings = new JsonObject();
            }
            else
            {
                try
                {
                    settings = JsonNode.Parse(existingContent)?.AsObject() ?? new JsonObject();
                }
                catch (JsonException ex)
                {
                    InteractionService.DisplayError($"Failed to parse existing {AspireConfigFile.FileName} at '{configPath}': {ex.Message}");
                    InteractionService.DisplayMessage(KnownEmojis.Warning, $"Fix or remove {AspireConfigFile.FileName} and re-run `aspire init`.");
                    return ExitCodeConstants.FailedToCreateNewProject;
                }
            }
        }
        else
        {
            settings = new JsonObject();
        }

        // Ensure appHost section exists
        if (settings["appHost"] is not JsonObject appHost)
        {
            appHost = new JsonObject();
            settings["appHost"] = appHost;
        }

        // Set path (always — this is the primary purpose of DropAspireConfig)
        appHost["path"] = appHostPath;

        // Set language if provided and not already present
        if (language is not null && appHost["language"] is null)
        {
            appHost["language"] = language;
        }

        // Write default profiles with random ports for dashboard/OTLP/resource service.
        // Matches the profile structure used by `aspire new` templates (see Templates/*/aspire.config.json).
        // Normally scaffolding + codegen creates these, but our thin init skips scaffolding.
        if (settings["profiles"] is null)
        {
            var ports = AppHostProfilePortGenerator.Generate(Random.Shared);

            settings["profiles"] = new JsonObject
            {
                ["https"] = new JsonObject
                {
                    ["applicationUrl"] = $"https://localhost:{ports.DashboardHttpsPort};http://localhost:{ports.DashboardHttpPort}",
                    ["environmentVariables"] = new JsonObject
                    {
                        ["ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL"] = $"https://localhost:{ports.OtlpHttpsPort}",
                        ["ASPIRE_RESOURCE_SERVICE_ENDPOINT_URL"] = $"https://localhost:{ports.ResourceServiceHttpsPort}"
                    }
                },
                ["http"] = new JsonObject
                {
                    ["applicationUrl"] = $"http://localhost:{ports.DashboardHttpPort}",
                    ["environmentVariables"] = new JsonObject
                    {
                        ["ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL"] = $"http://localhost:{ports.OtlpHttpPort}",
                        ["ASPIRE_RESOURCE_SERVICE_ENDPOINT_URL"] = $"http://localhost:{ports.ResourceServiceHttpPort}",
                        ["ASPIRE_ALLOW_UNSECURED_TRANSPORT"] = "true"
                    }
                }
            };
        }

        var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(configPath, settings.ToJsonString(jsonOptions));

        InteractionService.DisplayMessage(KnownEmojis.CheckMarkButton, $"Created {AspireConfigFile.FileName}");
        return ExitCodeConstants.Success;
    }

}
