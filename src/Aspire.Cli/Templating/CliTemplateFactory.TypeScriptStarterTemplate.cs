// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Configuration;
using Aspire.Cli.Interaction;
using Aspire.Cli.Projects;
using Aspire.Cli.Resources;
using Microsoft.Extensions.Logging;
using Spectre.Console;

namespace Aspire.Cli.Templating;

internal sealed partial class CliTemplateFactory
{
    private async Task<TemplateResult> ApplyTypeScriptStarterTemplateAsync(CallbackTemplate template, TemplateInputs inputs, System.CommandLine.ParseResult parseResult, CancellationToken cancellationToken)
    {
        var projectName = inputs.Name;
        if (string.IsNullOrWhiteSpace(projectName))
        {
            var defaultName = template.Name;
            projectName = await _prompter.PromptForProjectNameAsync(defaultName, parseResult, cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(inputs.Version))
        {
            _interactionService.DisplayError("Unable to determine Aspire version for the TypeScript starter template.");
            return new TemplateResult(CliExitCodes.InvalidCommand);
        }

        var aspireVersion = inputs.Version;
        var outputPath = await ResolveOutputPathAsync(inputs, template.PathDeriver, projectName, parseResult, cancellationToken);
        if (outputPath is null)
        {
            return new TemplateResult(CliExitCodes.FailedToCreateNewProject);
        }

        _logger.LogDebug("Applying TypeScript starter template. ProjectName: {ProjectName}, OutputPath: {OutputPath}, AspireVersion: {AspireVersion}.", projectName, outputPath, aspireVersion);

        var useLocalhostTld = await ResolveUseLocalhostTldAsync(parseResult, cancellationToken);

        TemplateResult templateResult;
        try
        {
            if (!Directory.Exists(outputPath))
            {
                Directory.CreateDirectory(outputPath);
            }

            templateResult = await _interactionService.ShowStatusAsync(
                TemplatingStrings.CreatingNewProject,
                (Func<Task<TemplateResult>>)(async () =>
                {
                    var projectNameLower = projectName.ToLowerInvariant();

                    // Generate random ports (matching .NET template port ranges)
                    var ports = GenerateRandomPorts();
                    var hostName = useLocalhostTld ? $"{projectNameLower}.dev.localhost" : "localhost";
                    string ApplyAllTokens(string content) => ApplyTokens(content, projectName, projectNameLower, aspireVersion, ports, hostName);
                    _logger.LogDebug("Copying embedded TypeScript starter template files to '{OutputPath}'.", outputPath);
                    await CopyTemplateTreeToDiskAsync("ts-starter", outputPath, ApplyAllTokens, cancellationToken);

                    // Persist the template SDK version before restore so integration and codegen package
                    // resolution stays aligned with the project we just created. Only persist the
                    // channel when NewCommand resolved an Explicit one (--channel, or a registered
                    // channel matching CliExecutionContext.IdentityChannel). Implicit (nuget.org)
                    // selections leave the channel unwritten so `aspire add`/`aspire restore` use
                    // the user's ambient NuGet config without a per-project pin.
                    var config = AspireConfigFile.LoadOrCreate(outputPath, aspireVersion);
                    if (!string.IsNullOrEmpty(inputs.Channel))
                    {
                        config.Channel = inputs.Channel;
                    }
                    config.Save(outputPath);

                    var appHostProject = _projectFactory.TryGetProject(new FileInfo(Path.Combine(outputPath, "apphost.ts")));
                    if (appHostProject is not IGuestAppHostSdkGenerator guestProject)
                    {
                        _interactionService.DisplayError("Automatic 'aspire restore' is unavailable for the new TypeScript starter project because no TypeScript AppHost SDK generator was found.");
                        return new TemplateResult((int)CliExitCodes.FailedToBuildArtifacts, outputPath);
                    }

                    _logger.LogDebug("Generating SDK code for TypeScript starter in '{OutputPath}'.", outputPath);
                    var restoreSucceeded = await guestProject.BuildAndGenerateSdkAsync(new DirectoryInfo(outputPath), packageSourceOverride: inputs.Source, cancellationToken: cancellationToken);
                    if (!restoreSucceeded)
                    {
                        _interactionService.DisplayError("Automatic 'aspire restore' failed for the new TypeScript starter project. Run 'aspire restore' in the project directory for more details.");
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

        _interactionService.DisplaySuccess($"Created TypeScript starter project at {outputPath.EscapeMarkup()}");
        DisplayPostCreationInstructions(outputPath);

        return templateResult;
    }
}
