// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRERADIUS004 // Experimental: ConfigureRadiusInfrastructure escape-hatch construct types are consumed internally by the publisher.

using System.Diagnostics.CodeAnalysis;
using Azure.Provisioning;
using Azure.Provisioning.Primitives;

namespace Aspire.Hosting.Radius.Publishing.Constructs;

/// <summary>
/// A single entry in a secret store's <c>data</c> map. For inline (Radius-created)
/// stores both <see cref="Value"/> (a reference to a valueless <c>@secure()</c> param)
/// and optionally <see cref="Encoding"/> are assigned. For existing/sealed references
/// nothing is assigned, so the entry emits as an empty object (<c>{}</c>) naming a key
/// to expose from the referenced <c>Secret</c>.
/// </summary>
[Experimental("ASPIRERADIUS004", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public sealed class RadiusSecretStoreDataEntryConstruct : ProvisionableConstruct
{
    private BicepValue<string>? _value;
    private BicepValue<string>? _encoding;

    /// <summary>The secret value — a reference to a valueless <c>@secure()</c> param (inline mode only).</summary>
    public BicepValue<string> Value
    {
        get { Initialize(); return _value!; }
        set { Initialize(); _value!.Assign(value); }
    }

    /// <summary>The per-key encoding (e.g. <c>base64</c>/<c>raw</c>), emitted only when assigned.</summary>
    public BicepValue<string> Encoding
    {
        get { Initialize(); return _encoding!; }
        set { Initialize(); _encoding!.Assign(value); }
    }

    /// <inheritdoc />
    protected override void DefineProvisionableProperties()
    {
        _value = DefineProperty<string>(nameof(Value), ["value"]);
        _encoding = DefineProperty<string>(nameof(Encoding), ["encoding"]);
    }
}

/// <summary>
/// Represents an <c>Applications.Core/secretStores@2023-10-01-preview</c> resource in the
/// Bicep AST. Carries the store <see cref="StoreName"/>, its <see cref="StoreType"/>,
/// exactly one of <see cref="EnvironmentId"/> / <see cref="ApplicationId"/> (the scope),
/// an optional <see cref="ResourceReference"/> (<c>&lt;namespace&gt;/&lt;name&gt;</c> for the
/// existing/sealed modes), and the <see cref="Data"/> map.
/// </summary>
[Experimental("ASPIRERADIUS004", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public sealed class RadiusSecretStoreConstruct : ProvisionableResource
{
    private BicepValue<string>? _name;
    private BicepValue<string>? _type;
    private BicepValue<string>? _environmentId;
    private BicepValue<string>? _applicationId;
    private BicepValue<string>? _resourceReference;
    private BicepDictionary<RadiusSecretStoreDataEntryConstruct>? _data;

    /// <summary>The resource name.</summary>
    public BicepValue<string> StoreName
    {
        get { Initialize(); return _name!; }
        set { Initialize(); _name!.Assign(value); }
    }

    /// <summary>The Radius secret-store <c>type</c> string (e.g. <c>basicAuthentication</c>).</summary>
    public BicepValue<string> StoreType
    {
        get { Initialize(); return _type!; }
        set { Initialize(); _type!.Assign(value); }
    }

    /// <summary>The environment scope reference (<c>properties.environment</c>). Set only for environment-scoped stores.</summary>
    public BicepValue<string> EnvironmentId
    {
        get { Initialize(); return _environmentId!; }
        set { Initialize(); _environmentId!.Assign(value); }
    }

    /// <summary>The application scope reference (<c>properties.application</c>). Set only for application-scoped stores.</summary>
    public BicepValue<string> ApplicationId
    {
        get { Initialize(); return _applicationId!; }
        set { Initialize(); _applicationId!.Assign(value); }
    }

    /// <summary>The <c>&lt;namespace&gt;/&lt;name&gt;</c> reference to an existing cluster <c>Secret</c> (existing/sealed modes).</summary>
    public BicepValue<string> ResourceReference
    {
        get { Initialize(); return _resourceReference!; }
        set { Initialize(); _resourceReference!.Assign(value); }
    }

    /// <summary>The <c>data</c> map keyed by secret key name.</summary>
    public BicepDictionary<RadiusSecretStoreDataEntryConstruct> Data
    {
        get { Initialize(); return _data!; }
        set { Initialize(); _data!.Assign(value); }
    }

    /// <summary>Initializes a new <see cref="RadiusSecretStoreConstruct"/> with the given Bicep identifier.</summary>
    public RadiusSecretStoreConstruct(string bicepIdentifier)
        : base(bicepIdentifier, new Azure.Core.ResourceType("Applications.Core/secretStores"), "2023-10-01-preview")
    {
    }

    /// <inheritdoc />
    protected override void DefineProvisionableProperties()
    {
        _name = DefineProperty<string>(nameof(StoreName), ["name"]);
        _type = DefineProperty<string>(nameof(StoreType), ["properties", "type"]);
        _environmentId = DefineProperty<string>(nameof(EnvironmentId), ["properties", "environment"]);
        _applicationId = DefineProperty<string>(nameof(ApplicationId), ["properties", "application"]);
        _resourceReference = DefineProperty<string>(nameof(ResourceReference), ["properties", "resource"]);
        _data = DefineDictionaryProperty<RadiusSecretStoreDataEntryConstruct>(nameof(Data), ["properties", "data"]);
    }
}
