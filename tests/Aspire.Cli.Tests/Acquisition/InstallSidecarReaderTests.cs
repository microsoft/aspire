// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
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

        var reader = CliTestHelper.CreateSidecarReader(outputHelper);
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

        var reader = CliTestHelper.CreateSidecarReader(outputHelper);
        var result = reader.TryRead(workspace.WorkspaceRoot.FullName);

        var notFound = Assert.IsType<InstallSidecarReadResult.NotFound>(result);
        Assert.Equal(expectedPath, notFound.SidecarPath);
    }

    [Fact]
    public void TryRead_ReturnsNotFoundForEmptyBinaryDir()
    {
        var reader = CliTestHelper.CreateSidecarReader(outputHelper);

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

            var reader = CliTestHelper.CreateSidecarReader(outputHelper);
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

        var reader = CliTestHelper.CreateSidecarReader(outputHelper);
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

        var reader = CliTestHelper.CreateSidecarReader(outputHelper);
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

        var reader = CliTestHelper.CreateSidecarReader(outputHelper);
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

        var reader = CliTestHelper.CreateSidecarReader(outputHelper);
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

    [Fact]
    public void TryRead_HandlesUtf8Bom_ReturnsOk()
    {
        // `localhive.ps1` writes the sidecar via `Set-Content -Encoding UTF8`,
        // which on Windows PowerShell 5.x prepends a UTF-8 BOM (0xEF 0xBB 0xBF).
        // `JsonDocument.Parse` tolerates the BOM today; pin that behavior so a
        // future parser change does not silently break sidecars planted by the
        // legacy PS 5.x writer.
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var sidecarPath = Path.Combine(workspace.WorkspaceRoot.FullName, InstallSidecarReader.SidecarFileName);
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("{\"source\":\"script\"}")).ToArray();
        File.WriteAllBytes(sidecarPath, bytes);

        var reader = CliTestHelper.CreateSidecarReader(outputHelper);
        var result = reader.TryRead(workspace.WorkspaceRoot.FullName);

        var ok = Assert.IsType<InstallSidecarReadResult.Ok>(result);
        Assert.Equal(InstallSource.Script, ok.Info.Source);
        Assert.Equal("script", ok.Info.RawSource);
    }

    [Fact]
    public void ReadSourceField_HandlesUtf8Bom_ReturnsScript()
    {
        // Same Windows PowerShell 5.x BOM scenario as TryRead_HandlesUtf8Bom_ReturnsOk,
        // but exercising the lightweight ReadSourceField path.
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var sidecarPath = Path.Combine(workspace.WorkspaceRoot.FullName, InstallSidecarReader.SidecarFileName);
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("{\"source\":\"script\"}")).ToArray();
        File.WriteAllBytes(sidecarPath, bytes);

        Assert.Equal("script", InstallSidecarReader.ReadSourceField(sidecarPath));
    }

    [Fact]
    public void TryRead_IgnoresUnknownAdditionalFields_ReturnsOk()
    {
        // The sidecar contract reserves room for future fields. Older parents
        // must ignore unknown properties (and nested shapes) rather than reject
        // them, so a newer CLI can extend the sidecar without breaking
        // discovery on older installs.
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        WriteSidecar(workspace.WorkspaceRoot.FullName, "{\"source\":\"script\",\"futureField\":\"value\",\"nested\":{\"a\":1,\"b\":[1,2]}}");

        var reader = CliTestHelper.CreateSidecarReader(outputHelper);
        var result = reader.TryRead(workspace.WorkspaceRoot.FullName);

        var ok = Assert.IsType<InstallSidecarReadResult.Ok>(result);
        Assert.Equal(InstallSource.Script, ok.Info.Source);
        Assert.Equal("script", ok.Info.RawSource);
    }

    [Fact]
    public void TryRead_PopulatesIdentityFields_WhenPresent()
    {
        // The identity overrides (channel/version/commit) plus the optional
        // NuGet service-index override all parse straight off the sidecar.
        // Older sidecars omit these fields; the resolver layer applies its
        // own fallback when they are null, so the reader's contract is just
        // "round-trip what's on disk".
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        WriteSidecar(
            workspace.WorkspaceRoot.FullName,
            """
            {
              "source": "script",
              "channel": "staging",
              "version": "13.5.0-preview.1.99999.1",
              "commit": "deadbeef",
              "nugetServiceIndexOverride": "http://localhost:5400/v3/index.json"
            }
            """);

        var reader = CliTestHelper.CreateSidecarReader(outputHelper);
        var ok = Assert.IsType<InstallSidecarReadResult.Ok>(reader.TryRead(workspace.WorkspaceRoot.FullName));

        Assert.Equal(InstallSource.Script, ok.Info.Source);
        Assert.Equal("staging", ok.Info.Channel);
        Assert.Equal("13.5.0-preview.1.99999.1", ok.Info.Version);
        Assert.Equal("deadbeef", ok.Info.Commit);
        Assert.Equal("http://localhost:5400/v3/index.json", ok.Info.NuGetServiceIndexOverride);
    }

    [Fact]
    public void TryRead_IdentityFieldsDefaultToNull_WhenAbsent()
    {
        // Source-only sidecar (the shape shipped before the identity sidecar
        // work) must continue to parse cleanly with all identity fields null.
        // This is the bytes-on-disk compatibility guarantee that lets older
        // installers and parents coexist with the new resolver.
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        WriteSidecar(workspace.WorkspaceRoot.FullName, "{\"source\":\"script\"}");

        var reader = CliTestHelper.CreateSidecarReader(outputHelper);
        var ok = Assert.IsType<InstallSidecarReadResult.Ok>(reader.TryRead(workspace.WorkspaceRoot.FullName));

        Assert.Equal(InstallSource.Script, ok.Info.Source);
        Assert.Null(ok.Info.Channel);
        Assert.Null(ok.Info.Version);
        Assert.Null(ok.Info.Commit);
        Assert.Null(ok.Info.NuGetServiceIndexOverride);
    }

    private static string WriteSidecar(string binaryDir, string content)
    {
        var path = Path.Combine(binaryDir, InstallSidecarReader.SidecarFileName);
        File.WriteAllText(path, content);
        return path;
    }

}
