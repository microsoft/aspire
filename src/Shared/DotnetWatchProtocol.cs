// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aspire.Shared;

internal static class DotnetWatchProtocol
{
    internal const int MaximumMessageBytes = 1024 * 1024;
    private static readonly UTF8Encoding s_utf8 = new(false, true);

    internal static async Task<string> ReadStringAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[1];
        uint length = 0;
        // The pinned resource protocol uses BinaryWriter strings: a 7-bit encoded byte
        // count followed by UTF-8 bytes, not a newline-delimited message.
        for (var index = 0; index < 5; index++)
        {
            await stream.ReadExactlyAsync(buffer, cancellationToken).ConfigureAwait(false);
            var value = buffer[0];
            if (index == 4 && value > 7)
            {
                throw new InvalidDataException("Invalid .NET watch message length.");
            }

            length |= (uint)(value & 0x7f) << (index * 7);
            if ((value & 0x80) == 0)
            {
                if (length > MaximumMessageBytes)
                {
                    throw new InvalidDataException("The .NET watch message exceeds the supported size.");
                }

                var payload = new byte[(int)length];
                await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
                return s_utf8.GetString(payload);
            }
        }

        throw new InvalidDataException("Invalid .NET watch message length.");
    }

    internal static async Task WriteStringAsync(Stream stream, string value, CancellationToken cancellationToken)
    {
        var bytes = s_utf8.GetBytes(value);
        if (bytes.Length > MaximumMessageBytes)
        {
            throw new InvalidDataException("The .NET watch request exceeds the supported size.");
        }

        var length = (uint)bytes.Length;
        var prefix = new byte[5];
        var count = 0;
        while (length >= 128)
        {
            prefix[count++] = (byte)(length | 0x80);
            length >>= 7;
        }

        prefix[count++] = (byte)length;
        await stream.WriteAsync(prefix.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
    }

    internal static string CreateLaunchRequest(
        string entryPoint,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environment,
        bool excludeProfile,
        string? profile)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("EntryPoint", entryPoint);
            writer.WriteStartArray("ApplicationArguments");
            // The pinned server sets cwd but omits --project/--file. Explicit identity is
            // required for file apps and directories containing more than one project.
            writer.WriteStringValue(entryPoint.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ? "--file" : "--project");
            writer.WriteStringValue(entryPoint);
            if (arguments.Count > 0)
            {
                // The upstream launcher does not add the dotnet run/application separator.
                // Include it in the request so application options cannot become SDK options.
                writer.WriteStringValue("--");
            }
            foreach (var argument in arguments)
            {
                writer.WriteStringValue(argument);
            }
            writer.WriteEndArray();
            writer.WriteStartObject("EnvironmentVariables");
            foreach (var (name, value) in environment)
            {
                writer.WriteString(name, value);
            }
            writer.WriteEndObject();
            // Optional<string?> has three states: no value disables profiles, a null value
            // selects the default, and a string selects a named profile.
            writer.WriteStartObject("LaunchProfileName");
            writer.WriteBoolean("HasValue", !excludeProfile);
            writer.WriteString("Value", profile);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return s_utf8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }
}

internal sealed class DotnetWatchStatus
{
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("projects")]
    public required string[] Projects { get; init; }

    [JsonPropertyName("success")]
    public bool? Success { get; init; }

    [JsonPropertyName("exitCode")]
    public int? ExitCode { get; init; }
}

[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(DotnetWatchStatus))]
internal sealed partial class DotnetWatchJsonContext : JsonSerializerContext;
