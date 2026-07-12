// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Serialization;

namespace Aspire.Dashboard.Api;

// Keep the React transport compatible with Native AOT. Reflection-based serialization
// would make this endpoint harder to move into the standalone dashboard backend later.
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(DeckConfig))]
[JsonSerializable(typeof(DeckManageDataResponse))]
[JsonSerializable(typeof(DeckManageDataRequest))]
[JsonSerializable(typeof(DeckResource[]))]
[JsonSerializable(typeof(DeckExecuteCommandRequest))]
[JsonSerializable(typeof(DeckCommandResponse))]
[JsonSerializable(typeof(DeckConsoleLogEvent))]
[JsonSerializable(typeof(DeckInteraction[]))]
[JsonSerializable(typeof(DeckRespondInteractionRequest))]
[JsonSerializable(typeof(DeckMetricSummary[]))]
[JsonSerializable(typeof(DeckMetricSeriesResponse))]
[JsonSerializable(typeof(DeckMetricDimensionFilter[]))]
[JsonSerializable(typeof(DeckMetricDimensionSeries[]))]
[JsonSerializable(typeof(DeckMetricExemplar[]))]
[JsonSerializable(typeof(DeckMetricAttribute[]))]
internal sealed partial class DeckApiJsonSerializerContext : JsonSerializerContext;
