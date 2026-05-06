// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Serialization;

namespace Aspire.Hosting;

[JsonSerializable(typeof(ClientConfiguration))]
[JsonSerializable(typeof(EndpointsManifest))]
[JsonSerializable(typeof(DevelopmentManifest))]
[JsonSerializable(typeof(MSBuildPropertiesOutput))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.Unspecified,
    WriteIndented = true)]
internal partial class ManifestJsonContext : JsonSerializerContext
{
}
