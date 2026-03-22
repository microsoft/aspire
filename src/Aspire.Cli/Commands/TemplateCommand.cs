// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using Aspire.Cli.Configuration;
using Aspire.Cli.Interaction;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Templating;
using Aspire.Cli.Utils;

namespace Aspire.Cli.Commands;

internal sealed class TemplateCommand : BaseCommand
{
    private readonly Func<ParseResult, CancellationToken, Task<int>> _executeCallback;

    public TemplateCommand(ITemplate template, Func<ParseResult, CancellationToken, Task<int>> executeCallback, IFeatures features, ICliUpdateNotifier updateNotifier, CliExecutionContext executionContext, IInteractionService interactionService, AspireCliTelemetry telemetry)
        : base(template.Name, template.Description, features, updateNotifier, executionContext, interactionService, telemetry)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(executeCallback);

        template.ApplyOptions(this);
        _executeCallback = executeCallback;
    }

    protected override Task<int> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        return _executeCallback(parseResult, cancellationToken);
    }
}
