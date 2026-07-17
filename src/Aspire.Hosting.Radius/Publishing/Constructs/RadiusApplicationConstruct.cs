// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRERADIUS004 // Experimental: ConfigureRadiusInfrastructure escape-hatch construct types are consumed internally by the publisher.

using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.Radius.ResourceMapping;
using Azure.Provisioning;
using Azure.Provisioning.Primitives;

namespace Aspire.Hosting.Radius.Publishing.Constructs;

/// <summary>
/// Represents a <c>Radius.Core/applications</c> resource in the Bicep AST.
/// </summary>
[Experimental("ASPIRERADIUS004", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public sealed class RadiusApplicationConstruct : ProvisionableResource
{
    private BicepValue<string>? _name;
    private BicepValue<string>? _environmentId;

    /// <summary>The resource name.</summary>
    public BicepValue<string> ApplicationName
    {
        get { Initialize(); return _name!; }
        set { Initialize(); _name!.Assign(value); }
    }

    /// <summary>Reference to the environment resource ID.</summary>
    public BicepValue<string> EnvironmentId
    {
        get { Initialize(); return _environmentId!; }
        set { Initialize(); _environmentId!.Assign(value); }
    }

    /// <summary>Initializes a new <see cref="RadiusApplicationConstruct"/> with the given Bicep identifier.</summary>
    public RadiusApplicationConstruct(string bicepIdentifier)
        : base(bicepIdentifier, new Azure.Core.ResourceType(RadiusResourceTypes.Applications), RadiusResourceTypes.RadiusApiVersion)
    {
    }

    /// <inheritdoc />
    protected override void DefineProvisionableProperties()
    {
        _name = DefineProperty<string>(nameof(ApplicationName), ["name"]);
        _environmentId = DefineProperty<string>(nameof(EnvironmentId), ["properties", "environment"]);
    }
}
