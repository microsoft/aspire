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
using Aspire.Hosting;
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

    private async Task<int> DropCSharpSingleFileSkeletonAsync(DirectoryInfo workingDirectory, CancellationToken cancellationToken)
    {
        var appHostPath = Path.Combine(workingDirectory.FullName, "apphost.cs");
        if (File.Exists(appHostPath))
        {
            InteractionService.DisplayMessage(KnownEmojis.CheckMarkButton, "apphost.cs already exists — skipping.");
            return ExitCodeConstants.Success;
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

        // Ensure the workspace has a NuGet.config that exposes the configured channel's
        // package sources. This is required so MSBuild can resolve
        // `#:sdk Aspire.AppHost.Sdk@<version>` from the apphost.cs SDK directive — both
        // for `aspire add` (`dotnet package add --file apphost.cs`) and for
        // `dotnet run --file apphost.cs`. Without it, any non-stable channel (PR/run
        // hives, locally-built `local-*`/`dev-*` hives, the staging channel, etc.)
        // is invisible and SDK resolution fails. Mirrors how `aspire new` handles
        // template output via the same shared service; `NuGetConfigMerger` underneath
        // creates a new file or merges missing sources into an existing one, so adding
        // hives later is handled the same way as for templates.
        var createdNuGetConfig = await _templateNuGetConfigService.CreateOrUpdateNuGetConfigWithoutPromptAsync(
            channelName: null,
            outputPath: workingDirectory.FullName,
            cancellationToken).ConfigureAwait(false);
        if (createdNuGetConfig)
        {
            // Use a confirmation message that does NOT contain the literal substring
            // "NuGet.config" — the AspireInitAsync E2E helper false-matches that
            // substring as a Y/n prompt and gets out of sync with the real prompts.
            InteractionService.DisplayMessage(KnownEmojis.CheckMarkButton, "Created package sources file");
        }

        // Generate one set of ports so aspire.config.json (used by `aspire run`) and
        // apphost.run.json (used by `dotnet run apphost.cs`) agree on the dashboard /
        // OTLP / resource service endpoints.
        var ports = AppHostProfilePortGenerator.Generate(Random.Shared);

        // Drop aspire.config.json. The returned ports are whatever ended up effective
        // in aspire.config.json — newly generated, or pre-existing if the file already
        // had a `profiles` section. Use the SAME ports for apphost.run.json so the two
        // files always agree on dashboard / OTLP / resource service endpoints.
        var (configResult, effectivePorts) = DropAspireConfig(workingDirectory, "apphost.cs", language: null, ports);
        if (configResult != ExitCodeConstants.Success)
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

        return ExitCodeConstants.Success;
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

        // Resolve the channel-aware template package version + feed mapping. The running
        // CLI binary's identity channel (CliExecutionContext.Channel — stable, staging,
        // daily, pr-<N>, or local) drives the selection so a developer scaffolding with a
        // pr-<N> CLI gets a project wired to the matching pr-<N> hive. PR hives are
        // intentionally excluded — init should produce the same template on every machine
        // for a given CLI build.
        TemplatePackageSelection selection;
        try
        {
            var query = new TemplatePackageQuery(
                ChannelOverride: _executionContext.Channel,
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

    private (int ExitCode, AppHostProfilePorts EffectivePorts) DropAspireConfig(DirectoryInfo directory, string appHostPath, string? language, AppHostProfilePorts? ports = null)
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
                    return (ExitCodeConstants.FailedToCreateNewProject, default);
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

        InteractionService.DisplayMessage(KnownEmojis.CheckMarkButton, $"Created {AspireConfigFile.FileName}");
        return (ExitCodeConstants.Success, effectivePorts);
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

        InteractionService.DisplayMessage(KnownEmojis.CheckMarkButton, $"Created {fileName}");
    }

}
