// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Marks a resource as serving a browser-debuggable WebAssembly client.
/// A hidden child <see cref="ExecutableResource"/> is created for the debugger;
/// when the user clicks "Debug in Browser", the child resource is started via DCP
/// with ExecutionType=IDE and a browser launch configuration, causing the IDE
/// to open a debug-enabled browser navigated to the app URL.
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
    /// The name of the child debugger resource created for this annotation.
    /// Set during resource registration so the command handler can reference it.
    /// </summary>
    internal string? DebuggerResourceName { get; set; }
}
