// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.Globalization;
using System.Runtime.InteropServices;
using Aspire.Cli.Certificates;
using Aspire.Cli.Commands;
using Aspire.Cli.Configuration;
using Aspire.Cli.DotNet;
using Aspire.Cli.Interaction;
using Aspire.Cli.Packaging;
using Aspire.Cli.Projects;
using Aspire.Cli.Resources;
using Aspire.Cli.Telemetry;
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
    ITemplateVersionPrompter templateVersionPrompter,
    CliExecutionContext executionContext,
    IDotNetSdkInstaller sdkInstaller,
    IFeatures features,
    IConfigurationService configurationService,
    AspireCliTelemetry telemetry,
    ICliHostEnvironment hostEnvironment,
    TemplateNuGetConfigService templateNuGetConfigService)
    : ITemplateFactory
{
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

    public IEnumerable<ITemplate> GetTemplates()
    {
        if (!IsDotNetOnPath())
        {
            return [];
        }

        var showAllTemplates = features.IsFeatureEnabled(KnownFeatures.ShowAllTemplates, false);
        return GetTemplatesCore(showAllTemplates);
    }

    public async Task<IEnumerable<ITemplate>> GetTemplatesAsync(CancellationToken cancellationToken = default)
    {
        if (!await IsDotNetSdkAvailableAsync(cancellationToken))
        {
            return [];
        }

        var showAllTemplates = features.IsFeatureEnabled(KnownFeatures.ShowAllTemplates, false);
        return GetTemplatesCore(showAllTemplates);
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
        var dotnetFileName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "dotnet.exe" : "dotnet";
        var pathVariable = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;

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

    private IEnumerable<ITemplate> GetTemplatesCore(bool showAllTemplates)
    {
        yield return new CallbackTemplate(
            "aspire-starter",
            TemplatingStrings.AspireStarter_Description,
            projectName => $"./{projectName}",
            ApplyExtraAspireStarterOptions,
            (template, inputs, parseResult, ct) => ApplyTemplateAsync(template, inputs, parseResult, PromptForExtraAspireStarterOptionsAsync, ct),
            languageId: KnownLanguageId.CSharp
            );

        yield return new CallbackTemplate(
            "aspire-ts-cs-starter",
            TemplatingStrings.AspireJsFrontendStarter_Description,
            projectName => $"./{projectName}",
            ApplyExtraAspireJsFrontendStarterOptions,
            (template, inputs, parseResult, ct) => ApplyTemplateAsync(template, inputs, parseResult, PromptForExtraAspireJsFrontendStarterOptionsAsync, ct),
            languageId: KnownLanguageId.CSharp
            );

        if (showAllTemplates)
        {
            yield return new CallbackTemplate(
                KnownTemplateId.DotNetEmptyAppHost,
                TemplatingStrings.AspireEmptyDotNetTemplate_Description,
                projectName => $"./{projectName}",
                ApplyDevLocalhostTldOption,
                ApplyTemplateWithNoExtraArgsAsync,
                languageId: KnownLanguageId.CSharp,
                isEmpty: true
                );

            yield return new CallbackTemplate(
                "aspire-apphost",
                TemplatingStrings.AspireAppHost_Description,
                projectName => $"./{projectName}",
                ApplyDevLocalhostTldOption,
                ApplyTemplateWithNoExtraArgsAsync,
                languageId: KnownLanguageId.CSharp
                );

            yield return new CallbackTemplate(
                "aspire-servicedefaults",
                TemplatingStrings.AspireServiceDefaults_Description,
                projectName => $"./{projectName}",
                _ => { },
                ApplyTemplateWithNoExtraArgsAsync,
                languageId: KnownLanguageId.CSharp
                );
        }

        // Folded into the last yieled template.
        var msTestTemplate = new CallbackTemplate(
            "aspire-mstest",
            TemplatingStrings.AspireMSTest_Description,
            projectName => $"./{projectName}",
            _ => { },
            ApplyTemplateWithNoExtraArgsAsync,
            languageId: KnownLanguageId.CSharp
            );

        // Folded into the last yielded template.
        var nunitTemplate = new CallbackTemplate(
            "aspire-nunit",
            TemplatingStrings.AspireNUnit_Description,
            projectName => $"./{projectName}",
            _ => { },
            ApplyTemplateWithNoExtraArgsAsync,
            languageId: KnownLanguageId.CSharp
            );

        // Folded into the last yielded template.
        var xunitTemplate = new CallbackTemplate(
            "aspire-xunit",
            TemplatingStrings.AspireXUnit_Description,
            projectName => $"./{projectName}",
            _ => { },
            (template, inputs, parseResult, ct) => ApplyTemplateAsync(template, inputs, parseResult, PromptForExtraAspireXUnitOptionsAsync, ct),
            languageId: KnownLanguageId.CSharp
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
                async (template, inputs, parseResult, ct) =>
                {
                    var testTemplate = await prompter.PromptForTemplateAsync(
                        [msTestTemplate, xunitTemplate, nunitTemplate],
                        ct
                    );

                    var testCallbackTemplate = (CallbackTemplate)testTemplate;
                    return await testCallbackTemplate.ApplyTemplateAsync(inputs, parseResult, ct);
                },
                languageId: KnownLanguageId.CSharp);
        }
    }

    private CallbackTemplate CreateSingleFileTemplate()
    {
        return new CallbackTemplate(
            "aspire-apphost-singlefile",
            TemplatingStrings.AspireAppHostSingleFile_Description,
            projectName => $"./{projectName}",
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
        var binding = PromptBinding.CreateBoolAsSelection(result, _localhostTldOption, TemplatingStrings.Yes, TemplatingStrings.No);

        var selected = await interactionService.PromptForSelectionAsync(
            TemplatingStrings.UseLocalhostTld_Prompt,
            [TemplatingStrings.No, TemplatingStrings.Yes],
            choice => choice,
            binding: binding,
            cancellationToken: cancellationToken);

        if (string.Equals(selected, TemplatingStrings.Yes, StringComparisons.CliInputOrOutput))
        {
            interactionService.DisplayMessage(KnownEmojis.CheckMark, TemplatingStrings.UseLocalhostTld_UsingLocalhostTld);
            extraArgs.Add("--localhost-tld");
        }
    }

    private async Task PromptForRedisCacheOptionAsync(ParseResult result, List<string> extraArgs, CancellationToken cancellationToken)
    {
        var binding = PromptBinding.CreateBoolAsSelection(result, _useRedisCacheOption, TemplatingStrings.Yes, TemplatingStrings.No);

        var selected = await interactionService.PromptForSelectionAsync(
            TemplatingStrings.UseRedisCache_Prompt,
            [TemplatingStrings.Yes, TemplatingStrings.No],
            choice => choice,
            binding: binding,
            cancellationToken: cancellationToken);

        if (string.Equals(selected, TemplatingStrings.Yes, StringComparisons.CliInputOrOutput))
        {
            interactionService.DisplayMessage(KnownEmojis.CheckMark, TemplatingStrings.UseRedisCache_UsingRedisCache);
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

            var createTestProject = await interactionService.PromptForSelectionAsync(
                TemplatingStrings.PromptForTFMOptions_Prompt,
                [TemplatingStrings.No, TemplatingStrings.Yes],
                choice => choice,
                cancellationToken: cancellationToken);

            if (string.Equals(createTestProject, TemplatingStrings.No, StringComparisons.CliInputOrOutput))
            {
                return;
            }
        }

        var testFramework = await interactionService.PromptForSelectionAsync(
            TemplatingStrings.PromptForTFM_Prompt,
            ["MSTest", "NUnit", "xUnit.net", TemplatingStrings.None],
            choice => choice,
            binding: binding,
            cancellationToken: cancellationToken);

        if (!string.Equals(testFramework, TemplatingStrings.None, StringComparisons.CliInputOrOutput))
        {
            if (string.Equals(testFramework, "xUnit.net", StringComparison.OrdinalIgnoreCase))
            {
                await PromptForXUnitVersionOptionsAsync(result, extraArgs, cancellationToken);
            }

            interactionService.DisplayMessage(KnownEmojis.CheckMark, string.Format(CultureInfo.CurrentCulture, TemplatingStrings.PromptForTFM_UsingForTesting, testFramework));

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

    private async Task<TemplateResult> ApplyTemplateAsync(CallbackTemplate template, TemplateInputs inputs, ParseResult parseResult, Func<ParseResult, CancellationToken, Task<string[]>> extraArgsCallback, CancellationToken cancellationToken)
    {
        if (!await SdkInstallHelper.EnsureSdkInstalledAsync(sdkInstaller, interactionService, telemetry, cancellationToken))
        {
            return new TemplateResult(ExitCodeConstants.SdkNotInstalled);
        }

        var name = await GetProjectNameAsync(inputs, parseResult, cancellationToken);
        var outputPath = await GetOutputPathAsync(inputs, template.PathDeriver, name, parseResult, cancellationToken);

        return await ApplyTemplateAsync(template, inputs, name, outputPath, parseResult, extraArgsCallback, cancellationToken);
    }

    private async Task<TemplateResult> ApplyTemplateAsync(CallbackTemplate template, TemplateInputs inputs, string name, string outputPath, ParseResult parseResult, Func<ParseResult, CancellationToken, Task<string[]>> extraArgsCallback, CancellationToken cancellationToken)
    {
        try
        {
            var source = inputs.Source;
            var selectedTemplateDetails = await GetProjectTemplatesVersionAsync(inputs, cancellationToken: cancellationToken);

            // Some templates have additional arguments that need to be applied to the `dotnet new` command
            // when it is executed. This callback will get those arguments and potentially prompt for them.
            var extraArgs = await extraArgsCallback(parseResult, cancellationToken);
            using var temporaryConfig = selectedTemplateDetails.Channel.Type == PackageChannelType.Explicit ? await TemporaryNuGetConfig.CreateAsync(selectedTemplateDetails.Channel.Mappings!) : null;

            var templateInstallCollector = new OutputCollector();
            var templateInstallResult = await interactionService.ShowStatusAsync<(int ExitCode, string? TemplateVersion)>(
                TemplatingStrings.GettingTemplates,
                async () =>
                {
                    var options = new ProcessInvocationOptions()
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
                }, emoji: KnownEmojis.Ice);

            if (templateInstallResult.ExitCode != 0)
            {
                interactionService.DisplayLines(templateInstallCollector.GetLines());
                interactionService.DisplayError(string.Format(CultureInfo.CurrentCulture, TemplatingStrings.TemplateInstallationFailed, templateInstallResult.ExitCode, executionContext.LogFilePath));
                return new TemplateResult(ExitCodeConstants.FailedToInstallTemplates);
            }

            interactionService.DisplayMessage(KnownEmojis.Package, string.Format(CultureInfo.CurrentCulture, TemplatingStrings.UsingProjectTemplatesVersion, templateInstallResult.TemplateVersion));

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
                    return new TemplateResult(ExitCodeConstants.FailedToCreateNewProject);
                }

                interactionService.DisplayLines(newProjectCollector.GetLines());
                interactionService.DisplayError(string.Format(CultureInfo.CurrentCulture, TemplatingStrings.ProjectCreationFailed, newProjectExitCode, executionContext.LogFilePath));
                return new TemplateResult(ExitCodeConstants.FailedToCreateNewProject);
            }

            // Trust certificates (result not used since we're not launching an AppHost)
            _ = await certificateService.EnsureCertificatesTrustedAsync(cancellationToken);

            // For explicit channels, optionally create or update a NuGet.config. If none exists in the current
            // working directory, create one in the newly created project's output directory.
            await templateNuGetConfigService.PromptToCreateOrUpdateNuGetConfigAsync(selectedTemplateDetails.Channel, outputPath, cancellationToken);

            interactionService.DisplaySuccess(string.Format(CultureInfo.CurrentCulture, TemplatingStrings.ProjectCreatedSuccessfully, outputPath));

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

    private async Task<string> GetProjectNameAsync(TemplateInputs inputs, ParseResult parseResult, CancellationToken cancellationToken)
    {
        if (inputs.Name is not { } name || !ProjectNameValidator.IsProjectNameValid(name))
        {
            var defaultName = executionContext.WorkingDirectory.Name;

            if (!hostEnvironment.SupportsInteractiveInput)
            {
                return defaultName;
            }

            name = await prompter.PromptForProjectNameAsync(defaultName, parseResult, cancellationToken);
        }

        return name;
    }

    private async Task<string> GetOutputPathAsync(TemplateInputs inputs, Func<string, string> pathDeriver, string projectName, ParseResult parseResult, CancellationToken cancellationToken)
    {
        if (inputs.Output is not { } outputPath)
        {
            var defaultPath = pathDeriver(projectName);

            if (!hostEnvironment.SupportsInteractiveInput)
            {
                outputPath = defaultPath;
            }
            else
            {
                outputPath = await prompter.PromptForOutputPath(defaultPath, parseResult, cancellationToken);
            }
        }

        outputPath = Path.GetFullPath(outputPath);

        // When running in extension mode (VS Code), the folder picker returns the parent
        // directory the user selected. Append the project name as a subdirectory so the
        // project gets its own clean folder, matching the git-clone convention.
        if (ExtensionHelper.IsExtensionHost(interactionService, out _, out _)
            && !projectName.Equals(".", StringComparison.Ordinal)
            && !projectName.Equals("..", StringComparison.Ordinal))
        {
            var normalizedOutputPath = Path.TrimEndingDirectorySeparator(outputPath);

            if (!string.Equals(Path.GetFileName(normalizedOutputPath), projectName, StringComparison.OrdinalIgnoreCase))
            {
                outputPath = Path.Combine(normalizedOutputPath, projectName);
            }
            else
            {
                outputPath = normalizedOutputPath;
            }
        }

        return outputPath;
    }

    private async Task<(NuGetPackage Package, PackageChannel Channel)> GetProjectTemplatesVersionAsync(TemplateInputs inputs, CancellationToken cancellationToken)
    {
        var allChannels = await packagingService.GetChannelsAsync(cancellationToken);
        
        // Check if channel was provided via inputs (highest priority)
        var channelName = inputs.Channel;
        
        // If no channel in inputs, check for global channel setting
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

        if (inputs.Version is { } version)
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

        // In non-interactive mode, automatically select the highest version
        if (!hostEnvironment.SupportsInteractiveInput)
        {
            return orderedPackagesFromChannels.First();
        }

        var selectedPackageFromChannel = await templateVersionPrompter.PromptForTemplatesVersionAsync(orderedPackagesFromChannels, cancellationToken);
        return selectedPackageFromChannel;
    }

}
