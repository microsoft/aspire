// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Serialization;

namespace Aspire.Dashboard.Backend;

// Every payload in the versioned React contract must be registered here. The standalone
// backend is Native AOT, so accidentally adding a reflection-serialized endpoint must fail
// during development instead of becoming a runtime-only failure in a published binary.
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(DashboardApiDiscovery))]
[JsonSerializable(typeof(DashboardConfiguration))]
internal sealed partial class DashboardBackendJsonSerializerContext : JsonSerializerContext;
