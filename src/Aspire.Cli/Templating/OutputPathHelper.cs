// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Cli.Resources;
using Spectre.Console;

namespace Aspire.Cli.Templating;

internal static class OutputPathHelper
{
    /// <summary>
    /// Returns a unique default output path based on the given base name.
    /// If <c>./{baseName}</c> already exists and is non-empty, appends
    /// a numeric suffix (<c>-2</c>, <c>-3</c>, …) until an available name is found.
    /// </summary>
    internal static string GetUniqueDefaultOutputPath(string baseName, string workingDirectory)
    {
        var candidate = $"./{baseName}";
        if (!IsNonEmptyDirectory(candidate, workingDirectory))
        {
            return candidate;
        }

        for (var i = 2; i < 1000; i++)
        {
            candidate = $"./{baseName}-{i}";
            if (!IsNonEmptyDirectory(candidate, workingDirectory))
            {
                return candidate;
            }
        }

        // Fallback — extremely unlikely to reach here.
        throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, "Unable to find a unique output directory name based on '{0}' after 999 attempts.", baseName));
    }

    /// <summary>
    /// Creates a validator that checks whether the given output path contains invalid characters
    /// or refers to a non-empty existing directory.
    /// </summary>
    internal static Func<string, ValidationResult> CreateOutputPathValidator(string workingDirectory)
    {
        return path =>
        {
            if (ContainsInvalidPathChars(path))
            {
                return ValidationResult.Error(string.Format(CultureInfo.CurrentCulture, NewCommandStrings.OutputPathContainsInvalidCharacters, path));
            }

            if (IsNonEmptyDirectory(path, workingDirectory))
            {
                var fullPath = Path.GetFullPath(path, workingDirectory);
                return ValidationResult.Error(string.Format(CultureInfo.CurrentCulture, NewCommandStrings.OutputDirectoryNotEmpty, fullPath));
            }

            return ValidationResult.Success();
        };
    }

    /// <summary>
    /// Validates a (possibly relative) output path before resolution. Returns an error message if the path
    /// contains invalid characters or targets a non-empty existing directory, or <see langword="null"/> if valid.
    /// </summary>
    internal static string? ValidateOutputPath(string path, string workingDirectory)
    {
        if (ContainsInvalidPathChars(path))
        {
            return string.Format(CultureInfo.CurrentCulture, NewCommandStrings.OutputPathContainsInvalidCharacters, path);
        }

        if (IsNonEmptyDirectory(path, workingDirectory))
        {
            var fullPath = Path.GetFullPath(path, workingDirectory);
            return string.Format(CultureInfo.CurrentCulture, NewCommandStrings.OutputDirectoryNotEmpty, fullPath);
        }

        return null;
    }

    /// <summary>
    /// Validates the resolved (absolute) output path and returns an error message if it's
    /// a non-empty existing directory, or <see langword="null"/> if valid.
    /// </summary>
    internal static string? ValidateResolvedOutputPath(string absolutePath)
    {
        if (Directory.Exists(absolutePath) && Directory.EnumerateFileSystemEntries(absolutePath).Any())
        {
            return string.Format(CultureInfo.CurrentCulture, NewCommandStrings.OutputDirectoryNotEmpty, absolutePath);
        }

        return null;
    }

    private static bool ContainsInvalidPathChars(string path)
    {
        return path.AsSpan().IndexOfAny(Path.GetInvalidPathChars()) >= 0;
    }

    private static bool IsNonEmptyDirectory(string relativePath, string workingDirectory)
    {
        var fullPath = Path.GetFullPath(relativePath, workingDirectory);
        return Directory.Exists(fullPath) && Directory.EnumerateFileSystemEntries(fullPath).Any();
    }
}
