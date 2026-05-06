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

    /// <summary>
    /// Optional region to create the dev tunnel in.
    /// If not specified, the region will be selected automatically based on the ping.
    /// </summary>
    public DevTunnelRegion? Region { get; set; }

    internal string RegionCode =>
        Region switch
        {
            DevTunnelRegion.WestEurope => "euw",
            DevTunnelRegion.UkSouth => "uks1",
            DevTunnelRegion.NorthEurope => "eun1",
            DevTunnelRegion.EastUs => "use",
            DevTunnelRegion.EastUs2 => "use2",
            DevTunnelRegion.WestUs2 => "usw2",
            DevTunnelRegion.WestUs3 => "usw3",
            DevTunnelRegion.CentralIndia => "inc1",
            DevTunnelRegion.SouthEastAsia => "asse",
            DevTunnelRegion.BrazilSouth => "brs",
            DevTunnelRegion.AustraliaCentral => "auc1",
            DevTunnelRegion.AustraliaEast => "aue",
            DevTunnelRegion.JapanEast => "jpe1",
            null => string.Empty,
            _ => throw new ArgumentException("Invalid region specified", nameof(Region)),
        };

    internal string ToLoggerString() => $"{{ Description={Description}, AllowAnonymous={AllowAnonymous}, Labels=[{string.Join(", ", Labels ?? [])}], Region={Region} }}";
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

/// <summary>
/// Region options for dev tunnel creation.
/// </summary>
public enum DevTunnelRegion : byte
{
    /// <summary>
    /// West Europe region.
    /// </summary>
    WestEurope,

    /// <summary>
    /// UK South region.
    /// </summary>
    UkSouth,

    /// <summary>
    /// North Europe region.
    /// </summary>
    NorthEurope,

    /// <summary>
    /// East US region.
    /// </summary>
    EastUs,

    /// <summary>
    /// East US 2 region.
    /// </summary>
    EastUs2,

    /// <summary>
    /// Central India region.
    /// </summary>
    CentralIndia,

    /// <summary>
    /// West US 3 region.
    /// </summary>
    WestUs3,

    /// <summary>
    /// West US 2 region.
    /// </summary>
    WestUs2,

    /// <summary>
    /// Southeast Asia region.
    /// </summary>
    SouthEastAsia,

    /// <summary>
    /// Brazil South region.
    /// </summary>
    BrazilSouth,

    /// <summary>
    /// Australia Central region.
    /// </summary>
    AustraliaCentral,

    /// <summary>
    /// Australia East region.
    /// </summary>
    AustraliaEast,

    /// <summary>
    /// Japan East region.
    /// </summary>
    JapanEast
}
