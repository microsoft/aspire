// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Dotnet;

/// <summary>
/// Represents an executable acquired and launched as a .NET CLI tool package.
/// </summary>
/// <remarks>
/// Use <see cref="DotnetCliToolResourceExtensions.AddDotnetCliTool(IDistributedApplicationBuilder, string, string)"/>
/// to configure acquisition and execution. Tools require .NET SDK 10 or later and retain ordinary
/// executable environment, arguments, endpoints, and manifest publishing support.
/// </remarks>
[Experimental("ASPIREDOTNETCLITOOL001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
[DebuggerDisplay("Type = {GetType().Name,nq}, Name = {Name}, Tool = {ToolConfiguration?.PackageId}")]
#pragma warning disable ASPIRETOOLRESOURCE001 // Tools opt into the experimental executable classification contract.
public class DotnetCliToolResource : ExecutableResource, IToolResource
#pragma warning restore ASPIRETOOLRESOURCE001
{
    /// <summary>
    /// Initializes a .NET CLI tool resource.
    /// </summary>
    /// <param name="name">The resource name.</param>
    /// <param name="packageId">The NuGet tool package ID.</param>
    /// <exception cref="ArgumentException">The name or package ID is empty or whitespace.</exception>
    /// <exception cref="ArgumentNullException">The name or package ID is null.</exception>
    public DotnetCliToolResource(string name, string packageId) : base(name, "dotnet", ".")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        Annotations.Add(new DotnetCliToolAnnotation { PackageId = packageId });
    }

    internal DotnetCliToolAnnotation? ToolConfiguration
        => this.TryGetLastAnnotation<DotnetCliToolAnnotation>(out var annotation) ? annotation : null;

    internal IEnumerable<ResourcePropertySnapshot> CreateSnapshotProperties()
        => ToolConfiguration is { } configuration
            ? DotnetToolHelpers.CreateSnapshotProperties(configuration.PackageId, configuration.Version)
            : [];
}
