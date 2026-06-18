// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.CommandLine.Invocation;

namespace Aspire.Cli.Commands;

/// <summary>
/// Replaces the built-in <see cref="VersionOption"/> action so <c>--version</c> reports the
/// CLI's resolved identity version (<see cref="CliExecutionContext.IdentityVersion"/>) with the
/// optional commit SHA (<see cref="CliExecutionContext.IdentityCommit"/>) appended as build metadata
/// (e.g., <c>13.4.5+abcdef01</c>). This honors <c>ASPIRE_CLI_VERSION</c> / <c>ASPIRE_CLI_COMMIT</c> /
/// the install sidecar so an emulated build reports the version and commit it is pretending to be.
/// When no override is in effect, these fall back to the assembly's informational version,
/// preserving the default System.CommandLine output exactly. See <c>docs/specs/cli-identity-sidecar.md</c>.
/// </summary>
internal sealed class IdentityVersionAction(CliExecutionContext executionContext) : SynchronousCommandLineAction
{
    public override bool ClearsParseErrors => true;

    public override int Invoke(ParseResult parseResult)
    {
        var version = executionContext.IdentityVersion;
        var commit = executionContext.IdentityCommit;
        var output = string.IsNullOrEmpty(commit) ? version : $"{version}+{commit}";
        parseResult.InvocationConfiguration.Output.WriteLine(output);
        return CliExitCodes.Success;
    }
}
