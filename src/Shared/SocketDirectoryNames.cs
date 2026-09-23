// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Shared;

/// <summary>
/// Directory names shared by socket path construction and validation.
/// </summary>
internal static class SocketDirectoryNames
{
    // Keep socket directory names compact because the full path counts against Unix socket limits.

    /// <summary>Per-user Aspire state directory beneath the user profile.</summary>
    internal const string Aspire = ".aspire";

    /// <summary>CLI state directory beneath the Aspire state directory.</summary>
    internal const string Cli = "cli";

    /// <summary>CLI and AppHost backchannel socket directory beneath the CLI state directory.</summary>
    internal const string Backchannels = "bch";

    /// <summary>Terminal-host socket and metadata directory beneath the Aspire state directory.</summary>
    internal const string Terminals = "trmnl";

    /// <summary>Windows PTY proxy socket directory beneath the Aspire state directory.</summary>
    internal const string Pty = "pty";

    /// <summary>Prefix for temporary DCP session directories containing the logging socket.</summary>
    internal const string DcpPrefix = "aspire-dcp";
}
