// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Telemetry;

/// <summary>
/// Detects whether the current CLI invocation targets the hidden <c>aspire agent telemetry</c>
/// command used by the agent telemetry hook scripts.
/// </summary>
/// <remarks>
/// This check runs against the raw command-line arguments before the host and telemetry
/// pipeline are built, which lets the CLI:
/// <list type="bullet">
///   <item>apply the agent-specific opt-out (<c>ASPIRE_CLI_AGENT_TELEMETRY_OPTOUT</c>) before
///   any reported telemetry provider is created; and</item>
///   <item>suppress the generic <c>aspire/cli/main</c> reported span so a hook event emits only
///   the single dedicated <c>aspire/cli/agent_telemetry</c> span rather than two spans.</item>
/// </list>
/// The hook scripts always invoke the command as <c>aspire agent telemetry ...</c> with no global
/// options preceding the command path, so the detector requires <c>agent</c> and <c>telemetry</c>
/// to be the first two arguments. Matching only the leading tokens (rather than anywhere in the
/// argument list) avoids false positives where <c>agent</c>/<c>telemetry</c> appear as option or
/// positional values of an unrelated command (for example <c>aspire config set agent telemetry</c>).
/// A manual invocation that places a global option before the command path is intentionally not
/// matched; the only consequence is that the generic main span is not suppressed for that rare case.
/// </remarks>
internal static class AgentTelemetryInvocation
{
    private const string AgentCommandName = "agent";
    private const string TelemetryCommandName = "telemetry";

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="args"/> represents an
    /// <c>aspire agent telemetry</c> invocation.
    /// </summary>
    /// <param name="args">The raw command-line arguments passed to the CLI.</param>
    public static bool Matches(string[]? args)
    {
        return args is { Length: >= 2 } &&
            string.Equals(args[0], AgentCommandName, StringComparison.Ordinal) &&
            string.Equals(args[1], TelemetryCommandName, StringComparison.Ordinal);
    }
}
