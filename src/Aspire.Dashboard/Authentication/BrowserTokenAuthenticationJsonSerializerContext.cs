// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Serialization;

namespace Aspire.Dashboard.Authentication;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(BrowserTokenLoginRequest))]
[JsonSerializable(typeof(bool))]
internal sealed partial class BrowserTokenAuthenticationJsonSerializerContext : JsonSerializerContext;

internal sealed record BrowserTokenLoginRequest(string Token);
