// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Represents an annotation for specifying custom base images in generated Dockerfiles.
/// </summary>
/// <remarks>
/// This annotation allows developers to override the default base images used when generating
/// Dockerfiles for resources. It supports specifying separate build-time and runtime base images
/// for multi-stage builds.
/// </remarks>
public class DockerfileBaseImageAnnotation : IResourceAnnotation
{
    /// <summary>
    /// Gets or sets the base image to use for the build stage in multi-stage Dockerfiles.
    /// </summary>
    /// <remarks>
    /// This image is used during the build phase where dependencies are installed and
    /// the application is compiled or prepared. If not specified, the default build image
    /// for the resource type will be used.
    /// </remarks>
    public string? BuildImage { get; set; }

    /// <summary>
    /// Gets or sets the base image to use for the runtime stage in multi-stage Dockerfiles.
    /// </summary>
    /// <remarks>
    /// This image is used for the final runtime stage where the application actually runs.
    /// If not specified, the default runtime image for the resource type will be used.
    /// </remarks>
    public string? RuntimeImage { get; set; }
}
