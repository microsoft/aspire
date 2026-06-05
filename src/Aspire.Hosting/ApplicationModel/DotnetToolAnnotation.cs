// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;

namespace Aspire.Hosting.ApplicationModel;
/// <summary>
/// Represents an annotation for dotnet tool resources.
/// </summary>
[Experimental("ASPIREDOTNETTOOL", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public class DotnetToolAnnotation : IResourceAnnotation
{
    /// <summary>
    /// The NuGet package ID of the .NET tool to execute. You can optionally specify a version using the <c>@</c> syntax, for example <c>dotnetsay@2.1</c>.
    /// </summary>
    public required string PackageId { get; set; }

    /// <summary>
    /// The version of the tool package to install.
    /// </summary>
    public string? Version { get; set; }

    /// <summary>
    /// Allows prerelease packages to be selected when resolving the version to install.
    /// </summary>
    public bool Prerelease { get; set; }

    /// <summary>
    /// NuGet package sources to use during installation
    /// </summary>
    public List<string> Sources { get; } = [];

    /// <summary>
    /// Are custom sources used in addition or instead of existing feeds.
    /// </summary>
    /// <remarks>
    /// This value has no impact if <see cref="Sources"/> is empty.
    /// </remarks>
    public bool IgnoreExistingFeeds { get; set; }

    /// <summary>
    /// Treats package source failures as warnings.
    /// </summary>
    public bool IgnoreFailedSources { get; set; }
}
