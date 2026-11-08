// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES005 // Internal consumer of the experimental output diffing API.

namespace Aspire.Hosting.Pipelines;

/// <summary>
/// Compares captured output without retaining operation state.
/// </summary>
internal sealed class DefaultPipelineOutputComparer : IPipelineOutputComparer<PipelineOutputSnapshot>
{
    /// <inheritdoc />
    public Task<IReadOnlyList<PipelineOutputChange>> CompareAsync(
        PipelineOutputSnapshot before, PipelineOutputSnapshot after, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        return Task.FromResult(Compare(before.Files, after.Files, after.OutputDirectory));
    }

    /// <summary>
    /// Compares entry kinds, hashes, and available Git modes without accessing the filesystem.
    /// </summary>
    internal static IReadOnlyList<PipelineOutputChange> Compare(
        IReadOnlyDictionary<string, PipelineOutputFileSnapshot> before,
        IReadOnlyDictionary<string, PipelineOutputFileSnapshot> after,
        string outputDirectory)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        var paths = before.Keys.Union(after.Keys, PipelineOutputRoot.PathComparer).Order(PipelineOutputRoot.PathComparer);
        var changes = new List<PipelineOutputChange>();
        foreach (var path in paths)
        {
            var hadBefore = before.TryGetValue(path, out var beforeSnapshot);
            var hasAfter = after.TryGetValue(path, out var afterSnapshot);
            if (!hadBefore)
            {
                changes.Add(new(PipelineOutputChangeKind.Added, Path.GetRelativePath(outputDirectory, path)));
            }
            else if (!hasAfter)
            {
                changes.Add(new(PipelineOutputChangeKind.Removed, Path.GetRelativePath(outputDirectory, path)));
            }
            else if (beforeSnapshot.IsLink != afterSnapshot.IsLink || !beforeSnapshot.Hash.Span.SequenceEqual(afterSnapshot.Hash.Span) ||
                beforeSnapshot.GitMode is not null && afterSnapshot.GitMode is not null && beforeSnapshot.GitMode != afterSnapshot.GitMode)
            {
                changes.Add(new(PipelineOutputChangeKind.Changed, Path.GetRelativePath(outputDirectory, path)));
            }
        }

        return changes.AsReadOnly();
    }
}

#pragma warning restore ASPIREPIPELINES005
