// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIREPIPELINES002

using Aspire.Hosting.Pipelines;

namespace Aspire.Hosting.Tests.Pipelines;

[Trait("Partition", "4")]
public class PipelineScopeAnnotationTests
{
    [Fact]
    public async Task ResolveAsync_ReturnsNull_WhenNotInCI()
    {
        var annotation = new PipelineScopeAnnotation(_ =>
        {
            // Simulate not being in a CI environment
            return Task.FromResult<PipelineScopeResult?>(null);
        });

        var context = new PipelineScopeContext { CancellationToken = CancellationToken.None };
        var result = await annotation.ResolveAsync(context);

        Assert.Null(result);
    }

    [Fact]
    public async Task ResolveAsync_ReturnsScope_WhenInCI()
    {
        var annotation = new PipelineScopeAnnotation(_ =>
        {
            return Task.FromResult<PipelineScopeResult?>(new PipelineScopeResult
            {
                RunId = "12345-1",
                JobId = "build"
            });
        });

        var context = new PipelineScopeContext { CancellationToken = CancellationToken.None };
        var result = await annotation.ResolveAsync(context);

        Assert.NotNull(result);
        Assert.Equal("12345-1", result.RunId);
        Assert.Equal("build", result.JobId);
    }

    [Fact]
    public async Task ResolveAsync_GitHubActionsPattern_ComputesPredictableScope()
    {
        // Simulate the GitHub Actions pattern: GITHUB_RUN_ID + GITHUB_RUN_ATTEMPT + GITHUB_JOB
        var annotation = CreateGitHubActionsScopeAnnotation(
            runId: "98765",
            runAttempt: "2",
            jobId: "deploy");

        var context = new PipelineScopeContext { CancellationToken = CancellationToken.None };
        var result = await annotation.ResolveAsync(context);

        Assert.NotNull(result);
        Assert.Equal("98765-2", result.RunId);
        Assert.Equal("deploy", result.JobId);
    }

    [Fact]
    public async Task ResolveAsync_GitHubActionsPattern_DefaultsRunAttemptTo1()
    {
        var annotation = CreateGitHubActionsScopeAnnotation(
            runId: "12345",
            runAttempt: null,
            jobId: "build");

        var context = new PipelineScopeContext { CancellationToken = CancellationToken.None };
        var result = await annotation.ResolveAsync(context);

        Assert.NotNull(result);
        Assert.Equal("12345-1", result.RunId);
    }

    [Fact]
    public async Task ResolveAsync_GitHubActionsPattern_ReturnsNull_WhenRunIdMissing()
    {
        var annotation = CreateGitHubActionsScopeAnnotation(
            runId: null,
            runAttempt: "1",
            jobId: "build");

        var context = new PipelineScopeContext { CancellationToken = CancellationToken.None };
        var result = await annotation.ResolveAsync(context);

        Assert.Null(result);
    }

    [Fact]
    public void ScopeMapAnnotation_MapsJobIdsToSteps()
    {
        var map = new PipelineScopeMapAnnotation(new Dictionary<string, IReadOnlyList<string>>
        {
            ["build"] = ["compile", "unit-test"],
            ["deploy"] = ["push-image", "deploy-app"]
        });

        Assert.Equal(2, map.ScopeToSteps.Count);
        Assert.Equal(["compile", "unit-test"], map.ScopeToSteps["build"]);
        Assert.Equal(["push-image", "deploy-app"], map.ScopeToSteps["deploy"]);
    }

    [Fact]
    public void ScopeMapAnnotation_EmptyMap_IsValid()
    {
        var map = new PipelineScopeMapAnnotation(new Dictionary<string, IReadOnlyList<string>>());

        Assert.Empty(map.ScopeToSteps);
    }

    /// <summary>
    /// Simulates the GitHub Actions scope annotation pattern with injectable env var values.
    /// </summary>
    private static PipelineScopeAnnotation CreateGitHubActionsScopeAnnotation(
        string? runId, string? runAttempt, string? jobId)
    {
        return new PipelineScopeAnnotation(_ =>
        {
            if (string.IsNullOrEmpty(runId) || string.IsNullOrEmpty(jobId))
            {
                return Task.FromResult<PipelineScopeResult?>(null);
            }

            return Task.FromResult<PipelineScopeResult?>(new PipelineScopeResult
            {
                RunId = $"{runId}-{runAttempt ?? "1"}",
                JobId = jobId
            });
        });
    }
}
