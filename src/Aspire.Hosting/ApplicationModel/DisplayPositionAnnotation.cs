// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// The default position of a resource in lists of resources. Lower values appear first.
/// </summary>
/// <param name="position">The default position.</param>
public sealed class DisplayPositionAnnotation(int position) : IResourceAnnotation
{
    /// <summary>Gets the default position.</summary>
    public int Position { get; } = position;
}
