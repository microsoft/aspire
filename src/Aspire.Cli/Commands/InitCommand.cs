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
using Aspire.Cli.Packaging;
using Aspire.Cli.Projects;
using Aspire.Cli.Resources;
using Aspire.Cli.Scaffolding;
using Aspire.Cli.Templating;
using Aspire.Hosting;
using Aspire.Hosting.Utils;
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

    protected override bool UpdateNotificationsEnabled => true;

    private readonly CliExecutionContext _executionContext;
    private readonly ILanguageService _languageService;
    private readonly ISolutionLocator _solutionLocator;
    private readonly AgentInitCommand _agentInitCommand;
    private readonly IDotNetCliRunner _runner;
    private readonly ICertificateService _certificateService;
    private readonly IScaffoldingService _scaffoldingService;
    private readonly ILanguageDiscovery _languageDiscovery;
    private readonly TemplateNuGetConfigService _templateNuGetConfigService;
    private readonly IPackagingService _packagingService;

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
        AgentInitCommand agentInitCommand,
        IDotNetCliRunner runner,
        ICertificateService certificateService,
        IScaffoldingService scaffoldingService,
        ILanguageDiscovery languageDiscovery,
        TemplateNuGetConfigService templateNuGetConfigService,
        IPackagingService packagingService,
        CommonCommandServices services)
        : base("init", InitCommandStrings.Description, services)
    {
        _executionContext = services.ExecutionContext;
        _languageService = languageService;
        _solutionLocator = solutionLocator;
        _agentInitCommand = agentInitCommand;
        _runner = runner;
        _certificateService = certificateService;
        _scaffoldingService = scaffoldingService;
        _languageDiscovery = languageDiscovery;
        _templateNuGetConfigService = templateNuGetConfigService;
        _packagingService = packagingService;

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
        Options.Add(AgentInitCommand.s_skillLocationsOption);
        Options.Add(AgentInitCommand.s_skillsOption);
    }

    protected override async Task<CommandResult> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
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

        if (dropResult != CliExitCodes.Success)
        {
            return CommandResult.Failure(dropResult, InteractionServiceStrings.ProjectCouldNotBeCreated);
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
        var skillLocationsBinding = PromptBinding.Create(parseResult, AgentInitCommand.s_skillLocationsOption);
        var skillsBinding = PromptBinding.Create(parseResult, AgentInitCommand.s_skillsOption);
        // aspire init creates an AppHost in an existing repo, so pre-select every bundle skill
        // (which includes aspireify as the natural follow-up wiring skill).
        var agentInitResult = await _agentInitCommand.PromptAndChainAsync(
            InteractionService,
            CliExitCodes.Success,
            workspaceRoot,
            agentInitBinding,
            skillLocationsBinding,
            skillsBinding,
            null,
            cancellationToken);

        // Step 5: Print follow-up commands only when the user selected the one-time init skill.
        if (agentInitResult.ExitCode == CliExitCodes.Success &&
            agentInitResult.SelectedSkills.Any(static skill => skill.HasName(CommonAgentApplicators.AspireifySkillName)))
        {
            var commands = GetAspireifyCommands(agentInitResult.SelectedLocations);
            if (commands.Count > 0)
            {
                InteractionService.DisplayEmptyLine();
                InteractionService.DisplayMessage(
                    KnownEmojis.Dizzy,
                    commands.Count == 1
                        ? InitCommandStrings.AppHostCreatedRunOne
                        : InitCommandStrings.AppHostCreatedRunOneOf);
                InteractionService.DisplayEmptyLine();

                foreach (var command in commands)
                {
                    InteractionService.DisplaySubtleMessage($"  {command}");
                }
            }
        }

        return CommandResult.FromExitCode(agentInitResult.ExitCode);
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
                string.Format(CultureInfo.CurrentCulture, InitCommandStrings.DeprecatedOptionWarning, optionName));
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

    private async Task<int> DropCSharpSingleFileSkeletonAsync(DirectoryInfo workingDirectory, CancellationToken cancellationToken)
    {
        // Ensure the workspace has a NuGet.config that exposes the running CLI binary's
        // identity-channel package sources (CliExecutionContext.IdentityChannel — stable,
        // staging, daily, pr-<N>, or local). Run this BEFORE the apphost.cs-already-exists
        // early return so re-running `aspire init` against a workspace produced by a
        // previous broken CLI (which left apphost.cs without a workspace NuGet.config)
        // recovers cleanly. The config is also required so MSBuild can resolve
        // `#:sdk Aspire.AppHost.Sdk@<version>` from the SDK directive — both for
        // `aspire add` (`dotnet package add --file apphost.cs`) and for
        // `dotnet run --file apphost.cs`. Without it, any non-stable channel (PR/run
        // hives, locally-built `local-*`/`dev-*` hives, the staging channel, etc.) is
        // invisible and SDK resolution fails. `NuGetConfigMerger` underneath creates a
        // new file or merges missing sources into an existing one.
        var createdNuGetConfig = await _templateNuGetConfigService.CreateOrUpdateNuGetConfigWithoutPromptAsync(
            channelName: _executionContext.IdentityChannel,
            outputPath: workingDirectory.FullName,
            cancellationToken).ConfigureAwait(false);
        if (createdNuGetConfig)
        {
            InteractionService.DisplayMessage(KnownEmojis.Package, TemplatingStrings.NuGetConfigCreatedOrUpdatedConfirmationMessage);
        }

        var appHostPath = Path.Combine(workingDirectory.FullName, "apphost.cs");
        if (File.Exists(appHostPath))
        {
            InteractionService.DisplayMessage(KnownEmojis.Information, string.Format(CultureInfo.CurrentCulture, InitCommandStrings.FileAlreadyExistsSkipping, "apphost.cs"));
            return CliExitCodes.Success;
        }

        // Drop bare single-file apphost. Pin the SDK version so later operations
        // (project updating, version parsing in ProjectUpdater/FallbackProjectParser)
        // can locate and update the directive — they expect the @<version> form.
        // Use IdentitySdkVersion (build-metadata stripped) rather than IdentityVersion:
        // the directive references the published Aspire.AppHost.Sdk NuGet package, whose
        // version never carries a +<sha> suffix. This also matches the empty-apphost
        // template path (CliTemplateFactory.EmptyTemplate) so both emit the same form.
        var aspireVersion = _executionContext.IdentitySdkVersion;
        var appHostContent = $$"""
            #:sdk Aspire.AppHost.Sdk@{{aspireVersion}}

            var builder = DistributedApplication.CreateBuilder(args);

            // The aspireify skill will wire up your projects here.

            builder.Build().Run();
            """;
        File.WriteAllText(appHostPath, appHostContent);
        InteractionService.DisplayMessage(KnownEmojis.CheckMarkButton, string.Format(CultureInfo.CurrentCulture, InitCommandStrings.CreatedFile, "apphost.cs"));

        // Generate one set of ports so aspire.config.json (used by `aspire run`) and
        // apphost.run.json (used by `dotnet run apphost.cs`) agree on the dashboard /
        // OTLP / resource service endpoints.
        var ports = AppHostProfilePortGenerator.Generate(Random.Shared);

        // Drop aspire.config.json. The returned ports are whatever ended up effective
        // in aspire.config.json — newly generated, or pre-existing if the file already
        // had a `profiles` section. Use the SAME ports for apphost.run.json so the two
        // files always agree on dashboard / OTLP / resource service endpoints.
        //
        // Persist the running CLI's identity channel (e.g. `daily`, `staging`, `pr-<N>`)
        // so subsequent commands like `aspire add` resolve packages against the matching
        // channel. Resolve through PackagingService and only persist when the identity
        // matches a registered Explicit channel — mirrors `NewCommand.cs:316-402`.
        //
        // `ResolvePersistableChannelNameAsync` filters out identities that aren't
        // registered as channels on this CLI install (e.g. `local`, `staging` on a CLI
        // without the staging feature flag, stale `pr-<N>` after the hive is gone),
        // the Implicit `default` channel that no CLI identity ever has, and `stable`
        // because the public-feed behavior is already the default. Non-default
        // Explicit channels are persisted so subsequent commands can match a PSM rule.
        // See https://github.com/microsoft/aspire/issues/17295.
        var resolvedChannel = await ResolvePersistableChannelNameAsync(cancellationToken);
        var (configResult, effectivePorts) = DropAspireConfig(workingDirectory, "apphost.cs", language: null, resolvedChannel, ports);
        if (configResult != CliExitCodes.Success)
        {
            return configResult;
        }

        // Drop apphost.run.json so `dotnet run apphost.cs` picks up the dashboard /
        // OTLP / resource service env vars from the file-based launch profile. Without
        // this file the AppHost crashes at startup because DashboardOptions validation
        // requires ASPNETCORE_URLS and ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL to be set
        // (these env vars are otherwise injected by the Aspire CLI when running via
        // `aspire run`, but `dotnet run apphost.cs` does not go through that path).
        DropAppHostRunJson(workingDirectory, effectivePorts);

        return CliExitCodes.Success;
    }

    private async Task<int> DropCSharpProjectSkeletonAsync(FileInfo solutionFile, CancellationToken cancellationToken)
    {
        var solutionDir = solutionFile.Directory!;
        var solutionName = Path.GetFileNameWithoutExtension(solutionFile.Name);
        var appHostDirName = $"{solutionName}.AppHost";
        var appHostDirPath = Path.Combine(solutionDir.FullName, appHostDirName);

        // Drop the solution-directory NuGet.config BEFORE the AppHost-dir-already-exists
        // early return so re-running `aspire init` against a workspace produced by a
        // previous broken CLI (which left a `<sln>.AppHost/` without a workspace
        // NuGet.config) recovers cleanly. Writing here is also required BEFORE
        // `_runner.NewProjectAsync` so the aspire-apphost template's built-in `restore`
        // post-action (template.json post-action id "restore", conditioned on
        // !skipRestore which defaults to false) can resolve the
        // `Aspire.AppHost.Sdk/<version>` reference from the channel-matched hive. The
        // post-action currently runs with continueOnError=true so a missing nuget.config
        // wouldn't fail init, but its restore would still emit confusing errors and waste
        // work — and a future template change that drops continueOnError would break init
        // outright.
        //
        // Source: CliExecutionContext.IdentityChannel (stable / staging / daily / pr-<N> /
        // local). NuGetConfigMerger underneath creates a new file or merges missing
        // sources into an existing one, so adding hives later is handled the same way as
        // for templates. Mirrors what DropCSharpSingleFileSkeletonAsync already does for
        // the apphost.cs path on every channel.
        var createdNuGetConfig = await _templateNuGetConfigService.CreateOrUpdateNuGetConfigWithoutPromptAsync(
            channelName: _executionContext.IdentityChannel,
            outputPath: solutionDir.FullName,
            cancellationToken).ConfigureAwait(false);
        if (createdNuGetConfig)
        {
            InteractionService.DisplayMessage(KnownEmojis.Package, TemplatingStrings.NuGetConfigCreatedOrUpdatedConfirmationMessage);
        }

        if (Directory.Exists(appHostDirPath))
        {
            InteractionService.DisplayMessage(KnownEmojis.Information, string.Format(CultureInfo.CurrentCulture, InitCommandStrings.FileAlreadyExistsSkipping, $"{appHostDirName}/"));
            return CliExitCodes.Success;
        }

        // Resolve the channel-aware template package version + feed mapping. The running
        // CLI binary's identity channel (CliExecutionContext.IdentityChannel — stable, staging,
        // daily, pr-<N>, or local) drives the selection so a developer scaffolding with a
        // pr-<N> CLI gets a project wired to the matching pr-<N> hive. PR hives are
        // intentionally excluded — init should produce the same template on every machine
        // for a given CLI build.
        TemplatePackageSelection selection;
        try
        {
            var query = new TemplatePackageQuery(
                RequestedChannel: _executionContext.IdentityChannel,
                VersionOverride: null,
                SourceOverride: null,
                IncludePrHives: false);

            selection = await _templateNuGetConfigService.ResolveTemplatePackageAsync(query, cancellationToken);
        }
        catch (ChannelNotFoundException) when
            (string.Equals(_executionContext.IdentityChannel, PackageChannelNames.Local, StringComparison.OrdinalIgnoreCase))
        {
            // Locally-built CLI (identity=local) on a machine where ~/.aspire/hives/local
            // isn't installed. The PackagingService produces no "local" channel in that
            // case, but init's contract is that identity-as-request is implicit — so fall
            // back to the implicit channel (ambient NuGet) instead of failing. This branch
            // lives here, NOT in the resolver, so that an explicit `aspire new --channel local`
            // without the hive correctly errors instead of silently switching feeds.
            var fallbackQuery = new TemplatePackageQuery(
                RequestedChannel: null,
                VersionOverride: null,
                SourceOverride: null,
                IncludePrHives: false);

            selection = await _templateNuGetConfigService.ResolveTemplatePackageAsync(fallbackQuery, cancellationToken);
        }
        catch (ChannelNotFoundException ex)
        {
            InteractionService.DisplayError(ex.Message);
            return CliExitCodes.FailedToInstallTemplates;
        }
        catch (EmptyChoicesException ex)
        {
            InteractionService.DisplayError(ex.Message);
            return CliExitCodes.FailedToInstallTemplates;
        }
        catch (NuGetPackageCacheException ex)
        {
            // Surface NuGet feed search failures (offline, inaccessible feed, etc.) with a friendly error
            // instead of letting them bubble up to the top-level "unexpected error" handler. The pre-extraction
            // init code went straight to `dotnet new install` and never invoked a NuGet search, so this catch
            // restores parity with the prior init failure mode for these scenarios.
            InteractionService.DisplayError(ex.Message);
            return CliExitCodes.FailedToInstallTemplates;
        }

        // The aspire-apphost template ships in the Aspire.ProjectTemplates package.
        // `dotnet new` does not install templates implicitly, so on a fresh machine
        // (or after a CLI update) the template will be missing. Install first.
        var installOutcome = await _templateNuGetConfigService.InstallTemplatePackageAsync(
            selection,
            _runner,
            InitCommandStrings.InstallingAspireProjectTemplates,
            statusEmoji: null,
            cancellationToken);

        if (installOutcome.ExitCode != 0)
        {
            InteractionService.DisplayLines(installOutcome.OutputLines);
            InteractionService.DisplayError(string.Format(CultureInfo.CurrentCulture, TemplatingStrings.TemplateInstallationFailed, installOutcome.ExitCode));
            return CliExitCodes.FailedToInstallTemplates;
        }

        // Use the aspire-apphost template to generate a correct AppHost project
        // with proper launchSettings.json, .csproj, and Program.cs.
        var result = await InteractionService.ShowStatusAsync(
            InitCommandStrings.CreatingAppHostFromTemplate,
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
            InteractionService.DisplayError(string.Format(CultureInfo.CurrentCulture, InitCommandStrings.FailedToCreateAppHostFromTemplate, result));
            return CliExitCodes.FailedToCreateNewProject;
        }

        InteractionService.DisplayMessage(KnownEmojis.CheckMarkButton, string.Format(CultureInfo.CurrentCulture, InitCommandStrings.CreatedFile, $"{appHostDirName}/"));

        return CliExitCodes.Success;
    }

    private async Task<int> DropPolyglotSkeletonAsync(string languageId, DirectoryInfo workingDirectory, CancellationToken cancellationToken)
    {
        var language = _languageDiscovery.GetLanguageById(languageId)
            ?? throw new NotSupportedException($"Polyglot skeleton not yet supported for language: {languageId}");

        var existingAppHostFileName = language.DetectionPatterns
            .Where(pattern => !pattern.Contains('*', StringComparison.Ordinal))
            .FirstOrDefault(pattern => File.Exists(Path.Combine(workingDirectory.FullName, pattern)));
        if (existingAppHostFileName is not null)
        {
            InteractionService.DisplayMessage(KnownEmojis.Information, string.Format(CultureInfo.CurrentCulture, InitCommandStrings.FileAlreadyExistsSkipping, existingAppHostFileName));
            return CliExitCodes.Success;
        }

        var appHostPath = ScaffoldingService.GetAppHostPath(workingDirectory, language);
        var displayPath = PathNormalizer.NormalizePathForStorage(Path.GetRelativePath(workingDirectory.FullName, appHostPath));
        if (File.Exists(appHostPath))
        {
            InteractionService.DisplayMessage(KnownEmojis.Information, string.Format(CultureInfo.CurrentCulture, InitCommandStrings.FileAlreadyExistsSkipping, displayPath));
            return CliExitCodes.Success;
        }

        // Resolve and pass the running CLI's identity channel through to the scaffolder
        // so it lands in aspire.config.json#channel. Only persist when the identity
        // resolves to a persistable registered Explicit channel — see
        // `ResolvePersistableChannelNameAsync` for the full rationale. Additionally,
        // if aspire.config.json already carries a channel value, suppress the pass-through:
        // `ScaffoldingService.cs:93-95` writes
        // `config.Channel = context.Channel` unconditionally when non-empty, so without
        // this guard a user-edited channel would be silently overwritten.
        var resolvedChannel = await ResolvePersistableChannelNameAsync(cancellationToken);
        if (!string.IsNullOrEmpty(resolvedChannel))
        {
            var existing = TryLoadExistingChannel(workingDirectory);
            if (!string.IsNullOrEmpty(existing))
            {
                resolvedChannel = null;
            }
        }

        var context = new ScaffoldContext(language, workingDirectory, workingDirectory.Name, Channel: resolvedChannel);
        var scaffolded = await _scaffoldingService.ScaffoldAsync(context, cancellationToken);
        if (!scaffolded)
        {
            return CliExitCodes.FailedToCreateNewProject;
        }

        InteractionService.DisplayMessage(KnownEmojis.CheckMarkButton, string.Format(CultureInfo.CurrentCulture, InitCommandStrings.CreatedFile, displayPath));
        return CliExitCodes.Success;
    }

    private (int ExitCode, AppHostProfilePorts EffectivePorts) DropAspireConfig(DirectoryInfo directory, string appHostPath, string? language, string? channel, AppHostProfilePorts? ports = null)
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
                    InteractionService.DisplayError(string.Format(CultureInfo.CurrentCulture, InitCommandStrings.FailedToParseExistingConfig, AspireConfigFile.FileName, configPath, ex.Message));
                    InteractionService.DisplayMessage(KnownEmojis.Warning, string.Format(CultureInfo.CurrentCulture, InitCommandStrings.FixOrRemoveConfigAndRerun, AspireConfigFile.FileName));
                    return (CliExitCodes.FailedToCreateNewProject, default);
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

        // Persist the channel at the top level so `aspire add` / `integration list` /
        // `integration search` resolve packages against the channel the CLI scaffolded
        // for. Only write when not already present so a user-edited value wins. Leaving
        // the channel unset on a non-stable CLI causes downstream commands to fall back
        // to implicit nuget.org versions that don't line up with the CLI build.
        if (!string.IsNullOrEmpty(channel) && settings["channel"] is null)
        {
            settings["channel"] = channel;
        }

        // Resolve the effective ports. Three cases:
        //   1. profiles is null → write fresh profiles, return those ports
        //   2. profiles exists and parses cleanly → adopt those ports, return them (so
        //      apphost.run.json stays in sync with what `aspire run` will use)
        //   3. profiles exists but doesn't match the expected 6-port shape (user-customized
        //      or older format) → PRESERVE the existing profiles untouched and just generate
        //      fresh ports for apphost.run.json. This is strictly safer than overwriting,
        //      even if the two files end up disagreeing on dashboard ports — the user has
        //      already opted into a custom config and we shouldn't trash their data.
        AppHostProfilePorts effectivePorts;
        var existingProfilesObject = settings["profiles"] as JsonObject;
        if (existingProfilesObject is not null && TryReadAppHostProfilePorts(existingProfilesObject, out var readPorts))
        {
            effectivePorts = readPorts;
        }
        else if (existingProfilesObject is not null)
        {
            // Existing profiles can't be parsed into our expected shape — leave them alone
            // and just generate fresh ports for apphost.run.json. We deliberately don't
            // overwrite the user's customizations, even though it means the two files may
            // bind to different dashboard URLs in this edge case.
            effectivePorts = ports ?? AppHostProfilePortGenerator.Generate(Random.Shared);
        }
        else
        {
            // Matches the profile structure used by `aspire new` templates (see Templates/*/aspire.config.json).
            // Normally scaffolding + codegen creates these, but our thin init skips scaffolding.
            effectivePorts = ports ?? AppHostProfilePortGenerator.Generate(Random.Shared);

            // Two profiles (https + http) so `aspire run` can pick either based on user choice.
            // Each carries the dashboard URL (applicationUrl) plus the OTLP and resource-service
            // endpoint env vars consumed by DashboardOptionsValidator at AppHost startup.
            settings["profiles"] = new JsonObject
            {
                ["https"] = new JsonObject
                {
                    ["applicationUrl"] = $"https://localhost:{effectivePorts.DashboardHttpsPort};http://localhost:{effectivePorts.DashboardHttpPort}",
                    ["environmentVariables"] = new JsonObject
                    {
                        [KnownConfigNames.DashboardOtlpGrpcEndpointUrl] = $"https://localhost:{effectivePorts.OtlpHttpsPort}",
                        [KnownConfigNames.ResourceServiceEndpointUrl] = $"https://localhost:{effectivePorts.ResourceServiceHttpsPort}"
                    }
                },
                ["http"] = new JsonObject
                {
                    ["applicationUrl"] = $"http://localhost:{effectivePorts.DashboardHttpPort}",
                    ["environmentVariables"] = new JsonObject
                    {
                        [KnownConfigNames.DashboardOtlpGrpcEndpointUrl] = $"http://localhost:{effectivePorts.OtlpHttpPort}",
                        [KnownConfigNames.ResourceServiceEndpointUrl] = $"http://localhost:{effectivePorts.ResourceServiceHttpPort}",
                        [KnownConfigNames.AllowUnsecuredTransport] = "true"
                    }
                }
            };
        }

        File.WriteAllText(configPath, JsonSerializer.Serialize(settings, JsonSourceGenerationContext.RelaxedEscaping.JsonObject));

        InteractionService.DisplayMessage(KnownEmojis.CheckMarkButton, string.Format(CultureInfo.CurrentCulture, InitCommandStrings.CreatedFile, AspireConfigFile.FileName));
        return (CliExitCodes.Success, effectivePorts);
    }

    /// <summary>
    /// Resolves the <see cref="CliExecutionContext.IdentityChannel"/> into a channel name
    /// safe to persist into <c>aspire.config.json#channel</c>. Returns <c>null</c> when:
    /// <list type="bullet">
    /// <item><description>The identity is empty (no channel context).</description></item>
    /// <item><description>The identity doesn't match any registered channel (e.g. <c>local</c>,
    /// <c>staging</c> on a CLI without the staging feature flag, or a stale <c>pr-{N}</c> on a
    /// machine without the matching hive). Persisting these would pin a name no PSM rule can
    /// satisfy and zero out polyglot <c>aspire add</c> discovery via
    /// <c>IntegrationPackageSearchService.cs</c> line 28-30.</description></item>
    /// <item><description>The matched channel is <see cref="PackageChannelType.Implicit"/>.
    /// In production the only Implicit channel created by <c>PackagingService.GetChannelsAsync</c>
    /// is <c>default</c> (the unscoped nuget.org aggregator), which no CLI identity ever
    /// resolves to — this branch exists defensively in case a future <c>PackagingService</c>
    /// adds another Implicit channel whose name happens to collide with a CLI identity.</description></item>
    /// <item><description>The matched channel is <see cref="PackageChannelNames.Stable"/>.
    /// Stable uses the same public-feed package set users get by default, but pinning it
    /// per-project makes package discovery use only the synthetic NuGet.org config and
    /// hides packages from ambient private feeds.</description></item>
    /// </list>
    /// Mirrors the resolution logic in <c>NewCommand.cs:316-402</c> and the warning in
    /// <c>ScaffoldingService.cs:84-92</c> against falling back to <c>IdentityChannel</c> blindly.
    /// </summary>
    private async Task<string?> ResolvePersistableChannelNameAsync(CancellationToken cancellationToken)
    {
        var identityChannel = _executionContext.IdentityChannel;
        if (string.IsNullOrWhiteSpace(identityChannel))
        {
            return null;
        }

        IEnumerable<PackageChannel> channels;
        try
        {
            channels = await _packagingService.GetChannelsAsync(cancellationToken, identityChannel);
        }
        catch (Exception)
        {
            // Channel discovery is best-effort here — failing to resolve must not break
            // `aspire init`. Skip persistence and let downstream commands re-resolve.
            return null;
        }

        var match = channels.FirstOrDefault(c => string.Equals(c.Name, identityChannel, StringComparisons.ChannelName));
        return match?.ShouldPersistChannelName() is true ? match.Name : null;
    }

    /// <summary>
    /// Best-effort read of the persisted channel from <c>aspire.config.json</c> in
    /// <paramref name="directory"/>. Used by the polyglot path to avoid overwriting a
    /// user-edited value via <c>ScaffoldingService</c>, which writes the context channel
    /// unconditionally. Returns <c>null</c> if the file is absent, unparseable, or has
    /// no <c>channel</c> key — those cases all mean "no user-set value to preserve".
    /// </summary>
    private static string? TryLoadExistingChannel(DirectoryInfo directory)
    {
        try
        {
            return AspireConfigFile.Load(directory.FullName)?.Channel;
        }
        catch
        {
            return null;
        }
    }

    // Best-effort extraction of the dashboard / OTLP / resource service ports from an
    // existing `profiles` section. Returns true only if every expected port can be parsed
    // from the https + http profiles, otherwise the caller falls back to fresh ports.
    private static bool TryReadAppHostProfilePorts(JsonObject profiles, out AppHostProfilePorts ports)
    {
        ports = default;

        if (profiles["https"] is not JsonObject https || profiles["http"] is not JsonObject http)
        {
            return false;
        }

        var httpsEnv = https["environmentVariables"] as JsonObject;
        var httpEnv = http["environmentVariables"] as JsonObject;
        if (httpsEnv is null || httpEnv is null)
        {
            return false;
        }

        if (!TryParseHostPort(https["applicationUrl"]?.GetValue<string>(), "https", out var dashboardHttps)
            || !TryParseHostPort(http["applicationUrl"]?.GetValue<string>(), "http", out var dashboardHttp)
            || !TryParseHostPort(httpsEnv[KnownConfigNames.DashboardOtlpGrpcEndpointUrl]?.GetValue<string>(), "https", out var otlpHttps)
            || !TryParseHostPort(httpEnv[KnownConfigNames.DashboardOtlpGrpcEndpointUrl]?.GetValue<string>(), "http", out var otlpHttp)
            || !TryParseHostPort(httpsEnv[KnownConfigNames.ResourceServiceEndpointUrl]?.GetValue<string>(), "https", out var resourceServiceHttps)
            || !TryParseHostPort(httpEnv[KnownConfigNames.ResourceServiceEndpointUrl]?.GetValue<string>(), "http", out var resourceServiceHttp))
        {
            return false;
        }

        ports = new AppHostProfilePorts(
            DashboardHttpsPort: dashboardHttps,
            DashboardHttpPort: dashboardHttp,
            OtlpHttpsPort: otlpHttps,
            OtlpHttpPort: otlpHttp,
            ResourceServiceHttpsPort: resourceServiceHttps,
            ResourceServiceHttpPort: resourceServiceHttp);
        return true;
    }

    // Parses the first `<scheme>://host:<port>` segment from a (possibly semicolon-
    // separated) URL list. Returns false if no segment with the requested scheme is found
    // or the port can't be parsed.
    private static bool TryParseHostPort(string? value, string scheme, out int port)
    {
        port = 0;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        foreach (var raw in value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri))
            {
                continue;
            }

            if (string.Equals(uri.Scheme, scheme, StringComparison.OrdinalIgnoreCase) && uri.Port > 0)
            {
                port = uri.Port;
                return true;
            }
        }

        return false;
    }

    // Writes apphost.run.json next to the single-file AppHost so that
    // `dotnet run apphost.cs` (.NET file-based runner) picks up the dashboard / OTLP /
    // resource service launch profile env vars. Mirrors the structure shipped by the
    // aspire-apphost-singlefile MSBuild template. Skips if the file already exists.
    private void DropAppHostRunJson(DirectoryInfo directory, AppHostProfilePorts ports)
    {
        const string fileName = "apphost.run.json";
        var path = Path.Combine(directory.FullName, fileName);
        if (File.Exists(path))
        {
            return;
        }

        // Shape mirrors a Properties/launchSettings.json (the schema the .NET file-based
        // runner inherits for `[file].run.json`): a `profiles` map with `commandName: Project`
        // entries. The https / http pair gives `dotnet run apphost.cs` a working dashboard
        // URL plus the OTLP and resource-service endpoint env vars that DashboardOptionsValidator
        // requires — without these the AppHost crashes at startup (see #15986).
        var settings = new JsonObject
        {
            ["$schema"] = "https://json.schemastore.org/launchsettings.json",
            ["profiles"] = new JsonObject
            {
                ["https"] = new JsonObject
                {
                    ["commandName"] = "Project",
                    ["dotnetRunMessages"] = true,
                    ["launchBrowser"] = true,
                    ["applicationUrl"] = $"https://localhost:{ports.DashboardHttpsPort};http://localhost:{ports.DashboardHttpPort}",
                    ["environmentVariables"] = new JsonObject
                    {
                        ["ASPNETCORE_ENVIRONMENT"] = "Development",
                        ["DOTNET_ENVIRONMENT"] = "Development",
                        [KnownConfigNames.DashboardOtlpGrpcEndpointUrl] = $"https://localhost:{ports.OtlpHttpsPort}",
                        [KnownConfigNames.ResourceServiceEndpointUrl] = $"https://localhost:{ports.ResourceServiceHttpsPort}"
                    }
                },
                ["http"] = new JsonObject
                {
                    ["commandName"] = "Project",
                    ["dotnetRunMessages"] = true,
                    ["launchBrowser"] = true,
                    ["applicationUrl"] = $"http://localhost:{ports.DashboardHttpPort}",
                    ["environmentVariables"] = new JsonObject
                    {
                        ["ASPNETCORE_ENVIRONMENT"] = "Development",
                        ["DOTNET_ENVIRONMENT"] = "Development",
                        [KnownConfigNames.DashboardOtlpGrpcEndpointUrl] = $"http://localhost:{ports.OtlpHttpPort}",
                        [KnownConfigNames.ResourceServiceEndpointUrl] = $"http://localhost:{ports.ResourceServiceHttpPort}",
                        [KnownConfigNames.AllowUnsecuredTransport] = "true"
                    }
                }
            }
        };

        File.WriteAllText(path, JsonSerializer.Serialize(settings, JsonSourceGenerationContext.RelaxedEscaping.JsonObject));

        InteractionService.DisplayMessage(KnownEmojis.CheckMarkButton, string.Format(CultureInfo.CurrentCulture, InitCommandStrings.CreatedFile, fileName));
    }

}
