// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using Aspire.Cli.Templating;
using Microsoft.Extensions.Configuration;

namespace Aspire.Cli.Commands;

internal sealed class TemplateCommand : BaseCommand
{
    private readonly Func<ParseResult, CancellationToken, Task<CommandResult>> _executeCallback;
    private readonly IConfiguration _configuration;

    internal override bool PrefetchesTemplatePackageMetadata => true;

    public TemplateCommand(ITemplate template, Func<ParseResult, CancellationToken, Task<CommandResult>> executeCallback, IConfiguration configuration, CommonCommandServices services)
        : base(template.Name, template.Description, services)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(executeCallback);
        ArgumentNullException.ThrowIfNull(configuration);

        template.ApplyOptions(this);
        _executeCallback = executeCallback;
        _configuration = configuration;
    }

    // Template commands are user-facing interactive commands (e.g., `aspire new aspire-starter`)
    // and should show update notifications, just like the parent NewCommand.
    protected override bool UpdateNotificationsEnabled => true;

    internal override void PrepareForExecution(ParseResult parseResult)
    {
        if (NewCommand.GetEffectiveSource(parseResult, _configuration) is not null)
        {
            // The foreground template lookup applies either --source or the configured source.
            // Background prefetch does not know about either input and could contact fallback feeds.
            DisableTemplatePackageMetadataPrefetchingForInvocation();
        }
    }

    protected override Task<CommandResult> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        return _executeCallback(parseResult, cancellationToken);
    }
}
