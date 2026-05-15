// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Annotation that controls the lifetime of an executable resource.
/// </summary>
[DebuggerDisplay("Type = {GetType().Name,nq}")]
public sealed class ExecutableLifetimeAnnotation : IResourceAnnotation
{
    /// <summary>
    /// Gets or sets the lifetime type for the executable resource.
    /// </summary>
    public required Lifetime Lifetime { get; set; }
}
