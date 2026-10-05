// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Configuration;

namespace Aspire.Cli.Commands;

internal abstract class BaseConfigSubCommand(string name, string description, IConfigurationService configurationService, CommonCommandServices services) : BaseCommand(name, description, services)
{
    protected IConfigurationService ConfigurationService { get; } = configurationService;

    /// <summary>
    /// Extension-compatible method to execute the subcommand. Prompts for input if necessary.
    /// </summary>
    public abstract Task<int> InteractiveExecuteAsync(CancellationToken cancellationToken);
}
