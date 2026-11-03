// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.FileSystemGlobbing.Abstractions;

namespace Aspire.Hosting.Pipelines;

/// <summary>
/// Presents directory links as file entries so glob traversal never follows them.
/// </summary>
internal sealed class PipelineOutputDirectory(DirectoryInfo directory, CancellationToken cancellationToken) : DirectoryInfoWrapper(directory)
{
    private readonly DirectoryInfo _directory = directory;

    public override IEnumerable<FileSystemInfoBase> EnumerateFileSystemInfos()
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_directory.Exists || _directory.IsJunctionOrSymlink())
        {
            yield break;
        }
        foreach (var entry in _directory.EnumerateFileSystemInfos())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry is DirectoryInfo child && !child.IsJunctionOrSymlink())
            {
                yield return new PipelineOutputDirectory(child, cancellationToken);
            }
            else
            {
                yield return new FileInfoWrapper(new FileInfo(entry.FullName));
            }
        }
    }

    public override DirectoryInfoBase GetDirectory(string path) =>
        new PipelineOutputDirectory(new DirectoryInfo(Path.GetFullPath(path, _directory.FullName)), cancellationToken);
}

internal static class PipelineOutputFileSystemExtensions
{
    // FileSystemInfo.Attributes can return -1 for a missing entry, which would look like a link.
    // Use the throwing API so capture preserves file-open failures rather than inventing link state.
    internal static bool IsJunctionOrSymlink(this FileSystemInfo entry) => File.GetAttributes(entry.FullName).IsJunctionOrSymlink();

    internal static bool IsJunctionOrSymlink(this FileAttributes attributes) => (attributes & FileAttributes.ReparsePoint) != 0;

    internal static bool IsDirectory(this FileAttributes attributes) => (attributes & FileAttributes.Directory) != 0;
}
