// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using PolyType;
using StreamJsonRpc;

namespace Aspire.Hosting.Native.Client;

/// <summary>Defines the native server's bootstrap and capability transport contract.</summary>
[JsonRpcContract]
[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
internal partial interface INativeAppHostRpc
{
    [JsonRpcMethod("authenticate")]
    Task<bool> AuthenticateAsync(string token);

    [JsonRpcMethod("getApplicationServer")]
    Task<JsonElement> GetApplicationServerAsync();

    [JsonRpcMethod("invokeCapability")]
    Task<JsonElement> InvokeCapabilityAsync(string capability, Dictionary<string, JsonElement> arguments);

    [JsonRpcMethod("expectIntegrationHost")]
    Task<string> ExpectIntegrationHostAsync(string packageName);

    [JsonRpcMethod("waitIntegrationHostRegistration")]
    Task<bool> WaitIntegrationHostRegistrationAsync(string authority);

    [JsonRpcMethod("abandonIntegrationHostRegistration")]
    Task<bool> AbandonIntegrationHostRegistrationAsync(string authority);

    [JsonRpcMethod("getIntegrationCapabilities")]
    Task<JsonElement> GetIntegrationCapabilitiesAsync();

    [JsonRpcMethod("registerAsIntegrationHost")]
    Task<bool> RegisterIntegrationHostAsync(string authority);

    [JsonRpcMethod("invokeGuestCallback")]
    Task<JsonElement> InvokeGuestCallbackAsync(string callback, Dictionary<string, JsonElement> arguments);
}
