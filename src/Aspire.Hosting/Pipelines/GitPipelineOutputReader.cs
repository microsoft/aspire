// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.FileSystemGlobbing;

namespace Aspire.Hosting.Pipelines;

/// <summary>
/// Reads Git membership and metadata and computes hashes for pipeline outputs.
/// </summary>
internal sealed class GitPipelineOutputReader(string repositoryRoot, IProcessRunner processRunner)
{
    private static readonly ReadOnlyDictionary<string, string> s_environmentVariables = new(new Dictionary<string, string> { ["GIT_OPTIONAL_LOCKS"] = "0" });
    private static readonly SearchValues<char> s_hexDigits = SearchValues.Create("0123456789abcdefABCDEF");
    private static readonly SearchValues<char> s_octalDigits = SearchValues.Create("01234567");
    private static readonly SearchValues<char> s_indexTags = SearchValues.Create("HhSsMmRrCcKk?");
    private static readonly SearchValues<char> s_statusCodes = SearchValues.Create(".MADRCUT");

    /// <summary>
    /// Creates a reader for the directory's worktree, or returns null when Git or the worktree is unavailable.
    /// </summary>
    internal static async Task<GitPipelineOutputReader?> TryCreateAsync(string directory, IProcessRunner processRunner, CancellationToken cancellationToken)
    {
        try
        {
            var result = await processRunner.RunAndCaptureTextAsync("git",
                ["--no-optional-locks", "rev-parse", "--is-inside-work-tree", "--show-cdup"], directory,
                s_environmentVariables, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (result.ExitCode != 0)
            {
                return null;
            }

            var output = result.StandardOutput.AsSpan();
            if (output.StartsWith("false\n", StringComparison.Ordinal) || output.StartsWith("false\r\n", StringComparison.Ordinal))
            {
                return null;
            }

            // Inside a worktree: "true\n../../\n" ("true\n\n" at its root).
            // --show-cdup supplies only parent segments. Outside, its unused value can be absolute.
            // https://git-scm.com/docs/git-rev-parse#Documentation/git-rev-parse.txt---show-cdup
            Span<Range> lines = stackalloc Range[3];
            if (output.Split(lines, '\n') != lines.Length || !output[lines[2]].IsEmpty)
            {
                ThrowMalformed("worktree discovery", "lines", output);
            }

            var flag = output[lines[0]];
            flag = !flag.IsEmpty && flag[^1] == '\r' ? flag[..^1] : flag;
            var pathToRoot = output[lines[1]];
            pathToRoot = !pathToRoot.IsEmpty && pathToRoot[^1] == '\r' ? pathToRoot[..^1] : pathToRoot;
            var parents = pathToRoot;
            while (parents.StartsWith("../", StringComparison.Ordinal))
            {
                parents = parents[3..];
            }
            if (!flag.SequenceEqual("true") || !parents.IsEmpty)
            {
                ThrowMalformed("worktree discovery", "parent path", output);
            }

            return new(Path.GetFullPath(pathToRoot.IsEmpty ? "." : pathToRoot.ToString(), directory), processRunner);
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 2) // ERROR_FILE_NOT_FOUND on Windows; ENOENT on Unix.
        {
            return null;
        }
    }

    /// <summary>
    /// Selects existing output leaves using Git membership and the ordered matcher rules.
    /// </summary>
    internal async Task<IReadOnlyList<SelectedFile>> SelectFilesAsync(string directory, Matcher matcher, CancellationToken cancellationToken)
    {
        var literal = OperatingSystem.IsWindows() ? ":(top,literal,icase)" : ":(top,literal)";
        // Omitting a trailing slash lets Git consult absent indexed ancestor ignore files.
        var scope = string.Equals(directory, Path.TrimEndingDirectorySeparator(repositoryRoot), PipelineOutputRoot.PathComparison) ? ":/" :
            literal + Path.GetRelativePath(repositoryRoot, directory).Replace(Path.DirectorySeparatorChar, '/').TrimEnd('/');
        var index = await ReadIndexAsync(scope, cancellationToken).ConfigureAwait(false);
        var candidates = new HashSet<string>(index.Keys, PipelineOutputRoot.PathComparer);
        candidates.UnionWith(await ReadUntrackedPathsAsync(scope, cancellationToken).ConfigureAwait(false));
        var relative = candidates.Where(path => PipelineOutputRoot.IsWithinDirectory(directory, path))
            .Select(path => Path.GetRelativePath(directory, path));
        var matches = matcher.Match(directory, relative).Files;
        var entries = new List<SelectedFile>(matches.TryGetNonEnumeratedCount(out var count) ? count : 0);
        foreach (var match in matches)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = new FileInfo(Path.GetFullPath(match.Path, directory));
            index.TryGetValue(file.FullName, out var indexed);
            if (indexed is { IsGitLink: true })
            {
                continue;
            }
            try
            {
                var attributes = File.GetAttributes(file.FullName);
                if (attributes.IsDirectory() && !attributes.IsJunctionOrSymlink())
                {
                    continue;
                }
            }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
            {
                continue;
            }
            entries.Add(new(file, indexed));
        }
        return entries;
    }

    private async Task<Dictionary<string, IndexEntry?>> ReadIndexAsync(string scope, CancellationToken cancellationToken)
    {
        var output = await RunGitAsync(["ls-files", "-t", "-v", "--stage", "--full-name", "-z", "--", scope], cancellationToken).ConfigureAwait(false);
        if (!TryGetRecordPayload(output.AsSpan(), out var records))
        {
            ThrowMalformed("index", "record framing", output.AsSpan());
        }

        var index = new Dictionary<string, IndexEntry?>(PipelineOutputRoot.PathComparer);
        if (!records.IsEmpty)
        {
            foreach (var range in records.Split('\0'))
            {
                var entry = IndexEntry.Parse(records[range]);
                var path = Path.GetFullPath(entry.Path, repositoryRoot);
                if (entry.IsMerged)
                {
                    index[path] = entry;
                }
                else
                {
                    // Unmerged paths remain members, but have no normal index hash to reuse.
                    index.TryAdd(path, null);
                }
            }
        }
        return index;
    }

    private async Task<IReadOnlyList<string>> ReadUntrackedPathsAsync(string scope, CancellationToken cancellationToken)
    {
        var output = await RunGitAsync(["ls-files", "--others", "--exclude-standard", "--full-name", "-z", "--", scope], cancellationToken).ConfigureAwait(false);
        if (!TryGetRecordPayload(output.AsSpan(), out var records))
        {
            ThrowMalformed("untracked paths", "record framing", output.AsSpan());
        }

        var paths = new List<string>();
        if (!records.IsEmpty)
        {
            foreach (var range in records.Split('\0'))
            {
                var path = records[range];
                if (!path.EndsWith("/", StringComparison.Ordinal))
                {
                    paths.Add(Path.GetFullPath(path.ToString(), repositoryRoot));
                }
            }
        }
        return paths;
    }

    /// <summary>
    /// Reads tracked working-tree changes and modes without interpreting HEAD or staged changes.
    /// </summary>
    internal async Task<IReadOnlyDictionary<string, StatusEntry>> ReadStatusAsync(CancellationToken cancellationToken)
    {
        var output = await RunGitAsync(["status", "--porcelain=v2", "-z", "--untracked-files=no", "--no-renames"], cancellationToken).ConfigureAwait(false);
        if (!TryGetRecordPayload(output.AsSpan(), out var records))
        {
            ThrowMalformed("status", "record framing", output.AsSpan());
        }

        var working = new Dictionary<string, StatusEntry>(PipelineOutputRoot.PathComparer);
        if (!records.IsEmpty)
        {
            foreach (var range in records.Split('\0'))
            {
                var entry = StatusEntry.Parse(records[range]);
                working[Path.GetFullPath(entry.Path, repositoryRoot)] = entry;
            }
        }
        return working;
    }

    /// <summary>
    /// Hashes a link's destination text without applying file attributes or filters.
    /// </summary>
    internal async Task<ReadOnlyMemory<byte>> HashLinkTargetAsync(string target, CancellationToken cancellationToken)
    {
        // Git stores a link's destination text. hash-object on a filename follows
        // the link, so hash the destination text through stdin without filters.
        // https://git-scm.com/docs/git-hash-object
        var output = await RunGitAsync(["hash-object", "--stdin"], cancellationToken, Encoding.UTF8.GetBytes(target)).ConfigureAwait(false);
        var id = output.AsSpan().TrimEnd("\r\n");
        if (!TryParseObjectId(id, out var hash))
        {
            ThrowMalformed("link object ID", "object ID", id);
        }

        return hash;
    }

    /// <summary>
    /// Hashes ordinary files in one batch using Git's configured attributes and filters.
    /// </summary>
    internal async Task<ReadOnlyMemory<byte>[]> HashFilesAsync(IEnumerable<FileInfo> files, CancellationToken cancellationToken)
    {
        // --stdin-paths reads one absolute UTF-8 filename per line, e.g. C:/repo/雪.txt\n.
        // Git opens the files and applies their attributes. One batch avoids a process per file.
        // https://git-scm.com/docs/git-hash-object
        var input = new ArrayBufferWriter<byte>();
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = file.FullName.AsSpan();
            ThrowIfUnsupportedPath(path);
            var bytes = input.GetSpan(Encoding.UTF8.GetByteCount(path) + 1);
            var written = Encoding.UTF8.GetBytes(path, bytes);
            if (OperatingSystem.IsWindows())
            {
                // Git attribute lookup uses '/' separators; Unix backslashes are filename characters.
                // https://github.com/git/git/blob/v2.56.0/attr.c#L1165-L1184
                bytes[..written].Replace((byte)'\\', (byte)'/');
            }
            bytes[written] = (byte)'\n';
            input.Advance(written + 1);
        }
        var output = await RunGitAsync(["hash-object", "--stdin-paths"], cancellationToken, input.WrittenMemory).ConfigureAwait(false);
        return ReadObjectIds(output.AsSpan());
    }

    /// <summary>
    /// Rejects filenames that cannot be represented by the newline-delimited Git hashing input.
    /// </summary>
    internal static void ThrowIfUnsupportedPath(ReadOnlySpan<char> path)
    {
        if (path.IndexOfAny('\r', '\n') >= 0)
        {
            throw new DistributedApplicationException("Git pipeline output hashing does not support filenames containing CR or LF.");
        }
    }

    private static ReadOnlyMemory<byte>[] ReadObjectIds(ReadOnlySpan<char> output)
    {
        var ids = new List<ReadOnlyMemory<byte>>();
        foreach (var line in output.EnumerateLines())
        {
            if (!line.IsEmpty)
            {
                if (!TryParseObjectId(line, out var hash))
                {
                    ThrowMalformed("object IDs", "object ID", line);
                }

                ids.Add(hash);
            }
        }
        return ids.ToArray();
    }

    private static bool TryParseObjectId(ReadOnlySpan<char> id, out ReadOnlyMemory<byte> hash)
    {
        hash = default;
        // Full object IDs use 40 hexadecimal digits for SHA-1 or 64 for SHA-256.
        // https://github.com/git/git/blob/v2.56.0/Documentation/technical/hash-function-transition.adoc
        if (id.Length is not (40 or 64) || id.IndexOfAnyExcept(s_hexDigits) >= 0)
        {
            return false;
        }

        var bytes = new byte[id.Length];
        Encoding.ASCII.GetBytes(id, bytes);
        hash = bytes;
        return true;
    }

    private static bool IsMode(ReadOnlySpan<char> mode) => mode.Length == 6 && mode.IndexOfAnyExcept(s_octalDigits) < 0;

    /// <summary>
    /// Throws a malformed-output error with bounded Git diagnostics and optional additional data.
    /// </summary>
    [DoesNotReturn]
    internal static void ThrowMalformed(string kind, string field, ReadOnlySpan<char> value, IEnumerable<KeyValuePair<string, object?>>? additionalData = null)
    {
        var exception = new DistributedApplicationException($"Git returned malformed pipeline output {kind}.");
        exception.Data["GitRecordKind"] = kind;
        exception.Data["GitField"] = field;
        // A misbehaving filter can emit a large response instead of an object ID.
        const int MaxDiagnosticValueLength = 256;
        exception.Data["GitValue"] = value[..Math.Min(value.Length, MaxDiagnosticValueLength)].ToString();
        exception.Data["GitValueLength"] = value.Length;
        if (additionalData is not null)
        {
            foreach (var entry in additionalData)
            {
                exception.Data[entry.Key] = entry.Value;
            }
        }

        throw exception;
    }

    private static bool TryGetRecordPayload(ReadOnlySpan<char> output, out ReadOnlySpan<char> records)
    {
        records = default;
        if (output.IsEmpty)
        {
            return true;
        }

        // -z emits raw paths, including spaces/tabs/newlines, with one NUL after each record.
        if (output[^1] != '\0' || output[0] == '\0' || output.Contains("\0\0", StringComparison.Ordinal))
        {
            return false;
        }

        records = output[..^1];
        return true;
    }

    private async Task<string> RunGitAsync(string[] arguments, CancellationToken cancellationToken, ReadOnlyMemory<byte> input = default)
    {
        var result = await processRunner.RunAndCaptureTextAsync("git", ["--no-optional-locks", .. arguments], repositoryRoot,
            s_environmentVariables, input, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new DistributedApplicationException($"Git could not capture pipeline output (exit code {result.ExitCode}): {result.StandardError.Trim()}");
        }
        return result.StandardOutput;
    }

    internal sealed record SelectedFile(FileInfo File, IndexEntry? Index);

    internal sealed record IndexEntry(string Mode, ReadOnlyMemory<byte> Hash, char Stage, char Tag, string Path)
    {
        internal bool IsReusable => Tag == 'H';
        // Git index modes: 100644 regular non-executable, 100755 regular executable, 160000 gitlink (submodule).
        // https://github.com/git/git/blob/v2.56.0/Documentation/gitformat-index.adoc
        internal bool IsRegularFile => Mode is "100644" or "100755";
        internal bool IsGitLink => Mode == "160000";
        internal bool IsMerged => Stage == '0';

        /// <summary>
        /// Parses an index entry while preserving its repository-relative path.
        /// </summary>
        internal static IndexEntry Parse(ReadOnlySpan<char> record)
        {
            // -t -v --stage: "H 100644 <oid> 0\tpath\0". h and S/s are index-ignore hints.
            // Stage 0 is the normal (merged) entry; stages 1/2/3 are base/ours/theirs.
            // https://git-scm.com/docs/git-ls-files
            var tab = record.IndexOf('\t');
            Span<Range> fields = stackalloc Range[4];
            var header = tab > 0 ? record[..tab] : [];
            if (header.Split(fields, ' ') != fields.Length || tab == record.Length - 1)
            {
                ThrowMalformed("index", "record", record);
            }

            var tag = header[fields[0]];
            var mode = header[fields[1]];
            var objectId = header[fields[2]];
            var stage = header[fields[3]];
            var path = record[(tab + 1)..];
            if (tag.Length != 1 || tag.IndexOfAnyExcept(s_indexTags) >= 0 || !IsMode(mode) || mode.SequenceEqual("000000") ||
                stage.Length != 1 || stage[0] is < '0' or > '3')
            {
                ThrowMalformed("index", "record", record);
            }
            if (!TryParseObjectId(objectId, out var hash))
            {
                ThrowMalformed("index", "object ID", objectId);
            }

            return new(mode.ToString(), hash, stage[0], tag[0], path.ToString());
        }
    }

    internal sealed record StatusEntry(char Y, string Mode, string Path)
    {
        // Git's zero mode means there is no working-tree entry at this indexed path.
        internal bool IsDeleted => Y == 'D' || Mode == "000000";

        /// <summary>
        /// Parses the working-tree status, mode, and path from an ordinary or unmerged status entry.
        /// </summary>
        internal static StatusEntry Parse(ReadOnlySpan<char> record)
        {
            // Ordinary: "1 XY sub mH mI mW hH hI path\0"; unmerged:
            // "u XY sub m1 m2 m3 mW h1 h2 h3 path\0". Paths can contain spaces/tabs.
            // Only Y, mW and path affect capture. Preserve framing without interpreting unused fields.
            // https://git-scm.com/docs/git-status#_porcelain_format_version_2
            var ordinary = record.StartsWith("1 ", StringComparison.Ordinal);
            if (!ordinary && !record.StartsWith("u ", StringComparison.Ordinal))
            {
                ThrowMalformed("status", "record type", record);
            }

            Span<Range> fields = stackalloc Range[ordinary ? 9 : 11];
            if (record.Split(fields, ' ') != fields.Length)
            {
                ThrowMalformed("status", "record", record);
            }

            var status = record[fields[1]];
            var workingTreeMode = record[fields[ordinary ? 5 : 6]];
            var path = record[fields[^1]];
            if (status.Length != 2 || status[1..].IndexOfAnyExcept(s_statusCodes) >= 0 || !IsMode(workingTreeMode) || path.IsEmpty)
            {
                ThrowMalformed("status", "record", record);
            }

            return new(status[1], workingTreeMode.ToString(), path.ToString());
        }
    }
}
