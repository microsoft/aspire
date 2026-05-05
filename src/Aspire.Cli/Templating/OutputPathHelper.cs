// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Cli.Resources;
using Spectre.Console;

namespace Aspire.Cli.Templating;

internal static class OutputPathHelper
{
    /// <summary>
    /// Returns a unique default output path based on the template name.
    /// If <c>./{templateName}</c> already exists and is non-empty, appends
    /// a numeric suffix (<c>-2</c>, <c>-3</c>, …) until an available name is found.
    /// </summary>
    internal static string GetUniqueDefaultOutputPath(string templateName, string workingDirectory)
    {
        var candidate = $"./{templateName}";
        if (!IsNonEmptyDirectory(candidate, workingDirectory))
        {
            return candidate;
        }

        for (var i = 2; i < 1000; i++)
        {
            candidate = $"./{templateName}-{i}";
            if (!IsNonEmptyDirectory(candidate, workingDirectory))
            {
                return candidate;
            }
        }

        // Fallback — extremely unlikely to reach here.
        return $"./{templateName}";
    }

    /// <summary>
    /// Creates a validator that checks whether the given output path refers to a non-empty existing directory.
    /// </summary>
    internal static Func<string, ValidationResult> CreateOutputPathValidator(string workingDirectory)
    {
        return path =>
        {
            if (IsNonEmptyDirectory(path, workingDirectory))
            {
                var fullPath = Path.GetFullPath(path, workingDirectory);
                return ValidationResult.Error(string.Format(CultureInfo.CurrentCulture, NewCommandStrings.OutputDirectoryNotEmpty, fullPath));
            }

            return ValidationResult.Success();
        };
    }

    /// <summary>
    /// Validates the resolved (absolute) output path and returns an error message if it's a non-empty existing directory, or <see langword="null"/> if valid.
    /// </summary>
    internal static string? ValidateOutputPath(string absolutePath)
    {
        if (Directory.Exists(absolutePath) && Directory.EnumerateFileSystemEntries(absolutePath).Any())
        {
            return string.Format(CultureInfo.CurrentCulture, NewCommandStrings.OutputDirectoryNotEmpty, absolutePath);
        }

        return null;
    }

    private static bool IsNonEmptyDirectory(string relativePath, string workingDirectory)
    {
        var fullPath = Path.GetFullPath(relativePath, workingDirectory);
        return Directory.Exists(fullPath) && Directory.EnumerateFileSystemEntries(fullPath).Any();
    }
}
