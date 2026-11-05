// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO.Hashing;
using System.Text;
using Microsoft.Extensions.FileSystemGlobbing;

namespace Aspire.Hosting.Pipelines;

/// <summary>
/// Captures file snapshots without retaining file content.
/// </summary>
internal sealed class PipelineOutputSnapshotter
{
    private readonly IPipelineOutputFileHasher? _hasher;

    /// <summary>
    /// Creates a snapshotter with an optional custom hasher.
    /// </summary>
    public PipelineOutputSnapshotter(IPipelineOutputFileHasher? hasher = null)
    {
        _hasher = hasher;
    }

    /// <summary>
    /// Captures all filesystem leaves in the output directory, treating a missing directory as empty.
    /// </summary>
    internal async Task<IReadOnlyDictionary<string, PipelineOutputFileSnapshot>> CaptureAsync(string outputDirectory, CancellationToken cancellationToken = default)
    {
        var directory = PipelineOutputRoot.NormalizeDirectoryPath(outputDirectory);
        var matcher = new Matcher(PipelineOutputRoot.PathComparison, preserveFilterOrder: true);
        matcher.AddInclude("**/*");
        if (!ValidateOutputDirectory(directory, cancellationToken))
        {
            return new Dictionary<string, PipelineOutputFileSnapshot>(PipelineOutputRoot.PathComparer);
        }
        return await CaptureAsync(directory, matcher, git: null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Captures matched leaves using Git membership when available and the configured hashing policy.
    /// </summary>
    internal async Task<IReadOnlyDictionary<string, PipelineOutputFileSnapshot>> CaptureAsync(string directory, Matcher matcher, GitPipelineOutputReader? git, CancellationToken cancellationToken)
    {
        if (git is null)
        {
            return await CapturePathsAsync(GetFilesystemFiles(directory, matcher, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        var entries = await git.SelectFilesAsync(directory, matcher, cancellationToken).ConfigureAwait(false);
        if (_hasher is null)
        {
            return await GitPipelineOutputSnapshotter.CaptureAsync(git, entries, cancellationToken).ConfigureAwait(false);
        }
        // Custom hashing uses Git only for selection, not its hash or mode policy.
        return await CapturePathsAsync(entries.Select(entry => entry.File), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Selects filesystem leaves within the directory using the ordered matcher rules.
    /// </summary>
    internal static IEnumerable<FileInfo> GetFilesystemFiles(string directory, Matcher matcher, CancellationToken cancellationToken) =>
        matcher.Execute(new PipelineOutputDirectory(new DirectoryInfo(directory), cancellationToken)).Files
            .Select(match => Path.GetFullPath(match.Path, directory)).Where(path => PipelineOutputRoot.IsWithinDirectory(directory, path))
            .Select(path => new FileInfo(path));

    /// <summary>
    /// Returns false for a missing directory, which generation may create. Rejects file roots as invalid
    /// destinations and link roots because capture does not traverse them. Inspection errors propagate
    /// rather than allowing unreadable output to appear empty.
    /// </summary>
    internal static bool ValidateOutputDirectory(string directory, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(directory);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
        if (!attributes.IsDirectory() || attributes.IsJunctionOrSymlink())
        {
            throw new IOException($"Pipeline output root must be a directory, not a file or link: {directory}");
        }
        return true;
    }

    /// <summary>
    /// Captures each distinct leaf with bounded hashing concurrency and owned snapshot hashes.
    /// </summary>
    internal async Task<IReadOnlyDictionary<string, PipelineOutputFileSnapshot>> CapturePathsAsync(IEnumerable<FileInfo> files, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshots = new Dictionary<string, PipelineOutputFileSnapshot>(PipelineOutputRoot.PathComparer);
        var uniqueFiles = files.DistinctBy(file => file.FullName, PipelineOutputRoot.PathComparer).ToArray();
        var results = new PipelineOutputFileSnapshot[uniqueFiles.Length];
        // Awaiting the bounded loop drains workers and their owned streams before failure escapes.
        await Parallel.ForEachAsync(Enumerable.Range(0, uniqueFiles.Length), cancellationToken, async (index, token) =>
        {
            results[index] = await CaptureFileAsync(uniqueFiles[index], token).ConfigureAwait(false);
        }).ConfigureAwait(false);

        for (var index = 0; index < uniqueFiles.Length; index++)
        {
            snapshots.Add(uniqueFiles[index].FullName, results[index]);
        }
        return snapshots;
    }

    private async Task<PipelineOutputFileSnapshot> CaptureFileAsync(FileInfo file, CancellationToken cancellationToken)
    {
        var attributes = File.GetAttributes(file.FullName);
        var isLink = attributes.IsJunctionOrSymlink();
        FileSystemInfo entry = attributes.IsDirectory() ? new DirectoryInfo(file.FullName) : file;
        ReadOnlyMemory<byte> hash = _hasher is not null
            ? await _hasher.HashAsync(entry, cancellationToken).ConfigureAwait(false)
            : await HashAsync(entry, isLink, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return new PipelineOutputFileSnapshot(isLink, hash.ToArray());
    }

    private static async Task<byte[]> HashAsync(FileSystemInfo file, bool isLink, CancellationToken cancellationToken)
    {
        if (isLink)
        {
            var target = file.LinkTarget ?? throw new IOException($"Cannot read pipeline output link target: {file.FullName}");
            return XxHash3.Hash(Encoding.UTF8.GetBytes(target));
        }
        using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = new XxHash3();
        await hash.AppendAsync(stream, cancellationToken).ConfigureAwait(false);
        return hash.GetCurrentHash();
    }
}
