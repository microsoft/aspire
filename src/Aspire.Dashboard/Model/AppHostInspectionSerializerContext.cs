// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Serialization;
using Aspire.Shared;

namespace Aspire.Dashboard.Model;

/// <summary>
/// Source-generated serializer context for the AppHost MSBuild inspection JSON. The records carry
/// explicit <c>JsonPropertyName</c> attributes that match the MSBuild output, so no naming policy is
/// required; case-insensitive matching is enabled only as a defensive measure.
/// </summary>
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(AppHostProjectInspectionOutput))]
internal sealed partial class AppHostInspectionSerializerContext : JsonSerializerContext;
