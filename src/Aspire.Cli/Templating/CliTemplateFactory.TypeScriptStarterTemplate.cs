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
    private async Task<TemplateResult> ApplyTypeScriptStarterTemplateAsync(CallbackTemplate _, TemplateInputs inputs, System.CommandLine.ParseResult parseResult, CancellationToken cancellationToken)
    {
        var projectName = inputs.Name;
        if (string.IsNullOrWhiteSpace(projectName))
        {
            var defaultName = _executionContext.WorkingDirectory.Name;
            projectName = await _prompter.PromptForProjectNameAsync(defaultName, cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(inputs.Version))
        {
            _interactionService.DisplayError("Unable to determine Aspire version for the TypeScript starter template.");
            return new TemplateResult(ExitCodeConstants.InvalidCommand);
        }

        var aspireVersion = inputs.Version;
        var outputPath = inputs.Output;
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            var defaultOutputPath = $"./{projectName}";
            outputPath = await _prompter.PromptForOutputPath(defaultOutputPath, cancellationToken);
        }
        outputPath = Path.GetFullPath(outputPath, _executionContext.WorkingDirectory.FullName);

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
                async () =>
                {
                    var projectNameLower = projectName.ToLowerInvariant();

                    // Generate random ports (matching .NET template port ranges)
                    var ports = GenerateRandomPorts();
                    var hostName = useLocalhostTld ? $"{projectNameLower}.dev.localhost" : "localhost";
                    string ApplyAllTokens(string content) => ApplyTokens(content, projectName, projectNameLower, aspireVersion, ports, hostName);
                    _logger.LogDebug("Copying embedded TypeScript starter template files to '{OutputPath}'.", outputPath);
                    await CopyTemplateTreeToDiskAsync("ts-starter", outputPath, ApplyAllTokens, cancellationToken);

                    // Write channel to aspire.config.json before restore so package resolution uses the selected channel.
                    if (!string.IsNullOrEmpty(inputs.Channel))
                    {
                        var config = AspireConfigFile.Load(outputPath);
                        if (config is not null)
                        {
                            config.Channel = inputs.Channel;
                            config.Save(outputPath);
                        }
                    }

                    var appHostProject = _projectFactory.TryGetProject(new FileInfo(Path.Combine(outputPath, "apphost.ts")));
                    if (appHostProject is not IGuestAppHostSdkGenerator guestProject)
                    {
                        _interactionService.DisplayError("Automatic 'aspire restore' is unavailable for the new TypeScript starter project because no TypeScript AppHost SDK generator was found.");
                        return new TemplateResult(ExitCodeConstants.FailedToBuildArtifacts, outputPath);
                    }

                    _logger.LogDebug("Generating SDK code for TypeScript starter in '{OutputPath}'.", outputPath);
                    var restoreSucceeded = await guestProject.BuildAndGenerateSdkAsync(new DirectoryInfo(outputPath), cancellationToken);
                    if (!restoreSucceeded)
                    {
                        _interactionService.DisplayError("Automatic 'aspire restore' failed for the new TypeScript starter project. Run 'aspire restore' in the project directory for more details.");
                        return new TemplateResult(ExitCodeConstants.FailedToBuildArtifacts, outputPath);
                    }

                    return new TemplateResult(ExitCodeConstants.Success, outputPath);
                }, emoji: KnownEmojis.Rocket);

            if (templateResult.ExitCode != ExitCodeConstants.Success)
            {
                return templateResult;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _interactionService.DisplayError($"Failed to create project files: {ex.Message}");
            return new TemplateResult(ExitCodeConstants.FailedToCreateNewProject);
        }

        _interactionService.DisplaySuccess($"Created TypeScript starter project at {outputPath.EscapeMarkup()}");
        DisplayPostCreationInstructions(outputPath);

        return templateResult;
    }
}
