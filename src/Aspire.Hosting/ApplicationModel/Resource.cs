// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Represents an abstract resource that can be used by an application, that implements <see cref="IResource"/>.
/// </summary>
[DebuggerDisplay("{DebuggerToString(),nq}")]
public abstract class Resource : IResource
{
    /// <summary>
    /// Gets the name of the resource.
    /// </summary>
    public virtual string Name { get; }

    /// <summary>
    /// Gets the default human-readable name of this resource, falling back to <see cref="Name"/>.
    /// </summary>
    /// <remarks>
    /// This is presentation metadata only. Use <see cref="ResourceBuilderExtensions.WithResourceDisplayName{T}"/>
    /// to override the label without changing resource identity, service discovery, or telemetry identity.
    /// </remarks>
    public virtual string ResourceDisplayName => Name;

    /// <summary>
    /// Gets the default human-readable name of this resource's type.
    /// </summary>
    /// <remarks>
    /// Override this property to identify the service, such as Redis or PostgreSQL Server,
    /// independently of its technical resource type. The default is the resource type.
    /// This label does not change the resource name, service discovery, or OpenTelemetry identity.
    /// Use <see cref="ResourceBuilderExtensions.WithResourceTypeDisplayName{T}"/> to override the label for an instance.
    /// </remarks>
    public virtual string ResourceTypeDisplayName => this.GetResourceType();

    /// <summary>
    /// Gets the annotations associated with the resource.
    /// </summary>
    public virtual ResourceAnnotationCollection Annotations { get; } = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="Resource"/> class.
    /// </summary>
    /// <param name="name">The name of the resource.</param>
    protected Resource(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        Name = name;
    }

    private string DebuggerToString()
    {
        return $@"Type = {GetType().Name}, Name = ""{Name}"", Annotations = {Annotations.Count}";
    }
}
