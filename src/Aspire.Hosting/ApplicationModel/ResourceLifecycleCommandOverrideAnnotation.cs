// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Replaces a built-in lifecycle command for a resource without changing its other lifecycle commands.
/// </summary>
/// <remarks>
/// The last override matching the built-in command's name wins. It must be configured before
/// resource preparation. This does not add a command when no matching built-in command exists.
/// </remarks>
/// <param name="commandName">The name of the built-in command to customize.</param>
/// <param name="factory">Creates a replacement from the original command, allowing delegation when the override is inactive.</param>
[Experimental("ASPIREWATCH001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public sealed class ResourceLifecycleCommandOverrideAnnotation(
    string commandName,
    Func<ResourceCommandAnnotation, ResourceCommandAnnotation> factory) : IResourceAnnotation
{
    /// <summary>
    /// Gets the name of the built-in command.
    /// </summary>
    public string CommandName { get; } = commandName ?? throw new ArgumentNullException(nameof(commandName));

    /// <summary>
    /// Gets the replacement factory.
    /// </summary>
    public Func<ResourceCommandAnnotation, ResourceCommandAnnotation> Factory { get; } =
        factory ?? throw new ArgumentNullException(nameof(factory));
}
