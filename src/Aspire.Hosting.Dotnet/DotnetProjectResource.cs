// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.ApplicationModel;

#pragma warning disable ASPIREPROJECTS001 // ProjectLaunchDefaultsAnnotation is experimental.

namespace Aspire.Hosting.Dotnet;

/// <summary>
/// A resource that represents a specified C# project or file-based app added by path.
/// </summary>
/// <remarks>
/// <para>
/// A <see cref="DotnetProjectResource"/> is added by path and is
/// launched as an executable: <c>dotnet run --project &lt;path&gt;</c> for a project file, or
/// <c>dotnet run --file &lt;path&gt;</c> for a file-based app (a <c>.cs</c> file).
/// </para>
/// </remarks>
[Experimental("ASPIREDOTNETPROJECT001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
[AspireExport(ExposeProperties = true)]
public class DotnetProjectResource : ExecutableResource, IResourceWithServiceDiscovery
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DotnetProjectResource"/> class.
    /// </summary>
    /// <param name="name">The name of the resource in the application model.</param>
    /// <param name="workingDirectory">The working directory for the app, typically the directory containing the project or <c>.cs</c> file.</param>
    public DotnetProjectResource(string name, string workingDirectory) : base(name, "dotnet", workingDirectory)
    {
        // Ensure uniform C# project defaults, including the Rebuild command and Kestrel endpoint wiring.
        Annotations.Add(new ProjectLaunchDefaultsAnnotation());
    }
}
