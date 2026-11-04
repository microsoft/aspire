// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Stores a serialized ATS (Aspire Type System) annotation payload on a resource, keyed by a stable
/// annotation ID. The annotation can be set and read across languages (for example, set by a C#
/// integration and read by a TypeScript integration) because the value is a serialized JSON string
/// of a declared DTO payload rather than a live object.
/// </summary>
/// <param name="annotationId">The stable annotation identifier.</param>
/// <param name="json">The serialized JSON payload.</param>
internal sealed class AtsAnnotation(string annotationId, string json) : IResourceAnnotation
{
    /// <summary>
    /// Gets the stable annotation identifier.
    /// </summary>
    public string AnnotationId { get; } = annotationId;

    /// <summary>
    /// Gets the serialized JSON payload.
    /// </summary>
    public string Json { get; } = json;
}
