// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Commands.Sdk;

/// <summary>
/// Parent command for SDK-related operations.
/// Usage: aspire sdk [subcommand]
/// </summary>
internal sealed class SdkCommand : ParentCommand
{
    public SdkCommand(
        SdkGenerateCommand generateCommand,
        SdkDumpCommand dumpCommand,
        CommonCommandServices services)
        : base("sdk", "Commands for generating SDKs for building Aspire integrations in other languages.", services)
    {
        Hidden = true;
        Subcommands.Add(generateCommand);
        Subcommands.Add(dumpCommand);
    }
}
