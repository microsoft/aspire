// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Aspire.Hosting.Native.Rpc;

internal sealed partial class NativeRpcConnection
{
    private JsonNode? Authenticate(JsonNode? parameters)
    {
        if (parameters is not JsonArray { Count: 1 } authentication)
        {
            throw RpcFault.InvalidArgument("Authentication requires one token.");
        }
        var supplied = Encoding.UTF8.GetBytes(RpcArguments.String(authentication[0]));
        try
        {
            _authenticated = CryptographicOperations.FixedTimeEquals(supplied, _token);

            return JsonValue.Create(_authenticated);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(supplied);
        }
    }

    private JsonNode GetApplicationServer(JsonNode? parameters)
    {
        if (parameters is not null and not JsonArray { Count: 0 })
        {
            throw RpcFault.InvalidArgument("Server entry-point discovery takes no arguments.");
        }

        return _handles.Add(_server, NativeRpcContract.ApplicationServerTypeId);
    }

    private static JsonNode GetCapabilities(JsonNode? parameters)
    {
        if (parameters is not null and not JsonArray { Count: 0 })
        {
            throw RpcFault.InvalidArgument("Capability discovery takes no arguments.");
        }

        return s_contract.DeepClone();
    }

    private JsonNode GetAppHostServerConfiguration(JsonNode? parameters)
    {
        if (_languages is null || parameters is not null and not JsonArray { Count: 0 } ||
            _providers?.IsProvider(this) == true)
        {
            throw RpcFault.InvalidArgument("AppHost configuration requires an application connection and no arguments.");
        }

        return _languages.ProviderConfiguration();
    }

    private JsonNode Bootstrap(string method, JsonNode? parameters)
    {
        if (_languages is null)
        {
            throw RpcFault.MethodNotFound("The RPC method is not implemented.");
        }
        if (parameters is not JsonArray { Count: > 0 and <= 2 } arguments ||
            method == "getRuntimeSpec" && arguments.Count != 1 ||
            arguments.Count == 2 && arguments[1] is not null)
        {
            throw RpcFault.InvalidArgument("Bootstrap requires a language and does not support assembly filtering.");
        }

        return method == "getRuntimeSpec"
            ? _languages.RuntimeSpec(RpcArguments.String(arguments[0]))
            : _languages.GeneratedSdk(RpcArguments.String(arguments[0]));
    }

    private async Task<JsonNode?> InvokeCapabilityAsync(JsonNode? parameters)
    {
        if (parameters is not JsonArray { Count: 2 } arguments || arguments[1] is not JsonObject capabilityArguments)
        {
            throw RpcFault.InvalidArgument("Capability invocation requires an ID and an argument object.");
        }
        var capabilityId = RpcArguments.String(arguments[0]);
        if (_providers is not null)
        {
            if (_providers.IsProvider(this))
            {
                return await _providers.InvokeCoreAsync(this, capabilityId, capabilityArguments).ConfigureAwait(false);
            }
            if (!s_coreCapabilities.Contains(capabilityId))
            {
                return await _providers.InvokeAsync(this, capabilityId, capabilityArguments).ConfigureAwait(false);
            }
        }

        return await NativeDispatch.InvokeAsync(_server, _handles, this, this, capabilityId, capabilityArguments).ConfigureAwait(false);
    }

    private async Task<JsonNode?> InvokeProviderControlAsync(string method, JsonNode? parameters)
    {
        switch (method)
        {
            case "getIntegrationCapabilities":
                if (_providers is null || parameters is not null and not JsonArray { Count: 0 })
                {
                    throw RpcFault.InvalidArgument("Provider discovery takes no arguments.");
                }
                return _providers.InspectCapabilities();
            case "expectIntegrationHost":
                if (_providers is null || parameters is not JsonArray { Count: 1 } expectation || _providers.IsProvider(this))
                {
                    throw RpcFault.InvalidArgument("Expected registration requires an application connection and one package identity.");
                }
                return JsonValue.Create(_providers.Expect(this, RpcArguments.String(expectation[0])));
            case "registerAsIntegrationHost":
                if (_providers is null || _peer is null || parameters is not JsonArray { Count: 1 } registration)
                {
                    throw RpcFault.InvalidArgument("Provider registration requires one expected authority.");
                }
                await _providers.RegisterAsync(this, RpcArguments.String(registration[0]), _peer).ConfigureAwait(false);
                return JsonValue.Create(true);
            case "waitIntegrationHostRegistration":
                if (_providers is null || parameters is not JsonArray { Count: 1 } activation)
                {
                    throw RpcFault.InvalidArgument("Provider activation requires one expected authority.");
                }
                await _providers.WaitRegistrationAsync(this, RpcArguments.String(activation[0])).ConfigureAwait(false);
                return JsonValue.Create(true);
            case "abandonIntegrationHostRegistration":
                if (_providers is null || parameters is not JsonArray { Count: 1 } abandonment)
                {
                    throw RpcFault.InvalidArgument("Abandoning activation requires one expected authority.");
                }
                return JsonValue.Create(_providers.AbandonRegistration(this, RpcArguments.String(abandonment[0])));
            case "invokeGuestCallback":
                if (_providers is null || parameters is not JsonArray { Count: 2 } callback || callback[1] is not JsonObject callbackArguments)
                {
                    throw RpcFault.InvalidArgument("Callback invocation requires its authority and arguments.");
                }
                return await _providers.InvokeCallbackAsync(this, RpcArguments.String(callback[0]), callbackArguments).ConfigureAwait(false);
            default:
                throw RpcFault.MethodNotFound("The RPC method is not implemented.");
        }
    }
}
