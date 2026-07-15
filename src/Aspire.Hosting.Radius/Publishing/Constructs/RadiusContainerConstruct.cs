// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRERADIUS004 // Experimental: ConfigureRadiusInfrastructure escape-hatch construct types are consumed internally by the publisher.

using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.Radius.ResourceMapping;
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
[Experimental("ASPIRERADIUS004", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public sealed class RadiusContainerConstruct : ProvisionableResource
{
    private readonly string _containerName;
    private BicepValue<string>? _name;
    private BicepValue<string>? _image;
    private BicepValue<string>? _applicationId;
    private BicepValue<string>? _environmentId;
    private BicepDictionary<ConnectionConstruct>? _connections;
    private BicepDictionary<ContainerEnvVarConstruct>? _env;
    private BicepDictionary<ContainerPortConstruct>? _ports;

    /// <summary>The resource name.</summary>
    public BicepValue<string> ContainerName
    {
        get { Initialize(); return _name!; }
        set { Initialize(); _name!.Assign(value); }
    }

    /// <summary>
    /// The immutable <c>properties.containers</c> map key captured at construction (the Aspire
    /// resource name). The container v2 schema and the Radius recipe require this to equal the
    /// top-level <c>name:</c> (<see cref="ContainerName"/>); the publisher validates that invariant
    /// after running <c>ConfigureRadiusInfrastructure</c> callbacks so service discovery (which is
    /// derived from the resource name) cannot silently disagree with the generated Service name.
    /// </summary>
    internal string ContainerMapKey => _containerName;

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

    /// <summary>Reference to the environment resource ID.</summary>
    public BicepValue<string> EnvironmentId
    {
        get { Initialize(); return _environmentId!; }
        set { Initialize(); _environmentId!.Assign(value); }
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

    /// <summary>
    /// Environment variables for the container, keyed by variable name. Each entry carries
    /// a <c>value</c> (a literal or a reference to a Bicep parameter for secret values).
    /// </summary>
    public BicepDictionary<ContainerEnvVarConstruct> Env
    {
        get { Initialize(); return _env!; }
        set { Initialize(); _env!.Assign(value); }
    }

    /// <summary>
    /// Ports exposed by the container, keyed by port name. Each entry carries a
    /// <c>containerPort</c> and an optional <c>protocol</c>.
    /// </summary>
    public BicepDictionary<ContainerPortConstruct> Ports
    {
        get { Initialize(); return _ports!; }
        set { Initialize(); _ports!.Assign(value); }
    }

    /// <summary>
    /// Initializes a new <see cref="RadiusContainerConstruct"/> with the given Bicep
    /// identifier and Radius container resource name.
    /// </summary>
    /// <param name="bicepIdentifier">
    /// The Bicep identifier for this resource. May be sanitized (e.g., hyphens become
    /// underscores) so it remains a valid C#/Bicep identifier.
    /// </param>
    /// <param name="containerName">
    /// The Radius container resource name as it should appear in the deployed manifest.
    /// This value is used as the map key under <c>properties.containers</c>, which the
    /// Radius container v2 schema requires to match the resource <c>name</c> field.
    /// It must be the unsanitized resource name (hyphens preserved); using
    /// <c>BicepIdentifier</c> here would emit a key that does not match the <c>name</c>
    /// when the resource name contains characters that get sanitized.
    /// </param>
    public RadiusContainerConstruct(string bicepIdentifier, string containerName)
        : base(bicepIdentifier, new Azure.Core.ResourceType(RadiusResourceTypes.Containers), RadiusResourceTypes.RadiusApiVersion)
    {
        ArgumentException.ThrowIfNullOrEmpty(containerName);
        _containerName = containerName;
    }

    /// <inheritdoc />
    protected override void DefineProvisionableProperties()
    {
        _name = DefineProperty<string>(nameof(ContainerName), ["name"]);
        // The container v2 schema keys `properties.containers` by the resource name (the
        // value emitted at `name:`), not by the Bicep identifier. Using BicepIdentifier
        // here would (a) snapshot the pre-rename identifier if a ConfigureRadiusInfrastructure
        // callback renamed it, and (b) emit a key with sanitized characters (hyphens →
        // underscores) that no longer matches `name`.
        _image = DefineProperty<string>(nameof(Image), ["properties", "containers", _containerName, "image"]);
        _applicationId = DefineProperty<string>(nameof(ApplicationId), ["properties", "application"]);
        // The Radius.Compute/containers v2 schema requires an explicit environment reference
        // so the control plane can resolve the recipe pack that provisions the container.
        _environmentId = DefineProperty<string>(nameof(EnvironmentId), ["properties", "environment"]);
        _connections = DefineDictionaryProperty<ConnectionConstruct>(nameof(Connections), ["properties", "connections"]);
        // env and ports live inside the per-container object under `properties.containers.<name>`,
        // keyed (like image) by the resource name so the map key matches the emitted `name:`.
        _env = DefineDictionaryProperty<ContainerEnvVarConstruct>(nameof(Env), ["properties", "containers", _containerName, "env"]);
        _ports = DefineDictionaryProperty<ContainerPortConstruct>(nameof(Ports), ["properties", "containers", _containerName, "ports"]);
    }
}
