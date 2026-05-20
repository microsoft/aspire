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
        var result = reader.TryRead(workspace.WorkspaceRoot.FullName);

        var ok = Assert.IsType<InstallSidecarReadResult.Ok>(result);
        Assert.Equal(expected, ok.Info.Source);
        Assert.Equal(wireValue, ok.Info.RawSource);
    }

    [Fact]
    public void TryRead_ReturnsNotFoundWhenSidecarMissing()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var expectedPath = Path.Combine(workspace.WorkspaceRoot.FullName, InstallSidecarReader.SidecarFileName);

        var reader = new InstallSidecarReader();
        var result = reader.TryRead(workspace.WorkspaceRoot.FullName);

        var notFound = Assert.IsType<InstallSidecarReadResult.NotFound>(result);
        Assert.Equal(expectedPath, notFound.SidecarPath);
    }

    [Fact]
    public void TryRead_ReturnsNotFoundForEmptyBinaryDir()
    {
        var reader = new InstallSidecarReader();

        var empty = Assert.IsType<InstallSidecarReadResult.NotFound>(reader.TryRead(string.Empty));
        Assert.Equal(string.Empty, empty.SidecarPath);

        var nullResult = Assert.IsType<InstallSidecarReadResult.NotFound>(reader.TryRead(null!));
        Assert.Equal(string.Empty, nullResult.SidecarPath);
    }

    [Fact]
    public void TryRead_UnreadableSidecar_ReturnsInvalidWithReason()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Unix file modes are required to create a deterministic unreadable sidecar.");
            return;
        }

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var sidecarPath = WriteSidecar(workspace.WorkspaceRoot.FullName, "{\"source\":\"script\"}");
        var originalMode = File.GetUnixFileMode(sidecarPath);

        try
        {
            File.SetUnixFileMode(sidecarPath, UnixFileMode.None);

            var reader = new InstallSidecarReader();
            var result = reader.TryRead(workspace.WorkspaceRoot.FullName);

            var invalid = Assert.IsType<InstallSidecarReadResult.Invalid>(result);
            Assert.Equal(sidecarPath, invalid.SidecarPath);
            Assert.NotEmpty(invalid.Reason);
        }
        finally
        {
            File.SetUnixFileMode(sidecarPath, originalMode | UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [Fact]
    public void TryRead_MalformedJson_ReturnsInvalidWithParseReason()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var sidecarPath = WriteSidecar(workspace.WorkspaceRoot.FullName, "{not valid json");

        var reader = new InstallSidecarReader();
        var result = reader.TryRead(workspace.WorkspaceRoot.FullName);

        var invalid = Assert.IsType<InstallSidecarReadResult.Invalid>(result);
        Assert.Equal(sidecarPath, invalid.SidecarPath);
        Assert.NotEmpty(invalid.Reason);
    }

    [Theory]
    [InlineData("{\"source\":\"\"}",          "",             "empty source string")]
    [InlineData("{\"source\":\"future-route\"}", "future-route", "unknown but well-formed source")]
    [InlineData("[\"script\"]",               "",             "non-object root element")]
    [InlineData("{\"source\": 42}",           "",             "non-string source field")]
    public void TryRead_UnknownOrMalformedSource_ReturnsUnknownEnumWithRawSourcePreserved(string sidecarBody, string expectedRawSource, string scenario)
    {
        // All four shapes round-trip via the parser as InstallSource.Unknown so
        // a future-route or otherwise-unrecognized sidecar never blocks the
        // discovery walk. RawSource preserves the literal wire value so a
        // future client can re-interpret it without re-reading the file.
        _ = scenario; // surfaced in test name for debuggability
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        WriteSidecar(workspace.WorkspaceRoot.FullName, sidecarBody);

        var reader = new InstallSidecarReader();
        var result = reader.TryRead(workspace.WorkspaceRoot.FullName);

        var ok = Assert.IsType<InstallSidecarReadResult.Ok>(result);
        Assert.Equal(InstallSource.Unknown, ok.Info.Source);
        Assert.Equal(expectedRawSource, ok.Info.RawSource);
    }

    [Fact]
    public void TryRead_SidecarPathIsAbsolutePathOfReadFile()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        WriteSidecar(workspace.WorkspaceRoot.FullName, "{\"source\":\"script\"}");

        var reader = new InstallSidecarReader();
        var result = reader.TryRead(workspace.WorkspaceRoot.FullName);

        var ok = Assert.IsType<InstallSidecarReadResult.Ok>(result);
        var expectedPath = Path.Combine(workspace.WorkspaceRoot.FullName, InstallSidecarReader.SidecarFileName);
        Assert.Equal(expectedPath, ok.Info.SidecarPath);
    }

    [Fact]
    public void ReadSourceField_ReturnsRawSource()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var sidecarPath = WriteSidecar(workspace.WorkspaceRoot.FullName, "{\"source\":\"script\"}");

        var result = InstallSidecarReader.ReadSourceField(sidecarPath);

        Assert.Equal("script", result);
    }

    [Fact]
    public void ReadSourceField_MissingSidecar_ReturnsNull()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var sidecarPath = Path.Combine(workspace.WorkspaceRoot.FullName, InstallSidecarReader.SidecarFileName);

        var result = InstallSidecarReader.ReadSourceField(sidecarPath);

        Assert.Null(result);
    }

    [Fact]
    public void ReadSourceField_MalformedJson_ReturnsNull()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var sidecarPath = WriteSidecar(workspace.WorkspaceRoot.FullName, "{not valid json");

        var result = InstallSidecarReader.ReadSourceField(sidecarPath);

        Assert.Null(result);
    }

    [Fact]
    public void ReadSourceField_UnreadableSidecar_ReturnsNull()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Unix file modes are required to create a deterministic unreadable sidecar.");
            return;
        }

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var sidecarPath = WriteSidecar(workspace.WorkspaceRoot.FullName, "{\"source\":\"script\"}");
        var originalMode = File.GetUnixFileMode(sidecarPath);

        try
        {
            File.SetUnixFileMode(sidecarPath, UnixFileMode.None);

            var result = InstallSidecarReader.ReadSourceField(sidecarPath);

            Assert.Null(result);
        }
        finally
        {
            File.SetUnixFileMode(sidecarPath, originalMode | UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
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
        Assert.Null(InstallSource.Unknown.ToWireString());
    }

    [Fact]
    public void TryRead_OversizedSidecar_ReturnsInvalid()
    {
        // Discovery walks PATH and reads any .aspire-install.json next to a candidate
        // binary. A pathological (or hostile) file planted next to such a candidate
        // must not be parsed into memory in full.
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var sidecarPath = Path.Combine(workspace.WorkspaceRoot.FullName, InstallSidecarReader.SidecarFileName);
        var oversized = new string('a', (int)InstallSidecarReader.MaxSidecarBytes + 1);
        File.WriteAllText(sidecarPath, $"{{\"source\":\"{oversized}\"}}");

        var reader = new InstallSidecarReader();
        var result = reader.TryRead(workspace.WorkspaceRoot.FullName);

        var invalid = Assert.IsType<InstallSidecarReadResult.Invalid>(result);
        Assert.Equal(sidecarPath, invalid.SidecarPath);
        Assert.Contains("exceeds", invalid.Reason);
    }

    [Fact]
    public void ReadSourceField_OversizedSidecar_ReturnsNull()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var sidecarPath = Path.Combine(workspace.WorkspaceRoot.FullName, InstallSidecarReader.SidecarFileName);
        var oversized = new string('a', (int)InstallSidecarReader.MaxSidecarBytes + 1);
        File.WriteAllText(sidecarPath, $"{{\"source\":\"{oversized}\"}}");

        Assert.Null(InstallSidecarReader.ReadSourceField(sidecarPath));
    }

    private static string WriteSidecar(string binaryDir, string content)
    {
        var path = Path.Combine(binaryDir, InstallSidecarReader.SidecarFileName);
        File.WriteAllText(path, content);
        return path;
    }

}
