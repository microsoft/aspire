// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Represents a resource backed by a .NET program, such as a project file or a file-based app.
/// </summary>
/// <remarks>
/// This interface identifies the source-program model shared by legacy <see cref="ProjectResource"/> instances
/// and language integrations such as <c>Aspire.Hosting.Dotnet</c>. Implementing this interface does not by itself
/// enable SDK container publishing; use the publishing configuration supplied by the resource's builder API.
/// </remarks>
[Experimental("ASPIREPROJECTS001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public interface IDotnetProgramResource : IResource
{
}
