// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRERADIUS004 // Experimental: ConfigureRadiusInfrastructure escape-hatch construct types are consumed internally by the publisher.

using System.Diagnostics.CodeAnalysis;
using Azure.Provisioning;
using Azure.Provisioning.Primitives;

namespace Aspire.Hosting.Radius.Publishing.Constructs;

/// <summary>
/// Represents a single port entry in a container's <c>ports</c> block.
/// </summary>
/// <remarks>
/// The Radius container schema models <c>ports</c> as a map keyed by the port name, where
/// each entry exposes a <c>containerPort</c> (the port the application listens on inside the
/// container) and an optional <c>protocol</c> (<c>TCP</c> or <c>UDP</c>).
/// See: https://github.com/radius-project/radius/blob/main/eng/design-notes/extensibility/2025-08-container-resource-type.md
/// </remarks>
[Experimental("ASPIRERADIUS004", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
[AspireExport]
public sealed class ContainerPortConstruct : ProvisionableConstruct
{
    private BicepValue<int>? _containerPort;
    private BicepValue<string>? _protocol;

    /// <summary>The port the application listens on inside the container.</summary>
    public BicepValue<int> ContainerPort
    {
        get { Initialize(); return _containerPort!; }
        set { Initialize(); _containerPort!.Assign(value); }
    }

    /// <summary>The transport protocol (<c>TCP</c> or <c>UDP</c>).</summary>
    public BicepValue<string> Protocol
    {
        get { Initialize(); return _protocol!; }
        set { Initialize(); _protocol!.Assign(value); }
    }

    /// <inheritdoc />
    protected override void DefineProvisionableProperties()
    {
        _containerPort = DefineProperty<int>(nameof(ContainerPort), ["containerPort"]);
        _protocol = DefineProperty<string>(nameof(Protocol), ["protocol"]);
    }
}
