// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.GitHubPages;

/// <summary>
/// Represents the static website deployment target for a resource assigned to GitHub Pages.
/// </summary>
/// <remarks>
/// This metadata-only target is created during publish preparation and has no process or container lifetime.
/// </remarks>
[Experimental("ASPIREGITHUBPAGES001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
[AspireExport]
public sealed class GitHubPagesSiteResource : Resource, IResourceWithoutLifetime
{
    internal GitHubPagesSiteResource(IResource source, GitHubPagesEnvironmentResource environment)
        : base($"{source.Name}-github-pages")
    {
        Source = source;
        Environment = environment;
    }

    /// <summary>
    /// Gets the resource whose image supplies the static website files.
    /// </summary>
    public IResource Source { get; }

    /// <summary>
    /// Gets the environment that receives the static website.
    /// </summary>
    public GitHubPagesEnvironmentResource Environment { get; }

    /// <summary>
    /// Gets the independently selectable pipeline step that writes the website bundle.
    /// </summary>
    public string BundleStepName => $"bundle-{Source.Name}";
}
