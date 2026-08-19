// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRERADIUS004 // Experimental: ConfigureRadiusInfrastructure escape-hatch construct types are consumed internally by the publisher.

using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.Radius.ResourceMapping;
using Azure.Provisioning;
using Azure.Provisioning.Primitives;

namespace Aspire.Hosting.Radius.Publishing.Constructs;

/// <summary>
/// A single entry in a <c>Radius.Security/secrets</c> resource's <c>data</c> map.
/// </summary>
/// <remarks>
/// The encoding vocabulary differs from the legacy <c>Applications.Core/secretStores</c> type:
/// the new type accepts <c>string</c> / <c>base64</c>, where the legacy type accepted
/// <c>raw</c> / <c>base64</c>. Emitting <c>raw</c> here fails schema validation.
/// </remarks>
[Experimental("ASPIRERADIUS004", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public sealed class RadiusSecuritySecretDataEntryConstruct : ProvisionableConstruct
{
    private BicepValue<string>? _value;
    private BicepValue<string>? _encoding;

    /// <summary>
    /// The secret value. Normally a reference to a valueless <c>@secure()</c> param so no
    /// credential is written into the published artifacts.
    /// </summary>
    public BicepValue<string> Value
    {
        get { Initialize(); return _value!; }
        set { Initialize(); _value!.Assign(value); }
    }

    /// <summary>
    /// The per-key encoding — <c>string</c> or <c>base64</c>. Emitted only when assigned.
    /// </summary>
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
/// Represents a <c>Radius.Security/secrets</c> resource in the Bicep AST — the
/// <c>Radius.*</c> UDT replacement for <c>Applications.Core/secretStores</c>.
/// </summary>
/// <remarks>
/// <para>
/// Unlike the legacy secret-store type, which is scoped by exactly one of
/// <c>properties.environment</c> / <c>properties.application</c>, this type requires
/// <see cref="EnvironmentId"/> and treats <see cref="ApplicationId"/> as optional. <c>data</c>
/// is required, so there is no "reference an existing cluster Secret" mode: every entry carries
/// its own value.
/// </para>
/// <para>
/// This construct is the canonical secret primitive for the <c>Radius.*</c> surface. It is used
/// today to hold credentials that a UDT backing resource consumes by resource ID (for example
/// the RabbitMQ <c>properties.password</c>), and is intended to be reused rather than duplicated
/// by later work that needs to hand a secret to a container env var.
/// </para>
/// </remarks>
[Experimental("ASPIRERADIUS004", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public sealed class RadiusSecuritySecretConstruct : ProvisionableResource
{
    private BicepValue<string>? _name;
    private BicepValue<string>? _kind;
    private BicepValue<string>? _environmentId;
    private BicepValue<string>? _applicationId;
    private BicepDictionary<RadiusSecuritySecretDataEntryConstruct>? _data;

    /// <summary>The resource name.</summary>
    public BicepValue<string> SecretName
    {
        get { Initialize(); return _name!; }
        set { Initialize(); _name!.Assign(value); }
    }

    /// <summary>The secret <c>kind</c> (e.g. <c>generic</c>). Emitted only when assigned.</summary>
    public BicepValue<string> Kind
    {
        get { Initialize(); return _kind!; }
        set { Initialize(); _kind!.Assign(value); }
    }

    /// <summary>The environment scope reference (<c>properties.environment</c>). Required by the type.</summary>
    public BicepValue<string> EnvironmentId
    {
        get { Initialize(); return _environmentId!; }
        set { Initialize(); _environmentId!.Assign(value); }
    }

    /// <summary>The optional application scope reference (<c>properties.application</c>).</summary>
    public BicepValue<string> ApplicationId
    {
        get { Initialize(); return _applicationId!; }
        set { Initialize(); _applicationId!.Assign(value); }
    }

    /// <summary>The <c>data</c> map keyed by secret key name.</summary>
    public BicepDictionary<RadiusSecuritySecretDataEntryConstruct> Data
    {
        get { Initialize(); return _data!; }
        set { Initialize(); _data!.Assign(value); }
    }

    /// <summary>Initializes a new <see cref="RadiusSecuritySecretConstruct"/> with the given Bicep identifier.</summary>
    public RadiusSecuritySecretConstruct(string bicepIdentifier)
        : base(bicepIdentifier, new Azure.Core.ResourceType(RadiusResourceTypes.SecuritySecrets), RadiusResourceTypes.RadiusApiVersion)
    {
    }

    /// <inheritdoc />
    protected override void DefineProvisionableProperties()
    {
        _name = DefineProperty<string>(nameof(SecretName), ["name"]);
        _kind = DefineProperty<string>(nameof(Kind), ["properties", "kind"]);
        _environmentId = DefineProperty<string>(nameof(EnvironmentId), ["properties", "environment"]);
        _applicationId = DefineProperty<string>(nameof(ApplicationId), ["properties", "application"]);
        _data = DefineDictionaryProperty<RadiusSecuritySecretDataEntryConstruct>(nameof(Data), ["properties", "data"]);
    }
}
