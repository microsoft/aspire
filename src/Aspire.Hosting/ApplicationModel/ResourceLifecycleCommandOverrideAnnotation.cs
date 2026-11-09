// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Replaces a built-in lifecycle command without changing a resource's other commands.
/// </summary>
/// <remarks>
/// Configure overrides before resource preparation. The last override matching the built-in
/// command's name wins. An override does not add a command that the resource does not support.
/// The factory receives the original command so it can preserve metadata and delegate to its callbacks.
/// </remarks>
/// <param name="commandName">The built-in command's name.</param>
/// <param name="factory">The factory that creates a replacement from the original command.</param>
[Experimental("ASPIREWATCH001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public sealed class ResourceLifecycleCommandOverrideAnnotation(
    string commandName,
    Func<ResourceCommandAnnotation, ResourceCommandAnnotation> factory) : IResourceAnnotation
{
    /// <summary>
    /// Gets the name of the built-in command to replace.
    /// </summary>
    public string CommandName { get; } = commandName ?? throw new ArgumentNullException(nameof(commandName));

    /// <summary>
    /// Gets the factory that receives the original built-in command.
    /// </summary>
    public Func<ResourceCommandAnnotation, ResourceCommandAnnotation> Factory { get; } =
        factory ?? throw new ArgumentNullException(nameof(factory));
}
