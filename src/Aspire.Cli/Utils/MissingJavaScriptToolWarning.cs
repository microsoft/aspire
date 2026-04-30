// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Cli.Projects;
using Aspire.Cli.Resources;

namespace Aspire.Cli.Utils;

internal static class MissingJavaScriptToolWarning
{
    private static readonly string[] s_missingToolPrefixes =
    [
        "npm is not installed or not found in PATH.",
        "npx is not installed or not found in PATH.",
        "bun is not installed or not found in PATH.",
        "yarn is not installed or not found in PATH.",
        "pnpm is not installed or not found in PATH."
    ];

    public static bool IsMatch(IEnumerable<(OutputLineStream Stream, string Line)> lines)
    {
        foreach (var (_, line) in lines)
        {
            if (s_missingToolPrefixes.Any(prefix => line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    public static string GetMessage(DirectoryInfo directory, LanguageInfo? language)
    {
        var (installCommand, installDisplayName) = GetMessageParts(directory, language);
        return string.Format(CultureInfo.CurrentCulture, ErrorStrings.ProjectFilesCreatedButNodeToolsNotFound, installCommand, installDisplayName);
    }

    private static (string InstallCommand, string InstallDisplayName) GetMessageParts(DirectoryInfo directory, LanguageInfo? language)
    {
        if (TypeScriptAppHostToolchainResolver.IsTypeScriptLanguage(language))
        {
            var toolchain = TypeScriptAppHostToolchainResolver.Resolve(directory, logger: null);
            return (TypeScriptAppHostToolchainResolver.GetInstallCommand(toolchain), TypeScriptAppHostToolchainResolver.GetDisplayName(toolchain));
        }

        return ("npm install", "Node.js");
    }
}
