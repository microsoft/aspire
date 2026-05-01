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
            // Some templates have additional arguments that need to be applied to the `dotnet new` command
            // when it is executed. This callback will get those arguments and potentially prompt for them.
            // Run before resolving the template package so prompt/error precedence is unchanged.
            var extraArgs = await extraArgsCallback(parseResult, cancellationToken);

            var query = new TemplatePackageQuery(
                ChannelOverride: inputs.Channel,
                VersionOverride: inputs.Version,
                SourceOverride: inputs.Source,
                IncludePrHives: true);

            var selectedTemplateDetails = await templateNuGetConfigService.ResolveTemplatePackageAsync(query, cancellationToken);

            var installOutcome = await templateNuGetConfigService.InstallTemplatePackageAsync(
                selectedTemplateDetails,
                runner,
                TemplatingStrings.GettingTemplates,
                statusEmoji: KnownEmojis.Ice,
                cancellationToken);

            if (installOutcome.ExitCode != 0)
            {
                interactionService.DisplayLines(installOutcome.OutputLines);
                interactionService.DisplayError(string.Format(CultureInfo.CurrentCulture, TemplatingStrings.TemplateInstallationFailed, installOutcome.ExitCode, executionContext.LogFilePath));
                return new TemplateResult(ExitCodeConstants.FailedToInstallTemplates);
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
            name = await prompter.PromptForProjectNameAsync(defaultName, parseResult, cancellationToken);
        }

        return name;
    }

    private async Task<string> GetOutputPathAsync(TemplateInputs inputs, Func<string, string> pathDeriver, string projectName, ParseResult parseResult, CancellationToken cancellationToken)
    {
        if (inputs.Output is not { } outputPath)
        {
            var defaultPath = pathDeriver(projectName);
            outputPath = await prompter.PromptForOutputPath(defaultPath, parseResult, cancellationToken);
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
}
