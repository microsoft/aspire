// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using Aspire.Cli.Templating;

namespace Aspire.Cli.Commands;

internal sealed class TemplateCommand : BaseCommand
{
    private readonly Func<ParseResult, CancellationToken, Task<CommandResult>> _executeCallback;

    public TemplateCommand(ITemplate template, Func<ParseResult, CancellationToken, Task<CommandResult>> executeCallback, CommonCommandServices services)
        : base(template.Name, template.Description, services)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(executeCallback);

        template.ApplyOptions(this);
        _executeCallback = executeCallback;
    }

    // Template commands are user-facing interactive commands (e.g., `aspire new aspire-starter`)
    // and should show update notifications, just like the parent NewCommand.
    protected override bool UpdateNotificationsEnabled => true;

    protected override Task<CommandResult> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        return _executeCallback(parseResult, cancellationToken);
    }
}
