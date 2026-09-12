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
    private readonly Type _valueType;

    internal ProvisioningParameterProxy(ProvisioningParameter value, Type valueType)
    {
        Inner = value;
        _valueType = valueType;
    }

    internal ProvisioningParameter Inner { get; }

    [AspireExport]
    internal BicepValueProxy Value
    {
        get => BicepValueProxy.Create(Inner.Value, _valueType);
        set => value.AssignTo(Inner.Value, _valueType);
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
    private readonly Type _valueType;

    internal ProvisioningOutputProxy(ProvisioningOutput value, Type valueType)
    {
        Inner = value;
        _valueType = valueType;
    }

    internal ProvisioningOutput Inner { get; }

    [AspireExport]
    internal BicepValueProxy Value
    {
        get => BicepValueProxy.Create(Inner.Value, _valueType);
        set => value.AssignTo(Inner.Value, _valueType);
    }
}

[AspireExport]
internal sealed class ProvisioningVariableProxy
{
    private readonly Type _valueType;

    internal ProvisioningVariableProxy(ProvisioningVariable value, Type valueType)
    {
        Inner = value;
        _valueType = valueType;
    }

    internal ProvisioningVariable Inner { get; }

    [AspireExport]
    internal BicepValueProxy Value
    {
        get => BicepValueProxy.Create(Inner.Value, _valueType);
        set => value.AssignTo(Inner.Value, _valueType);
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

        var valueType = GetSystemType(type);
        var parameter = new ProvisioningParameter(bicepIdentifier, valueType)
        {
            IsSecure = isSecure
        };
        infrastructure.Add(parameter);
        return new(parameter, valueType);
    }

    [AspireExport]
    internal static ProvisioningOutputProxy AddBicepOutput(
        this AzureResourceInfrastructure infrastructure,
        string bicepIdentifier,
        ProvisioningValueType type)
    {
        ArgumentNullException.ThrowIfNull(infrastructure);
        ArgumentException.ThrowIfNullOrEmpty(bicepIdentifier);

        var valueType = GetSystemType(type);
        var output = new ProvisioningOutput(bicepIdentifier, valueType);
        infrastructure.Add(output);
        return new(output, valueType);
    }

    [AspireExport]
    internal static ProvisioningVariableProxy AddBicepVariable(
        this AzureResourceInfrastructure infrastructure,
        string bicepIdentifier,
        ProvisioningValueType type)
    {
        ArgumentNullException.ThrowIfNull(infrastructure);
        ArgumentException.ThrowIfNullOrEmpty(bicepIdentifier);

        var valueType = GetSystemType(type);
        var variable = new ProvisioningVariable(bicepIdentifier, valueType);
        infrastructure.Add(variable);
        return new(variable, valueType);
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
