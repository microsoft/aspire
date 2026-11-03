// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aspire.Shared.Telemetry;

[JsonSourceGenerationOptions(
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    AllowTrailingCommas = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    Converters = [typeof(FlexibleBooleanConverter)])]
[JsonSerializable(typeof(InternalMicrosoftDetectorCacheEntry))]
internal partial class InternalMicrosoftDetectorJsonContext : JsonSerializerContext;
