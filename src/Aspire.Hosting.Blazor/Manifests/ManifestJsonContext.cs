// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aspire.Hosting;

[JsonSerializable(typeof(ClientConfiguration))]
[JsonSerializable(typeof(EndpointsManifest))]
[JsonSerializable(typeof(DevelopmentManifest))]
[JsonSerializable(typeof(MSBuildPropertiesOutput))]
[JsonSourceGenerationOptions(
    WriteIndented = true)]
internal partial class ManifestJsonContext : JsonSerializerContext
{
    /// <summary>
    /// A context instance that uses <see cref="JavaScriptEncoder.UnsafeRelaxedJsonEscaping"/>
    /// to avoid over-escaping non-ASCII characters in client configuration JSON.
    /// </summary>
    internal static ManifestJsonContext Relaxed { get; } = new(new JsonSerializerOptions
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    });
}
