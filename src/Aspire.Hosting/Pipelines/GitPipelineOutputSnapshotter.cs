// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Pipelines;

/// <summary>
/// Snapshots selected working-tree files using Git's hashes and reported modes.
/// </summary>
internal static class GitPipelineOutputSnapshotter
{
    /// <summary>
    /// Captures selected leaves using working-tree modes, reusable index hashes, and residual Git hashing.
    /// </summary>
    internal static async Task<IReadOnlyDictionary<string, PipelineOutputFileSnapshot>> CaptureAsync(
        GitPipelineOutputReader git, IReadOnlyList<GitPipelineOutputReader.SelectedFile> entries, CancellationToken cancellationToken)
    {
        var working = await git.ReadStatusAsync(cancellationToken).ConfigureAwait(false);
        var files = new Dictionary<string, PipelineOutputFileSnapshot>(entries.Count, PipelineOutputRoot.PathComparer);
        var residual = new List<(FileInfo File, string? Mode)>();
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = entry.File.FullName;
            var state = working.GetValueOrDefault(path);
            if (state is { IsDeleted: true })
            {
                continue;
            }
            // Status supplies the working-tree mode; absent status retains Git's index mode.
            // Git supplies no mode for an untracked file. Do not infer one from filesystem flags.
            var mode = state?.Mode ?? entry.Index?.Mode;
            if (entry.File.IsJunctionOrSymlink())
            {
                var target = entry.File.LinkTarget ?? throw new IOException($"Cannot read pipeline output link target: {path}");
                // Both captures hash the same engine-read target text. Index link hashes may use
                // a different target representation, so they are never reused.
                var hash = await git.HashLinkTargetAsync(target, cancellationToken).ConfigureAwait(false);
                files.Add(path, new(IsLink: true, hash, mode));
            }
            else if (entry.Index is { IsReusable: true, IsRegularFile: true } indexed && state?.Y is null or '.')
            {
                files.Add(path, new(IsLink: false, indexed.Hash, mode));
            }
            else
            {
                residual.Add((entry.File, mode));
            }
        }
        if (residual.Count != 0)
        {
            var hashes = await git.HashFilesAsync(residual.Select(entry => entry.File), cancellationToken).ConfigureAwait(false);
            if (hashes.Length != residual.Count)
            {
                GitPipelineOutputReader.ThrowMalformed("object IDs", "count", [],
                    [new("ExpectedCount", residual.Count), new("ActualCount", hashes.Length)]);
            }
            for (var i = 0; i < residual.Count; i++)
            {
                files.Add(residual[i].File.FullName, new(IsLink: false, hashes[i], residual[i].Mode));
            }
        }
        return files;
    }
}
