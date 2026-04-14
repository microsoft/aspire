// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Azure.Provisioning;
using Azure.Provisioning.Primitives;

namespace Aspire.Hosting.Radius.Publishing.Constructs;

/// <summary>
/// Represents a connection entry in a container's connections block.
/// </summary>
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
