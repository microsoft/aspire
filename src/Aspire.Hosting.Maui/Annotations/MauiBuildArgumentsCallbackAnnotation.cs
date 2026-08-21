// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Maui.Annotations;

/// <summary>
/// Annotation that registers a callback used to inspect or modify the arguments passed to
/// <c>dotnet</c> for a particular <see cref="MauiBuildStep"/> of a MAUI platform resource.
/// </summary>
internal sealed class MauiBuildArgumentsCallbackAnnotation(
    MauiBuildStep step,
    Func<MauiBuildArgumentsCallbackContext, Task> callback) : IResourceAnnotation
{
    /// <summary>
    /// Gets the build step this callback participates in.
    /// </summary>
    public MauiBuildStep Step { get; } = step;

    /// <summary>
    /// Gets the callback invoked with the mutable argument list for <see cref="Step"/>.
    /// </summary>
    public Func<MauiBuildArgumentsCallbackContext, Task> Callback { get; } = callback;
}
