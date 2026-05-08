// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;

namespace Aspire.Hosting;

/// <summary>
/// Parses Chromium's DevToolsActivePort file into a browser-level CDP endpoint.
/// </summary>
internal static class ChromiumDevToolsActivePortParser
{
    internal static Uri? TryParseBrowserDebugEndpoint(string activePortFileContents)
    {
        if (string.IsNullOrWhiteSpace(activePortFileContents))
        {
            return null;
        }

        // Chromium writes DevToolsActivePort as a two-line hand-off file while starting a debug-enabled browser:
        //
        // 51943
        // /devtools/browser/4c8404fb-06f8-45f0-9d89-112233445566
        //
        // The second line is the browser target path later exposed as webSocketDebuggerUrl from /json/version:
        // https://chromedevtools.github.io/devtools-protocol/#how-do-i-access-the-browser-target
        // Some Chromium builds have been observed to omit the leading slash, so normalize that before composing the
        // loopback websocket URI. Additional trailing lines are ignored because only the browser target endpoint matters.
        using var reader = new StringReader(activePortFileContents);
        var portLine = reader.ReadLine();
        var browserPathLine = reader.ReadLine();

        if (!int.TryParse(portLine, NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port <= 0)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(browserPathLine))
        {
            return null;
        }

        if (!browserPathLine.StartsWith("/", StringComparison.Ordinal))
        {
            browserPathLine = $"/{browserPathLine}";
        }

        return Uri.TryCreate($"ws://127.0.0.1:{port}{browserPathLine}", UriKind.Absolute, out var browserEndpoint)
            ? browserEndpoint
            : null;
    }
}
