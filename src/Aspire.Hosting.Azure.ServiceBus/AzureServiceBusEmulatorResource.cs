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
    : ContainerResource(innerResource.Name), IResource, IResourceWithConnectionString, IContainerProjection<AzureServiceBusResource, AzureServiceBusEmulatorResource>
{
    // The path to the emulator configuration files in the container.
    internal const string EmulatorConfigFilesPath = "/ServiceBus_Emulator/ConfigFiles";
    // The path to the emulator configuration file in the container.
    internal const string EmulatorConfigJsonFile = "Config.json";

    private readonly AzureServiceBusResource _innerResource = innerResource ?? throw new ArgumentNullException(nameof(innerResource));

    /// <inheritdoc />
    public override ResourceAnnotationCollection Annotations => _innerResource.Annotations;

    ReferenceExpression IResourceWithConnectionString.ConnectionStringExpression => CreateConnectionString(_innerResource);

    internal static ReferenceExpression CreateConnectionString(AzureServiceBusResource owner) =>
        ReferenceExpression.Create($"Endpoint=sb://{owner.EmulatorEndpoint.Property(EndpointProperty.HostAndPort)};SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;");

    internal static IEnumerable<KeyValuePair<string, ReferenceExpression>> GetConnectionProperties(AzureServiceBusResource owner)
    {
        yield return new("Host", ReferenceExpression.Create($"{owner.EmulatorEndpoint.Property(EndpointProperty.Host)}"));
        yield return new("Port", ReferenceExpression.Create($"{owner.EmulatorEndpoint.Property(EndpointProperty.Port)}"));
        yield return new("Uri", ReferenceExpression.Create($"sb://{owner.EmulatorEndpoint.Property(EndpointProperty.HostAndPort)}"));
        yield return new("ConnectionString", CreateConnectionString(owner));
    }

    IEnumerable<KeyValuePair<string, ReferenceExpression>> IResourceWithConnectionString.GetConnectionProperties() =>
        GetConnectionProperties(_innerResource);

    /// <inheritdoc />
    public static AzureServiceBusEmulatorResource CreateProjection(AzureServiceBusResource owner) => new(owner);
}
