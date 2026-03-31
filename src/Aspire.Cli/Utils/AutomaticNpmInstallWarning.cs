// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Utils;

internal static class AutomaticNpmInstallWarning
{
    private const string MissingNpmPrefix = "npm is not installed or not found in PATH.";
    private const string MissingNpxPrefix = "npx is not installed or not found in PATH.";

    internal const string Message = "Project files were created, but Aspire could not run 'npm install' automatically because the required Node.js tools were not found on PATH. You may see missing package errors or red squiggles in your IDE until you install Node.js and run 'npm install' in the project directory.";

    public static bool IsMatch(IEnumerable<(OutputLineStream Stream, string Line)> lines)
    {
        foreach (var (_, line) in lines)
        {
            if (line.StartsWith(MissingNpmPrefix, StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith(MissingNpxPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}