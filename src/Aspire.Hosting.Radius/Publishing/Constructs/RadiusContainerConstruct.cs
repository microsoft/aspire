// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Azure.Provisioning;
using Azure.Provisioning.Primitives;

namespace Aspire.Hosting.Radius.Publishing.Constructs;

/// <summary>
/// Represents a <c>Radius.Compute/containers</c> resource in the Bicep AST.
/// </summary>
/// <remarks>
/// Aligned with the Radius container v2 schema (<c>Radius.Compute/containers@2025-08-01-preview</c>).
/// The <c>imagePullPolicy</c> property has been removed from the v2 schema.
/// See: https://github.com/radius-project/radius/blob/main/eng/design-notes/extensibility/2025-08-container-resource-type.md
/// </remarks>
public sealed class RadiusContainerConstruct : ProvisionableResource
{
    private BicepValue<string>? _name;
    private BicepValue<string>? _image;
    private BicepValue<string>? _applicationId;
    private BicepDictionary<ConnectionConstruct>? _connections;

    /// <summary>The resource name.</summary>
    public BicepValue<string> ContainerName
    {
        get { Initialize(); return _name!; }
        set { Initialize(); _name!.Assign(value); }
    }

    /// <summary>Container image (e.g., "nginx:latest").</summary>
    public BicepValue<string> Image
    {
        get { Initialize(); return _image!; }
        set { Initialize(); _image!.Assign(value); }
    }

    /// <summary>Reference to the application resource ID.</summary>
    public BicepValue<string> ApplicationId
    {
        get { Initialize(); return _applicationId!; }
        set { Initialize(); _applicationId!.Assign(value); }
    }

    /// <summary>
    /// Dictionary of named connections to other resources.
    /// Keys are connection names; values contain source resource ID references.
    /// </summary>
    public BicepDictionary<ConnectionConstruct> Connections
    {
        get { Initialize(); return _connections!; }
        set { Initialize(); _connections!.Assign(value); }
    }

    /// <summary>Initializes a new <see cref="RadiusContainerConstruct"/> with the given Bicep identifier.</summary>
    public RadiusContainerConstruct(string bicepIdentifier)
        : base(bicepIdentifier, new Azure.Core.ResourceType("Radius.Compute/containers"), "2025-08-01-preview")
    {
    }

    /// <inheritdoc />
    protected override void DefineProvisionableProperties()
    {
        _name = DefineProperty<string>(nameof(ContainerName), ["name"]);
        _image = DefineProperty<string>(nameof(Image), ["properties", "containers", BicepIdentifier, "image"]);
        _applicationId = DefineProperty<string>(nameof(ApplicationId), ["properties", "application"]);
        _connections = DefineDictionaryProperty<ConnectionConstruct>(nameof(Connections), ["properties", "connections"]);
    }
}
