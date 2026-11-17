// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Dcp.Process;
using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.Options;

#pragma warning disable ASPIREPIPELINES005 // Internal consumer of the experimental output diffing API.

namespace Aspire.Hosting.Pipelines;

/// <summary>
/// Captures pipeline output with the configured output selection and Git policy.
/// </summary>
internal sealed class DefaultPipelineOutputSnapshotter : IPipelineOutputSnapshotter<PipelineOutputSnapshot>
{
    private readonly PipelineOutputSnapshotter _snapshotter;
    private readonly IProcessRunner _processRunner;
    private readonly PipelineOutputDiffMode _mode;
    private readonly Matcher _matcher = new(PipelineOutputRoot.PathComparison, preserveFilterOrder: true);

    /// <summary>
    /// Creates a snapshotter with the configured output selection and Git policy.
    /// </summary>
    public DefaultPipelineOutputSnapshotter(PipelineOutputSnapshotter snapshotter, IProcessRunner processRunner, IOptions<PipelineOutputDiffOptions> options)
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
    public async Task<PipelineOutputSnapshot> CaptureAsync(string outputDirectory, CancellationToken ct)
    {
        var directory = PipelineOutputRoot.NormalizeDirectoryPath(outputDirectory);
        if (!PipelineOutputSnapshotter.ValidateOutputDirectory(directory, ct))
        {
            // Missing roots are empty, not an invitation to search parents.
            return new(directory, new Dictionary<string, PipelineOutputFileSnapshot>(PipelineOutputRoot.PathComparer));
        }
        var git = _mode is PipelineOutputDiffMode.Auto or PipelineOutputDiffMode.Git
            ? await GitPipelineOutputReader.TryCreateAsync(directory, _processRunner, ct).ConfigureAwait(false)
            : null;
        if (git is null && _mode is PipelineOutputDiffMode.Git)
        {
            throw new DistributedApplicationException($"Git is required to capture pipeline output, but the Git executable or a usable worktree is unavailable: {directory}");
        }
        return new(directory, await _snapshotter.CaptureAsync(directory, _matcher, git, ct).ConfigureAwait(false));
    }
}

#pragma warning restore ASPIREPIPELINES005
