// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using System.Text;

namespace Infrastructure.Tests.TestServices;

/// <summary>Captures HTTP requests and delegates responses to deterministic transport scenarios.</summary>
internal sealed class GitHubHttpMessageHandler(
    Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => send(request, cancellationToken);

    public static HttpResponseMessage Json(string json, string? next = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) };
        if (next is not null)
        {
            response.Headers.TryAddWithoutValidation("Link", next);
        }

        return response;
    }
}

/// <summary>Provides counted and interruptible response bodies for bounded-download and cancellation tests.</summary>
internal sealed class GitHubDownloadStream(string text, Func<CancellationToken, Task>? afterText = null) : Stream
{
    private readonly byte[] _bytes = Encoding.UTF8.GetBytes(text);
    private int _position;

    public int BytesRead => _position;
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => _position; set => throw new NotSupportedException(); }
    public override void Flush() => throw new NotSupportedException();
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_position == _bytes.Length)
        {
            if (afterText is not null)
            {
                await afterText(cancellationToken);
            }

            return 0;
        }

        var count = Math.Min(buffer.Length, _bytes.Length - _position);
        _bytes.AsMemory(_position, count).CopyTo(buffer);
        _position += count;
        return count;
    }
}
