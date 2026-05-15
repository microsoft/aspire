// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Configuration;
using Aspire.Cli.Interaction;
using Aspire.Cli.Resources;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Utils;

namespace Aspire.Cli.Commands;

internal sealed class CertificatesCommand : ParentCommand
{
    internal override HelpGroup HelpGroup => HelpGroup.ToolsAndConfiguration;

    public CertificatesCommand(CertificatesCleanCommand cleanCommand, CertificatesTrustCommand trustCommand, IInteractionService interactionService, IFeatures features, ICliUpdateNotifier updateNotifier, CliExecutionContext executionContext, AspireCliTelemetry telemetry)
        : base("certs", CertificatesCommandStrings.Description, features, updateNotifier, executionContext, interactionService, telemetry)
    {
        ArgumentNullException.ThrowIfNull(cleanCommand);
        ArgumentNullException.ThrowIfNull(trustCommand);

        Subcommands.Add(cleanCommand);
        Subcommands.Add(trustCommand);
    }
}
