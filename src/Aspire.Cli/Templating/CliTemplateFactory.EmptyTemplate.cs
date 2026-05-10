// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Interaction;
using Aspire.Cli.Resources;
using Aspire.Cli.Scaffolding;
using Aspire.Cli.Utils;
using Microsoft.Extensions.Logging;
using Spectre.Console;

namespace Aspire.Cli.Templating;

internal sealed partial class CliTemplateFactory
{
    private async Task<TemplateResult> ApplyEmptyAppHostTemplateAsync(CallbackTemplate template, TemplateInputs inputs, System.CommandLine.ParseResult parseResult, CancellationToken cancellationToken)
    {
        var languageId = inputs.Language;
        if (string.IsNullOrWhiteSpace(languageId))
        {
            _interactionService.DisplayError("Language selection is required.");
            return new TemplateResult(ExitCodeConstants.InvalidCommand);
        }

        var language = _languageDiscovery.GetLanguageById(languageId);
        if (language is null)
        {
            _interactionService.DisplayError($"Unknown language: '{languageId}'");
            return new TemplateResult(ExitCodeConstants.InvalidCommand);
        }

        var projectName = inputs.Name;
        if (string.IsNullOrWhiteSpace(projectName))
        {
            var defaultName = template.Name;
            projectName = await _prompter.PromptForProjectNameAsync(defaultName, parseResult, cancellationToken);
        }

        var outputPath = await ResolveOutputPathAsync(inputs, template.PathDeriver, projectName, parseResult, cancellationToken);
        if (outputPath is null)
        {
            return new TemplateResult(ExitCodeConstants.FailedToCreateNewProject);
        }

        _logger.LogDebug("Applying empty AppHost template. LanguageId: {LanguageId}, Language: {LanguageDisplayName}, ProjectName: {ProjectName}, OutputPath: {OutputPath}.", languageId, language.DisplayName, projectName, outputPath);

        var useLocalhostTld = await ResolveUseLocalhostTldAsync(parseResult, cancellationToken);

        TemplateResult templateResult;
        try
        {
            if (!Directory.Exists(outputPath))
            {
                Directory.CreateDirectory(outputPath);
            }

            var isCsharp = language.LanguageId.Value.Equals(Projects.KnownLanguageId.CSharp, StringComparison.OrdinalIgnoreCase);
            if (isCsharp)
            {
                // Do this first so there is no prompt while status is displayed for creating project.
                await _templateNuGetConfigService.PromptToCreateOrUpdateNuGetConfigAsync(inputs.Channel, outputPath, cancellationToken);
            }

            templateResult = await _interactionService.ShowStatusAsync(
                TemplatingStrings.CreatingNewProject,
                async () =>
                {
                    if (isCsharp)
                    {
                        _logger.LogDebug("Using embedded C# empty AppHost template for '{OutputPath}'.", outputPath);
                        await WriteCSharpEmptyAppHostAsync(inputs.Version, outputPath, projectName, useLocalhostTld, cancellationToken);
                    }
                    else
                    {
                        _logger.LogDebug("Using scaffolding service for language '{LanguageDisplayName}' in '{OutputPath}'.", language.DisplayName, outputPath);
                        var context = new ScaffoldContext(
                            language,
                            new DirectoryInfo(outputPath),
                            projectName,
                            SdkVersion: inputs.Version,
                            Channel: inputs.Channel);
                        if (!await _scaffoldingService.ScaffoldAsync(context, cancellationToken))
                        {
                            return new TemplateResult(ExitCodeConstants.FailedToCreateNewProject);
                        }

                        if (useLocalhostTld)
                        {
                            await ApplyLocalhostTldToScaffoldedRunProfileAsync(outputPath, projectName, cancellationToken);
                        }
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

        _interactionService.DisplaySuccess($"Created {language.DisplayName.EscapeMarkup()} project at {outputPath.EscapeMarkup()}");
        DisplayPostCreationInstructions(outputPath);

        return templateResult;
    }

    private async Task<bool> ResolveUseLocalhostTldAsync(System.CommandLine.ParseResult parseResult, CancellationToken cancellationToken)
    {
        var binding = PromptBinding.CreateBoolConfirm(parseResult, _localhostTldOption, defaultValue: false);

        var useLocalhostTld = await _interactionService.PromptConfirmAsync(
            TemplatingStrings.UseLocalhostTld_Prompt,
            binding: binding,
            cancellationToken: cancellationToken);

        if (useLocalhostTld)
        {
            _interactionService.DisplayMessage(KnownEmojis.CheckMarkButton, TemplatingStrings.UseLocalhostTld_UsingLocalhostTld);
        }

        return useLocalhostTld;
    }

    private async Task ApplyLocalhostTldToScaffoldedRunProfileAsync(string outputPath, string projectName, CancellationToken cancellationToken)
    {
        var hostName = $"{projectName.ToLowerInvariant()}.dev.localhost";

        var aspireConfigPath = Path.Combine(outputPath, Configuration.AspireConfigFile.FileName);
        if (!File.Exists(aspireConfigPath))
        {
            _logger.LogDebug("Skipping localhost TLD update because aspire.config.json was not found in '{OutputPath}'.", outputPath);
            return;
        }

        var content = await File.ReadAllTextAsync(aspireConfigPath, cancellationToken);
        var updatedContent = content.Replace("://localhost", $"://{hostName}", StringComparison.Ordinal);
        if (!string.Equals(content, updatedContent, StringComparison.Ordinal))
        {
            await File.WriteAllTextAsync(aspireConfigPath, updatedContent, cancellationToken);
        }
    }

    private async Task WriteCSharpEmptyAppHostAsync(string? templateVersion, string outputPath, string projectName, bool useLocalhostTld, CancellationToken cancellationToken)
    {
        var aspireVersion = string.IsNullOrWhiteSpace(templateVersion)
            ? VersionHelper.GetDefaultTemplateVersion()
            : templateVersion;
        var projectNameLower = projectName.ToLowerInvariant();
        var ports = GenerateRandomPorts();
        var hostName = useLocalhostTld ? $"{projectNameLower}.dev.localhost" : "localhost";
        string ApplyAllTokens(string content) => ApplyTokens(content, projectName, projectNameLower, aspireVersion, ports, hostName);

        _logger.LogDebug("Writing C# empty AppHost template files to '{OutputPath}' with Aspire version '{AspireVersion}'.", outputPath, aspireVersion);
        await CopyTemplateTreeToDiskAsync("empty-apphost", outputPath, ApplyAllTokens, cancellationToken);
    }
}
