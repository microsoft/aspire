// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.Globalization;
using Aspire.Cli.Certificates;
using Aspire.Cli.Commands;
using Aspire.Cli.Configuration;
using Aspire.Cli.DotNet;
using Aspire.Cli.Interaction;
using Aspire.Cli.NuGet;
using Aspire.Cli.Projects;
using Aspire.Cli.Resources;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Utils;

namespace Aspire.Cli.Templating;

internal class DotNetTemplateFactory(
    IInteractionService interactionService,
    IDotNetCliRunner runner,
    ICertificateService certificateService,
    INewCommandPrompter prompter,
    CliExecutionContext executionContext,
    IDotNetSdkInstaller sdkInstaller,
    IFeatures features,
    AspireCliTelemetry telemetry,
    ICliHostEnvironment hostEnvironment,
    TemplateNuGetConfigService templateNuGetConfigService,
    IAppHostInfoResolver appHostInfoResolver,
    IEnvironment environment)
    : ITemplateFactory
{
    private const string NoTestFramework = "None";

    // Template-specific options
    private readonly Option<bool?> _localhostTldOption = new("--localhost-tld")
    {
        Description = TemplatingStrings.UseLocalhostTld_Description,
        DefaultValueFactory = _ => false
    };
    private readonly Option<bool?> _useRedisCacheOption = new("--use-redis-cache")
    {
        Description = TemplatingStrings.UseRedisCache_Description,
        DefaultValueFactory = _ => false
    };
    private readonly Option<string?> _testFrameworkOption = new("--test-framework")
    {
        Description = TemplatingStrings.PromptForTFMOptions_Description
    };
    private readonly Option<string?> _xunitVersionOption = new("--xunit-version")
    {
        Description = TemplatingStrings.EnterXUnitVersion_Description
    };
    private readonly Option<FileInfo?> _appHostOption = new("--apphost")
    {
        Description = TemplatingStrings.AppHostProjectOptionDescription
    };

    public IEnumerable<ITemplate> GetTemplates()
    {
        if (!IsDotNetOnPath())
        {
            return [];
        }

        return GetTemplatesCore(ShouldShowAllTemplates());
    }

    public async Task<IEnumerable<ITemplate>> GetTemplatesAsync(CancellationToken cancellationToken = default)
    {
        if (!await IsDotNetSdkAvailableAsync(cancellationToken))
        {
            return [];
        }

        return GetTemplatesCore(ShouldShowAllTemplates());
    }

    public async Task<IEnumerable<ITemplate>> GetInitTemplatesAsync(CancellationToken cancellationToken = default)
    {
        if (!await IsDotNetSdkAvailableAsync(cancellationToken))
        {
            return [];
        }

        return [CreateSingleFileTemplate()];
    }

    private async Task<bool> IsDotNetSdkAvailableAsync(CancellationToken cancellationToken)
    {
        try
        {
            var check = await sdkInstaller.CheckAsync(cancellationToken);
            return check.Success;
        }
        catch
        {
            return false;
        }
    }

    private bool IsDotNetOnPath()
    {
        // Check the private SDK installation first.
        var sdkInstallPath = Path.Combine(executionContext.SdksDirectory.FullName, "dotnet", DotNetSdkInstaller.MinimumSdkVersion);
        if (Directory.Exists(sdkInstallPath))
        {
            return true;
        }

        // Fall back to checking for dotnet on the system PATH.
        var dotnetFileName = environment.IsWindows() ? "dotnet.exe" : "dotnet";
        var pathVariable = environment.GetEnvironmentVariable("PATH") ?? string.Empty;

        foreach (var directory in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                if (File.Exists(Path.Combine(directory, dotnetFileName)))
                {
                    return true;
                }
            }
            catch
            {
                // Skip directories that can't be accessed.
            }
        }

        return false;
    }

    private bool ShouldShowAllTemplates()
    {
        // This process-scoped switch lets tooling expose gated templates for one CLI invocation
        // without rewriting user settings or changing configuration-provider precedence.
        return features.IsFeatureEnabled(KnownFeatures.ShowAllTemplates, false) ||
            bool.TryParse(environment.GetEnvironmentVariable(CliConfigNames.ShowAllTemplates), out var showAllTemplates) &&
            showAllTemplates;
    }

    private IEnumerable<ITemplate> GetTemplatesCore(bool showAllTemplates)
    {
        yield return new CallbackTemplate(
            "aspire-starter",
            TemplatingStrings.AspireStarter_Description,
            (ctx, projectName) => OutputPathHelper.GetUniqueDefaultOutputPath(projectName, ctx.WorkingDirectory.FullName),
            ApplyExtraAspireStarterOptions,
            (template, inputs, parseResult, ct) => ApplyTemplateAsync(template, inputs, parseResult, PromptForExtraAspireStarterOptionsAsync, ct),
            languageId: KnownLanguageId.CSharp
            );

        yield return new CallbackTemplate(
            "aspire-ts-cs-starter",
            TemplatingStrings.AspireJsFrontendStarter_Description,
            (ctx, projectName) => OutputPathHelper.GetUniqueDefaultOutputPath(projectName, ctx.WorkingDirectory.FullName),
            ApplyExtraAspireJsFrontendStarterOptions,
            (template, inputs, parseResult, ct) => ApplyTemplateAsync(template, inputs, parseResult, PromptForExtraAspireJsFrontendStarterOptionsAsync, ct),
            languageId: KnownLanguageId.CSharp
            );

        if (showAllTemplates)
        {
            yield return new CallbackTemplate(
                KnownTemplateId.DotNetEmptyAppHost,
                TemplatingStrings.AspireEmptyDotNetTemplate_Description,
                (ctx, projectName) => OutputPathHelper.GetUniqueDefaultOutputPath(projectName, ctx.WorkingDirectory.FullName),
                ApplyDevLocalhostTldOption,
                ApplyTemplateWithNoExtraArgsAsync,
                languageId: KnownLanguageId.CSharp,
                isEmpty: true
                );

            yield return new CallbackTemplate(
                "aspire-apphost",
                TemplatingStrings.AspireAppHost_Description,
                (ctx, projectName) => OutputPathHelper.GetUniqueDefaultOutputPath(projectName, ctx.WorkingDirectory.FullName),
                ApplyDevLocalhostTldOption,
                ApplyTemplateWithNoExtraArgsAsync,
                languageId: KnownLanguageId.CSharp
                );

            yield return new CallbackTemplate(
                "aspire-servicedefaults",
                TemplatingStrings.AspireServiceDefaults_Description,
                (ctx, projectName) => OutputPathHelper.GetUniqueDefaultOutputPath(projectName, ctx.WorkingDirectory.FullName),
                _ => { },
                ApplyTemplateWithNoExtraArgsAsync,
                languageId: KnownLanguageId.CSharp
                );
        }

        if (!showAllTemplates)
        {
            yield break;
        }

        // Folded into the last yielded template.
        var msTestTemplate = new CallbackTemplate(
            "aspire-mstest",
            TemplatingStrings.AspireMSTest_Description,
            (ctx, projectName) => OutputPathHelper.GetUniqueDefaultOutputPath(projectName, ctx.WorkingDirectory.FullName),
            _ => { },
            ApplyTemplateWithNoExtraArgsAsync,
            languageId: KnownLanguageId.CSharp
            );

        // Folded into the last yielded template.
        var nunitTemplate = new CallbackTemplate(
            "aspire-nunit",
            TemplatingStrings.AspireNUnit_Description,
            (ctx, projectName) => OutputPathHelper.GetUniqueDefaultOutputPath(projectName, ctx.WorkingDirectory.FullName),
            _ => { },
            ApplyTemplateWithNoExtraArgsAsync,
            languageId: KnownLanguageId.CSharp
            );

        // Folded into the last yielded template.
        var xunitTemplate = new CallbackTemplate(
            "aspire-xunit",
            TemplatingStrings.AspireXUnit_Description,
            (ctx, projectName) => OutputPathHelper.GetUniqueDefaultOutputPath(projectName, ctx.WorkingDirectory.FullName),
            _ => { },
            (template, inputs, parseResult, ct) => ApplyTemplateAsync(template, inputs, parseResult, PromptForExtraAspireXUnitOptionsAsync, ct),
            languageId: KnownLanguageId.CSharp
            );

        // Prepends a test framework selection step then calls the
        // underlying test template.
        yield return new CallbackTemplate(
            "aspire-test",
            TemplatingStrings.IntegrationTestsTemplate_Description,
            (ctx, projectName) => OutputPathHelper.GetUniqueDefaultOutputPath(projectName, ctx.WorkingDirectory.FullName),
            command => AddOptionIfMissing(command, _appHostOption),
            async (template, inputs, parseResult, ct) =>
            {
                var appHostProject = parseResult.GetValue(_appHostOption);
                if (appHostProject is { Exists: false })
                {
                    interactionService.DisplayError(InteractionServiceStrings.ProjectOptionDoesntExist);
                    return new TemplateResult(CliExitCodes.FailedToCreateNewProject);
                }

                var testTemplate = await prompter.PromptForTemplateAsync(
                    [msTestTemplate, xunitTemplate, nunitTemplate],
                    ct
                );

                var testCallbackTemplate = (CallbackTemplate)testTemplate;
                Func<ParseResult, CancellationToken, Task<string[]>> extraArgsCallback = testCallbackTemplate.Name == "aspire-xunit"
                    ? PromptForExtraAspireXUnitOptionsAsync
                    : (_, _) => Task.FromResult(Array.Empty<string>());

                return await ApplyTemplateAsync(testCallbackTemplate, inputs, parseResult, extraArgsCallback, ct, appHostProject);
            },
            languageId: KnownLanguageId.CSharp);
    }

    private CallbackTemplate CreateSingleFileTemplate()
    {
        return new CallbackTemplate(
            "aspire-apphost-singlefile",
            TemplatingStrings.AspireAppHostSingleFile_Description,
            (ctx, projectName) => OutputPathHelper.GetUniqueDefaultOutputPath(projectName, ctx.WorkingDirectory.FullName),
            ApplyDevLocalhostTldOption,
            (template, inputs, parseResult, ct) => ApplySingleFileTemplate(template, inputs, parseResult, PromptForExtraAspireSingleFileOptionsAsync, ct),
            languageId: KnownLanguageId.CSharp,
            isEmpty: true
            );
    }

    private async Task<string[]> PromptForExtraAspireStarterOptionsAsync(ParseResult result, CancellationToken cancellationToken)
    {
        var extraArgs = new List<string>();

        await PromptForDevLocalhostTldOptionAsync(result, extraArgs, cancellationToken);
        await PromptForRedisCacheOptionAsync(result, extraArgs, cancellationToken);
        await PromptForTestFrameworkOptionsAsync(result, extraArgs, cancellationToken);

        return extraArgs.ToArray();
    }

    private async Task<string[]> PromptForExtraAspireSingleFileOptionsAsync(ParseResult result, CancellationToken cancellationToken)
    {
        var extraArgs = new List<string>();

        await PromptForDevLocalhostTldOptionAsync(result, extraArgs, cancellationToken);

        return extraArgs.ToArray();
    }

    private async Task<string[]> PromptForExtraAspireJsFrontendStarterOptionsAsync(ParseResult result, CancellationToken cancellationToken)
    {
        var extraArgs = new List<string>();

        await PromptForDevLocalhostTldOptionAsync(result, extraArgs, cancellationToken);
        await PromptForRedisCacheOptionAsync(result, extraArgs, cancellationToken);

        return extraArgs.ToArray();
    }

    private async Task<string[]> PromptForExtraAspireXUnitOptionsAsync(ParseResult result, CancellationToken cancellationToken)
    {
        var extraArgs = new List<string>();

        await PromptForXUnitVersionOptionsAsync(result, extraArgs, cancellationToken);

        return extraArgs.ToArray();
    }

    private async Task PromptForDevLocalhostTldOptionAsync(ParseResult result, List<string> extraArgs, CancellationToken cancellationToken)
    {
        var binding = PromptBinding.CreateBoolConfirm(result, _localhostTldOption, defaultValue: false);

        var useLocalhostTld = await interactionService.PromptConfirmAsync(
            TemplatingStrings.UseLocalhostTld_Prompt,
            binding: binding,
            cancellationToken: cancellationToken);

        if (useLocalhostTld)
        {
            interactionService.DisplayMessage(KnownEmojis.CheckMarkButton, TemplatingStrings.UseLocalhostTld_UsingLocalhostTld);
            extraArgs.Add("--localhost-tld");
        }
    }

    private async Task PromptForRedisCacheOptionAsync(ParseResult result, List<string> extraArgs, CancellationToken cancellationToken)
    {
        var binding = PromptBinding.CreateBoolConfirm(result, _useRedisCacheOption, interactiveDefault: true, nonInteractiveDefault: false);

        var useRedisCache = await interactionService.PromptConfirmAsync(
            TemplatingStrings.UseRedisCache_Prompt,
            binding: binding,
            cancellationToken: cancellationToken);

        if (useRedisCache)
        {
            interactionService.DisplayMessage(KnownEmojis.CheckMarkButton, TemplatingStrings.UseRedisCache_UsingRedisCache);
            extraArgs.Add("--use-redis-cache");
        }
    }

    private async Task PromptForTestFrameworkOptionsAsync(ParseResult result, List<string> extraArgs, CancellationToken cancellationToken)
    {
        var binding = PromptBinding.Create(result, _testFrameworkOption);
        var (wasProvided, _) = binding.Resolve();

        if (!wasProvided)
        {
            if (!hostEnvironment.SupportsInteractiveInput)
            {
                return;
            }

            var createTestProject = await interactionService.PromptConfirmAsync(
                TemplatingStrings.PromptForTFMOptions_Prompt,
                binding: PromptBinding.CreateDefault(false),
                cancellationToken: cancellationToken);

            if (!createTestProject)
            {
                return;
            }
        }

        var testFramework = await interactionService.PromptForSelectionAsync(
            TemplatingStrings.PromptForTFM_Prompt,
            ["MSTest", "NUnit", "xUnit.net", NoTestFramework],
            choice => string.Equals(choice, NoTestFramework, StringComparison.Ordinal) ? TemplatingStrings.None : choice,
            binding: binding,
            cancellationToken: cancellationToken);

        if (!string.Equals(testFramework, NoTestFramework, StringComparisons.CliInputOrOutput))
        {
            if (string.Equals(testFramework, "xUnit.net", StringComparison.OrdinalIgnoreCase))
            {
                await PromptForXUnitVersionOptionsAsync(result, extraArgs, cancellationToken);
            }

            interactionService.DisplayMessage(KnownEmojis.CheckMarkButton, string.Format(CultureInfo.CurrentCulture, TemplatingStrings.PromptForTFM_UsingForTesting, testFramework));

            extraArgs.Add("--test-framework");
            extraArgs.Add(testFramework);
        }
    }

    private async Task PromptForXUnitVersionOptionsAsync(ParseResult result, List<string> extraArgs, CancellationToken cancellationToken)
    {
        var binding = PromptBinding.Create(result, _xunitVersionOption, "v3mtp");

        var xunitVersion = await interactionService.PromptForSelectionAsync(
            TemplatingStrings.EnterXUnitVersion_Prompt,
            ["v2", "v3", "v3mtp"],
            choice => choice,
            binding: binding,
            cancellationToken: cancellationToken);

        extraArgs.Add("--xunit-version");
        extraArgs.Add(xunitVersion);
    }

    private void ApplyExtraAspireStarterOptions(Command command)
    {
        ApplyDevLocalhostTldOption(command);

        AddOptionIfMissing(command, _useRedisCacheOption);
        AddOptionIfMissing(command, _testFrameworkOption);
        AddOptionIfMissing(command, _xunitVersionOption);
    }

    private void ApplyExtraAspireJsFrontendStarterOptions(Command command)
    {
        ApplyDevLocalhostTldOption(command);

        AddOptionIfMissing(command, _useRedisCacheOption);
    }

    private void ApplyDevLocalhostTldOption(Command command)
    {
        AddOptionIfMissing(command, _localhostTldOption);
    }

    private static void AddOptionIfMissing(Command command, Option option)
    {
        if (!command.Options.Contains(option))
        {
            command.Options.Add(option);
        }
    }

    private async Task<TemplateResult> ApplyTemplateWithNoExtraArgsAsync(CallbackTemplate template, TemplateInputs inputs, ParseResult parseResult, CancellationToken cancellationToken)
    {
        return await ApplyTemplateAsync(template, inputs, parseResult, (_, _) => Task.FromResult(Array.Empty<string>()), cancellationToken);
    }

    private async Task<TemplateResult> ApplySingleFileTemplate(CallbackTemplate template, TemplateInputs inputs, ParseResult parseResult, Func<ParseResult, CancellationToken, Task<string[]>> extraArgsCallback, CancellationToken cancellationToken)
    {
        // For single-file templates invoked via InitCommand, use the working directory as the output
        if (inputs.UseWorkingDirectory)
        {
            return await ApplyTemplateAsync(
                template,
                inputs,
                executionContext.WorkingDirectory.Name,
                executionContext.WorkingDirectory.FullName,
                parseResult,
                extraArgsCallback,
                cancellationToken
                );
        }
        else
        {
            return await ApplyTemplateAsync(
                template,
                inputs,
                parseResult,
                extraArgsCallback,
                cancellationToken
                );
        }
    }

    private async Task<TemplateResult> ApplyTemplateAsync(
        CallbackTemplate template,
        TemplateInputs inputs,
        ParseResult parseResult,
        Func<ParseResult, CancellationToken, Task<string[]>> extraArgsCallback,
        CancellationToken cancellationToken,
        FileInfo? appHostProject = null)
    {
        if (!await SdkInstallHelper.EnsureSdkInstalledAsync(sdkInstaller, interactionService, telemetry, cancellationToken: cancellationToken))
        {
            return new TemplateResult(CliExitCodes.SdkNotInstalled);
        }

        var name = await GetProjectNameAsync(inputs, template.Name, parseResult, cancellationToken);
        var outputPath = await GetOutputPathAsync(inputs, template.PathDeriver, name, parseResult, cancellationToken);

        if (outputPath is null)
        {
            return new TemplateResult(CliExitCodes.FailedToCreateNewProject);
        }

        return await ApplyTemplateAsync(template, inputs, name, outputPath, parseResult, extraArgsCallback, cancellationToken, appHostProject);
    }

    private async Task<TemplateResult> ApplyTemplateAsync(
        CallbackTemplate template,
        TemplateInputs inputs,
        string name,
        string outputPath,
        ParseResult parseResult,
        Func<ParseResult, CancellationToken, Task<string[]>> extraArgsCallback,
        CancellationToken cancellationToken,
        FileInfo? appHostProject = null)
    {
        try
        {
            // Resolve the template package first, matching the pre-extraction order in
            // release/13.3. Surfacing channel/version errors before prompting for extra args
            // avoids discarding answers the user just gave.
            var query = new TemplatePackageQuery(
                RequestedChannel: inputs.Channel,
                VersionOverride: inputs.Version,
                SourceOverride: inputs.Source,
                IncludePrHives: true);

            var selectedTemplateDetails = await templateNuGetConfigService.ResolveTemplatePackageAsync(query, cancellationToken);

            // Some templates have additional arguments that need to be applied to the `dotnet new` command
            // when it is executed. This callback will get those arguments and potentially prompt for them.
            var extraArgs = await extraArgsCallback(parseResult, cancellationToken);
            if (appHostProject is not null)
            {
                var appHostInfo = await appHostInfoResolver.GetAppHostInfoAsync(appHostProject, cancellationToken);
                var appHostTargetFramework = GetAppHostTargetFramework(appHostInfo);

                extraArgs =
                [
                    .. extraArgs,
                    "--WithAppHostReference",
                    "true",
                    "--AppHostProjectPath",
                    EscapeMSBuildItemValue(Path.GetRelativePath(outputPath, appHostProject.FullName)),
                    "--AppHostProjectName",
                    Path.GetFileNameWithoutExtension(appHostProject.Name)
                ];

                if (appHostTargetFramework is not null)
                {
                    extraArgs = [.. extraArgs, "--AppHostTargetFramework", appHostTargetFramework];
                }
            }

            var installOutcome = await templateNuGetConfigService.InstallTemplatePackageAsync(
                selectedTemplateDetails,
                sourceOverride: inputs.Source,
                runner,
                TemplatingStrings.GettingTemplates,
                statusEmoji: KnownEmojis.Ice,
                cancellationToken);

            if (installOutcome.ExitCode != 0)
            {
                interactionService.DisplayLines(installOutcome.OutputLines);
                interactionService.DisplayError(string.Format(CultureInfo.CurrentCulture, TemplatingStrings.TemplateInstallationFailed, installOutcome.ExitCode));
                return new TemplateResult(CliExitCodes.FailedToInstallTemplates);
            }

            interactionService.DisplayMessage(KnownEmojis.Package, string.Format(CultureInfo.CurrentCulture, TemplatingStrings.UsingProjectTemplatesVersion, installOutcome.TemplateVersion));

            var newProjectCollector = new OutputCollector();
            var newProjectExitCode = await interactionService.ShowStatusAsync(
                TemplatingStrings.CreatingNewProject,
                async () =>
                {
                    var options = new ProcessInvocationOptions()
                    {
                        StandardOutputCallback = newProjectCollector.AppendOutput,
                        StandardErrorCallback = newProjectCollector.AppendOutput,
                    };

                    var result = await runner.NewProjectAsync(
                                template.Name,
                                name,
                                outputPath,
                                extraArgs,
                                options,
                                cancellationToken);

                    return result;
                }, emoji: KnownEmojis.Rocket);

            if (newProjectExitCode != 0)
            {
                // Exit code 73 indicates that the output directory already contains files from a previous project
                // See: https://github.com/microsoft/aspire/issues/9685
                if (newProjectExitCode == 73)
                {
                    interactionService.DisplayError(TemplatingStrings.ProjectAlreadyExists);
                    return new TemplateResult(CliExitCodes.FailedToCreateNewProject);
                }

                interactionService.DisplayLines(newProjectCollector.GetLines());
                interactionService.DisplayError(string.Format(CultureInfo.CurrentCulture, TemplatingStrings.ProjectCreationFailed, newProjectExitCode));
                return new TemplateResult(CliExitCodes.FailedToCreateNewProject);
            }

            // Trust certificates (result not used since we're not launching an AppHost)
            _ = await certificateService.EnsureCertificatesTrustedAsync(cancellationToken);

            // Persist the resolved channel and exact SDK version into the scaffolded project's
            // aspire.config.json for channels whose name should be pinned (pr-<N>, daily, staging,
            // local). Local hives can contain packages from several builds, so the version keeps
            // later integration discovery aligned with the template package selected here.
            // Without this pin, `aspire update` on the new project skips the local-config
            // step in its channel-resolution precedence and falls through to either an
            // interactive prompt (when hives exist) or the Implicit/nuget.org channel —
            // silently moving a project scaffolded by a PR or daily CLI onto stable/nuget.org.
            // The `stable` channel is intentionally NOT persisted (ShouldPersistChannelName
            // excludes it): its packages live on nuget.org, so `aspire add`/`aspire restore`
            // continue to use the ambient NuGet config without a per-project pin. Mirrors the
            // TypeScript starter behavior in CliTemplateFactory.TypeScriptStarterTemplate.
            if (selectedTemplateDetails.Channel.ShouldPersistChannelName())
            {
                var config = AspireConfigFile.LoadOrCreate(outputPath);
                config.Channel = selectedTemplateDetails.Channel.Name;
                config.SdkVersion = selectedTemplateDetails.Package.Version;
                config.Save(outputPath);
            }

            // For channels that route Aspire packages to a custom feed, optionally create or update
            // a NuGet.config. If none exists in the current working directory, create one in the
            // newly created project's output directory. The `stable` channel is skipped inside
            // PromptToCreateOrUpdateNuGetConfigAsync (ShouldCreateNuGetConfig) because its packages
            // are on nuget.org and a <clear/>-based config would clobber the user's ambient sources.
            if (!await TemplateNuGetConfigService.CreateOrUpdateNuGetConfigForSourceOverrideAsync(inputs.Source, selectedTemplateDetails.Channel, outputPath, cancellationToken, executionContext.NuGetServiceIndexOverride))
            {
                await templateNuGetConfigService.PromptToCreateOrUpdateNuGetConfigAsync(selectedTemplateDetails.Channel, outputPath, cancellationToken);
            }

            interactionService.DisplaySuccess(string.Format(CultureInfo.CurrentCulture, TemplatingStrings.ProjectCreatedSuccessfully, outputPath));

            return new TemplateResult(
                CliExitCodes.Success,
                outputPath,
                appHostProject is null ? null : Path.Combine(outputPath, "IntegrationTest1.cs"));
        }
        catch (OperationCanceledException)
        {
            return new TemplateResult(CliExitCodes.Cancelled);
        }
        catch (Exceptions.ChannelNotFoundException ex)
        {
            interactionService.DisplayError(ex.Message);
            return new TemplateResult(CliExitCodes.FailedToCreateNewProject);
        }
        catch (EmptyChoicesException ex)
        {
            interactionService.DisplayError(ex.Message);
            return new TemplateResult(CliExitCodes.FailedToCreateNewProject);
        }
        catch (NuGetPackageCacheException ex)
        {
            // Surface NuGet feed search failures (offline, inaccessible feed, etc.) with a friendly error
            // instead of letting them bubble up to the top-level "unexpected error" handler. The pre-extraction
            // init code went straight to `dotnet new install` and never invoked a NuGet search, so this catch
            // restores parity with the prior init failure mode for these scenarios.
            interactionService.DisplayError(ex.Message);
            return new TemplateResult(CliExitCodes.FailedToCreateNewProject);
        }
    }

    private static string? GetAppHostTargetFramework(AppHostProjectInfo appHostInfo)
    {
        if (appHostInfo.ExitCode != 0)
        {
            return null;
        }

        var targetFrameworks = appHostInfo.TargetFrameworks?.Split(
            ';',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (targetFrameworks is { Length: > 0 })
        {
            // Target the first declared framework so the generated single-target test project
            // matches an AppHost inner build without guessing which SDK is preferred or installed.
            return targetFrameworks[0];
        }

        return string.IsNullOrWhiteSpace(appHostInfo.TargetFramework)
            ? null
            : appHostInfo.TargetFramework.Trim();
    }

    private static string EscapeMSBuildItemValue(string value)
    {
        // MSBuild item Include values use %-escaped sequences. Escape existing '%' first so a literal
        // value like "foo%3Bbar" remains literal instead of being decoded into "foo;bar".
        return value
            .Replace("%", "%25", StringComparison.Ordinal)
            .Replace("$", "%24", StringComparison.Ordinal)
            .Replace("@", "%40", StringComparison.Ordinal)
            .Replace("'", "%27", StringComparison.Ordinal)
            .Replace(";", "%3B", StringComparison.Ordinal)
            .Replace("?", "%3F", StringComparison.Ordinal)
            .Replace("*", "%2A", StringComparison.Ordinal)
            .Replace("(", "%28", StringComparison.Ordinal)
            .Replace(")", "%29", StringComparison.Ordinal);
    }

    private async Task<string> GetProjectNameAsync(TemplateInputs inputs, string templateName, ParseResult parseResult, CancellationToken cancellationToken)
    {
        if (inputs.Name is not { } name || !ProjectNameValidator.IsProjectNameValid(name))
        {
            var defaultName = templateName;
            name = await prompter.PromptForProjectNameAsync(defaultName, parseResult, cancellationToken);
        }

        return name;
    }

    private async Task<string?> GetOutputPathAsync(TemplateInputs inputs, Func<CliExecutionContext, string, string> pathDeriver, string projectName, ParseResult parseResult, CancellationToken cancellationToken)
    {
        var isExtensionHost = ExtensionHelper.IsExtensionHost(interactionService, out _, out _);
        var createProjectNameSubdirectory = await OutputPathHelper.PromptExtensionCreateProjectNameSubdirectoryAsync(
            interactionService,
            isExtensionHost,
            inputs.Output is not null,
            projectName,
            cancellationToken);

        var outputPathResolver = OutputPathHelper.CreateProjectNameSubdirectoryOutputPathResolver(createProjectNameSubdirectory, projectName);
        return await OutputPathHelper.ResolveOutputPathAsync(
            inputs.Output,
            executionContext.WorkingDirectory.FullName,
            async () =>
            {
                var defaultPath = pathDeriver(executionContext, projectName);
                var validator = OutputPathHelper.CreateOutputPathValidator(executionContext.WorkingDirectory.FullName);
                return await prompter.PromptForOutputPath(defaultPath, parseResult, validator, outputPathResolver, cancellationToken);
            },
            interactionService);
    }
}
