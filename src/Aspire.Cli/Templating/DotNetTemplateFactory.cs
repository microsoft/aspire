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
using Aspire.Cli.NuGet;
using Aspire.Cli.Packaging;
using Aspire.Cli.Projects;
using Aspire.Cli.Resources;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Utils;
using Microsoft.Extensions.Logging.Abstractions;

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
            (ctx, projectName) => OutputPathHelper.GetUniqueDefaultOutputPath(projectName, ctx.WorkingDirectory.FullName),
            ApplyExtraAspireStarterOptions,
            ApplyEmbeddedStarterTemplateAsync,
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
                ApplyEmbeddedAppHostTemplateAsync,
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

        // Folded into the last yieled template.
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
        if (showAllTemplates)
        {
            yield return new CallbackTemplate(
                "aspire-test",
                TemplatingStrings.IntegrationTestsTemplate_Description,
                (ctx, projectName) => OutputPathHelper.GetUniqueDefaultOutputPath(projectName, ctx.WorkingDirectory.FullName),
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
            (ctx, projectName) => OutputPathHelper.GetUniqueDefaultOutputPath(projectName, ctx.WorkingDirectory.FullName),
            ApplyDevLocalhostTldOption,
            (template, inputs, parseResult, ct) => ApplySingleFileTemplate(template, inputs, parseResult, PromptForExtraAspireSingleFileOptionsAsync, ct),
            languageId: KnownLanguageId.CSharp,
            isEmpty: true
            );
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

        return await ApplyTemplateAsync(template, inputs, name, outputPath, parseResult, extraArgsCallback, cancellationToken);
    }

    private async Task<TemplateResult> ApplyTemplateAsync(CallbackTemplate template, TemplateInputs inputs, string name, string outputPath, ParseResult parseResult, Func<ParseResult, CancellationToken, Task<string[]>> extraArgsCallback, CancellationToken cancellationToken)
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

            var installOutcome = await templateNuGetConfigService.InstallTemplatePackageAsync(
                selectedTemplateDetails,
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

            // Persist the resolved channel into the scaffolded project's aspire.config.json
            // for Explicit channels (pr-<N>, daily, staging, local). Without this pin, `aspire
            // update` on the new project skips the local-config step in its channel-resolution
            // precedence and falls through to either an interactive prompt (when hives exist)
            // or the Implicit/nuget.org channel — silently moving a project scaffolded by a
            // PR or daily CLI onto stable/nuget.org. Implicit channels (stable/nuget.org) are
            // not persisted so `aspire add`/`aspire restore` continue to use the ambient
            // NuGet config without a per-project pin. Mirrors the TypeScript starter behavior
            // in CliTemplateFactory.TypeScriptStarterTemplate.
            if (selectedTemplateDetails.Channel.Type is PackageChannelType.Explicit)
            {
                var config = AspireConfigFile.LoadOrCreate(outputPath);
                config.Channel = selectedTemplateDetails.Channel.Name;
                config.Save(outputPath);
            }

            // For explicit channels, optionally create or update a NuGet.config. If none exists in the current
            // working directory, create one in the newly created project's output directory.
            if (!await TemplateNuGetConfigService.CreateOrUpdateNuGetConfigForSourceOverrideAsync(inputs.Source, selectedTemplateDetails.Channel, outputPath, cancellationToken))
            {
                await templateNuGetConfigService.PromptToCreateOrUpdateNuGetConfigAsync(selectedTemplateDetails.Channel, outputPath, cancellationToken);
            }

            interactionService.DisplaySuccess(string.Format(CultureInfo.CurrentCulture, TemplatingStrings.ProjectCreatedSuccessfully, outputPath));

            return new TemplateResult(CliExitCodes.Success, outputPath);
        }
        catch (OperationCanceledException)
        {
            return new TemplateResult(CliExitCodes.Cancelled);
        }
        catch (CertificateServiceException ex)
        {
            interactionService.DisplayError(string.Format(CultureInfo.CurrentCulture, TemplatingStrings.CertificateTrustError, ex.Message));
            return new TemplateResult(CliExitCodes.FailedToTrustCertificates);
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

    /// <summary>
    /// Applies the <c>aspire-starter</c> template by rendering the multi-project C# starter
    /// that is embedded in the CLI (see <see cref="EmbeddedCSharpStarterTemplate"/>), instead
    /// of resolving and installing the <c>Aspire.ProjectTemplates</c> NuGet package and invoking
    /// <c>dotnet new aspire-starter</c>. The channel/version is still resolved so the generated
    /// projects reference a matching Aspire version, and the channel pin + NuGet.config behavior
    /// is preserved for parity with the previous flow and the other CLI-rendered starters.
    /// </summary>
    private async Task<TemplateResult> ApplyEmbeddedStarterTemplateAsync(CallbackTemplate template, TemplateInputs inputs, ParseResult parseResult, CancellationToken cancellationToken)
    {
        if (!await SdkInstallHelper.EnsureSdkInstalledAsync(sdkInstaller, interactionService, telemetry, cancellationToken: cancellationToken))
        {
            return new TemplateResult(CliExitCodes.SdkNotInstalled);
        }

        // The embedded starter intentionally does not produce a test project (no Tests project,
        // no .sln). The --test-framework / --xunit-version options remain registered so existing
        // invocations and scripts keep parsing, but a request for an actual framework is rejected
        // rather than silently ignored — otherwise users could believe tests were scaffolded.
        if (TryGetRequestedTestFramework(parseResult, out var requestedFramework))
        {
            interactionService.DisplayError(string.Format(CultureInfo.CurrentCulture, TemplatingStrings.TestFrameworkNotSupportedByStarter, requestedFramework));
            return new TemplateResult(CliExitCodes.InvalidCommand);
        }

        var name = await GetProjectNameAsync(inputs, template.Name, parseResult, cancellationToken);
        var outputPath = await GetOutputPathAsync(inputs, template.PathDeriver, name, parseResult, cancellationToken);

        if (outputPath is null)
        {
            return new TemplateResult(CliExitCodes.FailedToCreateNewProject);
        }

        try
        {
            // Resolve (but do NOT install) the template package so the generated project
            // references a version that matches the requested channel. Only the resolved
            // version and channel influence the output now; the template content itself is
            // embedded in the CLI rather than coming from the NuGet package.
            var query = new TemplatePackageQuery(
                RequestedChannel: inputs.Channel,
                VersionOverride: inputs.Version,
                SourceOverride: inputs.Source,
                IncludePrHives: true);

            var selection = await templateNuGetConfigService.ResolveTemplatePackageAsync(query, cancellationToken);

            // Reuse the existing localized prompts (and their displayed confirmations) by
            // capturing whether each flag ends up in the extra-args list the prompts populate.
            var promptedArgs = new List<string>();
            await PromptForDevLocalhostTldOptionAsync(parseResult, promptedArgs, cancellationToken);
            await PromptForRedisCacheOptionAsync(parseResult, promptedArgs, cancellationToken);
            var useLocalhostTld = promptedArgs.Contains("--localhost-tld");
            var useRedisCache = promptedArgs.Contains("--use-redis-cache");

            await interactionService.ShowStatusAsync(
                TemplatingStrings.CreatingNewProject,
                async () =>
                {
                    await EmbeddedCSharpStarterTemplate.RenderAsync(
                        outputPath: outputPath,
                        projectName: name,
                        useRedisCache: useRedisCache,
                        useLocalhostTld: useLocalhostTld,
                        templateVersion: selection.Package.Version,
                        logger: NullLogger.Instance,
                        cancellationToken: cancellationToken);
                    return 0;
                }, emoji: KnownEmojis.Rocket);

            // Trust certificates (result not used since we're not launching an AppHost).
            _ = await certificateService.EnsureCertificatesTrustedAsync(cancellationToken);

            // Persist an Explicit channel (pr-<N>, daily, staging, local) into the new project's
            // aspire.config.json so `aspire update`/`aspire add` stay on the same channel. Implicit
            // (stable/nuget.org) selections are left unwritten — matching the previous flow and the
            // TypeScript starter.
            if (selection.Channel.Type is PackageChannelType.Explicit)
            {
                var config = AspireConfigFile.LoadOrCreate(outputPath);
                config.Channel = selection.Channel.Name;
                config.Save(outputPath);
            }

            if (!await TemplateNuGetConfigService.CreateOrUpdateNuGetConfigForSourceOverrideAsync(inputs.Source, selection.Channel, outputPath, cancellationToken))
            {
                await templateNuGetConfigService.PromptToCreateOrUpdateNuGetConfigAsync(selection.Channel, outputPath, cancellationToken);
            }

            interactionService.DisplaySuccess(string.Format(CultureInfo.CurrentCulture, TemplatingStrings.ProjectCreatedSuccessfully, outputPath));

            return new TemplateResult(CliExitCodes.Success, outputPath);
        }
        catch (OperationCanceledException)
        {
            return new TemplateResult(CliExitCodes.Cancelled);
        }
        catch (CertificateServiceException ex)
        {
            interactionService.DisplayError(string.Format(CultureInfo.CurrentCulture, TemplatingStrings.CertificateTrustError, ex.Message));
            return new TemplateResult(CliExitCodes.FailedToTrustCertificates);
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
            interactionService.DisplayError(ex.Message);
            return new TemplateResult(CliExitCodes.FailedToCreateNewProject);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            interactionService.DisplayError(string.Format(CultureInfo.CurrentCulture, TemplatingStrings.FailedToCreateProjectFiles, ex.Message));
            return new TemplateResult(CliExitCodes.FailedToCreateNewProject);
        }
    }

    // Returns true (and the requested framework name) when the caller asked for an actual test
    // framework via --test-framework <fx> or --xunit-version. A missing value, or an explicit
    // "None", is treated as "no test project requested" and returns false.
    private bool TryGetRequestedTestFramework(ParseResult parseResult, out string framework)
    {
        framework = string.Empty;

        var (frameworkProvided, frameworkValue) = PromptBinding.Create(parseResult, _testFrameworkOption).Resolve();
        if (frameworkProvided
            && !string.IsNullOrWhiteSpace(frameworkValue)
            && !string.Equals(frameworkValue, TemplatingStrings.None, StringComparisons.CliInputOrOutput)
            && !string.Equals(frameworkValue, "None", StringComparison.OrdinalIgnoreCase))
        {
            framework = frameworkValue;
            return true;
        }

        // --xunit-version only makes sense alongside an xUnit test project, so treat it as a
        // request for one even if --test-framework was omitted.
        var (xunitProvided, xunitValue) = PromptBinding.Create(parseResult, _xunitVersionOption).Resolve();
        if (xunitProvided && !string.IsNullOrWhiteSpace(xunitValue))
        {
            framework = "xUnit.net";
            return true;
        }

        return false;
    }

    /// <summary>
    /// Applies the <c>aspire-apphost</c> template by rendering the single-project C# AppHost
    /// that is embedded in the CLI (see <see cref="EmbeddedCSharpAppHostTemplate"/>), instead of
    /// resolving and installing the <c>Aspire.ProjectTemplates</c> NuGet package and invoking
    /// <c>dotnet new aspire-apphost</c>. This is the same embedded template that <c>aspire init</c>
    /// renders, so <c>aspire new aspire-apphost</c> and <c>aspire init</c> now produce identical
    /// AppHost projects. The channel/version is still resolved so the generated project references
    /// a matching Aspire version, and the channel pin + NuGet.config behavior is preserved for
    /// parity with the previous flow and the other CLI-rendered templates.
    /// </summary>
    private async Task<TemplateResult> ApplyEmbeddedAppHostTemplateAsync(CallbackTemplate template, TemplateInputs inputs, ParseResult parseResult, CancellationToken cancellationToken)
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

        try
        {
            // Resolve (but do NOT install) the template package so the generated project
            // references a version that matches the requested channel. Only the resolved
            // version and channel influence the output now; the template content itself is
            // embedded in the CLI rather than coming from the NuGet package.
            var query = new TemplatePackageQuery(
                RequestedChannel: inputs.Channel,
                VersionOverride: inputs.Version,
                SourceOverride: inputs.Source,
                IncludePrHives: true);

            var selection = await templateNuGetConfigService.ResolveTemplatePackageAsync(query, cancellationToken);

            // Reuse the existing localized prompt (and its displayed confirmation) by capturing
            // whether the flag ends up in the extra-args list the prompt populates.
            var promptedArgs = new List<string>();
            await PromptForDevLocalhostTldOptionAsync(parseResult, promptedArgs, cancellationToken);
            var useLocalhostTld = promptedArgs.Contains("--localhost-tld");

            await interactionService.ShowStatusAsync(
                TemplatingStrings.CreatingNewProject,
                async () =>
                {
                    await EmbeddedCSharpAppHostTemplate.RenderAsync(
                        outputPath: outputPath,
                        projectName: name,
                        useLocalhostTld: useLocalhostTld,
                        templateVersion: selection.Package.Version,
                        logger: NullLogger.Instance,
                        cancellationToken: cancellationToken);
                    return 0;
                }, emoji: KnownEmojis.Rocket);

            // Trust certificates (result not used since we're not launching an AppHost).
            _ = await certificateService.EnsureCertificatesTrustedAsync(cancellationToken);

            // Persist an Explicit channel (pr-<N>, daily, staging, local) into the new project's
            // aspire.config.json so `aspire update`/`aspire add` stay on the same channel. Implicit
            // (stable/nuget.org) selections are left unwritten — matching the previous flow and the
            // other CLI-rendered templates.
            if (selection.Channel.Type is PackageChannelType.Explicit)
            {
                var config = AspireConfigFile.LoadOrCreate(outputPath);
                config.Channel = selection.Channel.Name;
                config.Save(outputPath);
            }

            if (!await TemplateNuGetConfigService.CreateOrUpdateNuGetConfigForSourceOverrideAsync(inputs.Source, selection.Channel, outputPath, cancellationToken))
            {
                await templateNuGetConfigService.PromptToCreateOrUpdateNuGetConfigAsync(selection.Channel, outputPath, cancellationToken);
            }

            interactionService.DisplaySuccess(string.Format(CultureInfo.CurrentCulture, TemplatingStrings.ProjectCreatedSuccessfully, outputPath));

            return new TemplateResult(CliExitCodes.Success, outputPath);
        }
        catch (OperationCanceledException)
        {
            return new TemplateResult(CliExitCodes.Cancelled);
        }
        catch (CertificateServiceException ex)
        {
            interactionService.DisplayError(string.Format(CultureInfo.CurrentCulture, TemplatingStrings.CertificateTrustError, ex.Message));
            return new TemplateResult(CliExitCodes.FailedToTrustCertificates);
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
            interactionService.DisplayError(ex.Message);
            return new TemplateResult(CliExitCodes.FailedToCreateNewProject);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            interactionService.DisplayError(string.Format(CultureInfo.CurrentCulture, TemplatingStrings.FailedToCreateProjectFiles, ex.Message));
            return new TemplateResult(CliExitCodes.FailedToCreateNewProject);
        }
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
                return await prompter.PromptForOutputPath(defaultPath, parseResult, validator, cancellationToken, outputPathResolver);
            },
            interactionService);
    }
}
