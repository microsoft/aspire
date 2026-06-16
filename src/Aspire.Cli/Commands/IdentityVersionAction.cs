// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.CommandLine.Invocation;

namespace Aspire.Cli.Commands;

/// <summary>
/// Replaces the built-in <see cref="VersionOption"/> action so <c>--version</c> reports the
/// CLI's resolved identity version (<see cref="CliExecutionContext.IdentityVersion"/>) rather
/// than reading the assembly's <see cref="System.Reflection.AssemblyInformationalVersionAttribute"/>
/// directly. This honors <c>ASPIRE_CLI_VERSION</c> / the install sidecar so an emulated build
/// reports the version it is pretending to be. When no override is in effect,
/// <see cref="CliExecutionContext.IdentityVersion"/> falls back to the assembly's informational
/// version, preserving the default System.CommandLine output exactly. See
/// <c>docs/specs/cli-identity-sidecar.md</c>.
/// </summary>
internal sealed class IdentityVersionAction(CliExecutionContext executionContext) : SynchronousCommandLineAction
{
    public override bool ClearsParseErrors => true;

    public override int Invoke(ParseResult parseResult)
    {
        parseResult.InvocationConfiguration.Output.WriteLine(executionContext.IdentityVersion);
        return CliExitCodes.Success;
    }
}
