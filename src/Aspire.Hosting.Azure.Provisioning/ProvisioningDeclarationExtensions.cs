// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Azure;
using Aspire.Hosting.Azure.Provisioning;
using Azure.Provisioning;

namespace Aspire.Hosting;

/// <summary>
/// Identifies the literal type of a Bicep declaration.
/// </summary>
internal enum ProvisioningValueType
{
    String,
    Boolean,
    Integer,
    Object,
    Guid
}

[AspireExport]
internal sealed class ProvisioningParameterProxy
{
    internal ProvisioningParameterProxy(ProvisioningParameter value)
    {
        Inner = value;
    }

    internal ProvisioningParameter Inner { get; }

    [AspireExport]
    internal BicepValueProxy Value
    {
        get => BicepValueProxy.Create(Inner.Value);
        set => value.AssignTo(Inner.Value);
    }

    [AspireExport]
    internal bool IsSecure
    {
        get => Inner.IsSecure;
        set => Inner.IsSecure = value;
    }
}

[AspireExport]
internal sealed class ProvisioningOutputProxy
{
    internal ProvisioningOutputProxy(ProvisioningOutput value)
    {
        Inner = value;
    }

    internal ProvisioningOutput Inner { get; }

    [AspireExport]
    internal BicepValueProxy Value
    {
        get => BicepValueProxy.Create(Inner.Value);
        set => value.AssignTo(Inner.Value);
    }
}

[AspireExport]
internal sealed class ProvisioningVariableProxy
{
    internal ProvisioningVariableProxy(ProvisioningVariable value)
    {
        Inner = value;
    }

    internal ProvisioningVariable Inner { get; }

    [AspireExport]
    internal BicepValueProxy Value
    {
        get => BicepValueProxy.Create(Inner.Value);
        set => value.AssignTo(Inner.Value);
    }
}

internal static class ProvisioningDeclarationExtensions
{
    [AspireExport]
    internal static ProvisioningParameterProxy AddBicepParameter(
        this AzureResourceInfrastructure infrastructure,
        string bicepIdentifier,
        ProvisioningValueType type,
        bool isSecure = false)
    {
        ArgumentNullException.ThrowIfNull(infrastructure);
        ArgumentException.ThrowIfNullOrEmpty(bicepIdentifier);

        var parameter = new ProvisioningParameter(bicepIdentifier, GetSystemType(type))
        {
            IsSecure = isSecure
        };
        infrastructure.Add(parameter);
        return new(parameter);
    }

    [AspireExport]
    internal static ProvisioningOutputProxy AddBicepOutput(
        this AzureResourceInfrastructure infrastructure,
        string bicepIdentifier,
        ProvisioningValueType type)
    {
        ArgumentNullException.ThrowIfNull(infrastructure);
        ArgumentException.ThrowIfNullOrEmpty(bicepIdentifier);

        var output = new ProvisioningOutput(bicepIdentifier, GetSystemType(type));
        infrastructure.Add(output);
        return new(output);
    }

    [AspireExport]
    internal static ProvisioningVariableProxy AddBicepVariable(
        this AzureResourceInfrastructure infrastructure,
        string bicepIdentifier,
        ProvisioningValueType type)
    {
        ArgumentNullException.ThrowIfNull(infrastructure);
        ArgumentException.ThrowIfNullOrEmpty(bicepIdentifier);

        var variable = new ProvisioningVariable(bicepIdentifier, GetSystemType(type));
        infrastructure.Add(variable);
        return new(variable);
    }

    private static Type GetSystemType(ProvisioningValueType type)
    {
        return type switch
        {
            ProvisioningValueType.String => typeof(string),
            ProvisioningValueType.Boolean => typeof(bool),
            ProvisioningValueType.Integer => typeof(int),
            ProvisioningValueType.Object => typeof(object),
            ProvisioningValueType.Guid => typeof(Guid),
            _ => throw new ArgumentOutOfRangeException(nameof(type))
        };
    }
}
