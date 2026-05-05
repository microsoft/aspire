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
    private async Task<TemplateResult> ApplyGoStarterTemplateAsync(CallbackTemplate _, TemplateInputs inputs, System.CommandLine.ParseResult parseResult, CancellationToken cancellationToken)
    {
        var projectName = inputs.Name;
        if (string.IsNullOrWhiteSpace(projectName))
        {
            var defaultName = _.Name;
            projectName = await _prompter.PromptForProjectNameAsync(defaultName, parseResult, cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(inputs.Version))
        {
            _interactionService.DisplayError("Unable to determine Aspire version for the Go starter template.");
            return new TemplateResult(ExitCodeConstants.InvalidCommand);
        }

        var aspireVersion = inputs.Version;
        var outputPath = await ResolveOutputPathAsync(inputs, _.PathDeriver, projectName, parseResult, cancellationToken);
        if (outputPath is null)
        {
            return new TemplateResult(ExitCodeConstants.FailedToCreateNewProject);
        }

        _logger.LogDebug("Applying Go starter template. ProjectName: {ProjectName}, OutputPath: {OutputPath}, AspireVersion: {AspireVersion}.", projectName, outputPath, aspireVersion);

        var useLocalhostTld = await ResolveUseLocalhostTldAsync(parseResult, cancellationToken);

        TemplateResult templateResult;
        try
        {
            if (!Directory.Exists(outputPath))
            {
                Directory.CreateDirectory(outputPath);
            }

            templateResult = await _interactionService.ShowStatusAsync(
                "Creating new Aspire Go project...",
                async () =>
                {
                    var projectNameLower = projectName.ToLowerInvariant();
                    var ports = GenerateRandomPorts();
                    var hostName = useLocalhostTld ? $"{projectNameLower}.dev.localhost" : "localhost";
                    string ApplyAllTokens(string content) => ApplyTokens(content, projectName, projectNameLower, aspireVersion, ports, hostName);

                    _logger.LogDebug("Copying embedded Go starter template files to '{OutputPath}'.", outputPath);
                    await CopyTemplateTreeToDiskAsync("go-starter", outputPath, ApplyAllTokens, cancellationToken);

                    // Write channel to settings.json before restore so package resolution uses the selected channel.
                    if (!string.IsNullOrEmpty(inputs.Channel))
                    {
                        var config = AspireJsonConfiguration.Load(outputPath);
                        if (config is not null)
                        {
                            config.Channel = inputs.Channel;
                            config.Save(outputPath);
                        }
                    }

                    var appHostProject = _projectFactory.TryGetProject(new FileInfo(Path.Combine(outputPath, "apphost.go")));
                    if (appHostProject is not IGuestAppHostSdkGenerator guestProject)
                    {
                        _interactionService.DisplayError("Automatic 'aspire restore' is unavailable for the new Go starter project because no Go AppHost SDK generator was found.");
                        return new TemplateResult(ExitCodeConstants.FailedToBuildArtifacts, outputPath);
                    }

                    _logger.LogDebug("Generating SDK code for Go starter in '{OutputPath}'.", outputPath);
                    var restoreSucceeded = await guestProject.BuildAndGenerateSdkAsync(new DirectoryInfo(outputPath), cancellationToken);
                    if (!restoreSucceeded)
                    {
                        _interactionService.DisplayError("Automatic 'aspire restore' failed for the new Go starter project. Run 'aspire restore' in the project directory for more details.");
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

        _interactionService.DisplaySuccess($"Created Go starter project at {outputPath.EscapeMarkup()}");
        DisplayPostCreationInstructions(outputPath);

        return templateResult;
    }
}
