// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.TestUtilities;
using Xunit;

namespace Infrastructure.Tests;

public sealed class HomebrewPublisherTests(ITestOutputHelper output)
{
    [Fact]
    [RequiresTools(["python3"])]
    public async Task PublisherPreservesDryRunAndReconcilesSubmissions()
    {
        var result = await TestProcessRunner.RunAsync(
            output,
            "python3",
            ["-m", "unittest", "discover", "-s", "tests/Infrastructure.Tests/Pipelines", "-p", "test_homebrew_publisher.py", "-v"],
            RepoRoot.Path);

        Assert.True(result.ExitCode == 0, result.Output);
    }
}
