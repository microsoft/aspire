// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using Microsoft.JSInterop;

namespace Aspire.Dashboard.Extensions;

/// <summary>
/// Extension methods for <see cref="IJSRuntime"/>.
/// </summary>
internal static class JSInteropExtensions
{
    /// <summary>
    /// Downloads a file to the browser with the specified filename and content.
    /// </summary>
    /// <param name="js">The JS runtime.</param>
    /// <param name="fileName">The name of the file to download.</param>
    /// <param name="content">The byte array content of the file.</param>
    public static async Task DownloadFileAsync(this IJSRuntime js, string fileName, byte[] content)
    {
        using var stream = new MemoryStream(content);
        await js.DownloadFileAsync(fileName, stream).ConfigureAwait(false);
    }

    /// <summary>
    /// Downloads a file to the browser with the specified filename and content.
    /// </summary>
    /// <param name="js">The JS runtime.</param>
    /// <param name="fileName">The name of the file to download.</param>
    /// <param name="content">The string content of the file (will be encoded as UTF-8).</param>
    public static Task DownloadFileAsync(this IJSRuntime js, string fileName, string content)
    {
        return js.DownloadFileAsync(fileName, Encoding.UTF8.GetBytes(content));
    }

    /// <summary>
    /// Downloads a file to the browser with the specified filename and stream content.
    /// </summary>
    /// <param name="js">The JS runtime.</param>
    /// <param name="fileName">The name of the file to download.</param>
    /// <param name="stream">The stream containing the file content. The stream position should be at the beginning.</param>
    public static async Task DownloadFileAsync(this IJSRuntime js, string fileName, Stream stream)
    {
        using var streamReference = new DotNetStreamReference(stream, leaveOpen: true);
        await js.InvokeVoidAsync("downloadStreamAsFile", fileName, streamReference).ConfigureAwait(false);
    }
}
