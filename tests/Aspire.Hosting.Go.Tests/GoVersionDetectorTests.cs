// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Go.Tests;

public class GoVersionDetectorTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public void Detect_ReturnsDefault_WhenGoModAbsent()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var version = GoVersionDetector.Detect(workspace.Path);

        Assert.Equal("1.26", version);
    }

    [Fact]
    public void Detect_ReadsGoDirective()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        File.WriteAllText(Path.Combine(workspace.Path, "go.mod"), "module example.com/m\n\ngo 1.23\n");

        var version = GoVersionDetector.Detect(workspace.Path);

        Assert.Equal("1.23", version);
    }

    [Fact]
    public void Detect_ReadsGoDirectiveWithPatch()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        File.WriteAllText(Path.Combine(workspace.Path, "go.mod"), "module example.com/m\n\ngo 1.23.4\n");

        var version = GoVersionDetector.Detect(workspace.Path);

        Assert.Equal("1.23.4", version);
    }

    [Fact]
    public void Detect_PrefersToolchainOverGoDirective()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        File.WriteAllText(Path.Combine(workspace.Path, "go.mod"),
            "module example.com/m\n\ngo 1.23\ntoolchain go1.23.6\n");

        var version = GoVersionDetector.Detect(workspace.Path);

        // toolchain pins the exact build toolchain — prefer it.
        Assert.Equal("1.23.6", version);
    }

    [Fact]
    public void Detect_FallsBackToGoDirective_WhenNoToolchain()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        File.WriteAllText(Path.Combine(workspace.Path, "go.mod"),
            "module example.com/m\n\ngo 1.24\n");

        var version = GoVersionDetector.Detect(workspace.Path);

        Assert.Equal("1.24", version);
    }

    [Fact]
    public void Detect_ReturnsDefault_WhenGoModHasNoRecognisedDirective()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        File.WriteAllText(Path.Combine(workspace.Path, "go.mod"), "module example.com/m\n");

        var version = GoVersionDetector.Detect(workspace.Path);

        Assert.Equal("1.26", version);
    }
}
