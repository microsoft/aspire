// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.Globalization;
using System.Text.RegularExpressions;
using Aspire.Cli.Configuration;
using Aspire.Cli.Interaction;
using Aspire.Cli.NuGet;
using Aspire.Cli.Packaging;
using Aspire.Cli.Projects;
using Aspire.Cli.Resources;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Templating;
using Aspire.Cli.Utils;
using Microsoft.Extensions.Configuration;
using Spectre.Console;

namespace Aspire.Cli.Commands;

internal sealed class NewCommand : BaseCommand, IPackageMetaPrefetchingCommand
{
    internal override HelpGroup HelpGroup => HelpGroup.AppCommands;

    protected override bool UpdateNotificationsEnabled => true;

    private readonly INewCommandPrompter _prompter;
    private readonly ITemplateProvider _templateProvider;
    private readonly ITemplate[] _templates;
    private readonly IFeatures _features;
    private readonly AgentInitCommand _agentInitCommand;
    private readonly ICliHostEnvironment _hostEnvironment;

    internal static readonly Option<string?> s_nameOption = new("--name", "-n")
    {
        Description = NewCommandStrings.NameArgumentDescription,
        Recursive = true
    };
    internal static readonly Option<string?> s_outputOption = new("--output", "-o")
    {
        Description = NewCommandStrings.OutputArgumentDescription,
        Recursive = true
    };
    private static readonly Option<string?> s_sourceOption = new("--source", "-s")
    {
        Description = NewCommandStrings.SourceArgumentDescription,
        Recursive = true
    };
    private static readonly Option<string?> s_versionOption = new("--version")
    {
        Description = NewCommandStrings.VersionArgumentDescription,
        Recursive = true,
        Hidden = true
    };

    internal static readonly Option<bool?> s_suppressAgentInitOption = new("--suppress-agent-init")
    {
        Description = SharedCommandStrings.AgentInitOptionDescription,
        Recursive = true
    };

    private readonly Option<string?> _channelOption;
    private readonly Option<string?> _languageOption;

    /// <summary>
    /// NewCommand prefetches both template and CLI package metadata.
    /// </summary>
    public bool PrefetchesTemplatePackageMetadata => true;

    /// <summary>
    /// NewCommand prefetches CLI package metadata for update notifications.
    /// </summary>
    public bool PrefetchesCliPackageMetadata => true;

    public NewCommand(
        INewCommandPrompter prompter,
        IInteractionService interactionService,
        ITemplateProvider templateProvider,
        AspireCliTelemetry telemetry,
        IFeatures features,
        ICliUpdateNotifier updateNotifier,
        CliExecutionContext executionContext,
        AgentInitCommand agentInitCommand,
        ICliHostEnvironment hostEnvironment,
        IConfiguration configuration)
        : base("new", NewCommandStrings.Description, features, updateNotifier, executionContext, interactionService, telemetry)
    {
        _prompter = prompter;
        _templateProvider = templateProvider;
        _features = features;
        _agentInitCommand = agentInitCommand;
        _hostEnvironment = hostEnvironment;

        Options.Add(s_nameOption);
        Options.Add(s_outputOption);
        Options.Add(s_sourceOption);
        Options.Add(s_versionOption);
        Options.Add(s_suppressAgentInitOption);

        // --channel is hidden and rejected at runtime; description text mentions
        // the staging variant to preserve compatibility with any docs that referenced
        // the prior wording while the option still parses.
        var isStagingEnabled = KnownFeatures.IsStagingChannelEnabled(_features, configuration)
            || string.Equals(ExecutionContext.IdentityChannel, PackageChannelNames.Staging, StringComparisons.ChannelName);
        _channelOption = new Option<string?>("--channel")
        {
            Description = isStagingEnabled
                ? NewCommandStrings.ChannelOptionDescriptionWithStaging
                : NewCommandStrings.ChannelOptionDescription,
            Recursive = true,
            Hidden = true
        };
        Options.Add(_channelOption);

        _languageOption = new Option<string?>("--language")
        {
            Description = NewCommandStrings.LanguageOptionDescription,
            Recursive = true
        };
        Options.Add(_languageOption);

        // Register template definitions as subcommands synchronously.
        // This uses GetTemplates() which returns template definitions without
        // performing any async I/O (e.g. SDK availability checks). Runtime
        // availability is checked in ExecuteAsync via GetTemplatesAsync().
        _templates = templateProvider.GetTemplates().ToArray();

        foreach (var template in _templates)
        {
            var templateCommand = new TemplateCommand(template, ExecuteAsync, features, updateNotifier, executionContext, InteractionService, Telemetry);
            Subcommands.Add(templateCommand);
        }
    }

    private string? ParseExplicitLanguageId(ParseResult parseResult)
    {
        var explicitLanguageId = parseResult.GetValue(_languageOption);
        return string.IsNullOrWhiteSpace(explicitLanguageId) ? null : NormalizeLanguageId(explicitLanguageId);
    }

    private static string NormalizeLanguageId(string languageId)
    {
        return languageId.Equals(KnownLanguageId.TypeScriptAlias, StringComparison.OrdinalIgnoreCase)
            ? KnownLanguageId.TypeScript
            : languageId;
    }

    private static string GetLanguageDisplayName(string languageId)
    {
        return NormalizeLanguageId(languageId) switch
        {
            KnownLanguageId.CSharp => KnownLanguageId.CSharpDisplayName,
            KnownLanguageId.TypeScript => "TypeScript (Node.js)",
            KnownLanguageId.Python => KnownLanguageId.PythonDisplayName,
            KnownLanguageId.Go => KnownLanguageId.GoDisplayName,
            KnownLanguageId.Java => KnownLanguageId.JavaDisplayName,
            KnownLanguageId.Rust => KnownLanguageId.RustDisplayName,
            _ => languageId
        };
    }

    private async Task<string> PromptForAppHostLanguageAsync(IReadOnlyList<string> selectableLanguages, CancellationToken cancellationToken)
    {
        var choices = selectableLanguages
            .Select(NormalizeLanguageId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(static languageId => (LanguageId: languageId, DisplayName: GetLanguageDisplayName(languageId)))
            .ToArray();

        var selected = await InteractionService.PromptForSelectionAsync(
            "Which language would you like to use?",
            choices,
            choice => choice.DisplayName.EscapeMarkup(),
            cancellationToken: cancellationToken);

        return selected.LanguageId;
    }

    private async Task<(bool Success, string? LanguageId)> ResolveSelectedLanguageAsync(ITemplate template, ParseResult parseResult, CancellationToken cancellationToken)
    {
        var explicitLanguageId = ParseExplicitLanguageId(parseResult);

        if (template.SelectableAppHostLanguages.Count == 0)
        {
            if (!string.IsNullOrWhiteSpace(explicitLanguageId) && !template.SupportsLanguage(explicitLanguageId))
            {
                InteractionService.DisplayError($"Template '{template.Name}' does not support language '{explicitLanguageId}'.");
                return (false, null);
            }

            return (true, explicitLanguageId ?? template.LanguageId);
        }

        if (!string.IsNullOrWhiteSpace(explicitLanguageId))
        {
            var normalizedExplicitLanguageId = NormalizeLanguageId(explicitLanguageId);
            if (!template.SelectableAppHostLanguages.Any(l => l.Equals(normalizedExplicitLanguageId, StringComparison.OrdinalIgnoreCase)))
            {
                InteractionService.DisplayError($"Template '{template.Name}' does not support language '{explicitLanguageId}'.");
                return (false, null);
            }

            return (true, normalizedExplicitLanguageId);
        }

        if (!_hostEnvironment.SupportsInteractiveInput)
        {
            return (true, NormalizeLanguageId(template.SelectableAppHostLanguages[0]));
        }

        var selectedLanguageId = await PromptForAppHostLanguageAsync(template.SelectableAppHostLanguages, cancellationToken);
        return (true, selectedLanguageId);
    }

    private ITemplate[] GetTemplatesForTemplateArgument(ITemplate[] availableTemplates, ParseResult parseResult)
    {
        var explicitLanguageId = ParseExplicitLanguageId(parseResult);
        var templates = availableTemplates.ToList();

        if (!string.IsNullOrWhiteSpace(explicitLanguageId))
        {
            templates = templates
                .Where(t => t.SupportsLanguage(explicitLanguageId))
                .ToList();
        }

        // Sort templates alphabetically by description, keeping empty templates at the end
        templates.Sort((a, b) =>
        {
            var aIsEmpty = a.IsEmpty;
            var bIsEmpty = b.IsEmpty;

            if (aIsEmpty != bIsEmpty)
            {
                return aIsEmpty ? 1 : -1;
            }

            return string.Compare(a.Description, b.Description, StringComparison.OrdinalIgnoreCase);
        });

        return templates.ToArray();
    }

    private ITemplate[] GetTemplatesForPrompt(ITemplate[] availableTemplates, ParseResult parseResult)
    {
        return GetTemplatesForTemplateArgument(availableTemplates, parseResult)
            .Where(static t => t.ShowInPrompt)
            .ToArray();
    }

    private async Task<ITemplate?> GetProjectTemplateAsync(ITemplate[] availableTemplates, ParseResult parseResult, CancellationToken cancellationToken)
    {
        // If a subcommand was matched (e.g., aspire new aspire-starter), find the template by command name
        if (parseResult.CommandResult.Command != this)
        {
            var subcommandTemplate = availableTemplates.SingleOrDefault(t => t.Name.Equals(parseResult.CommandResult.Command.Name, StringComparison.OrdinalIgnoreCase));
            if (subcommandTemplate is not null)
            {
                return subcommandTemplate;
            }

            // The template subcommand was parsed successfully but the template is
            // not available at runtime (e.g. .NET SDK is not installed).
            InteractionService.DisplayError($"Template '{parseResult.CommandResult.Command.Name}' is not available. Ensure the required runtime is installed.");
            return null;
        }

        var templatesForTemplateArgument = GetTemplatesForTemplateArgument(availableTemplates, parseResult);
        if (templatesForTemplateArgument.Length == 0)
        {
            InteractionService.DisplayError("No templates are available for the current environment.");
            return null;
        }

        if (!_hostEnvironment.SupportsInteractiveInput)
        {
            InteractionService.DisplayError(NewCommandStrings.NonInteractiveTemplateRequired);
            var templateNames = string.Join(", ", templatesForTemplateArgument.Select(t => t.Name));
            InteractionService.DisplaySubtleMessage(string.Format(CultureInfo.CurrentCulture, InteractionServiceStrings.NonInteractiveAvailableValues, templateNames));
            throw new NonInteractiveException("template");
        }

        var templatesForPrompt = GetTemplatesForPrompt(availableTemplates, parseResult);
        if (templatesForPrompt.Length == 0)
        {
            InteractionService.DisplayError("No templates are available for the current environment.");
            return null;
        }

        var result = await _prompter.PromptForTemplateAsync(templatesForPrompt, cancellationToken);

        return result;
    }

    protected override async Task<CommandResult> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        using var activity = Telemetry.StartDiagnosticActivity(this.Name);

        // --version and --channel are accepted for back-compat parsing but are no longer
        // honored. `aspire new` now always installs the project templates package whose
        // version matches the running CLI build, sourced from the CLI's identity channel
        // (CliExecutionContext.IdentityChannel). Surfacing a hard error here — rather than
        // silently ignoring the options — avoids users believing they have overridden the
        // pinned version/channel when in fact they have not.
        if (parseResult.GetValue(s_versionOption) is { } providedVersion && !string.IsNullOrWhiteSpace(providedVersion))
        {
            InteractionService.DisplayError(string.Format(
                CultureInfo.CurrentCulture,
                NewCommandStrings.VersionOptionNoLongerSupported,
                VersionHelper.GetDefaultTemplateVersion()));
            return CommandResult.Failure(CliExitCodes.InvalidCommand);
        }

        if (parseResult.GetValue(_channelOption) is { } providedChannel && !string.IsNullOrWhiteSpace(providedChannel))
        {
            InteractionService.DisplayError(string.Format(
                CultureInfo.CurrentCulture,
                NewCommandStrings.ChannelOptionNoLongerSupported,
                ExecutionContext.IdentityChannel));
            return CommandResult.Failure(CliExitCodes.InvalidCommand);
        }

        var source = parseResult.GetValue(s_sourceOption);
        if (!string.IsNullOrWhiteSpace(source) && PackageSourceOverrideMappings.HasCredentialMaterial(source))
        {
            InteractionService.DisplayError(NewCommandStrings.SourceWithCredentialsCannotBePersisted);
            return CommandResult.Failure(CliExitCodes.InvalidCommand);
        }

        // Resolve which templates are actually available at runtime (performs
        // async checks like SDK availability). This may be a subset of the
        // templates registered as subcommands.
        var availableTemplates = (await _templateProvider.GetTemplatesAsync(cancellationToken)).ToArray();

        var template = await GetProjectTemplateAsync(availableTemplates, parseResult, cancellationToken);
        if (template is null)
        {
            return CommandResult.Failure(CliExitCodes.InvalidCommand);
        }

        var (languageResolutionSuccess, selectedLanguageId) = await ResolveSelectedLanguageAsync(template, parseResult, cancellationToken);
        if (!languageResolutionSuccess)
        {
            return CommandResult.Failure(CliExitCodes.InvalidCommand);
        }

        // Pin the templates package version to the running CLI build and route resolution
        // through the CLI's identity channel. Identical pattern to InitCommand; the
        // resolver hard-fails when this version is not present in the identity channel.
        var inputs = new TemplateInputs
        {
            Name = parseResult.GetValue(s_nameOption),
            Output = parseResult.GetValue(s_outputOption),
            Source = source,
            Version = VersionHelper.GetDefaultTemplateVersion(),
            Channel = ExecutionContext.IdentityChannel,
            Language = selectedLanguageId
        };
        var templateResult = await template.ApplyTemplateAsync(inputs, parseResult, cancellationToken);

        var workspaceRoot = new DirectoryInfo(templateResult.OutputPath ?? ExecutionContext.WorkingDirectory.FullName);
        var agentInitBinding = PromptBinding.CreateInvertedBoolConfirm(parseResult, s_suppressAgentInitOption, defaultValue: true);
        var agentInitResult = await _agentInitCommand.PromptAndChainAsync(InteractionService, templateResult.ExitCode, workspaceRoot, agentInitBinding, cancellationToken);

        if (templateResult.OutputPath is not null && ExtensionHelper.IsExtensionHost(InteractionService, out var extensionInteractionService, out _))
        {
            extensionInteractionService.OpenEditor(templateResult.OutputPath);
        }

        return CommandResult.FromExitCode(agentInitResult.ExitCode);
    }
}

internal interface INewCommandPrompter
{
    Task<ITemplate> PromptForTemplateAsync(ITemplate[] validTemplates, CancellationToken cancellationToken);
    Task<string> PromptForProjectNameAsync(string defaultName, ParseResult parseResult, CancellationToken cancellationToken);
    Task<string> PromptForOutputPath(string v, ParseResult parseResult, Func<string, ValidationResult>? validator = null, CancellationToken cancellationToken = default, Func<string, string>? outputPathResolver = null);
}

internal class NewCommandPrompter(IInteractionService interactionService) : INewCommandPrompter
{
    public virtual async Task<string> PromptForOutputPath(string path, ParseResult parseResult, Func<string, ValidationResult>? validator = null, CancellationToken cancellationToken = default, Func<string, string>? outputPathResolver = null)
    {
        var resolvedValidator = validator;
        if (validator is not null && outputPathResolver is not null)
        {
            resolvedValidator = candidatePath => validator(outputPathResolver(candidatePath));
        }

        var outputPath = await interactionService.PromptForFilePathAsync(
            NewCommandStrings.EnterTheOutputPath,
            validator: resolvedValidator,
            binding: PromptBinding.Create(parseResult, NewCommand.s_outputOption, path),
            directory: true,
            cancellationToken: cancellationToken
            );

        return outputPathResolver?.Invoke(outputPath) ?? outputPath;
    }

    public virtual async Task<string> PromptForProjectNameAsync(string defaultName, ParseResult parseResult, CancellationToken cancellationToken)
    {
        return await interactionService.PromptForStringAsync(
            NewCommandStrings.EnterTheProjectName,
            binding: PromptBinding.Create(parseResult, NewCommand.s_nameOption, defaultName),
            validator: name => ProjectNameValidator.IsProjectNameValid(name)
                ? ValidationResult.Success()
                : ValidationResult.Error(NewCommandStrings.InvalidProjectName),
            cancellationToken: cancellationToken);
    }

    public virtual async Task<ITemplate> PromptForTemplateAsync(ITemplate[] validTemplates, CancellationToken cancellationToken)
    {
        return await interactionService.PromptForSelectionAsync(
            NewCommandStrings.SelectAProjectTemplate,
            validTemplates,
            t => t.Description.EscapeMarkup(),
            cancellationToken: cancellationToken
        );
    }
}

internal static partial class ProjectNameValidator
{
    // Regex for project name validation:
    // - Can be any characters except path separators (/ and \)
    // - Length: 1-254 characters
    // - Must not be empty or whitespace only
    [GeneratedRegex(@"^[^/\\]{1,254}$", RegexOptions.Compiled)]
    internal static partial Regex GetProjectNameRegex();

    public static bool IsProjectNameValid(string projectName)
    {
        if (string.IsNullOrWhiteSpace(projectName))
        {
            return false;
        }

        var regex = GetProjectNameRegex();
        return regex.IsMatch(projectName);
    }
}
