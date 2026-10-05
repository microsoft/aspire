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

        // IdentityVersion can itself carry a "+<sha>" build-metadata suffix: ASPIRE_CLI_VERSION /
        // the sidecar version field accept full informational versions like "13.4.5+73114e86" (see
        // ValidateVersion in IdentityResolver), and IdentitySdkVersion exists precisely to strip
        // that suffix off IdentityVersion. IdentityCommit resolves through its own independent
        // fallback chain. Split the version on its build metadata so we never emit a malformed
        // double-'+' (e.g. "13.4.5+73114e86+73114e86"), and prefer the explicit IdentityCommit but
        // fall back to the sha embedded in the version so a user-supplied "+<sha>" is preserved.
        var plusIndex = version.IndexOf('+');
        if (plusIndex > 0)
        {
            if (string.IsNullOrEmpty(commit))
            {
                commit = version[(plusIndex + 1)..];
            }

            version = version[..plusIndex];
        }

        var output = string.IsNullOrEmpty(commit) ? version : $"{version}+{commit}";
        parseResult.InvocationConfiguration.Output.WriteLine(output);
        return CliExitCodes.Success;
    }
}
