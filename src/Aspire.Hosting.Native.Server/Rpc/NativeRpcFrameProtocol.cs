// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text;

namespace Aspire.Hosting.Native.Rpc;

/// <summary>Validates LSP-style header bytes independently of stream buffering.</summary>
internal static class NativeRpcFrameProtocol
{
    public const int MaximumHeaderBytes = 8192;

    public static int ReadContentLength(ReadOnlySpan<byte> headers, int maximumRequestBytes)
    {
        // Content-Length: 42\r\nContent-Type: application/vscode-jsonrpc; charset=utf-8
        // Header names are ASCII case-insensitive; lengths count UTF-8 body bytes.
        // https://microsoft.github.io/language-server-protocol/specifications/lsp/3.17/specification/#headerPart
        int? contentLength = null;
        foreach (var range in headers.Split("\r\n"u8))
        {
            var line = headers[range];
            var separator = line.IndexOf((byte)':');
            if (separator <= 0)
            {
                throw new InvalidDataException("The JSON-RPC frame header is malformed.");
            }
            var name = line[..separator];
            if (Ascii.EqualsIgnoreCase(name, "Content-Length"u8))
            {
                var value = line[(separator + 1)..];
                while (!value.IsEmpty && char.IsWhiteSpace((char)value[0]))
                {
                    value = value[1..];
                }
                while (!value.IsEmpty && char.IsWhiteSpace((char)value[^1]))
                {
                    value = value[..^1];
                }
                if (contentLength is not null ||
                    !int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var length) ||
                    length > maximumRequestBytes)
                {
                    throw new InvalidDataException("The JSON-RPC frame length is invalid.");
                }
                contentLength = length;
            }
            else if (!Ascii.EqualsIgnoreCase(name, "Content-Type"u8))
            {
                throw new InvalidDataException("The JSON-RPC frame header is not supported.");
            }
        }

        return contentLength ?? throw new InvalidDataException("The JSON-RPC frame has no content length.");
    }
}
