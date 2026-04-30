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

        // Chromium writes DevToolsActivePort as two lines:
        //
        // 51943
        // /devtools/browser/4c8404fb-06f8-45f0-9d89-112233445566
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
