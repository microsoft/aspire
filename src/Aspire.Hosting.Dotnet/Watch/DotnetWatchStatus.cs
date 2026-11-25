// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aspire.Hosting.Dotnet.Watch;

internal sealed record DotnetWatchStatus(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("projects")] string[]? Projects,
    [property: JsonPropertyName("success")] bool? Success,
    [property: JsonPropertyName("error")] string? Error,
    [property: JsonPropertyName("exitCode")] int? ExitCode);

/// <summary>
/// Reads bounded UTF-8 status records from the pinned watcher's newline-delimited JSON pipe.
/// </summary>
internal static class DotnetWatchStatusReader
{
    internal const int MaximumRecordBytes = 64 * 1024;

    internal static async IAsyncEnumerable<DotnetWatchStatus> ReadAsync(
        Stream stream, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // For example: {"type":"build_complete","projects":["/src/Api.csproj"],"success":false}.
        // JSON escapes embedded newlines; only the byte newline terminates a record.
        var input = new byte[4096];
        var record = new ArrayBufferWriter<byte>();
        while (await stream.ReadAsync(input, cancellationToken).ConfigureAwait(false) is var count && count > 0)
        {
            var start = 0;
            for (var index = 0; index < count; index++)
            {
                if (input[index] != (byte)'\n')
                {
                    continue;
                }
                Append(input.AsSpan(start, index - start));
                var status = JsonSerializer.Deserialize(record.WrittenSpan, DotnetWatchStatusJsonContext.Default.DotnetWatchStatus)
                    ?? throw new InvalidDataException("The watcher sent a null status record.");
                Validate(status);
                record.Clear();
                start = index + 1;
                yield return status;
            }
            Append(input.AsSpan(start, count - start));
        }
        if (record.WrittenCount != 0)
        {
            throw new InvalidDataException("The watcher closed a partial status record.");
        }

        void Append(ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length > MaximumRecordBytes - record.WrittenCount)
            {
                throw new InvalidDataException($"A watch status exceeds {MaximumRecordBytes} bytes.");
            }
            record.Write(bytes);
        }
    }

    internal static void Validate(DotnetWatchStatus status)
    {
        if (status.Type is not (DotnetWatchStatusTypes.Building or DotnetWatchStatusTypes.BuildComplete or
            DotnetWatchStatusTypes.HotReloadApplied or DotnetWatchStatusTypes.Restarting or
            DotnetWatchStatusTypes.ProcessStarted or DotnetWatchStatusTypes.ProcessExited))
        {
            throw new InvalidDataException($"The watcher sent an unsupported status type '{status.Type}'.");
        }
        if (status.Projects is { } projects && (projects.Length > 256 ||
            projects.Any(path => string.IsNullOrWhiteSpace(path) || path.Length > 4096)))
        {
            throw new InvalidDataException("A watch status has missing, invalid, or too many project identities.");
        }
        if (status.Type == DotnetWatchStatusTypes.BuildComplete && status.Success is null)
        {
            throw new InvalidDataException("A watch build_complete record has no build result.");
        }
        if (status.Type is DotnetWatchStatusTypes.ProcessStarted or DotnetWatchStatusTypes.ProcessExited &&
            status.Projects is not { Length: > 0 })
        {
            throw new InvalidDataException("A watch process status has no project identity.");
        }
        if (status.Type == DotnetWatchStatusTypes.ProcessExited && status.ExitCode is null)
        {
            throw new InvalidDataException("A watch process_exited record has no observed exit code.");
        }
    }
}

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = false)]
[JsonSerializable(typeof(DotnetWatchStatus))]
internal partial class DotnetWatchStatusJsonContext : JsonSerializerContext;
