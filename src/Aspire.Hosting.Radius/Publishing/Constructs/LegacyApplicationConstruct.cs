// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Azure.Provisioning;
using Azure.Provisioning.Primitives;

namespace Aspire.Hosting.Radius.Publishing.Constructs;

/// <summary>
/// Represents a legacy <c>Applications.Core/applications@2023-10-01-preview</c>
/// resource in the Bicep AST. Parent for <c>Applications.*</c> portable
/// resources that still use the legacy fallback path.
/// </summary>
public sealed class LegacyApplicationConstruct : ProvisionableResource
{
    private BicepValue<string>? _name;
    private BicepValue<string>? _environmentId;

    /// <summary>The resource name.</summary>
    public BicepValue<string> ApplicationName
    {
        get { Initialize(); return _name!; }
        set { Initialize(); _name!.Assign(value); }
    }

    /// <summary>Reference to the legacy environment resource ID.</summary>
    public BicepValue<string> EnvironmentId
    {
        get { Initialize(); return _environmentId!; }
        set { Initialize(); _environmentId!.Assign(value); }
    }

    /// <summary>Initializes a new <see cref="LegacyApplicationConstruct"/> with the given Bicep identifier.</summary>
    public LegacyApplicationConstruct(string bicepIdentifier)
        : base(bicepIdentifier, new Azure.Core.ResourceType("Applications.Core/applications"), "2023-10-01-preview")
    {
    }

    /// <inheritdoc />
    protected override void DefineProvisionableProperties()
    {
        _name = DefineProperty<string>(nameof(ApplicationName), ["name"]);
        _environmentId = DefineProperty<string>(nameof(EnvironmentId), ["properties", "environment"]);
    }
}
