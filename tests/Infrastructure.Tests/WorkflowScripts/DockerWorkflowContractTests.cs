// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Xunit;

namespace Infrastructure.Tests.WorkflowScripts;

public class DockerWorkflowContractTests
{
    private readonly string _repoRoot = FindRepoRoot();

    [Fact]
    public async Task DockerWorkflowsUseVerifyDockerAction()
    {
        string[] workflowPaths =
        [
            ".github/workflows/build-cli-e2e-image.yml",
            ".github/workflows/run-tests.yml",
            ".github/workflows/reproduce-flaky-tests.yml",
            ".github/workflows/tests-daily-smoke.yml",
            ".github/workflows/deployment-tests.yml"
        ];

        foreach (var workflowPath in workflowPaths)
        {
            var workflowText = await ReadRepoFileAsync(workflowPath);

            Assert.Contains("uses: ./.github/actions/verify-docker", workflowText, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task RunTestsPolyglotJavaImageGateUsesTestProperty()
    {
        var workflowText = await ReadRepoFileAsync(".github/workflows/run-tests.yml");

        Assert.DoesNotContain("contains(inputs.testShortName, 'Java')", workflowText, StringComparison.Ordinal);
        Assert.Contains("fromJson(inputs.properties).requiresPolyglotJavaImage == true", workflowText, StringComparison.Ordinal);
        Assert.Contains("--require-java \"${{ fromJson(inputs.properties).requiresPolyglotJavaImage == true }}\"", workflowText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BuildCliE2EImageUsesSharedBuildxPathForJavaImage()
    {
        var workflowText = await ReadRepoFileAsync(".github/workflows/build-cli-e2e-image.yml");

        Assert.DoesNotContain("build_java_image", workflowText, StringComparison.Ordinal);
        Assert.DoesNotContain("DOCKER_BUILDKIT=1 docker build", workflowText, StringComparison.Ordinal);
        Assert.Contains("build_with_mirror_retry \"Java polyglot image\"", workflowText, StringComparison.Ordinal);
    }

    private Task<string> ReadRepoFileAsync(string relativePath)
        => File.ReadAllTextAsync(Path.Combine(_repoRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Aspire.slnx")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not find repository root");
    }
}
