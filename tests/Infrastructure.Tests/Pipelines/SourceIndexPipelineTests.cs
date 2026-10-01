// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Xunit;

namespace Infrastructure.Tests;

public sealed class SourceIndexPipelineTests
{
    [Fact]
    public async Task SourceIndexingRunsDailyFromMainAndCanBeStartedManually()
    {
        var pipeline = await ReadRepoFileAsync("eng/pipelines/azure-pipelines-source-index.yml");

        Assert.Contains("trigger: none", pipeline);
        Assert.Contains("pr: none", pipeline);
        Assert.Contains("cron: \"0 0 * * *\"", pipeline);
        Assert.Contains("displayName: Daily source indexing", pipeline);
        Assert.Contains("    - main", pipeline);
        Assert.Contains("always: true", pipeline);
        Assert.Contains("enableSourceIndex: true", pipeline);
        Assert.Contains(@".\build.cmd -restore -build -binarylog -ci", pipeline);
    }

    [Fact]
    public async Task OfficialPipelineDoesNotRunSourceIndexing()
    {
        var pipeline = await ReadRepoFileAsync("eng/pipelines/azure-pipelines.yml");

        Assert.DoesNotContain("- stage: source_index", pipeline);
        Assert.DoesNotContain("enableSourceIndex: true", pipeline);
    }
}
