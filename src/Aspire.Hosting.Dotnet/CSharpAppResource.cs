// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// A resource that represents a specified C# project or file-based app added by path.
/// </summary>
/// <remarks>
/// <para>
/// A <see cref="CSharpAppResource"/> is added by path and is
/// launched as an executable: <c>dotnet run --project &lt;path&gt;</c> for a project file, or
/// <c>dotnet run --file &lt;path&gt;</c> for a file-based app (a <c>.cs</c> file). 
/// </para>
/// </remarks>
/// <param name="name">The name of the resource in the application model.</param>
/// <param name="workingDirectory">The working directory for the app, typically the directory containing the project or <c>.cs</c> file.</param>
[AspireExport(ExposeProperties = true)]
public class CSharpAppResource(string name, string workingDirectory)
    : ExecutableResource(name, "dotnet", workingDirectory), IResourceWithServiceDiscovery, IProjectLaunchDefaultsResource
{
    // The project-defaults wiring lives in Aspire.Hosting core (WithProjectDefaults) and operates against
    // IProjectLaunchDefaultsResource. These members supply the small amount of per-endpoint state it
    // needs; they are implemented explicitly so they don't leak into the public/polyglot surface.
    private readonly Dictionary<EndpointAnnotation, string> _kestrelEndpointAnnotationHosts = new();
    private EndpointAnnotation? _defaultHttpsEndpoint;

    Dictionary<EndpointAnnotation, string> IProjectLaunchDefaultsResource.KestrelEndpointAnnotationHosts => _kestrelEndpointAnnotationHosts;

    EndpointAnnotation? IProjectLaunchDefaultsResource.DefaultHttpsEndpoint
    {
        get => _defaultHttpsEndpoint;
        set => _defaultHttpsEndpoint = value;
    }
}
