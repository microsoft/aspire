// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Marks a resource (gateway or host) as serving a browser-debuggable WebAssembly client.
/// At orchestration time, an <c>IdeSession</c> DCP resource is created for each annotation,
/// initially in "Initial" state. When the user invokes "Debug in Browser", the orchestrator
/// transitions the IdeSession to "Starting" and DCP initiates the IDE debug session.
/// </summary>
/// <param name="clientProjectPath">Absolute path to the WASM client <c>.csproj</c> for IDE symbol resolution.</param>
/// <param name="relativePath">
/// Optional path segment appended to the resolved endpoint URL (e.g., the WASM app's path prefix on a gateway).
/// </param>
internal sealed class BrowserDebugAnnotation(string clientProjectPath, string? relativePath = null) : IResourceAnnotation
{
    /// <summary>
    /// Absolute path to the WASM client .csproj file.
    /// The IDE uses this to locate assemblies, PDBs, and source files for symbol resolution.
    /// </summary>
    public string ClientProjectPath { get; } = clientProjectPath;

    /// <summary>
    /// Optional path appended to the base endpoint URL to form the app URL.
    /// For example, when a WASM app is served at "/{prefix}/" on a gateway.
    /// </summary>
    public string? RelativePath { get; } = relativePath;

    /// <summary>
    /// The DCP IdeSession name assigned to this annotation during orchestration.
    /// Set by the executor after creating the IdeSession resource.
    /// </summary>
    internal string? IdeSessionName { get; set; }
}
