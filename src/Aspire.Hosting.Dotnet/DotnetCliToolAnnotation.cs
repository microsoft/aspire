// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Dotnet;

/// <summary>
/// Configures acquisition of a .NET CLI tool package.
/// </summary>
/// <remarks>
/// Configure this annotation through the <c>WithTool*</c> builder methods. Acquisition uses
/// <c>dotnet tool exec</c> and does not permanently install the tool.
/// </remarks>
[Experimental("ASPIREDOTNETCLITOOL001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public class DotnetCliToolAnnotation : IResourceAnnotation
{
    /// <summary>
    /// Gets or sets the NuGet tool package ID, optionally including a version such as <c>dotnetsay@2.1</c>.
    /// </summary>
    public required string PackageId { get; set; }

    /// <summary>
    /// Gets or sets the package version or version range.
    /// </summary>
    public string? Version { get; set; }

    /// <summary>
    /// Gets or sets whether prerelease packages can be selected when no version is specified.
    /// </summary>
    public bool Prerelease { get; set; }

    /// <summary>
    /// Gets the NuGet package sources used for acquisition.
    /// </summary>
    public List<string> Sources { get; } = [];

    /// <summary>
    /// Gets or sets whether nonempty <see cref="Sources"/> replace the existing NuGet configuration.
    /// </summary>
    public bool IgnoreExistingFeeds { get; set; }

    /// <summary>
    /// Gets or sets whether package source failures are treated as warnings.
    /// </summary>
    public bool IgnoreFailedSources { get; set; }
}
