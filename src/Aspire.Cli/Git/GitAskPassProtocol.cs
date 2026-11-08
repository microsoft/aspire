// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers.Binary;
using System.Security.Authentication;
using System.Text;

namespace Aspire.Cli.Git;

internal static class GitAskPassProtocol
{
    internal const string SocketVariable = "ASPIRE_GIT_ASKPASS_SOCKET";
    internal const int MaxPromptBytes = 4096;
    internal const int MaxResponseBytes = 8192;
    internal static TimeSpan RequestTimeout => TimeSpan.FromSeconds(90);
    private static readonly UTF8Encoding s_encoding = new(false, true);

    // One request per connection: a big-endian uint32 length, then UTF-8
    // Git prompt bytes. Success is a length-prefixed UTF-8
    // response; an empty response is rejection. Never serialize exception messages.
    public static async Task<string> ReadAsync(Stream stream, int maximum, CancellationToken cancellationToken)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, cancellationToken);
        var length = BinaryPrimitives.ReadUInt32BigEndian(header);
        if (length == 0 || length > maximum)
        {
            throw new AuthenticationException("Invalid Git credential callback frame.");
        }
        var bytes = new byte[length];
        try
        {
            await stream.ReadExactlyAsync(bytes, cancellationToken);
            return s_encoding.GetString(bytes);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public static async Task WriteAsync(Stream stream, string value, int maximum, CancellationToken cancellationToken)
    {
        var length = s_encoding.GetByteCount(value);
        if (length == 0 || length > maximum)
        {
            throw new AuthenticationException("Invalid Git credential callback response.");
        }
        var bytes = s_encoding.GetBytes(value);
        try
        {
            var header = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(header, (uint)bytes.Length);
            await stream.WriteAsync(header, cancellationToken);
            await stream.WriteAsync(bytes, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public static void ValidateResponse(string response)
    {
        // Git consumes one line. Reject delimiters rather than silently truncating a secret.
        if (response.Length == 0 || s_encoding.GetByteCount(response) > MaxResponseBytes ||
            response.Any(char.IsControl))
        {
            throw new AuthenticationException("Invalid Git credential callback response.");
        }
    }
}
