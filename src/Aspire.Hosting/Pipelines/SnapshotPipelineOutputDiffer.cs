// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Dcp.Process;
using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.Options;

#pragma warning disable ASPIREPIPELINES005 // Internal consumer of the experimental output diffing API.

namespace Aspire.Hosting.Pipelines;

/// <summary>
/// Retains snapshots and one deferred capture strategy for an output diff operation.
/// </summary>
internal sealed class SnapshotPipelineOutputDiffer : IPipelineOutputDiffer
{
    private readonly PipelineOutputSnapshotter _snapshotter;
    private readonly IProcessRunner _processRunner;
    private readonly PipelineOutputDiffMode _mode;
    private readonly Matcher _matcher = new(PipelineOutputRoot.PathComparison, preserveFilterOrder: true);
    private string? _outputDirectory;
    private Func<string, CancellationToken, Task<IReadOnlyDictionary<string, PipelineOutputFileSnapshot>>>? _capture;
    private IReadOnlyDictionary<string, PipelineOutputFileSnapshot>? _before;
    private IReadOnlyDictionary<string, PipelineOutputFileSnapshot>? _after;

    /// <summary>
    /// Creates a differ with the configured output selection and Git policy.
    /// </summary>
    public SnapshotPipelineOutputDiffer(PipelineOutputSnapshotter snapshotter, IProcessRunner processRunner, IOptions<PipelineOutputDiffOptions> options)
    {
        _snapshotter = snapshotter;
        _processRunner = processRunner;
        var policy = options.Value;
        _mode = policy.Mode;
        if (policy.Rules.Count == 0)
        {
            _matcher.AddInclude("**/*");
        }
        foreach (var rule in policy.Rules)
        {
            if (string.Equals(rule.Action, "Include", StringComparison.OrdinalIgnoreCase))
            {
                _matcher.AddInclude(rule.Pattern);
            }
            else
            {
                _matcher.AddExclude(rule.Pattern);
            }
        }
    }

    /// <inheritdoc />
    public async Task CaptureBeforeAsync(string outputDirectory, CancellationToken ct)
    {
        if (_before is not null)
        {
            throw new InvalidOperationException("Pipeline output has already been captured before generation.");
        }
        _before = await CaptureAsync(outputDirectory, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task CaptureAfterAsync(string outputDirectory, CancellationToken ct)
    {
        if (_before is null || _after is not null)
        {
            throw new InvalidOperationException("Capture output before generation, then capture output after generation once.");
        }
        _after = await CaptureAsync(outputDirectory, ct).ConfigureAwait(false);
        _outputDirectory = PipelineOutputRoot.NormalizeDirectoryPath(outputDirectory);
    }

    /// <summary>
    /// Captures one output directory, selecting a strategy on the first existing directory.
    /// </summary>
    internal async Task<IReadOnlyDictionary<string, PipelineOutputFileSnapshot>> CaptureAsync(string outputDirectory, CancellationToken ct)
    {
        var directory = PipelineOutputRoot.NormalizeDirectoryPath(outputDirectory);
        if (!PipelineOutputSnapshotter.ValidateOutputDirectory(directory, ct))
        {
            // Missing roots are empty, not an invitation to search parents. The first existing
            // capture selects a strategy; after selection failures never switch strategies.
            return new Dictionary<string, PipelineOutputFileSnapshot>(PipelineOutputRoot.PathComparer);
        }
        if (_capture is null)
        {
            var git = _mode is PipelineOutputDiffMode.Auto or PipelineOutputDiffMode.Git
                ? await GitPipelineOutputReader.TryCreateAsync(directory, _processRunner, ct).ConfigureAwait(false)
                : null;
            if (git is null && _mode is PipelineOutputDiffMode.Git)
            {
                throw new DistributedApplicationException($"Git is required to capture pipeline output, but the Git executable or a usable worktree is unavailable: {directory}");
            }
            _capture = (path, token) => _snapshotter.CaptureAsync(path, _matcher, git, token);
        }
        return await _capture(directory, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<PipelineOutputChange>> CompareAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_before is null || _after is null)
        {
            throw new InvalidOperationException("Capture output before and after generation before comparing.");
        }
        return Task.FromResult(Compare(_before, _after, _outputDirectory!));
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
