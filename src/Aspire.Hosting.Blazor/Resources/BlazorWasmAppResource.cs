// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting;

/// <summary>
/// A resource representing a Blazor WebAssembly application project.
/// This is not a running process — it's metadata about a WASM project whose
/// static web assets are served through a Gateway.
/// Implements IResourceWithEnvironment so that WithReference() can be used
/// to declare service dependencies (the annotations are read at orchestration time).
/// Implements IResourceWithParent so that the orchestrator mirrors the gateway's
/// lifecycle state (Running, Stopped, etc.) to this child resource automatically.
/// </summary>
[Experimental("ASPIREBLAZOR001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public class BlazorWasmAppResource(string name, string projectPath) : Resource(name), IResourceWithEnvironment, IResourceWithParent
{
    /// <summary>Fully-qualified path to the .csproj file.</summary>
    public string ProjectPath { get; } = projectPath;

    /// <summary>Directory containing the .csproj file.</summary>
    public string ProjectDirectory => Path.GetDirectoryName(ProjectPath)!;

    /// <summary>
    /// Gets the parent gateway resource whose lifecycle state is mirrored to this resource.
    /// Set internally when <see cref="BlazorGatewayExtensions.WithBlazorClientApp"/> associates
    /// this WASM app with a gateway.
    /// </summary>
    public IResource Parent { get; internal set; } = null!;
}
