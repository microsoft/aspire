// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPROJECTIONS001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Azure;

/// <summary>
/// Wraps an <see cref="AzureEventHubsResource" /> in a type that exposes container extension methods.
/// </summary>
/// <param name="innerResource">The inner resource used to store annotations.</param>
public class AzureEventHubsEmulatorResource(AzureEventHubsResource innerResource)
    : ContainerResource(innerResource.Name), IResource, IResourceWithConnectionString, IContainerProjection<AzureEventHubsResource, AzureEventHubsEmulatorResource>
{
    // The path to the emulator configuration file in the container.
    // The path to the emulator configuration files in the container.
    internal const string EmulatorConfigFilesPath = "/Eventhubs_Emulator/ConfigFiles";
    // The path to the emulator configuration file in the container.
    internal const string EmulatorConfigJsonFile = "Config.json";

    private readonly AzureEventHubsResource _innerResource = innerResource ?? throw new ArgumentNullException(nameof(innerResource));

    /// <inheritdoc/>
    public override string Name => _innerResource.Name;

    /// <inheritdoc />
    public override ResourceAnnotationCollection Annotations => _innerResource.Annotations;

    ReferenceExpression IResourceWithConnectionString.ConnectionStringExpression => CreateConnectionString(_innerResource);

    internal static ReferenceExpression CreateConnectionString(AzureEventHubsResource owner) =>
        ReferenceExpression.Create($"Endpoint=sb://{owner.EmulatorEndpoint.Property(EndpointProperty.HostAndPort)};SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true");

    internal static IEnumerable<KeyValuePair<string, ReferenceExpression>> GetConnectionProperties(AzureEventHubsResource owner)
    {
        yield return new("Host", ReferenceExpression.Create($"{owner.EmulatorEndpoint.Property(EndpointProperty.Host)}"));
        yield return new("Port", ReferenceExpression.Create($"{owner.EmulatorEndpoint.Property(EndpointProperty.Port)}"));
        yield return new("Uri", ReferenceExpression.Create($"sb://{owner.EmulatorEndpoint.Property(EndpointProperty.HostAndPort)}"));
        yield return new("ConnectionString", ReferenceExpression.Create($"Endpoint={owner.EmulatorEndpoint.Property(EndpointProperty.HostAndPort)};SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true"));
    }

    IEnumerable<KeyValuePair<string, ReferenceExpression>> IResourceWithConnectionString.GetConnectionProperties() =>
        GetConnectionProperties(_innerResource);

    /// <inheritdoc />
    public static AzureEventHubsEmulatorResource CreateProjection(AzureEventHubsResource owner) => new(owner);
}
