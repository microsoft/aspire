// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting;

/// <summary>
/// A resource representing a Blazor WebAssembly application project.
/// This is not a running process — it's metadata about a WASM project whose
/// static web assets are served through a Gateway.
/// Implements IResourceWithEnvironment so that WithReference() can be used
/// to declare service dependencies (the annotations are read at orchestration time).
/// </summary>
public class BlazorWasmAppResource(string name, string projectPath) : Resource(name), IResourceWithEnvironment
{
    /// <summary>Fully-qualified path to the .csproj file.</summary>
    public string ProjectPath { get; } = projectPath;

    /// <summary>Directory containing the .csproj file.</summary>
    public string ProjectDirectory => Path.GetDirectoryName(ProjectPath)!;
}
