// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Represents an annotation for a container image.
/// </summary>
[DebuggerDisplay("Command = {Command,nq}, WorkingDirectory = {WorkingDirectory}")]
public sealed class ExecutableAnnotation : IResourceAnnotation
{
    /// <summary>
    /// Gets or sets the command associated with this executable resource.
    /// </summary>
    public required string Command { get; set; }

    /// <summary>
    /// Gets or sets the working directory for the executable resource.
    /// </summary>
    public required string WorkingDirectory { get; set; }

    /// <summary>
    /// Gets a value indicating whether the working directory was explicitly set through the resource builder.
    /// </summary>
    /// <remarks>
    /// The initial value is established by the resource's integration. Integrations that resolve runtime defaults later
    /// can use this value to avoid overwriting a working directory explicitly authored with <c>WithWorkingDirectory</c>.
    /// </remarks>
    [Experimental("ASPIREEXTENSION001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    public bool WorkingDirectoryExplicitlySet { get; internal set; }
}
