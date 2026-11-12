// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Declares an ATS (Aspire Type System) annotation: a stable identifier paired with the DTO payload
/// type used to (de)serialize the annotation value.
/// </summary>
/// <remarks>
/// <para>
/// An annotation declaration is the language-neutral contract for a piece of resource state. The
/// <see cref="Id"/> namespaces the stored value and <typeparamref name="TData"/> describes its shape.
/// Because the payload is a declared DTO that serializes to JSON by value, the same annotation can be
/// written in one language (for example, C#) and read in another (for example, TypeScript) as long as
/// both sides share the identifier and the DTO schema.
/// </para>
/// <para>
/// The declaration deliberately carries no live state. It only binds an identifier to a payload type
/// so that typed accessors can serialize and deserialize the value without exposing the JSON string
/// storage that backs it.
/// </para>
/// </remarks>
/// <typeparam name="TData">The annotation payload type.</typeparam>
internal sealed class AnnotationDefinition<TData>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="AnnotationDefinition{TData}"/> class.
    /// </summary>
    /// <param name="id">The stable annotation identifier.</param>
    public AnnotationDefinition(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        Id = id;
    }

    /// <summary>
    /// Gets the stable annotation identifier.
    /// </summary>
    public string Id { get; }
}
