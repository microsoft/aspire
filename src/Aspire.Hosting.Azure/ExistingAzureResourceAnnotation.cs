// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Azure;

/// <summary>
/// Represents a resource that is not managed by Aspire's provisioning or
/// container management layer.
/// </summary>
public sealed class ExistingAzureResourceAnnotation(object name, object? resourceGroup = null) : IResourceAnnotation
{
    /// <summary>
    /// Gets the name of the existing resource.
    /// </summary>
    /// <remarks>
    /// Supports a <see cref="string"/>, <see cref="ParameterResource"/>, or a <see cref="BicepOutputReference"/> via runtime validation.
    /// </remarks>
    public object Name { get; } = name;

    /// <summary>
    /// Gets the name of the existing resource group. If <see langword="null"/>, use the current resource group.
    /// </summary>
    /// <remarks>
    /// Supports a <see cref="string"/> or a <see cref="ParameterResource"/> via runtime validation.
    /// </remarks>
    public object? ResourceGroup { get; } = resourceGroup;
}
