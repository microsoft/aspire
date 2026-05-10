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
    private async Task<TemplateResult> ApplyPythonStarterTemplateAsync(CallbackTemplate template, TemplateInputs inputs, System.CommandLine.ParseResult parseResult, CancellationToken cancellationToken)
    {
        var projectName = inputs.Name;
        if (string.IsNullOrWhiteSpace(projectName))
        {
            var defaultName = template.Name;
            projectName = await _prompter.PromptForProjectNameAsync(defaultName, parseResult, cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(inputs.Version))
        {
            _interactionService.DisplayError("Unable to determine Aspire version for the Python starter template.");
            return new TemplateResult(ExitCodeConstants.InvalidCommand);
        }

        var aspireVersion = inputs.Version;
        var outputPath = await ResolveOutputPathAsync(inputs, template.PathDeriver, projectName, parseResult, cancellationToken);
        if (outputPath is null)
        {
            return new TemplateResult(ExitCodeConstants.FailedToCreateNewProject);
        }

        _logger.LogDebug("Applying Python starter template. ProjectName: {ProjectName}, OutputPath: {OutputPath}, AspireVersion: {AspireVersion}.", projectName, outputPath, aspireVersion);

        var useLocalhostTld = await ResolveUseLocalhostTldAsync(parseResult, cancellationToken);
        var useRedisCache = await ResolveUseRedisCacheAsync(parseResult, cancellationToken);

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

                    var ports = GenerateRandomPorts();
                    var hostName = useLocalhostTld ? $"{projectNameLower}.dev.localhost" : "localhost";
                    var conditions = new Dictionary<string, bool>
                    {
                        ["redis"] = useRedisCache,
                        ["no-redis"] = !useRedisCache,
                    };
                    string ApplyAllTokens(string content) => ConditionalBlockProcessor.Process(
                        ApplyTokens(content, projectName, projectNameLower, aspireVersion, ports, hostName),
                        conditions);
                    _logger.LogDebug("Copying embedded Python starter template files to '{OutputPath}'.", outputPath);
                    await CopyTemplateTreeToDiskAsync("py-starter", outputPath, ApplyAllTokens, cancellationToken);

                    if (useRedisCache)
                    {
                        AddRedisPackageToConfig(outputPath, aspireVersion);
                    }

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

                    var appHostProject = _projectFactory.TryGetProject(new FileInfo(Path.Combine(outputPath, "apphost.ts")));
                    if (appHostProject is not IGuestAppHostSdkGenerator guestProject)
                    {
                        _interactionService.DisplayError("Automatic 'aspire restore' is unavailable for the new Python starter project because no TypeScript AppHost SDK generator was found.");
                        return new TemplateResult(ExitCodeConstants.FailedToBuildArtifacts, outputPath);
                    }

                    _logger.LogDebug("Generating SDK code for Python starter in '{OutputPath}'.", outputPath);
                    var restoreSucceeded = await guestProject.BuildAndGenerateSdkAsync(new DirectoryInfo(outputPath), cancellationToken);
                    if (!restoreSucceeded)
                    {
                        _interactionService.DisplayError("Automatic 'aspire restore' failed for the new Python starter project. Run 'aspire restore' in the project directory for more details.");
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

        _interactionService.DisplaySuccess($"Created Python starter project at {outputPath.EscapeMarkup()}");
        DisplayPostCreationInstructions(outputPath);

        return templateResult;
    }

    private async Task<bool> ResolveUseRedisCacheAsync(System.CommandLine.ParseResult parseResult, CancellationToken cancellationToken)
    {
        var binding = PromptBinding.CreateBoolConfirm(parseResult, _useRedisCacheOption, interactiveDefault: true, nonInteractiveDefault: false);

        var useRedisCache = await _interactionService.PromptConfirmAsync(
            TemplatingStrings.UseRedisCache_Prompt,
            binding: binding,
            cancellationToken: cancellationToken);

        if (useRedisCache)
        {
            _interactionService.DisplayMessage(KnownEmojis.CheckMarkButton, TemplatingStrings.UseRedisCache_UsingRedisCache);
        }

        return useRedisCache;
    }

    private static void AddRedisPackageToConfig(string outputPath, string aspireVersion)
    {
        var config = AspireConfigFile.LoadOrCreate(outputPath);
        config.AddOrUpdatePackage("Aspire.Hosting.Redis", aspireVersion);
        config.Save(outputPath);
    }
}
