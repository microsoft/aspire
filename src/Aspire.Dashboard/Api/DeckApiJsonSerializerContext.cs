// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Serialization;

namespace Aspire.Dashboard.Api;

// Keep the React transport compatible with Native AOT. Reflection-based serialization
// would make this endpoint harder to move into the standalone dashboard backend later.
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(DeckConfig))]
[JsonSerializable(typeof(DeckResource[]))]
internal sealed partial class DeckApiJsonSerializerContext : JsonSerializerContext;
