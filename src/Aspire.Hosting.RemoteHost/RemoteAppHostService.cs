// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;
using Aspire.Hosting.RemoteHost.Ats;
using Microsoft.Extensions.Logging;
using StreamJsonRpc;

namespace Aspire.Hosting.RemoteHost;

internal sealed class RemoteAppHostService
{
    private readonly JsonRpcCallbackInvoker _callbackInvoker;
    private readonly CancellationTokenRegistry _cancellationTokenRegistry;
    private readonly ILogger<RemoteAppHostService> _logger;

    // ATS (Aspire Type System) components
    private readonly CapabilityDispatcher _capabilityDispatcher;

    public RemoteAppHostService(
        JsonRpcCallbackInvoker callbackInvoker,
        CancellationTokenRegistry cancellationTokenRegistry,
        CapabilityDispatcher capabilityDispatcher,
        ILogger<RemoteAppHostService> logger)
    {
        _callbackInvoker = callbackInvoker;
        _cancellationTokenRegistry = cancellationTokenRegistry;
        _capabilityDispatcher = capabilityDispatcher;
        _logger = logger;
    }

    /// <summary>
    /// Sets the JSON-RPC connection for callback invocation.
    /// </summary>
    public void SetClientConnection(JsonRpc clientRpc)
    {
        _callbackInvoker.SetConnection(clientRpc);
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
        _logger.LogDebug(">> invokeCapability({CapabilityId}) args: {Args}", capabilityId, args?.ToJsonString() ?? "null");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var result = await _capabilityDispatcher.InvokeAsync(capabilityId, args).ConfigureAwait(false);
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
}
