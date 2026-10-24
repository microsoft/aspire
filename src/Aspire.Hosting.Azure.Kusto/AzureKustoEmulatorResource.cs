// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Azure;

/// <summary>
/// A resource that represents a Kusto emulator running as a container.
/// </summary>
public class AzureKustoEmulatorResource : ContainerResource
{
    /// <inheritdoc/>
    public override string ServiceDisplayName => "Azure Data Explorer Emulator";

    /// <summary>
    /// Initializes a new instance of the <see cref="AzureKustoEmulatorResource"/> class.
    /// </summary>
    /// <param name="innerResource">The wrapped Kusto resource.</param>
    public AzureKustoEmulatorResource(AzureKustoClusterResource innerResource)
        : base(innerResource?.Name ?? throw new ArgumentNullException(nameof(innerResource)))
    {
        InnerResource = innerResource;
    }

    /// <inheritdoc />
    public override ResourceAnnotationCollection Annotations => InnerResource.Annotations;

    /// <summary>
    /// Gets the wrapped Kusto resource.
    /// </summary>
    internal AzureKustoClusterResource InnerResource { get; }
}
