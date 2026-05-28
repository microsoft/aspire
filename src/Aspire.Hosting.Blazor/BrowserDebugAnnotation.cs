// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting;

/// <summary>
/// Marks a resource as serving a browser-debuggable WebAssembly client.
/// Used as an idempotency marker to prevent duplicate debugger resource registration.
/// </summary>
/// <param name="clientProjectPath">Absolute path to the WASM client <c>.csproj</c> for IDE symbol resolution.</param>
/// <param name="relativePath">
/// Optional path segment appended to the resolved endpoint URL (e.g., the WASM app's path prefix on a gateway).
/// </param>
internal sealed class BrowserDebugAnnotation(string clientProjectPath, string? relativePath = null) : IResourceAnnotation
{
    /// <summary>
    /// Absolute path to the WASM client .csproj file.
    /// </summary>
    public string ClientProjectPath { get; } = clientProjectPath;

    /// <summary>
    /// Optional path appended to the base endpoint URL to form the app URL.
    /// </summary>
    public string? RelativePath { get; } = relativePath;

    /// <summary>
    /// The name of the child debugger resource created for this annotation.
    /// Set during resource registration so the command handler can reference it.
    /// </summary>
    internal string? DebuggerResourceName { get; set; }
}
