// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Projects;

/// <summary>
/// Writes the files the CLI generates on every launch without disturbing files that have not changed.
/// </summary>
/// <remarks>
/// Rewriting a file with identical content still moves its last-write time forward, and every
/// incremental build the CLI sits in front of -- MSBuild, javac, Maven, Gradle, and the IDE builders
/// that watch the same directories -- decides what to rebuild from those timestamps. Comparing before
/// writing costs a read of a file that is almost always still in the page cache and keeps those
/// timestamps stable across launches.
/// </remarks>
internal static class GeneratedFileWriter
{
    /// <summary>
    /// Writes <paramref name="content" /> only when it differs from what is already on disk.
    /// </summary>
    /// <remarks>
    /// A file that cannot be read is rewritten rather than treated as current. The CLI owns these
    /// files, so an unreadable one is far more likely to be a transient hold -- an antivirus scanner
    /// on Windows, a mode a user changed on POSIX -- than a deliberate state worth preserving, and
    /// preserving it would silently launch against stale generated code. The write that follows
    /// surfaces the real error if the condition is not transient.
    /// </remarks>
    /// <returns><see langword="true" /> when the file was written.</returns>
    public static async Task<bool> WriteIfChangedAsync(string path, string content, CancellationToken cancellationToken)
    {
        if (File.Exists(path))
        {
            try
            {
                var existing = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
                if (string.Equals(existing, content, StringComparison.Ordinal))
                {
                    return false;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        await File.WriteAllTextAsync(path, content, cancellationToken).ConfigureAwait(false);
        return true;
    }
}
