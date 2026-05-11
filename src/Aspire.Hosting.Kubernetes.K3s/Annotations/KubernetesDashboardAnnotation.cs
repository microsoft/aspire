// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Kubernetes.Annotations;

/// <summary>
/// Marker annotation that signals the Kubernetes Dashboard should be installed and
/// configured in the k3s cluster after startup.
/// </summary>
internal sealed class KubernetesDashboardAnnotation : IResourceAnnotation
{
    internal KubernetesDashboardAnnotation(string version)
    {
        Version = version;
    }

    internal string Version { get; }
}
