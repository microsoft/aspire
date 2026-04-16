// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;
using Aspire.Hosting.RemoteHost.Ats;
using Microsoft.Extensions.Logging;
using StreamJsonRpc;

namespace Aspire.Hosting.RemoteHost;

internal sealed class RemoteAppHostService
{
    private readonly JsonRpcAuthenticationState _authenticationState;
    private readonly JsonRpcCallbackInvoker _callbackInvoker;
    private readonly CancellationTokenRegistry _cancellationTokenRegistry;
    private readonly ExternalCapabilityRegistry _externalCapabilityRegistry;
    private readonly ILogger<RemoteAppHostService> _logger;
    private JsonRpc? _clientRpc;

    // ATS (Aspire Type System) components
    private readonly CapabilityDispatcher _capabilityDispatcher;

    public RemoteAppHostService(
        JsonRpcAuthenticationState authenticationState,
        JsonRpcCallbackInvoker callbackInvoker,
        CancellationTokenRegistry cancellationTokenRegistry,
        CapabilityDispatcher capabilityDispatcher,
        ExternalCapabilityRegistry externalCapabilityRegistry,
        ILogger<RemoteAppHostService> logger)
    {
        _authenticationState = authenticationState;
        _callbackInvoker = callbackInvoker;
        _cancellationTokenRegistry = cancellationTokenRegistry;
        _capabilityDispatcher = capabilityDispatcher;
        _externalCapabilityRegistry = externalCapabilityRegistry;
        _logger = logger;
    }

    /// <summary>
    /// Sets the JSON-RPC connection for callback invocation.
    /// </summary>
    public void SetClientConnection(JsonRpc clientRpc)
    {
        _clientRpc = clientRpc;
        _callbackInvoker.SetConnection(clientRpc);
    }

    /// <summary>
    /// Verifies the authentication token supplied by the client.
    /// Returns <c>true</c> on success; closes the connection and returns <c>false</c> on failure
    /// so that an unauthenticated client cannot keep retrying without limit.
    /// </summary>
    [JsonRpcMethod("authenticate")]
    public bool Authenticate(string token)
    {
        var authenticated = _authenticationState.Authenticate(token);
        if (!authenticated)
        {
            _logger.LogWarning("Rejected unauthenticated AppHost RPC client.");
            // Close the connection to prevent unlimited retry attempts.
            _ = Task.Run(() => _clientRpc?.Dispose());
        }

        return authenticated;
    }

    [JsonRpcMethod("ping")]
#pragma warning disable CA1822 // Mark members as static - JSON-RPC methods must be instance methods
    public string Ping()
#pragma warning restore CA1822
    {
        return "pong";
    }

    /// <summary>
    /// Cancels a CancellationToken by its ID.
    /// Called by the guest when an AbortSignal is aborted.
    /// </summary>
    /// <param name="tokenId">The token ID returned from capability invocation.</param>
    /// <returns>True if the token was found and cancelled, false otherwise.</returns>
    [JsonRpcMethod("cancelToken")]
    public bool CancelToken(string tokenId)
    {
        _authenticationState.ThrowIfNotAuthenticated();
        _logger.LogDebug("cancelToken({TokenId})", tokenId);
        return _cancellationTokenRegistry.Cancel(tokenId);
    }

    #region ATS Capabilities

    /// <summary>
    /// Invokes an ATS capability by ID.
    /// </summary>
    /// <param name="capabilityId">The capability ID (e.g., "aspire.redis/addRedis@1").</param>
    /// <param name="args">The arguments as a JSON object.</param>
    /// <returns>The result as JSON, or an error object.</returns>
    [JsonRpcMethod("invokeCapability")]
    public async Task<JsonNode?> InvokeCapabilityAsync(string capabilityId, JsonObject? args)
    {
        _authenticationState.ThrowIfNotAuthenticated();
        _logger.LogDebug(">> invokeCapability({CapabilityId}) args: {Args}", capabilityId, args?.ToJsonString() ?? "null");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var result = await _capabilityDispatcher.InvokeAsync(capabilityId, args, _callbackInvoker).ConfigureAwait(false);
            _logger.LogDebug("   invokeCapability({CapabilityId}) result: {Result}", capabilityId, result?.ToJsonString() ?? "null");
            return result;
        }
        catch (CapabilityException ex)
        {
            _logger.LogWarning("   invokeCapability({CapabilityId}) CapabilityException: {Code} - {Message}", capabilityId, ex.Error.Code, ex.Error.Message);
            if (ex.Error.Details != null)
            {
                _logger.LogWarning("   Details: param={Parameter}, expected={Expected}, actual={Actual}", ex.Error.Details.Parameter, ex.Error.Details.Expected, ex.Error.Details.Actual);
            }
            // Return structured error
            return new JsonObject
            {
                ["$error"] = ex.Error.ToJsonObject()
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "   invokeCapability({CapabilityId}) Exception: {ExceptionType} - {Message}", capabilityId, ex.GetType().Name, ex.Message);
            // Wrap unexpected errors
            var error = new AtsError
            {
                Code = AtsErrorCodes.InternalError,
                Message = ex.Message,
                Capability = capabilityId
            };
            return new JsonObject
            {
                ["$error"] = error.ToJsonObject()
            };
        }
        finally
        {
            _logger.LogDebug("<< invokeCapability({CapabilityId}) completed in {ElapsedMs}ms", capabilityId, sw.ElapsedMilliseconds);
        }
    }

    #endregion

    #region Integration Host Protocol

    /// <summary>
    /// Registers this connection as an integration host.
    /// The engine will later call getCapabilities on it to discover capabilities.
    /// </summary>
    [JsonRpcMethod("registerAsIntegrationHost")]
    public bool RegisterAsIntegrationHost()
    {
        _authenticationState.ThrowIfNotAuthenticated();

        if (_clientRpc is null)
        {
            return false;
        }

        _externalCapabilityRegistry.AddIntegrationHost(_clientRpc);
        return true;
    }

    // initializeIntegrationHosts RPC removed: the AppHost server now reads its IntegrationHosts
    // list from appsettings.json (written by the CLI's csproj generation step) and the
    // IntegrationHostLauncher's StartAsync spawns + initializes them as part of server boot,
    // before any guest connects. Symmetric with how .NET integrations are loaded — the server
    // gets its full integration set from its own startup config, not from a runtime RPC.

    /// <summary>
    /// Invokes a guest-owned callback on behalf of an integration host.
    /// The integration host receives a callback id string as an argument to <c>handleExternalCapability</c>
    /// and calls this method to trigger the callback, which is routed back to the originating guest
    /// via the owning scope's <see cref="JsonRpcCallbackInvoker"/>.
    /// </summary>
    /// <param name="callbackId">The callback id, as originally sent by the guest.</param>
    /// <param name="args">Positional argument payload shaped as <c>{ p0, p1, ... }</c>.</param>
    /// <returns>The result returned by the guest's callback, or <c>null</c> for void callbacks.</returns>
    [JsonRpcMethod("invokeGuestCallback")]
    public async Task<JsonNode?> InvokeGuestCallbackAsync(string callbackId, JsonObject? args)
    {
        _authenticationState.ThrowIfNotAuthenticated();
        _logger.LogDebug(">> invokeGuestCallback({CallbackId})", callbackId);

        var owner = _externalCapabilityRegistry.ResolveCallbackOwner(callbackId)
            ?? throw new InvalidOperationException(
                $"No owning guest connection is currently registered for callback '{callbackId}'. " +
                "The callback id must be in scope of an in-flight external capability invocation.");

        var result = await owner.InvokeAsync<JsonNode?>(callbackId, args, CancellationToken.None).ConfigureAwait(false);
        _logger.LogDebug("<< invokeGuestCallback({CallbackId})", callbackId);
        return result;
    }

    #endregion
}
