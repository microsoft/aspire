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
    /// Gets the annotations associated with the resource.
    /// </summary>
    public virtual ResourceAnnotationCollection Annotations { get; } = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="Resource"/> class.
    /// </summary>
    /// <param name="name">The name of the resource.</param>
    protected Resource(string name)
    {
        ModelName.ValidateName(nameof(Resource), name);

        Name = name;
    }

    private string DebuggerToString()
    {
        return $@"Type = {GetType().Name}, Name = ""{Name}"", Annotations = {Annotations.Count}";
    }
}
