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

        foreach (var line in File.ReadLines(goModPath))
        {
            var match = GoDirectiveRegex().Match(line);
            if (match.Success)
            {
                return match.Groups[1].Value;
            }
        }

        return DefaultGoVersion;
    }

    [GeneratedRegex(@"^go\s+(\d+\.\d+(?:\.\d+)?)")]
    private static partial Regex GoDirectiveRegex();
}
