// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Utils.EnvironmentChecker;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.Utils.EnvironmentChecks;

public class NodeJsRuntimeCheckTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task CheckAsync_ReturnsEmpty_WhenNoTypeScriptAppHostPresent()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var executionContext = BuildExecutionContext(workspace.WorkspaceRoot);
        var check = new NodeJsRuntimeCheck(executionContext, NullLogger<NodeJsRuntimeCheck>.Instance);

        var results = await check.CheckAsync(TestContext.Current.CancellationToken);

        Assert.Empty(results);
    }

    [Fact]
    public async Task CheckAsync_SkipsNodeProbe_WhenBunIsResolved()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        File.WriteAllText(Path.Combine(workspace.WorkspaceRoot.FullName, "apphost.ts"), string.Empty);
        File.WriteAllText(Path.Combine(workspace.WorkspaceRoot.FullName, "package.json"), """{ "packageManager": "bun@1.1.30" }""");

        var executionContext = BuildExecutionContext(workspace.WorkspaceRoot);
        var check = new NodeJsRuntimeCheck(executionContext, NullLogger<NodeJsRuntimeCheck>.Instance);

        var results = await check.CheckAsync(TestContext.Current.CancellationToken);

        Assert.Empty(results);
    }

    [Theory]
    [InlineData("v20.19.0", "20.19.0")]
    [InlineData("v22.13.5", "22.13.5")]
    [InlineData("v24.0.0", "24.0.0")]
    [InlineData("v18.20.4\n", "18.20.4")]
    [InlineData("node v22.13.0", "22.13.0")]
    public void ExtractVersion_ReturnsExpected(string input, string expected)
    {
        var result = NodeJsRuntimeCheck.ExtractVersion(input);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("unknown")]
    [InlineData("vNaN.NaN")]
    public void ExtractVersion_ReturnsNull_ForInvalidInput(string input)
    {
        var result = NodeJsRuntimeCheck.ExtractVersion(input);

        Assert.Null(result);
    }

    [Theory]
    [InlineData("20.19.0", true)]
    [InlineData("20.19.5", true)]
    [InlineData("20.20.0", true)]
    [InlineData("22.13.0", true)]
    [InlineData("22.13.5", true)]
    [InlineData("22.20.0", true)]
    [InlineData("24.0.0", true)]
    [InlineData("99.0.0", true)]
    public void IsSupportedVersion_ReturnsTrue_ForSupportedRanges(string version, bool expected)
    {
        Assert.Equal(expected, NodeJsRuntimeCheck.IsSupportedVersion(version));
    }

    [Theory]
    [InlineData("20.18.0")]
    [InlineData("20.0.0")]
    [InlineData("22.12.0")]
    [InlineData("22.0.0")]
    [InlineData("18.20.4")]
    [InlineData("16.0.0")]
    [InlineData("21.0.0")]
    public void IsSupportedVersion_ReturnsFalse_ForUnsupportedVersions(string version)
    {
        Assert.False(NodeJsRuntimeCheck.IsSupportedVersion(version));
    }

    [Fact]
    public void IsSupportedVersion_ReturnsTrue_ForUnparseableVersions()
    {
        // Defensive: when we can't parse the version, assume support to avoid noisy warnings.
        Assert.True(NodeJsRuntimeCheck.IsSupportedVersion("not-a-version"));
    }

    private static CliExecutionContext BuildExecutionContext(DirectoryInfo workingDirectory)
    {
        var aspireRoot = new DirectoryInfo(Path.Combine(workingDirectory.FullName, ".aspire"));
        var hives = new DirectoryInfo(Path.Combine(aspireRoot.FullName, "hives"));
        var cache = new DirectoryInfo(Path.Combine(aspireRoot.FullName, "cache"));
        var sdks = new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-sdks"));
        var logs = new DirectoryInfo(Path.Combine(aspireRoot.FullName, "logs"));
        var logFile = Path.Combine(logs.FullName, "test.log");

        return new CliExecutionContext(workingDirectory, hives, cache, sdks, logs, logFile);
    }
}
