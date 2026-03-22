// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Commands.Sdk;
using Aspire.TypeSystem;

namespace Aspire.Cli.Projects;

/// <summary>
/// Client for making JSON-RPC calls to the AppHost server.
/// Provides typed methods for known RPC operations.
/// </summary>
internal interface IAppHostRpcClient : IAsyncDisposable
{
    // ═══════════════════════════════════════════════════════════════
    // TYPED WRAPPERS FOR KNOWN RPC CALLS
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Gets the runtime specification for a language.
    /// RPC method: "getRuntimeSpec"
    /// </summary>
    Task<RuntimeSpec> GetRuntimeSpecAsync(string languageId, CancellationToken cancellationToken);

    /// <summary>
    /// Gets scaffold files for a new AppHost project.
    /// RPC method: "scaffoldAppHost"
    /// </summary>
    Task<Dictionary<string, string>> ScaffoldAppHostAsync(
        string languageId,
        string targetPath,
        string? projectName,
        CancellationToken cancellationToken);

    /// <summary>
    /// Generates code (e.g., TypeScript SDK) for a language.
    /// RPC method: "generateCode"
    /// </summary>
    Task<Dictionary<string, string>> GenerateCodeAsync(string languageId, CancellationToken cancellationToken);

    /// <summary>
    /// Gets the ATS capabilities, types, and diagnostics.
    /// RPC method: "getCapabilities"
    /// </summary>
    Task<CapabilitiesInfo> GetCapabilitiesAsync(CancellationToken cancellationToken);

    // ═══════════════════════════════════════════════════════════════
    // GENERIC INVOKE (for future/custom calls)
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Invokes an RPC method and returns the result.
    /// Use typed methods above when available.
    /// </summary>
    Task<T> InvokeAsync<T>(string methodName, object?[] parameters, CancellationToken cancellationToken);

    /// <summary>
    /// Invokes an RPC method without a return value.
    /// </summary>
    Task InvokeAsync(string methodName, object?[] parameters, CancellationToken cancellationToken);
}

/// <summary>
/// Factory for creating connected RPC clients.
/// </summary>
internal interface IAppHostRpcClientFactory
{
    /// <summary>
    /// Creates and connects an RPC client to the specified socket path.
    /// Handles platform-specific connection (Unix sockets vs named pipes).
    /// </summary>
    Task<IAppHostRpcClient> ConnectAsync(string socketPath, CancellationToken cancellationToken);
}
