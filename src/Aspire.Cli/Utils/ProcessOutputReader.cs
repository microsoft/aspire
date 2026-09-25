// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Text;

namespace Aspire.Cli.Utils;

/// <summary>
/// Captures both process streams while preserving StreamReader's encoding detection.
/// </summary>
internal static class ProcessOutputReader
{
    public static async Task<(string StandardOutput, string StandardError)> ReadAllTextAsync(Process process, CancellationToken cancellationToken)
    {
        var (output, error) = await process.ReadAllBytesAsync(cancellationToken).ConfigureAwait(false);
        return (
            Decode(output, process.StandardOutput.CurrentEncoding),
            Decode(error, process.StandardError.CurrentEncoding));
    }

    public static (string StandardOutput, string StandardError) ReadAllText(Process process)
    {
        var (output, error) = process.ReadAllBytes();
        return (
            Decode(output, process.StandardOutput.CurrentEncoding),
            Decode(error, process.StandardError.CurrentEncoding));
    }

    private static string Decode(byte[] bytes, Encoding encoding)
    {
        // Process.ReadAllText[Async] decodes bytes directly: it neither removes a UTF-8 BOM
        // nor detects UTF-16/UTF-32 BOMs. Capture bytes with the multiplexed API, then use the
        // original StreamReader behavior and per-stream fallback encoding for compatibility.
        using var reader = new StreamReader(new MemoryStream(bytes), encoding, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
}
