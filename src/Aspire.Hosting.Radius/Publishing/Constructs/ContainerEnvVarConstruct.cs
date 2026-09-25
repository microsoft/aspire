// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRERADIUS004 // Experimental: ConfigureRadiusInfrastructure escape-hatch construct types are consumed internally by the publisher.

using System.Diagnostics.CodeAnalysis;
using Azure.Provisioning;
using Azure.Provisioning.Primitives;

namespace Aspire.Hosting.Radius.Publishing.Constructs;

/// <summary>
/// Represents a single environment-variable entry in a container's <c>env</c> block.
/// </summary>
/// <remarks>
/// The Radius container schema models <c>env</c> as a map keyed by the variable name,
/// where each entry is an object carrying either a <c>value</c> or a <c>valueFrom</c> source.
/// The two forms are mutually exclusive: assign <see cref="Value"/> or the
/// <see cref="SecretName"/>/<see cref="SecretKey"/> pair, never both.
/// <para>
/// The <c>value</c> form is emitted for values that carry nothing sensitive. It ends up verbatim
/// in the Kubernetes <c>Deployment</c> spec, so a credential emitted this way is readable by
/// anyone who can read the Deployment or its rollout history — even when the Bicep composed it
/// from an <c>@secure()</c> parameter, which only keeps it out of the published <em>artifact</em>.
/// </para>
/// <para>
/// The <c>valueFrom.secretKeyRef</c> form points at a key of a <c>Radius.Security/secrets</c>
/// resource; <c>secretName</c> is that resource's <em>name</em>, which the secrets recipe also
/// uses as the Kubernetes <c>Secret</c> name. The container recipe emits these entries ahead of
/// the plain <c>value</c> ones so kubelet's <c>$(VAR)</c> expansion can reference them.
/// </para>
/// See: https://github.com/radius-project/radius/blob/main/eng/design-notes/extensibility/2025-08-container-resource-type.md
/// and Compute/containers/containers.yaml in radius-project/resource-types-contrib.
/// </remarks>
[Experimental("ASPIRERADIUS004", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public sealed class ContainerEnvVarConstruct : ProvisionableConstruct
{
    private BicepValue<string>? _value;
    private BicepValue<string>? _secretName;
    private BicepValue<string>? _secretKey;

    /// <summary>The environment variable value (a literal, or a reference to a Bicep parameter).</summary>
    public BicepValue<string> Value
    {
        get { Initialize(); return _value!; }
        set { Initialize(); _value!.Assign(value); }
    }

    /// <summary>
    /// The name of the <c>Radius.Security/secrets</c> resource supplying this value. Emitted only
    /// when assigned, and mutually exclusive with <see cref="Value"/>.
    /// </summary>
    public BicepValue<string> SecretName
    {
        get { Initialize(); return _secretName!; }
        set { Initialize(); _secretName!.Assign(value); }
    }

    /// <summary>
    /// The key within the secret resource's <c>data</c> map. Emitted only when assigned, and
    /// mutually exclusive with <see cref="Value"/>.
    /// </summary>
    public BicepValue<string> SecretKey
    {
        get { Initialize(); return _secretKey!; }
        set { Initialize(); _secretKey!.Assign(value); }
    }

    /// <inheritdoc />
    protected override void DefineProvisionableProperties()
    {
        _value = DefineProperty<string>(nameof(Value), ["value"]);
        _secretName = DefineProperty<string>(nameof(SecretName), ["valueFrom", "secretKeyRef", "secretName"]);
        _secretKey = DefineProperty<string>(nameof(SecretKey), ["valueFrom", "secretKeyRef", "key"]);
    }
}
