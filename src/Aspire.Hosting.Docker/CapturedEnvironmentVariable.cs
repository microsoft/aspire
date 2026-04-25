// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Docker;

/// <summary>
/// Represents a captured environment variable that will be written to the .env file 
/// adjacent to the Docker Compose file.
/// </summary>
[AspireExport(ExposeProperties = true)]
public sealed class CapturedEnvironmentVariable
{
    /// <summary>
    /// Gets the name of the environment variable.
    /// </summary>
    [AspireExportIgnore(Reason = "The dictionary key already identifies the captured environment variable in polyglot callbacks.")]
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
    /// <see cref="ContainerMountAnnotation"/>, <see cref="ContainerImageReference"/>,
    /// or <see cref="ContainerPortReference"/>.
    /// </summary>
    [AspireUnion(typeof(ParameterResource), typeof(ContainerMountAnnotation), typeof(ContainerImageReference), typeof(ContainerPortReference))]
    public object? Source { get; set; }

    /// <summary>
    /// Gets or sets the resource that this environment variable is associated with.
    /// This is useful when the source is an annotation on a resource, allowing you to 
    /// identify which resource this environment variable is related to.
    /// </summary>
    [AspireExportIgnore(Reason = "Resource is provenance metadata only; exporting it here would pull the broader IResource surface into the callback even though polyglot configureEnvFile only needs to mutate Description and DefaultValue.")]
    public IResource? Resource { get; set; }
}
