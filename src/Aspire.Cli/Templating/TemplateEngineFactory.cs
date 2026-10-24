// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.Globalization;
using System.IO.Hashing;
using System.Text;
using Aspire.Cli.Commands;
using Aspire.Cli.Git;
using Aspire.Cli.Interaction;
using Aspire.Cli.Packaging;
using Aspire.Cli.Resources;
using Aspire.Cli.Utils;
using Aspire.Shared;
using Microsoft.Extensions.Logging;
using Microsoft.TemplateEngine.Abstractions;
using Microsoft.TemplateEngine.Abstractions.Installer;
using Microsoft.TemplateEngine.Edge;
using Microsoft.TemplateEngine.Edge.Settings;
using Microsoft.TemplateEngine.Edge.Template;
using Microsoft.TemplateEngine.Utils;
using Spectre.Console;

namespace Aspire.Cli.Templating;

internal sealed class TemplateEngineFactory(
    CliExecutionContext executionContext,
    IInteractionService interactionService,
    INewCommandPrompter prompter,
    ICliHostEnvironment hostEnvironment,
    IGitRunner gitRunner,
    IPackagingService packagingService,
    ILogger<TemplateEngineFactory> logger) : ITemplateFactory, IDisposable
{
    private readonly List<IDisposable> _resources = [];
    private bool _disposed;

    public IEnumerable<ITemplate> GetTemplates() => [];

    public Task<IEnumerable<ITemplate>> GetInitTemplatesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IEnumerable<ITemplate>>([]);

    public async Task<IEnumerable<ITemplate>> GetTemplatesAsync(TemplateSource? source, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (source is null)
        {
            return [];
        }

        cancellationToken.ThrowIfCancellationRequested();
        var fullPath = source.Repository is null
            ? Path.GetFullPath(source.Path, executionContext.WorkingDirectory.FullName)
            : await GetRepositoryTemplateAsync(source.Repository, source.Path, source.RepositoryRef, cancellationToken);
        if (!Directory.Exists(fullPath))
        {
            throw new TemplateDiscoveryException(CliExitCodes.InvalidCommand,
                string.Format(CultureInfo.CurrentCulture, NewCommandStrings.TemplateDirectoryNotFound, fullPath));
        }

        // Register concrete built-ins instead of loading engine extensions dynamically, which
        // Native AOT cannot support. A virtual hive keeps these templates out of dotnet new's state.
        // https://github.com/dotnet/sdk/blob/main/src/TemplateEngine/Microsoft.TemplateEngine.Edge/DefaultTemplateEngineHost.cs
        var components = Microsoft.TemplateEngine.Edge.Components.AllComponents
            .Concat(Microsoft.TemplateEngine.Orchestrator.RunnableProjects.Components.AllComponents).ToList();
        var host = new TemplateEngineHost(components);
        _resources.Add(host);
        var settings = new EngineEnvironmentSettings(host, virtualizeSettings: true);
        _resources.Add(settings);
        var manager = new TemplatePackageManager(settings);
        _resources.Add(manager);
        var installed = await manager.GetBuiltInManagedProvider().InstallAsync([new InstallRequest(fullPath)], cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var failures = installed.Where(result => !result.Success).ToArray();
        if (failures.Length > 0)
        {
            throw new TemplateDiscoveryException(CliExitCodes.FailedToInstallTemplates,
                string.Format(CultureInfo.CurrentCulture, NewCommandStrings.LocalTemplateLoadFailed,
                    string.Join(", ", failures.Select(result => result.Error))));
        }

        var creator = new TemplateCreator(settings);
        var templates = (await manager.GetTemplatesAsync(cancellationToken))
            .OrderBy(template => template.Name, StringComparer.OrdinalIgnoreCase)
            .Select(template => new CallbackTemplate(
                GetTemplateName(template),
                template.Name,
                (context, name) => OutputPathHelper.GetUniqueDefaultOutputPath(name, context.WorkingDirectory.FullName),
                _ => { },
                (_, templateInputs, result, token) => ApplyAsync(settings, creator, template, templateInputs, result, token)))
            .ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        if (templates.Length == 0)
        {
            throw new TemplateDiscoveryException(CliExitCodes.InvalidCommand,
                string.Format(CultureInfo.CurrentCulture, NewCommandStrings.NoLocalTemplatesFound, fullPath));
        }

        // Returned callbacks still use the engine's mounts. Keep them alive through generation;
        // immutable repository checkouts are retained in the SHA-keyed cache across invocations.
        return templates;
    }

    private async Task<string> GetRepositoryTemplateAsync(string repository, string templatePath, string? reference, CancellationToken cancellationToken)
    {
        var cacheRoot = Path.Combine(executionContext.CacheDirectory.FullName, "git");
        var pathRoot = Path.Combine(cacheRoot, "repository");
        var relativePath = Path.GetRelativePath(pathRoot, Path.GetFullPath(templatePath, pathRoot));
        if (Path.IsPathRooted(templatePath) || relativePath == ".." ||
            relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new TemplateDiscoveryException(CliExitCodes.InvalidCommand, NewCommandStrings.TemplateRepositoryPathInvalid);
        }

        try
        {
            var sha = await interactionService.ShowStatusAsync(NewCommandStrings.ResolvingTemplateRepository,
                () => gitRunner.ResolveReferenceAsync(repository, reference, cancellationToken));
            cancellationToken.ThrowIfCancellationRequested();
            // Different paths and refs at the same commit share the complete repository snapshot.
            // NUL separates the repository address and SHA without ambiguous delimiters.
            var identity = $"{repository}\0{sha}";
            var key = Convert.ToHexString(XxHash3.Hash(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
            var cacheDirectory = Path.Combine(cacheRoot, key);
            DirectoryHelper.CreateWithOwnerOnlyPermissions(cacheRoot);
            if (Directory.Exists(cacheDirectory))
            {
                return GetRepositoryTemplatePath(cacheDirectory, relativePath);
            }

            using var checkout = TemporaryCacheDirectory.Create(cacheRoot, "repo", DeleteCheckout, DeleteLease);
            var repositoryRoot = new DirectoryInfo(Path.Combine(checkout.FullName, "repository"));
            await interactionService.ShowStatusAsync(NewCommandStrings.CloningTemplateRepository, async () =>
            {
                await gitRunner.CloneAsync(repository, repositoryRoot, sha, cancellationToken);
                return true;
            });
            cancellationToken.ThrowIfCancellationRequested();

            // Root templates must not copy Git history/configuration into generated projects.
            Directory.Move(Path.Combine(repositoryRoot.FullName, ".git"), Path.Combine(checkout.FullName, ".git"));
            GetRepositoryTemplatePath(checkout.FullName, relativePath);
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                // Publish only complete checkouts. Concurrent invocations can safely use the winner;
                // the losing temporary checkout is removed by its lease owner on disposal.
                checkout.MoveTo(cacheDirectory);
            }
            catch (IOException) when (Directory.Exists(cacheDirectory))
            {
                logger.LogDebug("Template checkout was already cached at {Path}.", cacheDirectory);
            }

            return GetRepositoryTemplatePath(cacheDirectory, relativePath);
        }
        catch (GitReferenceException ex)
        {
            throw new TemplateDiscoveryException(CliExitCodes.FailedToInstallTemplates,
                string.Format(CultureInfo.CurrentCulture, NewCommandStrings.TemplateRepositoryRefNotFound, ex.Reference));
        }
        catch (GitCommandException ex)
        {
            var message = ex.ExitCode is int exitCode
                ? string.Format(CultureInfo.CurrentCulture, NewCommandStrings.TemplateRepositoryGitExited, exitCode, ex.Message)
                : ex.Message;
            throw new TemplateDiscoveryException(CliExitCodes.FailedToInstallTemplates, message);
        }
    }

    private static string GetRepositoryTemplatePath(string checkout, string relativePath)
    {
        var repositoryRoot = Path.Combine(checkout, "repository");
        var fullPath = Path.GetFullPath(relativePath, repositoryRoot);
        // A repository-relative path must not escape through a checked-out symbolic link.
        for (var directory = new DirectoryInfo(fullPath); directory is not null && directory.FullName != repositoryRoot; directory = directory.Parent)
        {
            if (directory.LinkTarget is not null)
            {
                throw new TemplateDiscoveryException(CliExitCodes.InvalidCommand, NewCommandStrings.TemplateRepositoryPathInvalid);
            }
        }

        if (!Directory.Exists(fullPath))
        {
            throw new TemplateDiscoveryException(CliExitCodes.InvalidCommand,
                string.Format(CultureInfo.CurrentCulture, NewCommandStrings.TemplateDirectoryNotFound, fullPath));
        }

        return fullPath;
    }

    private void DeleteCheckout(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                // Git object files can be read-only on Windows. Do not follow repository
                // symlinks while clearing that bit or touch files outside this checkout.
                foreach (var file in Directory.EnumerateFiles(path, "*", new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    AttributesToSkip = FileAttributes.ReparsePoint
                }))
                {
                    var attributes = File.GetAttributes(file);
                    if ((attributes & FileAttributes.ReadOnly) != 0)
                    {
                        File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
                    }
                }

                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not delete template checkout {Path}.", path);
        }
    }

    private void DeleteLease(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not delete template checkout lease {Path}.", path);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        for (var i = _resources.Count - 1; i >= 0; i--)
        {
            _resources[i].Dispose();
        }

        _resources.Clear();
    }

    private async Task<TemplateResult> ApplyAsync(IEngineEnvironmentSettings settings, TemplateCreator creator, ITemplateInfo template, TemplateInputs inputs, ParseResult parseResult, CancellationToken cancellationToken)
    {
        var name = inputs.Name is { } explicitName && ProjectNameValidator.IsProjectNameValid(explicitName)
            ? explicitName
            : await prompter.PromptForProjectNameAsync(
                string.IsNullOrWhiteSpace(template.DefaultName) ? GetTemplateName(template) : template.DefaultName, parseResult, cancellationToken);
        var isExtensionHost = ExtensionHelper.IsExtensionHost(interactionService, out _, out _);
        var createSubdirectory = await OutputPathHelper.PromptExtensionCreateProjectNameSubdirectoryAsync(
            interactionService, isExtensionHost, inputs.Output is not null, name, cancellationToken);
        var outputPath = await OutputPathHelper.ResolveOutputPathAsync(
            inputs.Output,
            executionContext.WorkingDirectory.FullName,
            () => prompter.PromptForOutputPath(
                OutputPathHelper.GetUniqueDefaultOutputPath(name, executionContext.WorkingDirectory.FullName),
                parseResult,
                OutputPathHelper.CreateOutputPathValidator(executionContext.WorkingDirectory.FullName),
                OutputPathHelper.CreateProjectNameSubdirectoryOutputPathResolver(createSubdirectory, name),
                cancellationToken),
            interactionService);
        if (outputPath is null)
        {
            return new TemplateResult(CliExitCodes.FailedToCreateNewProject);
        }

        var parameters = new Dictionary<string, string?>(StringComparer.Ordinal);
        try
        {
            var promptState = hostEnvironment.SupportsInteractiveInput && template.ParameterDefinitions.Any(parameter =>
                !string.IsNullOrEmpty(parameter.Precedence.IsEnabledCondition) || !string.IsNullOrEmpty(parameter.Precedence.IsRequiredCondition))
                ? new TemplateParameterPromptState(settings, template, name, logger)
                : null;
            foreach (var parameter in promptState?.Parameters ?? template.ParameterDefinitions)
            {
                if (parameter.IsName || parameter.Type != "parameter" ||
                    parameter.Precedence.PrecedenceDefinition is PrecedenceDefinition.Implicit or PrecedenceDefinition.Disabled)
                {
                    continue;
                }

                cancellationToken.ThrowIfCancellationRequested();
                if (promptState is not null && !promptState.IsEnabled(parameter))
                {
                    continue;
                }

                string? value;
                if (parameter.Name.Equals("AspireVersion", StringComparison.OrdinalIgnoreCase))
                {
                    value = executionContext.IdentitySdkVersion;
                }
                else if (parameter.Name.Equals("AspireNuGetSource", StringComparison.OrdinalIgnoreCase))
                {
                    // Resolve policy without querying feeds, using the same identity as AspireVersion.
                    // PackagingService also handles PR hives and local package/source overrides.
                    var channels = (await packagingService.GetChannelsAsync(cancellationToken, executionContext.IdentityChannel)).ToArray();
                    var channel = channels.FirstOrDefault(channel =>
                        string.Equals(channel.Name, executionContext.IdentityChannel, StringComparisons.ChannelName) &&
                        channel.Mappings is { Length: > 0 });
                    if (channel is null)
                    {
                        interactionService.DisplayError(string.Format(CultureInfo.CurrentCulture,
                            NewCommandStrings.NoChannelFoundMatching,
                            executionContext.IdentityChannel,
                            string.Join(", ", channels.Select(channel => channel.Name))));
                        return new TemplateResult(CliExitCodes.InvalidCommand);
                    }

                    value = channel.SourceDetails;
                }
                else if (!hostEnvironment.SupportsInteractiveInput)
                {
                    // Leave unset values to the engine, including conditional requirements and defaults.
                    continue;
                }
                else
                {
                    value = await PromptForParameterAsync(
                        parameter, promptState?.IsRequired(parameter) ?? parameter.Precedence.IsRequired, cancellationToken);
                }

                if (value is null)
                {
                    continue;
                }

                if (promptState is not null && !promptState.TrySetValue(parameter, value))
                {
                    return CreationFailed(CreationResultStatus.InvalidParamValues, parameter.Name);
                }

                parameters[parameter.Name] = value;
            }
        }
        catch (TemplateAuthoringException ex)
        {
            return CreationFailed(CreationResultStatus.TemplateIssueDetected, ex.Message);
        }

        // Validate through the engine before it creates the output directory. TemplateCreator
        // returns post-actions as metadata; this flow deliberately does not execute them.
        var preview = await creator.InstantiateAsync(template, name, null, outputPath, parameters, dryRun: true, cancellationToken: cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (preview.Status != CreationResultStatus.Success)
        {
            return CreationFailed(preview);
        }

        var result = await interactionService.ShowStatusAsync(
            TemplatingStrings.CreatingNewProject,
            () => creator.InstantiateAsync(template, name, null, outputPath, parameters, cancellationToken: cancellationToken),
            emoji: KnownEmojis.Rocket);
        cancellationToken.ThrowIfCancellationRequested();
        if (result.Status != CreationResultStatus.Success)
        {
            return CreationFailed(result);
        }

        interactionService.DisplaySuccess(string.Format(CultureInfo.CurrentCulture, TemplatingStrings.ProjectCreatedSuccessfully, outputPath));
        return new TemplateResult(CliExitCodes.Success, outputPath);
    }

    private async Task<string?> PromptForParameterAsync(ITemplateParameter parameter, bool required, CancellationToken cancellationToken)
    {
        var prompt = (!string.IsNullOrWhiteSpace(parameter.DisplayName)
            ? parameter.DisplayName
            : !string.IsNullOrWhiteSpace(parameter.Description) ? parameter.Description : parameter.Name).EscapeMarkup();
        if (parameter.DataType.Equals("bool", StringComparison.OrdinalIgnoreCase))
        {
            var value = await interactionService.PromptConfirmAsync(
                prompt,
                binding: PromptBinding.CreateDefault(bool.TryParse(parameter.DefaultValue, out var defaultValue) && defaultValue),
                cancellationToken: cancellationToken);
            return value ? "true" : "false";
        }

        if (parameter.Choices is { Count: > 0 } choices)
        {
            // The engine represents multi-choice values as "red|blue", not comma-separated input.
            var defaults = parameter.DefaultValue?.Split('|') ?? [];
            var keys = choices.Keys.OrderByDescending(key => defaults.Contains(key, StringComparer.OrdinalIgnoreCase)).ToArray();
            string FormatChoice(string key) => (string.IsNullOrWhiteSpace(choices[key].DisplayName) ? key : choices[key].DisplayName).EscapeMarkup();
            if (parameter.AllowMultipleValues)
            {
                var selected = await interactionService.PromptForSelectionsAsync(
                    prompt, keys, FormatChoice,
                    preSelected: keys.Where(key => defaults.Contains(key, StringComparer.OrdinalIgnoreCase)),
                    optional: !required,
                    cancellationToken: cancellationToken);
                return string.Join('|', selected);
            }

            if (!required && string.IsNullOrEmpty(parameter.DefaultValue))
            {
                // The extension identifies choices by display text, so the skip label must also be unique.
                var skipLabel = TemplatingStrings.Skip.EscapeMarkup();
                while (keys.Any(key => FormatChoice(key) == skipLabel))
                {
                    skipLabel = $"({skipLabel})";
                }

                // Omission is distinct from an empty value or a template choice whose key is "Skip".
                var optionalChoices = keys.Select(key => (Value: (string?)key, DisplayName: FormatChoice(key)))
                    .Prepend((Value: (string?)null, DisplayName: skipLabel));
                var selected = await interactionService.PromptForSelectionAsync(
                    prompt, optionalChoices, choice => choice.DisplayName, cancellationToken: cancellationToken);
                return selected.Value;
            }

            return await interactionService.PromptForSelectionAsync(prompt, keys, FormatChoice, cancellationToken: cancellationToken);
        }

        return await interactionService.PromptForStringAsync(
            prompt,
            required: required,
            binding: PromptBinding.CreateDefault<string?>(parameter.DefaultValue ?? string.Empty),
            cancellationToken: cancellationToken);
    }

    private TemplateResult CreationFailed(ITemplateCreationResult result) => CreationFailed(result.Status, result.ErrorMessage);

    private TemplateResult CreationFailed(CreationResultStatus status, string? errorMessage)
    {
        interactionService.DisplayError(string.Format(CultureInfo.CurrentCulture, NewCommandStrings.LocalTemplateCreationFailed, status, errorMessage));
        return new TemplateResult(CliExitCodes.FailedToCreateNewProject);
    }

    private static string GetTemplateName(ITemplateInfo template) =>
        template.ShortNameList.Count > 0 ? template.ShortNameList[0] : template.Identity;
}
