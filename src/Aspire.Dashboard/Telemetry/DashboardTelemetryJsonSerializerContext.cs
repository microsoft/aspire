// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Serialization;

namespace Aspire.Dashboard.Telemetry;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(StartOperationRequest))]
[JsonSerializable(typeof(EndOperationRequest))]
[JsonSerializable(typeof(PostOperationRequest))]
[JsonSerializable(typeof(PostFaultRequest))]
[JsonSerializable(typeof(PostAssetRequest))]
[JsonSerializable(typeof(PostPropertyRequest))]
[JsonSerializable(typeof(PostCommandLineFlagsRequest))]
[JsonSerializable(typeof(StartOperationResponse))]
[JsonSerializable(typeof(TelemetryEventCorrelation))]
[JsonSerializable(typeof(TelemetryEnabledResponse))]
internal sealed partial class DashboardTelemetryJsonSerializerContext : JsonSerializerContext;
