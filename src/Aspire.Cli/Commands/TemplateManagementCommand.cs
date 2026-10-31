// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Cli.Interaction;
using Aspire.Cli.Resources;
using Aspire.Cli.Templating;
using Aspire.Cli.Utils;
using Spectre.Console;

namespace Aspire.Cli.Commands;

internal sealed class TemplateManagementCommand : ParentCommand
{
    internal override HelpGroup HelpGroup => HelpGroup.ToolsAndConfiguration;

    public TemplateManagementCommand(TemplateSourceStore store, CommonCommandServices services)
        : base("template", NewCommandStrings.TemplateCommandDescription, services)
    {
        Subcommands.Add(new SourceCommand(store, services));
    }

    internal sealed record AddSourceOptions(string Name, string? Path, string? Repository, string? Ref)
    {
        internal static Argument<string> NameArgument { get; } = new("name") { Description = NewCommandStrings.TemplateSourceNameDescription };
        internal static Option<string?> PathOption { get; } = new("--path") { Description = NewCommandStrings.TemplateSourcePathDescription };
        internal static Option<string?> RepositoryOption { get; } = new("--repository") { Description = NewCommandStrings.TemplateSourceRepositoryDescription };
        internal static Option<string?> RefOption { get; } = new("--ref") { Description = NewCommandStrings.TemplateSourceRefDescription };

        public static AddSourceOptions FromParseResult(ParseResult parseResult)
        {
            var options = new AddSourceOptions(
                parseResult.GetRequiredValue(NameArgument),
                parseResult.GetValue(PathOption),
                parseResult.GetValue(RepositoryOption),
                parseResult.GetValue(RefOption));
            options.Validate();
            return options;
        }

        public JsonObject ToJsonObject()
        {
            JsonObject source = new();
            foreach (var (field, value) in new[] { ("path", Path), ("repository", Repository), ("ref", Ref) })
            {
                if (value is not null)
                {
                    source[field] = value;
                }
            }
            return source;
        }

        private void Validate()
        {
            TemplateSourceConfiguration.ValidateName(Name);
            TemplateSourceConfiguration.Parse(Path, Repository, Ref, $"templateSources.{Name}", requireComplete: true);
        }
    }

    private sealed class SourceCommand : ParentCommand
    {
        public SourceCommand(TemplateSourceStore store, CommonCommandServices services)
            : base("source", NewCommandStrings.TemplateSourceCommandDescription, services)
        {
            Subcommands.Add(new AddSourceCommand(store, services));
            Subcommands.Add(new RemoveSourceCommand(store, services));
            Subcommands.Add(new ListSourcesCommand(store, services));
        }
    }

    private sealed class ListSourcesCommand : BaseCommand
    {
        private readonly Option<OutputFormat> _format = new("--format") { Description = SharedCommandStrings.LsFormatOptionDescription };
        private readonly TemplateSourceStore _store;

        public ListSourcesCommand(TemplateSourceStore store, CommonCommandServices services)
            : base("list", NewCommandStrings.TemplateSourceListDescription, services)
        {
            _store = store;
            Options.Add(_format);
        }

        protected override async Task<CommandResult> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
        {
            JsonObject sources;
            try
            {
                sources = await _store.ListAsync(cancellationToken);
            }
            catch (TemplateSourceConfigurationException ex)
            {
                return CommandResult.Failure(CliExitCodes.InvalidCommand, ex.Message);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                return CommandResult.Failure(CliExitCodes.InvalidCommand, NewCommandStrings.TemplateSourceReadFailed);
            }

            var orderedSources = sources.OrderBy(source => source.Key, StringComparers.TemplateSourceName)
                .ThenBy(source => source.Key, StringComparer.Ordinal);
            if (parseResult.GetValue(_format) == OutputFormat.Json)
            {
                JsonObject result = new();
                foreach (var (name, source) in orderedSources)
                {
                    result[name] = source?.DeepClone();
                }

                InteractionService.DisplayRawText(JsonSerializer.Serialize(result, JsonSourceGenerationContext.Default.JsonObject), ConsoleOutput.Standard);
            }
            else if (sources.Count == 0)
            {
                InteractionService.DisplayMessage(KnownEmojis.Information, NewCommandStrings.TemplateSourceListEmpty);
            }
            else
            {
                var table = new Table()
                    .AddBoldColumn(NewCommandStrings.TemplateSourceNameHeader)
                    .AddBoldColumn(NewCommandStrings.TemplateSourceConfigurationHeader);
                foreach (var (name, source) in orderedSources)
                {
                    var configuration = source is JsonObject definition
                        ? string.Join(Environment.NewLine, definition.Select(field => $"{field.Key}: {field.Value?.ToString() ?? "null"}"))
                        : source?.ToJsonString() ?? "null";
                    table.AddRow(name.EscapeMarkup(), string.IsNullOrEmpty(configuration) ? "-" : configuration.EscapeMarkup());
                }

                InteractionService.DisplayRenderable(table);
            }

            return CommandResult.Success();
        }
    }

    private sealed class AddSourceCommand : BaseCommand
    {
        private readonly Option<bool> _yesOption = new("--yes", "-y")
        {
            Description = NewCommandStrings.TemplateSourceYesOptionDescription
        };
        private readonly TemplateSourceStore _store;

        public AddSourceCommand(TemplateSourceStore store, CommonCommandServices services)
            : base("add", NewCommandStrings.TemplateSourceAddDescription, services)
        {
            _store = store;
            Arguments.Add(AddSourceOptions.NameArgument);
            Options.Add(AddSourceOptions.PathOption);
            Options.Add(AddSourceOptions.RepositoryOption);
            Options.Add(AddSourceOptions.RefOption);
            Options.Add(_yesOption);

            AddNonInteractiveRequiresYesValidator(this, _yesOption);
        }

        protected override async Task<CommandResult> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
        {
            try
            {
                var options = AddSourceOptions.FromParseResult(parseResult);
                var confirmed = await InteractionService.PromptConfirmAsync(
                    string.Format(CultureInfo.CurrentCulture, NewCommandStrings.TemplateSourceTrustPrompt,
                        options.Name.EscapeMarkup(), (options.Repository ?? options.Path!).EscapeMarkup()),
                    binding: PromptBinding.Create(parseResult, _yesOption),
                    cancellationToken: cancellationToken);
                if (!confirmed)
                {
                    return CommandResult.Cancelled();
                }

                await _store.AddAsync(options.Name, options.ToJsonObject(), cancellationToken);
                InteractionService.DisplaySuccess(string.Format(CultureInfo.CurrentCulture, NewCommandStrings.TemplateSourceAdded, options.Name));
                return CommandResult.Success();
            }
            catch (TemplateSourceConfigurationException ex)
            {
                return CommandResult.Failure(CliExitCodes.InvalidCommand, ex.Message);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                return CommandResult.Failure(CliExitCodes.InvalidCommand, NewCommandStrings.TemplateSourceWriteFailed);
            }
        }
    }

    private sealed class RemoveSourceCommand : BaseCommand
    {
        private readonly Argument<string> _name = new("name") { Description = NewCommandStrings.TemplateSourceNameDescription };
        private readonly TemplateSourceStore _store;

        public RemoveSourceCommand(TemplateSourceStore store, CommonCommandServices services)
            : base("remove", NewCommandStrings.TemplateSourceRemoveDescription, services)
        {
            _store = store;
            Arguments.Add(_name);
        }

        protected override async Task<CommandResult> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
        {
            var name = parseResult.GetRequiredValue(_name);
            try
            {
                if (!await _store.RemoveAsync(name, cancellationToken))
                {
                    return CommandResult.Failure(CliExitCodes.ConfigNotFound,
                        string.Format(CultureInfo.CurrentCulture, NewCommandStrings.TemplateSourceNotFound, name));
                }
                InteractionService.DisplaySuccess(string.Format(CultureInfo.CurrentCulture, NewCommandStrings.TemplateSourceRemoved, name));
                return CommandResult.Success();
            }
            catch (TemplateSourceConfigurationException ex)
            {
                return CommandResult.Failure(CliExitCodes.InvalidCommand, ex.Message);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                return CommandResult.Failure(CliExitCodes.InvalidCommand, NewCommandStrings.TemplateSourceWriteFailed);
            }
        }
    }
}
