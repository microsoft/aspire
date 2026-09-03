// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Stores the container configuration view applied to a resource for the current operation.
/// </summary>
internal sealed class ContainerResourceProjectionAnnotation : IResourceAnnotation
{
    internal ContainerResourceProjectionAnnotation(ContainerResource projection)
    {
        Projection = projection;
    }

    internal ContainerResource Projection { get; }
}
