// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.DevTunnels;

internal interface IDevTunnelClient
{
    Task<Version> GetVersionAsync(ILogger? logger = default, CancellationToken cancellationToken = default);

    Task<UserLoginStatus> GetUserLoginStatusAsync(ILogger? logger = default, CancellationToken cancellationToken = default);

    Task<UserLoginStatus> UserLoginAsync(LoginProvider provider, ILogger? logger = default, CancellationToken cancellationToken = default);

    Task<DevTunnelStatus> CreateTunnelAsync(string tunnelId, DevTunnelOptions options, ILogger? logger = default, CancellationToken cancellationToken = default);

    Task<DevTunnelPortList> GetPortListAsync(string tunnelId, ILogger? logger = default, CancellationToken cancellationToken = default);

    Task<DevTunnelPortStatus> CreatePortAsync(string tunnelId, int portNumber, DevTunnelPortOptions options, ILogger? logger = default, CancellationToken cancellationToken = default);

    Task<DevTunnelPortDeleteResult> DeletePortAsync(string tunnelId, int portNumber, ILogger? logger = default, CancellationToken cancellationToken = default);

    Task<DevTunnelStatus> GetTunnelAsync(string tunnelId, ILogger? logger = default, CancellationToken cancellationToken = default);

    Task<DevTunnelAccessStatus> GetAccessAsync(string tunnelId, int? portNumber = null, ILogger? logger = default, CancellationToken cancellationToken = default);
}

internal sealed record UserLoginStatus(string Status, LoginProvider Provider, string Username)
{
    public bool IsLoggedIn => string.Equals(Status, "Logged in", StringComparison.OrdinalIgnoreCase);
}

internal enum LoginProvider
{
    Microsoft,
    GitHub
}

internal sealed record DevTunnelStatus(string TunnelId, int HostConnections, int ClientConnections, string Description, IReadOnlyList<string> Labels)
{
    public IReadOnlyList<DevTunnelPort> Ports { get; init; } = [];
}

internal sealed record DevTunnelPortList
{
    public IReadOnlyList<DevTunnelPort> Ports { get; init; } = [];
}

internal sealed record DevTunnelPort(int PortNumber, string Protocol)
{
    public Uri? PortUri { get; init; }

    public int? ClientConnections { get; init; }
}

internal sealed record DevTunnelPortStatus(string TunnelId, int PortNumber, string Protocol, int ClientConnections);

internal sealed record DevTunnelPortDeleteResult(string DeletedPort);

internal sealed record DevTunnelAccessStatus
{
    public IReadOnlyList<AccessControlEntry> AccessControlEntries { get; init; } = [];

    public sealed record AccessControlEntry(string Type, bool IsDeny, bool IsInherited, IReadOnlyList<string> Subjects, IReadOnlyList<string> Scopes);

    internal string LogAnonymousAccessPolicy(ILogger logger)
    {
        const string AnonymousType = "Anonymous";
        const string ConnectScope = "connect";

        static bool HasConnectScope(AccessControlEntry entry) => entry.Scopes is { } scopes && scopes.Any(s => string.Equals(s, ConnectScope, StringComparison.OrdinalIgnoreCase));

        var entries = AccessControlEntries;

        var portHasInheritedAnonymousAllow = entries.Any(e => string.Equals(e.Type, AnonymousType, StringComparison.OrdinalIgnoreCase)
                                                              && !e.IsDeny
                                                              && e.IsInherited
                                                              && HasConnectScope(e));
        var portHasExplicitAnonymousAllow = entries.Any(e => string.Equals(e.Type, AnonymousType, StringComparison.OrdinalIgnoreCase)
                                                             && !e.IsDeny
                                                             && !e.IsInherited
                                                             && HasConnectScope(e));
        var portHasExplicitAnonymousDeny = entries.Any(e => string.Equals(e.Type, AnonymousType, StringComparison.OrdinalIgnoreCase)
                                                            && e.IsDeny
                                                            && HasConnectScope(e));

        // Derive tunnel-level allow from presence of inherited allow (since we don't receive tunnel access status directly here)
        var tunnelHasAnonymousAllow = portHasInheritedAnonymousAllow;

        string effective;
        if (tunnelHasAnonymousAllow && portHasInheritedAnonymousAllow && !portHasExplicitAnonymousDeny && !portHasExplicitAnonymousAllow)
        {
            // Case 1: tunnel allows anonymous; port inherits allow; no deny override
            logger.LogInformation("!! Anonymous access is allowed (inherited from tunnel) !!");
            effective = "Allowed";
        }
        else if (tunnelHasAnonymousAllow && portHasExplicitAnonymousDeny)
        {
            // Case 2: tunnel allows anonymous but port explicitly denies
            logger.LogInformation("Anonymous access is not allowed (tunnel allows it but port explicitly denies it)");
            effective = "Denied";
        }
        else if (!tunnelHasAnonymousAllow && portHasExplicitAnonymousAllow && !portHasExplicitAnonymousDeny)
        {
            // Case 3: tunnel does not allow but port explicitly allows
            logger.LogInformation("!! Anonymous access is allowed (port explicitly allows it) !!");
            effective = "Allowed";
        }
        else if (!tunnelHasAnonymousAllow && portHasExplicitAnonymousDeny)
        {
            // Case 4: tunnel does not allow and port explicitly denies
            logger.LogInformation("Anonymous access is not allowed (tunnel does not allow it and port explicitly denies it)");
            effective = "Denied";
        }
        else if (tunnelHasAnonymousAllow && portHasExplicitAnonymousAllow && !portHasExplicitAnonymousDeny)
        {
            // Case 5: tunnel allows anonymous; port allows anonymous; no deny override
            logger.LogInformation("!! Anonymous access is allowed (tunnel allows it and port allows it) !!");
            effective = "Allowed";
        }
        else if (!tunnelHasAnonymousAllow && !portHasExplicitAnonymousAllow && !portHasExplicitAnonymousDeny)
        {
            // Case 6: tunnel does not allow; port does not explicitly allow or deny
            logger.LogInformation("Anonymous access is not allowed (tunnel does not allow it and port does not explicitly allow or deny it)");
            effective = "Denied";
        }
        else
        {
            // Fallback / other combinations
            effective = "Unknown";
            logger.LogDebug("Anonymous access: TunnelAllow={TunnelAllow} InheritedAllow={InheritedAllow} ExplicitAllow={ExplicitAllow} ExplicitDeny={ExplicitDeny} Effective={Effective}",
                tunnelHasAnonymousAllow, portHasInheritedAnonymousAllow, portHasExplicitAnonymousAllow, portHasExplicitAnonymousDeny, effective);
        }

        return effective;
    }
}
