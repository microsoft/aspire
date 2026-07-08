// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRERADIUS004 // Experimental: ConfigureRadiusInfrastructure escape-hatch construct types are consumed internally by the publisher.

using System.Diagnostics.CodeAnalysis;
using Azure.Provisioning;
using Azure.Provisioning.Primitives;

namespace Aspire.Hosting.Radius.Publishing.Constructs;

/// <summary>
/// Represents a connection entry in a container's connections block.
/// </summary>
[Experimental("ASPIRERADIUS004", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public sealed class ConnectionConstruct : ProvisionableConstruct
{
    private BicepValue<string>? _source;

    /// <summary>Reference to the source resource ID.</summary>
    public BicepValue<string> Source
    {
        get { Initialize(); return _source!; }
        set { Initialize(); _source!.Assign(value); }
    }

    /// <summary>Initializes a new <see cref="ConnectionConstruct"/>.</summary>
    protected override void DefineProvisionableProperties()
    {
        _source = DefineProperty<string>(nameof(Source), ["source"]);
    }
}
