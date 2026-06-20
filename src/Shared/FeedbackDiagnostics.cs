// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Shared;

internal static class FeedbackDiagnostics
{
    public static string NormalizeAspireDoctorOutput(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return string.Empty;
        }

        return TryExtractJsonObject(output) ?? output.Trim();
    }

    private static string? TryExtractJsonObject(string output)
    {
        var startIndex = output.IndexOf('{', StringComparison.Ordinal);
        if (startIndex < 0)
        {
            return null;
        }

        var depth = 0;
        var inString = false;
        var escaped = false;

        // `aspire doctor --format json` can still be accompanied by progress text from
        // lower-level checks, for example:
        //   { "checks": [...] }
        //
        //   Checking Aspire environment...
        // Keep only the first complete JSON object so the issue prefill remains clean.
        for (var i = startIndex; i < output.Length; i++)
        {
            var c = output[i];
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (c == '\\')
                {
                    escaped = true;
                }
                else if (c == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (c == '"')
            {
                inString = true;
            }
            else if (c == '{')
            {
                depth++;
            }
            else if (c == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return output[startIndex..(i + 1)].Trim();
                }
            }
        }

        return null;
    }
}
