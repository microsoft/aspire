// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Cli.Interaction;
using Aspire.Cli.Resources;
using Spectre.Console;

namespace Aspire.Cli.Templating;

internal static class OutputPathHelper
{
    /// <summary>
    /// Resolves the output path by prompting if not provided, validating if explicit,
    /// and converting to an absolute path.
    /// </summary>
    internal static async Task<string?> ResolveOutputPathAsync(
        string? outputPath,
        string workingDirectory,
        Func<Task<string>> promptCallback,
        IInteractionService interactionService)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            outputPath = await promptCallback();
        }
        else
        {
            var earlyError = ValidateOutputPath(outputPath, workingDirectory);
            if (earlyError is not null)
            {
                interactionService.DisplayError(earlyError);
                return null;
            }
        }

        return Path.GetFullPath(outputPath, workingDirectory);
    }

    /// <summary>
    /// Returns a unique default output path based on the given base name.
    /// Invalid path characters are stripped from <paramref name="baseName"/>.
    /// If all characters are invalid, <c>"output"</c> is used as the fallback name.
    /// If <c>./{baseName}</c> already exists and is non-empty, appends
    /// a numeric suffix (<c>-2</c>, <c>-3</c>, …) until an available name is found.
    /// </summary>
    internal static string GetUniqueDefaultOutputPath(string baseName, string workingDirectory)
    {
        baseName = SanitizeBaseName(baseName);

        var baseCandidate = $"./{baseName}";
        if (!IsNonEmptyDirectory(baseCandidate, workingDirectory))
        {
            return baseCandidate;
        }

        for (var i = 2; i < 1000; i++)
        {
            var candidate = $"{baseCandidate}-{i}";
            if (!IsNonEmptyDirectory(candidate, workingDirectory))
            {
                return candidate;
            }
        }

        // Fallback — extremely unlikely to reach here.
        return baseCandidate;
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

    private static string SanitizeBaseName(string baseName)
    {
        var invalidChars = Path.GetInvalidPathChars();
        var sanitized = string.Concat(baseName.Where(c => !invalidChars.Contains(c)));
        return sanitized.Length > 0 ? sanitized : "output";
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
