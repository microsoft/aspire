// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Acquisition;
using Aspire.Cli.Tests.Utils;

namespace Aspire.Cli.Tests.Acquisition;

/// <summary>
/// Behavior tests for <see cref="InstallSidecarReader"/>. The sidecar contract
/// is documented in <c>docs/specs/install-routes.md</c>: a single-field JSON
/// file named <c>.aspire-install.json</c> with shape
/// <c>{ "source": "&lt;route&gt;" }</c> living next to the CLI binary.
/// </summary>
public class InstallSidecarReaderTests(ITestOutputHelper outputHelper)
{
    [Theory]
    [InlineData("script", "Script")]
    [InlineData("pr", "Pr")]
    [InlineData("winget", "Winget")]
    [InlineData("brew", "Brew")]
    [InlineData("dotnet-tool", "DotnetTool")]
    [InlineData("localhive", "LocalHive")]
    public void TryRead_ParsesEachKnownSource(string wireValue, string expectedEnumName)
    {
        var expected = Enum.Parse<InstallSource>(expectedEnumName);

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        WriteSidecar(workspace.WorkspaceRoot.FullName, $"{{\"source\":\"{wireValue}\"}}");

        var reader = new InstallSidecarReader();
        var info = reader.TryRead(workspace.WorkspaceRoot.FullName);

        Assert.NotNull(info);
        Assert.Equal(expected, info!.Source);
        Assert.Equal(wireValue, info.RawSource);
    }

    [Fact]
    public void TryRead_ReturnsNullWhenSidecarMissing()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var reader = new InstallSidecarReader();
        var info = reader.TryRead(workspace.WorkspaceRoot.FullName);

        Assert.Null(info);
    }

    [Fact]
    public void TryRead_ReturnsNullForEmptyBinaryDir()
    {
        var reader = new InstallSidecarReader();
        Assert.Null(reader.TryRead(string.Empty));
        Assert.Null(reader.TryRead(null!));
    }

    [Fact]
    public void TryRead_UnknownSourceIsUnknownEnumWithRawPreserved()
    {
        // A future install route writes a source string this build doesn't
        // know about yet. The reader must surface the raw string so callers
        // can log it, but classify as Unknown so behavior falls back to the
        // pre-sidecar layout heuristic.
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        WriteSidecar(workspace.WorkspaceRoot.FullName, "{\"source\":\"future-route\"}");

        var reader = new InstallSidecarReader();
        var info = reader.TryRead(workspace.WorkspaceRoot.FullName);

        Assert.NotNull(info);
        Assert.Equal(InstallSource.Unknown, info!.Source);
        Assert.Equal("future-route", info.RawSource);
    }

    [Fact]
    public void TryRead_MalformedJsonIsUnknownWithNullRaw()
    {
        // A truncated / corrupt sidecar must not throw — doctor and info
        // commands should still function. Treat as Unknown so the rest of
        // the diagnostic surface stays useful.
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        WriteSidecar(workspace.WorkspaceRoot.FullName, "{not valid json");

        var reader = new InstallSidecarReader();
        var info = reader.TryRead(workspace.WorkspaceRoot.FullName);

        Assert.NotNull(info);
        Assert.Equal(InstallSource.Unknown, info!.Source);
        Assert.Null(info.RawSource);
    }

    [Fact]
    public void TryRead_NonObjectRootIsUnknown()
    {
        // Schema regression guard: a sidecar that contains a JSON array
        // instead of an object must be classified as Unknown, not crash.
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        WriteSidecar(workspace.WorkspaceRoot.FullName, "[\"script\"]");

        var reader = new InstallSidecarReader();
        var info = reader.TryRead(workspace.WorkspaceRoot.FullName);

        Assert.NotNull(info);
        Assert.Equal(InstallSource.Unknown, info!.Source);
        Assert.Null(info.RawSource);
    }

    [Fact]
    public void TryRead_NonStringSourceFieldIsUnknown()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        WriteSidecar(workspace.WorkspaceRoot.FullName, "{\"source\": 42}");

        var reader = new InstallSidecarReader();
        var info = reader.TryRead(workspace.WorkspaceRoot.FullName);

        Assert.NotNull(info);
        Assert.Equal(InstallSource.Unknown, info!.Source);
        Assert.Null(info.RawSource);
    }

    [Fact]
    public void TryRead_SidecarPathIsAbsolutePathOfReadFile()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        WriteSidecar(workspace.WorkspaceRoot.FullName, "{\"source\":\"script\"}");

        var reader = new InstallSidecarReader();
        var info = reader.TryRead(workspace.WorkspaceRoot.FullName);

        Assert.NotNull(info);
        var expectedPath = Path.Combine(workspace.WorkspaceRoot.FullName, InstallSidecarReader.SidecarFileName);
        Assert.Equal(expectedPath, info!.SidecarPath);
    }

    [Theory]
    [InlineData("Script", "script")]
    [InlineData("Pr", "pr")]
    [InlineData("Winget", "winget")]
    [InlineData("Brew", "brew")]
    [InlineData("DotnetTool", "dotnet-tool")]
    [InlineData("LocalHive", "localhive")]
    public void ToWireString_RoundTripsWithParseInstallSource(string enumName, string expectedWire)
    {
        var source = Enum.Parse<InstallSource>(enumName);
        Assert.Equal(expectedWire, source.ToWireString());
        Assert.Equal(source, InstallSourceExtensions.ParseInstallSource(expectedWire));
    }

    [Fact]
    public void ToWireString_ReturnsNullForUnknown()
    {
        // Unknown is an internal sentinel and has no wire representation —
        // emitters must never produce it.
        Assert.Null(InstallSource.Unknown.ToWireString());
    }

    private static void WriteSidecar(string binaryDir, string content)
    {
        var path = Path.Combine(binaryDir, InstallSidecarReader.SidecarFileName);
        File.WriteAllText(path, content);
    }
}
