// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Configuration;
using Aspire.Cli.NuGet;

namespace Aspire.Cli.Commands;

internal abstract class BaseConfigSubCommand(string name, string description, IConfigurationService configurationService, CommonCommandServices services) : BaseCommand(name, description, services), IPackageMetaPrefetchingCommand
{
    protected IConfigurationService ConfigurationService { get; } = configurationService;

    public bool PrefetchesTemplatePackageMetadata => false;

    public bool PrefetchesCliPackageMetadata => false;

    /// <summary>
    /// Extension-compatible method to execute the subcommand. Prompts for input if necessary.
    /// </summary>
    public abstract Task<int> InteractiveExecuteAsync(CancellationToken cancellationToken);
}
