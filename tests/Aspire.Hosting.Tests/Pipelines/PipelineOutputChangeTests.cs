// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Pipelines;

namespace Aspire.Hosting.Tests.Pipelines;

public class PipelineOutputChangeTests
{
    [Theory]
    [InlineData("deployment.yaml")]
    [InlineData("generated/deployment.yaml")]
    [InlineData("../custom-coverage.yaml")]
    public void RelativePathsArePreserved(string path)
    {
        var change = new PipelineOutputChange(PipelineOutputChangeKind.Changed, path);

        Assert.Equal(path, change.Path);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("generated\0file")]
    public void InvalidPathsAreRejected(string path)
    {
        Assert.Throws<ArgumentException>(() => new PipelineOutputChange(PipelineOutputChangeKind.Added, path));
    }

    [Fact]
    public void RootedPathsAreRejected()
    {
        var path = Path.GetFullPath("deployment.yaml");
        Assert.True(Path.IsPathRooted(path));

        Assert.Throws<ArgumentException>(() => new PipelineOutputChange(PipelineOutputChangeKind.Removed, path));
    }

    [Fact]
    public void DiffingExportsOnlyConsumerContracts()
    {
        var types = typeof(IPipelineOutputDiffer).Assembly.GetExportedTypes()
            .Where(type => type.Namespace == typeof(IPipelineOutputDiffer).Namespace &&
                (type.Name.StartsWith("PipelineOutput", StringComparison.Ordinal) ||
                    type.Name.StartsWith("IPipelineOutput", StringComparison.Ordinal)) && type != typeof(IPipelineOutputService))
            .Select(type => type.Name).Order(StringComparer.Ordinal);

        Assert.Equal(new[]
        {
            "IPipelineOutputDiffer", "IPipelineOutputFileHasher", "PipelineOutputChange", "PipelineOutputChangeKind",
            "PipelineOutputDiffOptions", "PipelineOutputDiffRule", "PipelineOutputDiffingExtensions"
        }, types);
    }
}
