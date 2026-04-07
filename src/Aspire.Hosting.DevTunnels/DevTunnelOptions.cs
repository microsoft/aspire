// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.DevTunnels;

/// <summary>
/// Options for the dev tunnel resource. Controls the creation and access settings of the underyling dev tunnel.
/// </summary>
public sealed class DevTunnelOptions
{
    /// <summary>
    /// Optional description for the tunnel.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Whether to allow anonymous access to this dev tunnel. If <c>false</c>, authentication is required. Defaults to <c>false</c>.
    /// </summary>
    public bool AllowAnonymous { get; set; }

    /// <summary>
    /// Optional labels to attach to the tunnel.
    /// </summary>
    public List<string>? Labels { get; set; }

    internal string ToLoggerString() => $"{{ Description={Description}, AllowAnonymous={AllowAnonymous}, Labels=[{string.Join(", ", Labels ?? [])}] }}";
}

/// <summary>
/// Options for a dev tunnel port.
/// </summary>
public sealed class DevTunnelPortOptions
{
    /// <summary>
    /// A description for this port within the dev tunnel.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Whether to allow anonymous access to this port. If <c>null</c>, defaults to the parent tunnel's setting. Defaults to <c>null</c>.
    /// </summary>
    public bool? AllowAnonymous { get; set; }

    /// <summary>
    /// Protocol type to expose. "http", "https", or "auto". Defaults to match scheme of exposed endpoint.
    /// </summary>
    public string? Protocol { get; set; }

    /// <summary>
    /// Optional labels to attach to this tunnel port.
    /// </summary>
    public List<string>? Labels { get; set; }

    internal string ToLoggerString() => $"{{ Description={Description}, AllowAnonymous={AllowAnonymous}, Protocol={Protocol}, Labels=[{string.Join(", ", Labels ?? [])}] }}";
}
