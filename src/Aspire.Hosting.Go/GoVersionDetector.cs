// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.RegularExpressions;

namespace Aspire.Hosting.Go;

internal static partial class GoVersionDetector
{
    private const string DefaultGoVersion = "1.26";

    public static string Detect(string appDirectory)
    {
        var goModPath = Path.Combine(appDirectory, "go.mod");
        if (!File.Exists(goModPath))
        {
            return DefaultGoVersion;
        }

        string? goVersion = null;
        string? toolchainVersion = null;

        foreach (var line in File.ReadLines(goModPath))
        {
            if (toolchainVersion is null)
            {
                var toolchainMatch = ToolchainDirectiveRegex().Match(line);
                if (toolchainMatch.Success)
                {
                    // toolchain goX.Y.Z pins the exact build toolchain — prefer it over the
                    // minimum-version go directive because it reflects the actual toolchain used.
                    toolchainVersion = toolchainMatch.Groups[1].Value;
                    continue;
                }
            }

            if (goVersion is null)
            {
                var goMatch = GoDirectiveRegex().Match(line);
                if (goMatch.Success)
                {
                    goVersion = goMatch.Groups[1].Value;
                }
            }

            if (goVersion is not null && toolchainVersion is not null)
            {
                break;
            }
        }

        return toolchainVersion ?? goVersion ?? DefaultGoVersion;
    }

    // Matches: toolchain go1.26.1
    [GeneratedRegex(@"^toolchain\s+go(\d+\.\d+\.\d+)")]
    private static partial Regex ToolchainDirectiveRegex();

    // Matches: go 1.26  or  go 1.26.0
    [GeneratedRegex(@"^go\s+(\d+\.\d+(?:\.\d+)?)")]
    private static partial Regex GoDirectiveRegex();
}
