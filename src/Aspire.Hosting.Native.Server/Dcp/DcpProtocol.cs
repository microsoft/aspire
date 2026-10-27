// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Native.Dcp;

internal enum DcpResponseMode { Json, Empty }

/// <summary>Parses DCP log payloads independently of HTTP and retained log cursors.</summary>
internal static class DcpProtocol
{
    public static string[] ParseCompleteLogLines(ReadOnlySpan<char> text)
    {
        // Cursors count complete lines, not bytes. For "ready\npart", leave
        // the unfinished tail at the same cursor instead of duplicating it.
        var completeLength = text.LastIndexOf('\n') + 1;
        var complete = text[..completeLength];
        var lines = new string[complete.Count('\n')];
        for (var index = 0; index < lines.Length; index++)
        {
            var end = complete.IndexOf('\n');
            lines[index] = new string(complete[..end]);
            complete = complete[(end + 1)..];
        }

        return lines;
    }
}
