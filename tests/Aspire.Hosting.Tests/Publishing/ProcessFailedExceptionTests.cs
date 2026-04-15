// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Publishing;

namespace Aspire.Hosting.Tests.Publishing;

[Trait("Partition", "4")]
public class ProcessFailedExceptionTests
{
    [Fact]
    public void Message_IncludesTruncationSummary_WhenOutputExceedsDefaultLimit()
    {
        var buildOutput = Enumerable.Range(1, 60)
            .Select(i => $"output-{i:000}")
            .ToArray();

        var exception = new ProcessFailedException("Build failed.", 1, buildOutput, totalBuildOutputLineCount: 60);

        Assert.Contains("Build failed.", exception.Message);
        Assert.Contains("Build output truncated: showing last 50 of 60 lines.", exception.Message);
        Assert.DoesNotContain("output-001", exception.Message);
        Assert.Contains("output-011", exception.Message);
        Assert.Contains("output-060", exception.Message);
    }
}
