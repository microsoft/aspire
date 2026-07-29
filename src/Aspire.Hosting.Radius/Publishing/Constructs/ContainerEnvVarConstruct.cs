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
/// where each entry is an object carrying a <c>value</c> (or a <c>valueFrom</c> source).
/// This construct emits the <c>value</c> form. A secret-bound value is assigned a Bicep
/// <c>param</c> reference (to an <c>@secure()</c> parameter) so the literal secret never
/// appears in the published artifact.
/// See: https://github.com/radius-project/radius/blob/main/eng/design-notes/extensibility/2025-08-container-resource-type.md
/// </remarks>
[Experimental("ASPIRERADIUS004", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
[AspireExport]
public sealed class ContainerEnvVarConstruct : ProvisionableConstruct
{
    private BicepValue<string>? _value;

    /// <summary>The environment variable value (a literal, or a reference to a Bicep parameter).</summary>
    public BicepValue<string> Value
    {
        get { Initialize(); return _value!; }
        set { Initialize(); _value!.Assign(value); }
    }

    /// <inheritdoc />
    protected override void DefineProvisionableProperties()
    {
        _value = DefineProperty<string>(nameof(Value), ["value"]);
    }
}
