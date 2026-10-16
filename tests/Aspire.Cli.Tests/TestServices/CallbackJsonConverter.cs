// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Aspire.Cli.Tests.TestServices;

internal sealed class CallbackJsonConverter(Action onWrite) : JsonConverter<int>
{
    public static JsonValue CreateValue(Action onWrite)
        => JsonValue.Create(1, JsonMetadataServices.CreateValueInfo<int>(
            JsonSourceGenerationContext.Default.Options, new CallbackJsonConverter(onWrite)))!;

    public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.GetInt32();

    public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options)
    {
        onWrite();
        writer.WriteNumberValue(value);
    }
}
