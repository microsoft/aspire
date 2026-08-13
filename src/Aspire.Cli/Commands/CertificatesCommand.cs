// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Resources;

namespace Aspire.Cli.Commands;

internal sealed class CertificatesCommand : ParentCommand
{
    internal override HelpGroup HelpGroup => HelpGroup.ToolsAndConfiguration;

    public CertificatesCommand(CertificatesCleanCommand cleanCommand, CertificatesTrustCommand trustCommand,
        CommonCommandServices services)
        : base("certs", CertificatesCommandStrings.Description, services)
    {
        ArgumentNullException.ThrowIfNull(cleanCommand);
        ArgumentNullException.ThrowIfNull(trustCommand);

        Subcommands.Add(cleanCommand);
        Subcommands.Add(trustCommand);
    }
}
