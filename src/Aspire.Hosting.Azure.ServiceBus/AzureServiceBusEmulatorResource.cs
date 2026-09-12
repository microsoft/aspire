// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPROJECTIONS001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Azure;

/// <summary>
/// Wraps an <see cref="AzureServiceBusResource" /> in a type that exposes container extension methods.
/// </summary>
/// <param name="innerResource">The inner resource used to store annotations.</param>
public class AzureServiceBusEmulatorResource(AzureServiceBusResource innerResource)
    : ContainerResource(innerResource.Name), IResource, IContainerProjection<AzureServiceBusResource, AzureServiceBusEmulatorResource>
{
    // The path to the emulator configuration files in the container.
    internal const string EmulatorConfigFilesPath = "/ServiceBus_Emulator/ConfigFiles";
    // The path to the emulator configuration file in the container.
    internal const string EmulatorConfigJsonFile = "Config.json";

    private readonly AzureServiceBusResource _innerResource = innerResource ?? throw new ArgumentNullException(nameof(innerResource));

    /// <inheritdoc />
    public override ResourceAnnotationCollection Annotations => _innerResource.Annotations;

    /// <inheritdoc />
    public static AzureServiceBusEmulatorResource CreateProjection(AzureServiceBusResource owner) => new(owner);
}
