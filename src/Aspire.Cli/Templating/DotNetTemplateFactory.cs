// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.Globalization;
using Aspire.Cli.Certificates;
using Aspire.Cli.Configuration;
using Aspire.Cli.Commands;
using Aspire.Cli.DotNet;
using Aspire.Cli.Interaction;
using Aspire.Cli.Packaging;
using Aspire.Cli.Resources;
using Aspire.Cli.Utils;
using NuGetPackage = Aspire.Shared.NuGetPackageCli;
using Semver;
using Spectre.Console;

namespace Aspire.Cli.Templating;

internal class DotNetTemplateFactory(
    IInteractionService interactionService,
    IDotNetCliRunner runner,
    ICertificateService certificateService,
    IPackagingService packagingService,
    INewCommandPrompter prompter,
    CliExecutionContext executionContext,
    IFeatures features,
    IConfigurationService configurationService)
    : ITemplateFactory
{
    public IEnumerable<ITemplate> GetTemplates()
    {
        var showAllTemplates = features.IsFeatureEnabled(KnownFeatures.ShowAllTemplates, false);
        return GetTemplatesCore(showAllTemplates);
    }

    public IEnumerable<ITemplate> GetInitTemplates()
    {
        return GetTemplatesCore(showAllTemplates: true, nonInteractive: true);
    }

    private IEnumerable<ITemplate> GetTemplatesCore(bool showAllTemplates, bool nonInteractive = false)
    {
        yield return new CallbackTemplate(
            "aspire-starter",
            TemplatingStrings.AspireStarter_Description,
            projectName => $"./{projectName}",
            ApplyExtraAspireStarterOptions,
            nonInteractive
                ? ApplyTemplateWithNoExtraArgsAsync
                : (template, parseResult, ct) => ApplyTemplateAsync(template, parseResult, PromptForExtraAspireStarterOptionsAsync, ct)
            );

        yield return new CallbackTemplate(
            "aspire-ts-cs-starter",
            TemplatingStrings.AspireJsFrontendStarter_Description,
            projectName => $"./{projectName}",
            ApplyExtraAspireJsFrontendStarterOptions,
            nonInteractive
                ? ApplyTemplateWithNoExtraArgsAsync
                : (template, parseResult, ct) => ApplyTemplateAsync(template, parseResult, PromptForExtraAspireJsFrontendStarterOptionsAsync, ct)
            );

        // Single-file AppHost templates
        yield return new CallbackTemplate(
            "aspire-py-starter",
            TemplatingStrings.AspirePyStarter_Description,
            projectName => $"./{projectName}",
            ApplyDevLocalhostTldOption,
            nonInteractive
                ? ApplySingleFileTemplateWithNoExtraArgsAsync
                : (template, parseResult, ct) => ApplySingleFileTemplate(template, parseResult, PromptForExtraAspirePythonStarterOptionsAsync, ct)
            );

        yield return new CallbackTemplate(
            "aspire-apphost-singlefile",
            TemplatingStrings.AspireAppHostSingleFile_Description,
            projectName => $"./{projectName}",
            ApplyDevLocalhostTldOption,
            nonInteractive
                ? ApplySingleFileTemplateWithNoExtraArgsAsync
                : (template, parseResult, ct) => ApplySingleFileTemplate(template, parseResult, PromptForExtraAspireSingleFileOptionsAsync, ct)
            );

        if (showAllTemplates)
        {
            yield return new CallbackTemplate(
                "aspire",
                TemplatingStrings.AspireEmpty_Description,
                projectName => $"./{projectName}",
                ApplyDevLocalhostTldOption,
                ApplyTemplateWithNoExtraArgsAsync
                );

            yield return new CallbackTemplate(
                "aspire-apphost",
                TemplatingStrings.AspireAppHost_Description,
                projectName => $"./{projectName}",
                ApplyDevLocalhostTldOption,
                ApplyTemplateWithNoExtraArgsAsync
                );

            yield return new CallbackTemplate(
                "aspire-servicedefaults",
                TemplatingStrings.AspireServiceDefaults_Description,
                projectName => $"./{projectName}",
                _ => { },
                ApplyTemplateWithNoExtraArgsAsync
                );
        }

        // Folded into the last yieled template.
        var msTestTemplate = new CallbackTemplate(
            "aspire-mstest",
            TemplatingStrings.AspireMSTest_Description,
            projectName => $"./{projectName}",
            _ => { },
            ApplyTemplateWithNoExtraArgsAsync
            );

        // Folded into the last yielded template.
        var nunitTemplate = new CallbackTemplate(
            "aspire-nunit",
            TemplatingStrings.AspireNUnit_Description,
            projectName => $"./{projectName}",
            _ => { },
            ApplyTemplateWithNoExtraArgsAsync
            );

        // Folded into the last yielded template.
        var xunitTemplate = new CallbackTemplate(
            "aspire-xunit",
            TemplatingStrings.AspireXUnit_Description,
            projectName => $"./{projectName}",
            _ => { },
            nonInteractive
                ? ApplyTemplateWithNoExtraArgsAsync
                : (template, parseResult, ct) => ApplyTemplateAsync(template, parseResult, PromptForExtraAspireXUnitOptionsAsync, ct)
            );

        // Prepends a test framework selection step then calls the
        // underlying test template.
        if (showAllTemplates)
        {
            yield return new CallbackTemplate(
                "aspire-test",
                TemplatingStrings.IntegrationTestsTemplate_Description,
                projectName => $"./{projectName}",
                _ => { },
                async (template, parseResult, ct) =>
                {
                    var testTemplate = await prompter.PromptForTemplateAsync(
                        [msTestTemplate, xunitTemplate, nunitTemplate],
                        ct
                    );

                    var testCallbackTemplate = (CallbackTemplate)testTemplate;
                    return await testCallbackTemplate.ApplyTemplateAsync(parseResult, ct);
                });
        }
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

    private async Task<string[]> PromptForExtraAspirePythonStarterOptionsAsync(ParseResult result, CancellationToken cancellationToken)
    {
        var extraArgs = new List<string>();

        await PromptForDevLocalhostTldOptionAsync(result, extraArgs, cancellationToken);
        await PromptForRedisCacheOptionAsync(result, extraArgs, cancellationToken);

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
        var useLocalhostTld = result.GetValue<bool?>("--localhost-tld");
        if (!useLocalhostTld.HasValue)
        {
            useLocalhostTld = await interactionService.PromptForSelectionAsync(TemplatingStrings.UseLocalhostTld_Prompt, [TemplatingStrings.No, TemplatingStrings.Yes], choice => choice, cancellationToken) switch
            {
                var choice when string.Equals(choice, TemplatingStrings.Yes, StringComparisons.CliInputOrOutput) => true,
                var choice when string.Equals(choice, TemplatingStrings.No, StringComparisons.CliInputOrOutput) => false,
                _ => throw new InvalidOperationException(TemplatingStrings.UseLocalhostTld_UnexpectedChoice)
            };
        }

        if (useLocalhostTld ?? false)
        {
            interactionService.DisplayMessage("check_mark", TemplatingStrings.UseLocalhostTld_UsingLocalhostTld);
            extraArgs.Add("--localhost-tld");
        }
    }

    private async Task PromptForRedisCacheOptionAsync(ParseResult result, List<string> extraArgs, CancellationToken cancellationToken)
    {
        var useRedisCache = result.GetValue<bool?>("--use-redis-cache");
        if (!useRedisCache.HasValue)
        {
            useRedisCache = await interactionService.PromptForSelectionAsync(TemplatingStrings.UseRedisCache_Prompt, [TemplatingStrings.Yes, TemplatingStrings.No], choice => choice, cancellationToken) switch
            {
                var choice when string.Equals(choice, TemplatingStrings.Yes, StringComparisons.CliInputOrOutput) => true,
                var choice when string.Equals(choice, TemplatingStrings.No, StringComparisons.CliInputOrOutput) => false,
                _ => throw new InvalidOperationException(TemplatingStrings.UseRedisCache_UnexpectedChoice)
            };
        }

        if (useRedisCache ?? false)
        {
            interactionService.DisplayMessage("check_mark", TemplatingStrings.UseRedisCache_UsingRedisCache);
            extraArgs.Add("--use-redis-cache");
        }
    }

    private async Task PromptForTestFrameworkOptionsAsync(ParseResult result, List<string> extraArgs, CancellationToken cancellationToken)
    {
        var testFramework = result.GetValue<string?>("--test-framework");

        if (testFramework is null)
        {
            var createTestProject = await interactionService.PromptForSelectionAsync(
                TemplatingStrings.PromptForTFMOptions_Prompt,
                [TemplatingStrings.No, TemplatingStrings.Yes],
                choice => choice,
                cancellationToken);

            if (string.Equals(createTestProject, TemplatingStrings.No, StringComparisons.CliInputOrOutput))
            {
                return;
            }
        }

        if (string.IsNullOrEmpty(testFramework))
        {
            testFramework = await interactionService.PromptForSelectionAsync(
                TemplatingStrings.PromptForTFM_Prompt,
                ["MSTest", "NUnit", "xUnit.net", TemplatingStrings.None],
                choice => choice,
                cancellationToken);
        }

        if (testFramework is { } && !string.Equals(testFramework, TemplatingStrings.None, StringComparisons.CliInputOrOutput))
        {
            if (testFramework.ToLower() == "xunit.net")
            {
                await PromptForXUnitVersionOptionsAsync(result, extraArgs, cancellationToken);
            }

            interactionService.DisplayMessage("check_mark", string.Format(CultureInfo.CurrentCulture, TemplatingStrings.PromptForTFM_UsingForTesting, testFramework));

            extraArgs.Add("--test-framework");
            extraArgs.Add(testFramework);
        }
    }

    private async Task PromptForXUnitVersionOptionsAsync(ParseResult result, List<string> extraArgs, CancellationToken cancellationToken)
    {
        var xunitVersion = result.GetValue<string?>("--xunit-version");
        if (string.IsNullOrEmpty(xunitVersion))
        {
            xunitVersion = await interactionService.PromptForSelectionAsync(
                TemplatingStrings.EnterXUnitVersion_Prompt,
                ["v2", "v3", "v3mtp"],
                choice => choice,
                cancellationToken: cancellationToken);
        }

        extraArgs.Add("--xunit-version");
        extraArgs.Add(xunitVersion);
    }

    private static void ApplyExtraAspireStarterOptions(Command command)
    {
        ApplyDevLocalhostTldOption(command);

        var useRedisCacheOption = new Option<bool?>("--use-redis-cache");
        useRedisCacheOption.Description = TemplatingStrings.UseRedisCache_Description;
        useRedisCacheOption.DefaultValueFactory = _ => false;
        command.Options.Add(useRedisCacheOption);

        var testFrameworkOption = new Option<string?>("--test-framework");
        testFrameworkOption.Description = TemplatingStrings.PromptForTFMOptions_Description;
        command.Options.Add(testFrameworkOption);

        var xunitVersionOption = new Option<string?>("--xunit-version");
        xunitVersionOption.Description = TemplatingStrings.EnterXUnitVersion_Description;
        command.Options.Add(xunitVersionOption);
    }

    private static void ApplyExtraAspireJsFrontendStarterOptions(Command command)
    {
        ApplyDevLocalhostTldOption(command);

        var useRedisCacheOption = new Option<bool?>("--use-redis-cache");
        useRedisCacheOption.Description = TemplatingStrings.UseRedisCache_Description;
        useRedisCacheOption.DefaultValueFactory = _ => false;
        command.Options.Add(useRedisCacheOption);
    }

    private static void ApplyDevLocalhostTldOption(Command command)
    {
        var useLocalhostTldOption = new Option<bool?>("--localhost-tld");
        useLocalhostTldOption.Description = TemplatingStrings.UseLocalhostTld_Description;
        useLocalhostTldOption.DefaultValueFactory = _ => false;
        command.Options.Add(useLocalhostTldOption);
    }

    private async Task<TemplateResult> ApplyTemplateWithNoExtraArgsAsync(CallbackTemplate template, ParseResult parseResult, CancellationToken cancellationToken)
    {
        return await ApplyTemplateAsync(template, parseResult, (_, _) => Task.FromResult(Array.Empty<string>()), cancellationToken);
    }

    private async Task<TemplateResult> ApplySingleFileTemplate(CallbackTemplate template, ParseResult parseResult, Func<ParseResult, CancellationToken, Task<string[]>> extraArgsCallback, CancellationToken cancellationToken)
    {
        if (parseResult.CommandResult.Command is InitCommand)
        {
            return await ApplyTemplateAsync(
                template,
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
                parseResult,
                extraArgsCallback,
                cancellationToken
                );
        }
    }

    private Task<TemplateResult> ApplySingleFileTemplateWithNoExtraArgsAsync(CallbackTemplate template, ParseResult parseResult, CancellationToken cancellationToken)
    {
        return ApplySingleFileTemplate(
            template,
            parseResult,
            (_, _) => Task.FromResult(Array.Empty<string>()),
            cancellationToken);
    }

    private async Task<TemplateResult> ApplyTemplateAsync(CallbackTemplate template, ParseResult parseResult, Func<ParseResult, CancellationToken, Task<string[]>> extraArgsCallback, CancellationToken cancellationToken)
    {
        var name = await GetProjectNameAsync(parseResult, cancellationToken);
        var outputPath = await GetOutputPathAsync(parseResult, template.PathDeriver, name, cancellationToken);

        return await ApplyTemplateAsync(template, name, outputPath, parseResult, extraArgsCallback, cancellationToken);
    }

    private async Task<TemplateResult> ApplyTemplateAsync(CallbackTemplate template, string name, string outputPath, ParseResult parseResult, Func<ParseResult, CancellationToken, Task<string[]>> extraArgsCallback, CancellationToken cancellationToken)
    {
        try
        {
            var source = parseResult.GetValue<string?>("--source");
            var selectedTemplateDetails = await GetProjectTemplatesVersionAsync(parseResult, cancellationToken: cancellationToken);

            // Some templates have additional arguments that need to be applied to the `dotnet new` command
            // when it is executed. This callback will get those arguments and potentially prompt for them.
            var extraArgs = await extraArgsCallback(parseResult, cancellationToken);
            using var temporaryConfig = selectedTemplateDetails.Channel.Type == PackageChannelType.Explicit ? await TemporaryNuGetConfig.CreateAsync(selectedTemplateDetails.Channel.Mappings!) : null;

            var templateInstallCollector = new OutputCollector();
            var templateInstallResult = await interactionService.ShowStatusAsync<(int ExitCode, string? TemplateVersion)>(
                $":ice:  {TemplatingStrings.GettingTemplates}",
                async () =>
                {
                    var options = new DotNetCliRunnerInvocationOptions()
                    {
                        StandardOutputCallback = templateInstallCollector.AppendOutput,
                        StandardErrorCallback = templateInstallCollector.AppendOutput,
                    };

                    // Whilst we install the templates - if we are using an explicit channel we need to
                    // generate a temporary NuGet.config file to make sure we install the right package
                    // from the right feed. If we are using an implicit channel then we just use the
                    // ambient configuration (although we should still specify the source) because
                    // the user would have selected it.

                    var result = await runner.InstallTemplateAsync(
                        packageName: "Aspire.ProjectTemplates",
                        version: selectedTemplateDetails.Package.Version,
                        nugetConfigFile: temporaryConfig?.ConfigFile,
                        nugetSource: selectedTemplateDetails.Package.Source,
                        force: true,
                        options: options,
                        cancellationToken: cancellationToken);
                    return result;
                });

            if (templateInstallResult.ExitCode != 0)
            {
                interactionService.DisplayLines(templateInstallCollector.GetLines());
                interactionService.DisplayError(string.Format(CultureInfo.CurrentCulture, TemplatingStrings.TemplateInstallationFailed, templateInstallResult.ExitCode));
                return new TemplateResult(ExitCodeConstants.FailedToInstallTemplates);
            }

            interactionService.DisplayMessage($"package", string.Format(CultureInfo.CurrentCulture, TemplatingStrings.UsingProjectTemplatesVersion, templateInstallResult.TemplateVersion));

            var newProjectCollector = new OutputCollector();
            var newProjectExitCode = await interactionService.ShowStatusAsync(
                $":rocket:  {TemplatingStrings.CreatingNewProject}",
                async () =>
                {
                    var options = new DotNetCliRunnerInvocationOptions()
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
                });

            if (newProjectExitCode != 0)
            {
                // Exit code 73 indicates that the output directory already contains files from a previous project
                // See: https://github.com/dotnet/aspire/issues/9685
                if (newProjectExitCode == 73)
                {
                    interactionService.DisplayError(TemplatingStrings.ProjectAlreadyExists);
                    return new TemplateResult(ExitCodeConstants.FailedToCreateNewProject);
                }

                interactionService.DisplayLines(newProjectCollector.GetLines());
                interactionService.DisplayError(string.Format(CultureInfo.CurrentCulture, TemplatingStrings.ProjectCreationFailed, newProjectExitCode));
                return new TemplateResult(ExitCodeConstants.FailedToCreateNewProject);
            }

            await certificateService.EnsureCertificatesTrustedAsync(runner, cancellationToken);

            // For explicit channels, optionally create or update a NuGet.config. If none exists in the current
            // working directory, create one in the newly created project's output directory.
            await PromptToCreateOrUpdateNuGetConfigAsync(selectedTemplateDetails.Channel, outputPath, cancellationToken);

            interactionService.DisplaySuccess(string.Format(CultureInfo.CurrentCulture, TemplatingStrings.ProjectCreatedSuccessfully, outputPath.EscapeMarkup()));

            return new TemplateResult(ExitCodeConstants.Success, outputPath);
        }
        catch (OperationCanceledException)
        {
            interactionService.DisplayCancellationMessage();
            return new TemplateResult(ExitCodeConstants.FailedToCreateNewProject);
        }
        catch (CertificateServiceException ex)
        {
            interactionService.DisplayError(string.Format(CultureInfo.CurrentCulture, TemplatingStrings.CertificateTrustError, ex.Message));
            return new TemplateResult(ExitCodeConstants.FailedToTrustCertificates);
        }
        catch (Exceptions.ChannelNotFoundException ex)
        {
            interactionService.DisplayError(ex.Message);
            return new TemplateResult(ExitCodeConstants.FailedToCreateNewProject);
        }
        catch (EmptyChoicesException ex)
        {
            interactionService.DisplayError(ex.Message);
            return new TemplateResult(ExitCodeConstants.FailedToCreateNewProject);
        }
    }

    private async Task<string> GetProjectNameAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        if (parseResult.GetValue<string>("--name") is not { } name || !ProjectNameValidator.IsProjectNameValid(name))
        {
            var defaultName = executionContext.WorkingDirectory.Name;
            name = await prompter.PromptForProjectNameAsync(defaultName, cancellationToken);
        }

        return name;
    }

    private async Task<string> GetOutputPathAsync(ParseResult parseResult, Func<string, string> pathDeriver, string projectName, CancellationToken cancellationToken)
    {
        if (parseResult.GetValue<string>("--output") is not { } outputPath)
        {
            outputPath = await prompter.PromptForOutputPath(pathDeriver(projectName), cancellationToken);
        }

        return Path.GetFullPath(outputPath);
    }

    private async Task<(NuGetPackage Package, PackageChannel Channel)> GetProjectTemplatesVersionAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        var allChannels = await packagingService.GetChannelsAsync(cancellationToken);
        
        // Check if --channel option was provided (highest priority)
        var channelName = parseResult.GetValue<string?>("--channel");
        
        // If no --channel option, check for global channel setting
        if (string.IsNullOrEmpty(channelName))
        {
            channelName = await configurationService.GetConfigurationAsync("channel", cancellationToken);
        }
        
        IEnumerable<PackageChannel> channels;
        bool hasChannelSetting = !string.IsNullOrEmpty(channelName);
        
        if (hasChannelSetting)
        {
            // If --channel option is provided or global channel setting exists, find the matching channel
            // (--channel option takes precedence over global setting)
            var matchingChannel = allChannels.FirstOrDefault(c => string.Equals(c.Name, channelName, StringComparison.OrdinalIgnoreCase));
            if (matchingChannel is null)
            {
                throw new Exceptions.ChannelNotFoundException($"No channel found matching '{channelName}'. Valid options are: {string.Join(", ", allChannels.Select(c => c.Name))}");
            }
            channels = new[] { matchingChannel };
        }
        else
        {
            // If there are hives (PR build directories), include all channels.
            // Otherwise, only use the implicit/default channel to avoid prompting.
            var hasHives = executionContext.GetPrHiveCount() > 0;
            channels = hasHives 
                ? allChannels 
                : allChannels.Where(c => c.Type is PackageChannelType.Implicit);
        }

        var packagesFromChannels = await interactionService.ShowStatusAsync(TemplatingStrings.SearchingForAvailableTemplateVersions, async () =>
        {
            var results = new List<(NuGetPackage Package, PackageChannel Channel)>();
            var packagesFromChannelsLock = new object();

            await Parallel.ForEachAsync(channels, cancellationToken, async (channel, ct) =>
            {
                var templatePackages = await channel.GetTemplatePackagesAsync(executionContext.WorkingDirectory, ct);
                lock (packagesFromChannelsLock)
                {
                    results.AddRange(templatePackages.Select(p => (p, channel)));
                }
            });

            return results;
        });

        if (!packagesFromChannels.Any())
        {
            throw new EmptyChoicesException(TemplatingStrings.NoTemplateVersionsFound);
        }

        var orderedPackagesFromChannels = packagesFromChannels.OrderByDescending(p => SemVersion.Parse(p.Package.Version), SemVersion.PrecedenceComparer);

        if (parseResult.GetValue<string>("--version") is { } version)
        {
            var explicitPackageFromChannel = orderedPackagesFromChannels.FirstOrDefault(p => p.Package.Version == version);
            if (explicitPackageFromChannel.Package is not null)
            {
                return explicitPackageFromChannel;
            }
        }

        // If channel was specified via --channel option or global setting (but no --version), 
        // automatically select the highest version from that channel without prompting
        if (hasChannelSetting)
        {
            return orderedPackagesFromChannels.First();
        }

        var selectedPackageFromChannel = await prompter.PromptForTemplatesVersionAsync(orderedPackagesFromChannels, cancellationToken);
        return selectedPackageFromChannel;
    }

    /// <summary>
    /// Prompts to create or update a NuGet.config for explicit channels.
    /// When the output directory differs from the working directory, a NuGet.config is created/updated
    /// only in the output directory. When they are the same (in-place creation), existing behavior
    /// is preserved where the working directory NuGet.config is considered for updates.
    /// </summary>
    private async Task PromptToCreateOrUpdateNuGetConfigAsync(PackageChannel channel, string outputPath, CancellationToken cancellationToken)
    {
        if (channel.Type is not PackageChannelType.Explicit)
        {
            return;
        }

        var mappings = channel.Mappings;
        if (mappings is null || mappings.Length == 0)
        {
            return;
        }

        var workingDir = executionContext.WorkingDirectory;
        var outputDir = new DirectoryInfo(outputPath);

        // Determine if we're creating the project in-place (output directory same as working directory)
        var normalizedOutputPath = Path.GetFullPath(outputPath);
        var normalizedWorkingPath = workingDir.FullName;
        var isInPlaceCreation = string.Equals(normalizedOutputPath, normalizedWorkingPath, StringComparison.OrdinalIgnoreCase);

        var nugetConfigPrompter = new NuGetConfigPrompter(interactionService);

        if (!isInPlaceCreation)
        {
            // For subdirectory creation, always create/update NuGet.config in the output directory only
            // and ignore any existing NuGet.config in the working directory
            await nugetConfigPrompter.CreateOrUpdateWithoutPromptAsync(outputDir, channel, cancellationToken);
            return;
        }

        // In-place creation: preserve existing behavior
        // Prompt user before creating or updating NuGet.config
        await nugetConfigPrompter.PromptToCreateOrUpdateAsync(workingDir, channel, cancellationToken);
    }
}

