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
    /// Generates code (e.g., TypeScript SDK) for a language using all available ATS types.
    /// </summary>
    /// <remarks>
    /// Calls the <c>generateCode</c> RPC method with no assembly filter.
    /// </remarks>
    Task<Dictionary<string, string>> GenerateCodeAsync(string languageId, CancellationToken cancellationToken);

    /// <summary>
    /// Generates code for a language, scoped to a specific integration assembly.
    /// </summary>
    /// <remarks>
    /// Calls the <c>generateCode</c> RPC method with an assembly filter so that only types
    /// exported by the specified assembly (and their referenced types) are included.
    /// </remarks>
    /// <param name="languageId">The target language identifier.</param>
    /// <param name="assemblyName">The assembly name to scope code generation to.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task<Dictionary<string, string>> GenerateCodeForAssemblyAsync(string languageId, string assemblyName, CancellationToken cancellationToken);

    /// <summary>
    /// Gets the ATS capabilities, types, and diagnostics for all available assemblies.
    /// </summary>
    /// <remarks>
    /// Calls the <c>getCapabilities</c> RPC method with no assembly filter.
    /// </remarks>
    Task<CapabilitiesInfo> GetCapabilitiesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Gets the ATS capabilities, types, and diagnostics, scoped to the specified exporting assemblies.
    /// </summary>
    /// <remarks>
    /// Calls the <c>getCapabilities</c> RPC method with an assembly filter so that only
    /// capabilities and types exported by the specified assemblies are included.
    /// </remarks>
    /// <param name="assemblyNames">The assembly names to filter capabilities by.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task<CapabilitiesInfo> GetCapabilitiesForAssembliesAsync(IReadOnlyList<string> assemblyNames, CancellationToken cancellationToken);

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
