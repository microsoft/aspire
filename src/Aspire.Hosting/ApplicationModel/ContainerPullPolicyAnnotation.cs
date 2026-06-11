// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Image pull policies for container resources.
/// </summary>
public enum ImagePullPolicy
{
    /// <summary>
    /// Default image pull policy behavior. Currently this will be the same as the default behavior for your container runtime.
    /// </summary>
    Default = 0,
    /// <summary>
    /// Always pull the image when creating the container.
    /// </summary>
    Always,
    /// <summary>
    /// Pull the image only if it does not already exist.
    /// </summary>
    Missing,
    /// <summary>
    /// Never pull the image from the registry even if it is missing locally.
    /// </summary>
    Never,
}

/// <summary>
/// Annotation that controls the image pull policy for a container resource.
/// </summary>
[DebuggerDisplay("Type = {GetType().Name,nq}")]
public sealed class ContainerImagePullPolicyAnnotation : IResourceAnnotation
{
    /// <summary>
    /// Gets or sets the image pull policy for the container resource.
    /// </summary>
    public required ImagePullPolicy ImagePullPolicy { get; set; }
}
