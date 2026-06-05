// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Annotation that creates a custom connection property value injected into environment variables
/// when a resource is referenced using <c>WithReference()</c>.
/// </summary>
public sealed class ConnectionPropertyAnnotation : IResourceAnnotation
{
    /// <summary>
    /// Initializes a new instance of the ConnectionPropertyAnnotation class with the specified property name and
    /// reference expression.
    /// </summary>
    /// <param name="name">The name of the connection property to annotate. Cannot be null or empty.</param>
    /// <param name="referenceExpression">The reference expression associated with the connection property. Cannot be null.</param>
    public ConnectionPropertyAnnotation(string name, ReferenceExpression referenceExpression)
    {
        Name = name;
        Value = referenceExpression;
    }

    /// <summary>
    /// Gets the name associated with the current instance.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets the reference expression associated with this instance.
    /// </summary>
    public ReferenceExpression Value { get; }
}
