// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Configuration;
using Aspire.Cli.Interaction;
using Aspire.Cli.Projects;
using Microsoft.Extensions.Logging;
using Spectre.Console;

namespace Aspire.Cli.Templating;

internal sealed partial class CliTemplateFactory
{
    private async Task<TemplateResult> ApplyJavaStarterTemplateAsync(CallbackTemplate template, TemplateInputs inputs, System.CommandLine.ParseResult parseResult, CancellationToken cancellationToken)
    {
        var projectName = inputs.Name;
        if (string.IsNullOrWhiteSpace(projectName))
        {
            var defaultName = template.Name;
            projectName = await _prompter.PromptForProjectNameAsync(defaultName, parseResult, cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(inputs.Version))
        {
            _interactionService.DisplayError("Unable to determine Aspire version for the Java starter template.");
            return new TemplateResult(CliExitCodes.InvalidCommand);
        }

        var aspireVersion = inputs.Version;
        var outputPath = await ResolveOutputPathAsync(inputs, template.PathDeriver, projectName, parseResult, cancellationToken);
        if (outputPath is null)
        {
            return new TemplateResult(CliExitCodes.FailedToCreateNewProject);
        }

        _logger.LogDebug("Applying Java starter template. ProjectName: {ProjectName}, OutputPath: {OutputPath}, AspireVersion: {AspireVersion}.", projectName, outputPath, aspireVersion);

        var useLocalhostTld = await ResolveUseLocalhostTldAsync(parseResult, cancellationToken);

        TemplateResult templateResult;
        try
        {
            if (!Directory.Exists(outputPath))
            {
                Directory.CreateDirectory(outputPath);
            }

            templateResult = await _interactionService.ShowStatusAsync(
                "Creating new Aspire Java project...",
                (Func<Task<TemplateResult>>)(async () =>
                {
                    var projectNameLower = projectName.ToLowerInvariant();
                    var ports = GenerateRandomPorts();
                    var hostName = useLocalhostTld ? $"{projectNameLower}.dev.localhost" : "localhost";
                    string ApplyAllTokens(string content) => ApplyTokens(content, projectName, projectNameLower, aspireVersion, ports, hostName);

                    _logger.LogDebug("Copying embedded Java starter template files to '{OutputPath}'.", outputPath);
                    await CopyTemplateTreeToDiskAsync("java-starter", outputPath, ApplyAllTokens, cancellationToken);

                    // Persist the resolved SDK version, and the resolved channel when NewCommand
                    // resolved an Explicit one (pr-<N>, daily, staging, local), into the scaffolded
                    // project's aspire.config.json.
                    //
                    // The SDK version is written unconditionally so `aspire new --version` produces a
                    // project pinned to the version it was scaffolded with; without it the AppHost and
                    // the packages can resolve to different versions on the next restore. Implicit
                    // channel selections are left unwritten so `aspire add`/`aspire restore` use the
                    // user's ambient NuGet config without a per-project pin. Mirrors
                    // CliTemplateFactory.TypeScriptStarterTemplate and DotNetTemplateFactory.
                    var config = AspireConfigFile.LoadOrCreate(outputPath, aspireVersion);
                    if (!string.IsNullOrEmpty(inputs.Channel))
                    {
                        config.Channel = inputs.Channel;
                    }
                    config.Save(outputPath);

                    var appHostProject = _projectFactory.TryGetProject(new FileInfo(Path.Combine(outputPath, "AppHost.java")));
                    if (appHostProject is not IGuestAppHostSdkGenerator guestProject)
                    {
                        _interactionService.DisplayError("Automatic 'aspire restore' is unavailable for the new Java starter project because no Java AppHost SDK generator was found.");
                        return new TemplateResult((int)CliExitCodes.FailedToBuildArtifacts, outputPath);
                    }

                    _logger.LogDebug("Generating SDK code for Java starter in '{OutputPath}'.", outputPath);
                    var restoreSucceeded = await guestProject.BuildAndGenerateSdkAsync(new DirectoryInfo(outputPath), packageSourceOverride: inputs.Source, cancellationToken: cancellationToken);
                    if (!restoreSucceeded)
                    {
                        _interactionService.DisplayError("Automatic 'aspire restore' failed for the new Java starter project. Run 'aspire restore' in the project directory for more details.");
                        return new TemplateResult((int)CliExitCodes.FailedToBuildArtifacts, outputPath);
                    }
                    await _templateNuGetConfigService.CreateOrUpdateNuGetConfigForSourceOverrideAsync(inputs.Source, inputs.Channel, outputPath, cancellationToken);

                    return new TemplateResult((int)CliExitCodes.Success, outputPath);
                }), emoji: KnownEmojis.Rocket);

            if (templateResult.ExitCode != CliExitCodes.Success)
            {
                return templateResult;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _interactionService.DisplayError($"Failed to create project files: {ex.Message}");
            return new TemplateResult(CliExitCodes.FailedToCreateNewProject);
        }

        _interactionService.DisplaySuccess($"Created Java starter project at {outputPath.EscapeMarkup()}");
        DisplayPostCreationInstructions(outputPath);

        return templateResult;
    }
}
