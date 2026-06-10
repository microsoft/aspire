// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;
using Aspire.Hosting.RemoteHost.Ats;
using Aspire.Hosting.RemoteHost.Diagnostics;
using Microsoft.Extensions.Logging;
using StreamJsonRpc;

namespace Aspire.Hosting.RemoteHost;

internal sealed class RemoteAppHostService
{
    private readonly JsonRpcAuthenticationState _authenticationState;
    private readonly JsonRpcCallbackInvoker _callbackInvoker;
    private readonly CancellationTokenRegistry _cancellationTokenRegistry;
    private readonly ILogger<RemoteAppHostService> _logger;
    private readonly RemoteHostProfilingTelemetry _profilingTelemetry;
    private JsonRpc? _clientRpc;

    // ATS (Aspire Type System) components
    private readonly CapabilityDispatcher _capabilityDispatcher;

    public RemoteAppHostService(
        JsonRpcAuthenticationState authenticationState,
        JsonRpcCallbackInvoker callbackInvoker,
        CancellationTokenRegistry cancellationTokenRegistry,
        CapabilityDispatcher capabilityDispatcher,
        ILogger<RemoteAppHostService> logger,
        RemoteHostProfilingTelemetry profilingTelemetry)
    {
        _authenticationState = authenticationState;
        _callbackInvoker = callbackInvoker;
        _cancellationTokenRegistry = cancellationTokenRegistry;
        _capabilityDispatcher = capabilityDispatcher;
        _logger = logger;
        _profilingTelemetry = profilingTelemetry;
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
        using var activity = _profilingTelemetry.StartJsonRpcServerCall("authenticate");
        try
        {
            var authenticated = _authenticationState.Authenticate(token);
            activity.AddAuthenticationResult(authenticated);
            if (!authenticated)
            {
                _logger.LogWarning("Rejected unauthenticated AppHost RPC client.");
                // Close the connection to prevent unlimited retry attempts.
                _ = Task.Run(() => _clientRpc?.Dispose());
            }

            return authenticated;
        }
        catch (Exception ex)
        {
            activity.SetError(ex);
            throw;
        }
    }

    [JsonRpcMethod("ping")]
#pragma warning disable CA1822 // Mark members as static - JSON-RPC methods must be instance methods
    public string Ping()
#pragma warning restore CA1822
    {
        using var activity = _profilingTelemetry.StartJsonRpcServerCall("ping");
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
        using var activity = _profilingTelemetry.StartJsonRpcServerCall("cancelToken");
        try
        {
            _authenticationState.ThrowIfNotAuthenticated();
            _logger.LogDebug("cancelToken({TokenId})", tokenId);
            return _cancellationTokenRegistry.Cancel(tokenId);
        }
        catch (Exception ex)
        {
            activity.SetError(ex);
            throw;
        }
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
        using var activity = _profilingTelemetry.StartJsonRpcInvokeCapability(capabilityId, args);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            _authenticationState.ThrowIfNotAuthenticated();
            _logger.LogDebug(">> invokeCapability({CapabilityId}) args: {Args}", capabilityId, args?.ToJsonString() ?? "null");
            var result = await _capabilityDispatcher.InvokeAsync(capabilityId, args).ConfigureAwait(false);
            _logger.LogDebug("   invokeCapability({CapabilityId}) result: {Result}", capabilityId, result?.ToJsonString() ?? "null");
            return result;
        }
        catch (CapabilityException ex)
        {
            activity.SetError(ex);
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
        catch (InvalidOperationException ex) when (!_authenticationState.IsAuthenticated)
        {
            // ThrowIfNotAuthenticated throws InvalidOperationException for unauthenticated callers.
            // Let it propagate as a JSON-RPC error instead of wrapping it in a structured $error
            // payload, so the client surfaces it as an authentication failure rather than a
            // capability-level error.
            activity.SetError(ex);
            throw;
        }
        catch (Exception ex)
        {
            activity.SetError(ex);
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
}
