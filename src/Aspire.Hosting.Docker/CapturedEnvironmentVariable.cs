// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Docker;

/// <summary>
/// Represents a captured environment variable that will be written to the .env file 
/// adjacent to the Docker Compose file.
/// </summary>
public sealed class CapturedEnvironmentVariable
{
    /// <summary>
    /// Gets the name of the environment variable.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Gets or sets the description for the environment variable.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Gets or sets the default value for the environment variable.
    /// </summary>
    public string? DefaultValue { get; set; }

    /// <summary>
    /// Gets or sets the source object that originated this environment variable.
    /// This could be a <see cref="ParameterResource"/>,
    /// <see cref="ContainerMountAnnotation"/>, or other source types.
    /// </summary>
    public object? Source { get; set; }

    /// <summary>
    /// Gets or sets the resource that this environment variable is associated with.
    /// This is useful when the source is an annotation on a resource, allowing you to 
    /// identify which resource this environment variable is related to.
    /// </summary>
    public IResource? Resource { get; set; }
}
