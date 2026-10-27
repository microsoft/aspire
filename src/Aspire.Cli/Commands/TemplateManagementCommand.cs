// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Cli.Resources;
using Aspire.Cli.Templating;

namespace Aspire.Cli.Commands;

internal sealed class TemplateManagementCommand : ParentCommand
{
    internal override HelpGroup HelpGroup => HelpGroup.ToolsAndConfiguration;

    public TemplateManagementCommand(TemplateSourceStore store, CommonCommandServices services)
        : base("template", NewCommandStrings.TemplateCommandDescription, services)
    {
        Subcommands.Add(new SourceCommand(store, services));
    }

    private sealed class SourceCommand : ParentCommand
    {
        public SourceCommand(TemplateSourceStore store, CommonCommandServices services)
            : base("source", NewCommandStrings.TemplateSourceCommandDescription, services)
        {
            Subcommands.Add(new AddSourceCommand(store, services));
            Subcommands.Add(new RemoveSourceCommand(store, services));
        }
    }

    private sealed class AddSourceCommand : BaseCommand
    {
        private readonly Argument<string> _name = new("name") { Description = NewCommandStrings.TemplateSourceNameDescription };
        private readonly Option<string?> _path = new("--path") { Description = NewCommandStrings.TemplateSourcePathDescription };
        private readonly Option<string?> _repository = new("--repository") { Description = NewCommandStrings.TemplateSourceRepositoryDescription };
        private readonly Option<string?> _ref = new("--ref") { Description = NewCommandStrings.TemplateSourceRefDescription };
        private readonly Option<string?> _href = new("--href") { Description = NewCommandStrings.TemplateSourceHrefDescription };
        private readonly TemplateSourceStore _store;

        public AddSourceCommand(TemplateSourceStore store, CommonCommandServices services)
            : base("add", NewCommandStrings.TemplateSourceAddDescription, services)
        {
            _store = store;
            Arguments.Add(_name);
            Options.Add(_path);
            Options.Add(_repository);
            Options.Add(_ref);
            Options.Add(_href);
        }

        protected override async Task<CommandResult> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
        {
            var name = parseResult.GetRequiredValue(_name);
            JsonObject source = new();
            foreach (var (field, option) in new[] { ("path", _path), ("repository", _repository), ("ref", _ref), ("href", _href) })
            {
                if (parseResult.GetValue(option) is { } value)
                {
                    source[field] = value;
                }
            }
            try
            {
                await _store.AddAsync(name, source, cancellationToken);
                InteractionService.DisplaySuccess(string.Format(CultureInfo.CurrentCulture, NewCommandStrings.TemplateSourceAdded, name));
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
