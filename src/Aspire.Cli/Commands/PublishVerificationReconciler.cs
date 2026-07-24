// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using System.Text.Json;

namespace Aspire.Cli.Commands;

/// <summary>
/// Reconciles staged publisher output with Git-included logical destinations.
/// </summary>
internal static class PublishVerificationReconciler
{
    public static PublishVerificationInventory[] CreateGeneratedInventory(
        IReadOnlyList<PublishVerificationOutput> outputs)
    {
        return outputs
            .Select(output => new PublishVerificationInventory(
                output,
                EnumerateGeneratedFiles(output)))
            .ToArray();
    }

    public static async Task<PublishVerificationResult> ReconcileAsync(
        string repositoryRoot,
        IReadOnlyList<PublishVerificationOutput> outputs,
        IReadOnlyList<PublishVerificationInventory> inventory,
        IReadOnlySet<string> includedTargetFiles,
        IReadOnlySet<string> ignoredAbsentCandidates,
        CancellationToken cancellationToken)
    {
        var contentComparer = new PublishVerificationContentComparer(outputs);
        var inventoryByOutput = inventory.ToDictionary(
            item => (item.Output.IsPrimary, item.Output.PublisherName, item.Output.Name));
        var groups = new List<PublishVerificationGroup>(outputs.Count);

        foreach (var output in outputs
            .OrderBy(item => GetDisplayPath(repositoryRoot, item.LogicalTargetPath), StringComparer.Ordinal)
            .ThenByDescending(item => item.IsPrimary)
            .ThenBy(item => item.PublisherName, StringComparer.Ordinal)
            .ThenBy(item => item.Name, StringComparer.Ordinal))
        {
            var generatedFiles = inventoryByOutput[(output.IsPrimary, output.PublisherName, output.Name)].Files;
            var includedFiles = GetIncludedFiles(output, includedTargetFiles);
            var relativePaths = generatedFiles.Keys
                .Concat(includedFiles.Keys)
                .Distinct(PublishVerificationPathSafety.PathComparer)
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray();
            var staleFiles = new List<string>();
            var missingFiles = new List<string>();
            var orphanedFiles = new List<string>();

            foreach (var relativePath in relativePaths)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var hasGeneratedFile = generatedFiles.TryGetValue(relativePath, out var generatedFile);
                var hasIncludedTarget = includedFiles.TryGetValue(relativePath, out var targetPath);
                targetPath ??= generatedFile?.TargetPath;
                var targetExists = targetPath is not null && File.Exists(targetPath);
                var displayPath = string.IsNullOrEmpty(relativePath)
                    ? Path.GetFileName(output.LogicalTargetPath)
                    : relativePath;

                if (hasGeneratedFile)
                {
                    if (hasIncludedTarget)
                    {
                        if (!targetExists)
                        {
                            missingFiles.Add(displayPath);
                        }
                        else if (!await contentComparer.FilesEqualAsync(
                            generatedFile!.StagedPath,
                            targetPath!,
                            cancellationToken).ConfigureAwait(false))
                        {
                            staleFiles.Add(displayPath);
                        }
                    }
                    else if (!targetExists &&
                        !ignoredAbsentCandidates.Contains(generatedFile!.TargetPath))
                    {
                        missingFiles.Add(displayPath);
                    }
                }
                else if (hasIncludedTarget && targetExists)
                {
                    orphanedFiles.Add(displayPath);
                }
            }

            groups.Add(new PublishVerificationGroup(
                GetDisplayPath(repositoryRoot, output.LogicalTargetPath),
                staleFiles,
                missingFiles,
                orphanedFiles));
        }

        return new PublishVerificationResult(groups);
    }

    private static Dictionary<string, PublishVerificationGeneratedFile> EnumerateGeneratedFiles(
        PublishVerificationOutput output)
    {
        var files = new Dictionary<string, PublishVerificationGeneratedFile>(
            PublishVerificationPathSafety.PathComparer);
        if (output.Kind == PublishVerificationOutputKind.File)
        {
            if (File.Exists(output.OutputPath))
            {
                files.Add(
                    string.Empty,
                    CreateGeneratedFile(output.OutputPath, output.LogicalTargetPath));
            }

            return files;
        }

        if (!Directory.Exists(output.OutputPath))
        {
            return files;
        }

        foreach (var stagedPath in Directory.EnumerateFiles(output.OutputPath, "*", SearchOption.AllDirectories))
        {
            var relativePath = NormalizeRelativePath(Path.GetRelativePath(output.OutputPath, stagedPath));
            files.Add(
                relativePath,
                CreateGeneratedFile(
                    stagedPath,
                    Path.GetFullPath(relativePath, output.LogicalTargetPath)));
        }

        return files;
    }

    private static PublishVerificationGeneratedFile CreateGeneratedFile(
        string stagedPath,
        string targetPath)
    {
        var file = new FileInfo(stagedPath);
        return new PublishVerificationGeneratedFile(
            file.FullName,
            targetPath,
            file.Length,
            file.LastWriteTimeUtc);
    }

    private static Dictionary<string, string> GetIncludedFiles(
        PublishVerificationOutput output,
        IReadOnlySet<string> includedTargetFiles)
    {
        var files = new Dictionary<string, string>(PublishVerificationPathSafety.PathComparer);
        foreach (var includedFile in includedTargetFiles)
        {
            string? relativePath = null;
            if (output.Kind == PublishVerificationOutputKind.File)
            {
                if (PublishVerificationPathSafety.PathEquals(output.LogicalTargetPath, includedFile))
                {
                    relativePath = string.Empty;
                }
            }
            else if (PublishVerificationPathSafety.IsWithinRoot(output.LogicalTargetPath, includedFile))
            {
                relativePath = NormalizeRelativePath(Path.GetRelativePath(output.LogicalTargetPath, includedFile));
            }

            if (relativePath is not null)
            {
                files.Add(relativePath, includedFile);
            }
        }

        return files;
    }

    private static string GetDisplayPath(string repositoryRoot, string path)
    {
        return NormalizeRelativePath(Path.GetRelativePath(repositoryRoot, path));
    }

    private static string NormalizeRelativePath(string path)
    {
        return path.Replace(Path.DirectorySeparatorChar, '/');
    }
}

/// <summary>
/// Compares binary files exactly and normalizes output destination prefixes in UTF-8 text.
/// </summary>
internal sealed class PublishVerificationContentComparer
{
    private static readonly UTF8Encoding s_strictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private readonly Replacement[] _replacements;

    public PublishVerificationContentComparer(IReadOnlyList<PublishVerificationOutput> outputs)
    {
        _replacements = outputs
            .SelectMany(output =>
            {
                var canonicalPrefix = output.IsPrimary
                    ? "aspire-output://primary"
                    : $"aspire-output://named/{output.PublisherName}/{output.Name}";
                return GetPathVariants(output.OutputPath)
                    .Concat(GetPathVariants(output.LogicalTargetPath))
                    .Select(path => new Replacement(path, canonicalPrefix));
            })
            .DistinctBy(replacement => replacement.Source, PublishVerificationPathSafety.PathComparer)
            .OrderByDescending(replacement => replacement.Source.Length)
            .ThenBy(replacement => replacement.Source, StringComparer.Ordinal)
            .ToArray();
    }

    public async Task<bool> FilesEqualAsync(
        string stagedPath,
        string targetPath,
        CancellationToken cancellationToken)
    {
        var stagedBytesTask = File.ReadAllBytesAsync(stagedPath, cancellationToken);
        var targetBytesTask = File.ReadAllBytesAsync(targetPath, cancellationToken);
        await Task.WhenAll(stagedBytesTask, targetBytesTask).ConfigureAwait(false);

        var stagedBytes = await stagedBytesTask.ConfigureAwait(false);
        var targetBytes = await targetBytesTask.ConfigureAwait(false);
        if (!TryDecodeText(stagedBytes, out var stagedText) ||
            !TryDecodeText(targetBytes, out var targetText))
        {
            return stagedBytes.AsSpan().SequenceEqual(targetBytes);
        }

        return string.Equals(
            NormalizeText(stagedText),
            NormalizeText(targetText),
            StringComparison.Ordinal);
    }

    internal string NormalizeText(string text)
    {
        foreach (var replacement in _replacements)
        {
            text = text.Replace(
                replacement.Source,
                replacement.CanonicalPrefix,
                PublishVerificationPathSafety.PathComparison);
        }

        return text;
    }

    private static bool TryDecodeText(byte[] bytes, out string text)
    {
        if (bytes.Contains((byte)0))
        {
            text = string.Empty;
            return false;
        }

        try
        {
            text = s_strictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            text = string.Empty;
            return false;
        }

        foreach (var character in text)
        {
            if (char.IsControl(character) && character is not ('\r' or '\n' or '\t' or '\f'))
            {
                text = string.Empty;
                return false;
            }
        }

        return true;
    }

    private static IEnumerable<string> GetPathVariants(string path)
    {
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var forwardSlashPath = fullPath.Replace('\\', '/');
        var nativeSlashPath = Path.DirectorySeparatorChar == '\\'
            ? forwardSlashPath.Replace('/', '\\')
            : forwardSlashPath;
        var variants = new HashSet<string>(PublishVerificationPathSafety.PathComparer)
        {
            fullPath,
            forwardSlashPath,
            nativeSlashPath,
            JsonEscape(fullPath),
            JsonEscape(forwardSlashPath),
            JsonEscape(nativeSlashPath)
        };

        if (Uri.TryCreate(fullPath, UriKind.Absolute, out var uri))
        {
            variants.Add(uri.AbsoluteUri.TrimEnd('/'));
            variants.Add(JsonEscape(uri.AbsoluteUri.TrimEnd('/')));
        }

        return variants;
    }

    private static string JsonEscape(string value)
    {
        return JsonEncodedText.Encode(value).ToString();
    }

    private sealed record Replacement(string Source, string CanonicalPrefix);
}

internal sealed record PublishVerificationGeneratedFile(
    string StagedPath,
    string TargetPath,
    long Length,
    DateTime LastWriteTimeUtc);

internal sealed record PublishVerificationInventory(
    PublishVerificationOutput Output,
    IReadOnlyDictionary<string, PublishVerificationGeneratedFile> Files);

internal sealed record PublishVerificationGroup(
    string Destination,
    IReadOnlyList<string> StaleFiles,
    IReadOnlyList<string> MissingFiles,
    IReadOnlyList<string> OrphanedFiles);

internal sealed record PublishVerificationResult(IReadOnlyList<PublishVerificationGroup> Groups)
{
    public bool HasDrift => Groups.Any(group =>
        group.StaleFiles.Count > 0 ||
        group.MissingFiles.Count > 0 ||
        group.OrphanedFiles.Count > 0);
}

/// <summary>
/// Escapes control characters in repository paths before writing them to a terminal.
/// </summary>
internal static class PublishVerificationDisplayFormatter
{
    public static string EscapePath(string path)
    {
        var builder = new StringBuilder(path.Length);
        foreach (var character in path)
        {
            switch (character)
            {
                case '\r':
                    builder.Append(@"\r");
                    break;
                case '\n':
                    builder.Append(@"\n");
                    break;
                case '\t':
                    builder.Append(@"\t");
                    break;
                default:
                    if (char.IsControl(character))
                    {
                        builder.Append(@"\u");
                        builder.Append(((int)character).ToString("X4", System.Globalization.CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(character);
                    }
                    break;
            }
        }

        return builder.ToString();
    }
}

/// <summary>
/// Formats a pasteable regenerate command while redacting likely secret option values.
/// </summary>
internal static class PublishVerificationCommandFormatter
{
    private const string RedactedValue = "<redacted>";

    public static string Format(IReadOnlyList<string> arguments)
    {
        var redactedArguments = Redact(arguments);
        return string.Join(' ', redactedArguments.Select(Quote));
    }

    private static string[] Redact(IReadOnlyList<string> arguments)
    {
        var redacted = new string[arguments.Count];
        var redactNext = false;
        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            if (redactNext)
            {
                redacted[index] = RedactedValue;
                redactNext = false;
                continue;
            }

            var equalsIndex = argument.IndexOf('=');
            var optionName = equalsIndex >= 0 ? argument[..equalsIndex] : argument;
            if (!IsSensitiveOption(optionName))
            {
                redacted[index] = argument;
                continue;
            }

            if (equalsIndex >= 0)
            {
                redacted[index] = $"{optionName}={RedactedValue}";
            }
            else
            {
                redacted[index] = argument;
                redactNext = true;
            }
        }

        return redacted;
    }

    private static bool IsSensitiveOption(string optionName)
    {
        if (!optionName.StartsWith("-", StringComparison.Ordinal))
        {
            return false;
        }

        var normalized = optionName.Replace("_", "-", StringComparison.Ordinal).ToLowerInvariant();
        return normalized.Contains("password", StringComparison.Ordinal) ||
            normalized.Contains("secret", StringComparison.Ordinal) ||
            normalized.Contains("token", StringComparison.Ordinal) ||
            normalized.Contains("connection-string", StringComparison.Ordinal) ||
            normalized.Contains("credential", StringComparison.Ordinal) ||
            normalized.Contains("api-key", StringComparison.Ordinal);
    }

    private static string Quote(string argument)
    {
        if (argument.Length > 0 && argument.All(character =>
            char.IsAsciiLetterOrDigit(character) ||
            character is '_' or '-' or '.' or '/' or '\\' or ':' or '='))
        {
            return argument;
        }

        return OperatingSystem.IsWindows()
            ? $"'{argument.Replace("'", "''", StringComparison.Ordinal)}'"
            : $"'{argument.Replace("'", "'\"'\"'", StringComparison.Ordinal)}'";
    }
}
