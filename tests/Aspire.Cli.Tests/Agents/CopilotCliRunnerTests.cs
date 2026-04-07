// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Agents.CopilotCli;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.Agents;

public class CopilotCliRunnerTests
{
    [Fact]
    public async Task GetVersionAsync_ChecksForCopilot()
    {
        // Arrange
        var runner = new CopilotCliRunner(NullLogger<CopilotCliRunner>.Instance);

        // Act
        _ = await runner.GetVersionAsync(CancellationToken.None);

        // Assert
        // The result depends on whether copilot is actually installed on the system
        // This test just ensures we don't crash and do attempt the PATH lookup
        // Since we can't guarantee copilot is installed in the test environment,
        // we just verify the method completes without throwing
        // Version can be null (not installed) or a real version
    }

    [Theory]
    [InlineData("GitHub Copilot CLI 0.0.397", 0, 0, 397)]
    [InlineData("GitHub Copilot CLI 1.2.3", 1, 2, 3)]
    [InlineData("0.0.397", 0, 0, 397)]
    [InlineData("1.2.3", 1, 2, 3)]
    [InlineData("v1.2.3", 1, 2, 3)]
    [InlineData("V1.2.3", 1, 2, 3)]
    [InlineData("GitHub Copilot CLI 0.0.397\nsome other output", 0, 0, 397)]
    [InlineData("  GitHub Copilot CLI 0.0.397  ", 0, 0, 397)]
    [InlineData("GitHub Copilot CLI 0.0.397.", 0, 0, 397)]
    public void TryParseVersionOutput_ValidVersionStrings_ReturnsTrue(string input, int major, int minor, int patch)
    {
        var result = CopilotCliRunner.TryParseVersionOutput(input, out var version);

        Assert.True(result);
        Assert.NotNull(version);
        Assert.Equal(major, version.Major);
        Assert.Equal(minor, version.Minor);
        Assert.Equal(patch, version.Patch);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a version")]
    public void TryParseVersionOutput_InvalidVersionStrings_ReturnsFalse(string input)
    {
        var result = CopilotCliRunner.TryParseVersionOutput(input, out var version);

        Assert.False(result);
        Assert.Null(version);
    }
}
