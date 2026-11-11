// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text.RegularExpressions;

namespace Aspire.Hosting.DevTunnels;

/// <summary>
/// Recognizes host lifecycle messages without depending on console layout or separator punctuation.
/// </summary>
internal sealed partial class DevTunnelOutputParser(string tunnelId)
{
    private const int MaxPendingLength = 4096;
    private string? _pending;
    private int? _port;
    private bool _inspectUrl;
    private bool _readyWithoutId;

    // Color sequences can sit between a separator and the next message, such as:
    //   Connection ... restored.; \x1b[31mConnection ... closed.\x1b[0m
    // Remove them before detecting boundaries, not only when parsing each resulting message.
    internal static string[] SplitOutput(string content) => MessageBoundaryRegex().Split(AnsiRegex().Replace(content, ""));

    public Output Parse(string line)
    {
        // Observed CLI output (including a format documented by earlier versions):
        //   Hosting port: 7007
        //   Connect via browser: https://abc-7007.usw2.devtunnels.ms
        //   Inspect network activity: https://abc-7007-inspect.usw2.devtunnels.ms
        //   Hosting port 7007 at https://abc-7007.usw2.devtunnels.ms/
        // Labels and values can arrive on separate lines/batches. Keep only a bounded,
        // recognized prefix, never an arbitrary tail of unrecognized console output.
        var text = WhitespaceRegex().Replace(AnsiRegex().Replace(line, ""), " ").Trim();
        if (text.Length == 0)
        {
            return default;
        }

        var words = WordsRegex().Replace(text, " ").Trim().ToLowerInvariant();
        if (_pending is not null && StartsMessage(words))
        {
            // For example, "Inspect network activity:" may be abandoned before
            // "Connection to host tunnel relay closed." arrives. New messages must supersede
            // unfinished fields, especially disconnects; only actual continuations are joined.
            _pending = null;
            _port = null;
            _inspectUrl = false;
            _readyWithoutId = false;
        }
        if (_pending is { } pending)
        {
            text = pending + " " + text;
            words = WordsRegex().Replace(text, " ").Trim().ToLowerInvariant();
            _pending = null;
        }

        if (_readyWithoutId && words.StartsWith("for tunnel", StringComparison.Ordinal))
        {
            text = "Ready to accept connections " + text;
            words = "ready to accept connections " + words;
        }
        _readyWithoutId = false;
        if (words.StartsWith("hosting port ", StringComparison.Ordinal))
        {
            var match = PortRegex().Match(text);
            if (!match.Success || !int.TryParse(match.Groups["port"].ValueSpan, CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535)
            {
                return Unknown();
            }

            _port = port;
            _inspectUrl = false;
            var urlMatch = UrlRegex().Match(text);
            return urlMatch.Success ? ParseUrl(urlMatch.Value) : default;
        }

        if (words.StartsWith("connect via browser ", StringComparison.Ordinal)
            || words.StartsWith("inspect network activity ", StringComparison.Ordinal)
            || (_port is not null && words.StartsWith("at ", StringComparison.Ordinal))
            || Uri.TryCreate(text.Trim('<', '>', '(', ')'), UriKind.Absolute, out _))
        {
            _inspectUrl |= words.StartsWith("inspect network activity", StringComparison.Ordinal);
            var match = UrlRegex().Match(text);
            return match.Success ? ParseUrl(match.Value) : Unknown();
        }

        if (words.StartsWith("ready to accept connections", StringComparison.Ordinal))
        {
            if (words.EndsWith("for tunnel", StringComparison.Ordinal))
            {
                return Remember(text);
            }

            var match = ReadyRegex().Match(text);
            if (match.Success)
            {
                var reportedId = match.Groups["id"].Value;
                if (reportedId.Length == 0
                    || string.Equals(reportedId, tunnelId, StringComparison.OrdinalIgnoreCase)
                    || reportedId.StartsWith(tunnelId + ".", StringComparison.OrdinalIgnoreCase))
                {
                    _readyWithoutId = reportedId.Length == 0;
                    return new(OutputKind.Ready);
                }
            }

            return Unknown();
        }

        if (words.StartsWith("connection to host tunnel relay ", StringComparison.Ordinal))
        {
            var state = words["connection to host tunnel relay ".Length..].Split(' ')[0];
            return state switch
            {
                "restored" or "connected" => new(OutputKind.Connected),
                "closed" or "lost" or "disconnected" => new(OutputKind.Disconnected),
                _ => Unknown()
            };
        }

        if (IsPrefix(words, "hosting port")
            || IsPrefix(words, "connect via browser")
            || IsPrefix(words, "inspect network activity")
            || IsPrefix(words, "ready to accept connections for tunnel")
            || IsPrefix(words, "connection to host tunnel relay"))
        {
            return Remember(text);
        }

        return Unknown();
    }

    internal static bool IsPortUri(Uri uri, int port)
    {
        return uri.IsAbsoluteUri
            && uri.Scheme == Uri.UriSchemeHttps
            && uri.IsDefaultPort
            && uri.UserInfo.Length == 0
            && uri.Query.Length == 0
            && uri.Fragment.Length == 0
            && uri.AbsolutePath == "/"
            && uri.Host.EndsWith(".devtunnels.ms", StringComparison.OrdinalIgnoreCase)
            && uri.Host.Split('.')[0].EndsWith("-" + port.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    private Output ParseUrl(string text)
    {
        text = text.TrimEnd('.', ',', ';', ':', ')', ']', '>');
        if (_inspectUrl)
        {
            _inspectUrl = false;
            return default;
        }

        if (_port is { } port && Uri.TryCreate(text, UriKind.Absolute, out var uri) && IsPortUri(uri, port))
        {
            _port = null;
            return new(OutputKind.Port, port, uri);
        }

        return Unknown();
    }

    private Output Remember(string text)
    {
        if (text.Length > MaxPendingLength)
        {
            return Unknown();
        }

        _pending = text;
        return default;
    }

    private Output Unknown()
    {
        _pending = null;
        _port = null;
        _inspectUrl = false;
        return new(OutputKind.Unrecognized);
    }

    private static bool IsPrefix(string text, string phrase) =>
        phrase.Equals(text, StringComparison.Ordinal) || phrase.StartsWith(text + " ", StringComparison.Ordinal);

    private static bool StartsMessage(string words)
    {
        var separator = words.IndexOf(' ');
        var firstWord = separator < 0 ? words : words[..separator];
        return firstWord is "hosting" or "connect" or "inspect" or "ready" or "connection";
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"[\r\n]+|(?<=[\s;|])(?=(?:hosting\s+port|connect\s+via\s+browser|inspect\s+network\s+activity|ready\s+to\s+accept\s+connections|connection\s+to\s+host\s+tunnel\s+relay)\b)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MessageBoundaryRegex();

    [GeneratedRegex(@"[\s\p{P}\p{S}]+")]
    private static partial Regex WordsRegex();

    [GeneratedRegex(@"\x1b\[[0-?]*[ -/]*[@-~]")]
    private static partial Regex AnsiRegex();

    [GeneratedRegex(@"^hosting[\s\p{P}\p{S}]+port[\s\p{P}\p{S}]*(?<port>\d+)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PortRegex();

    [GeneratedRegex(@"https?://[^\s<>""'`]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UrlRegex();

    [GeneratedRegex(@"^ready[\s\p{P}\p{S}]+to[\s\p{P}\p{S}]+accept[\s\p{P}\p{S}]+connections(?:[\s\p{P}\p{S}]+for[\s\p{P}\p{S}]+tunnel[\s\p{P}\p{S}]+(?<id>[a-z0-9][a-z0-9.-]*))?[\s\p{P}\p{S}]*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReadyRegex();

    internal enum OutputKind
    {
        None,
        Port,
        Ready,
        Connected,
        Disconnected,
        Unrecognized
    }

    internal readonly record struct Output(OutputKind Kind, int Port = 0, Uri? Uri = null);
}
