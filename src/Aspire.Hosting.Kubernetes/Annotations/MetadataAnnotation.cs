// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Kubernetes.Resources;

/// <summary>
/// Provides a means of configuring the metadata on a custom resource.
/// </summary>
/// <param name="configure">The callback function that will configure the resource.</param>
internal sealed class MetadataAnnotation(Action<ObjectMetaV1> configure) : IResourceAnnotation
{
    /// <summary>
    /// Gets or sets the callback method that will configure the Metadata.
    /// </summary>
    public Action<ObjectMetaV1> Configure { get; set; } = configure ?? throw new ArgumentNullException(nameof(configure));
}