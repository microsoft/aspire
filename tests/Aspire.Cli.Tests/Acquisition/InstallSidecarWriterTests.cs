// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.Cli.Acquisition;

namespace Aspire.Cli.Tests.Acquisition;

public class InstallSidecarWriterTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public void UpdateForSelfUpdate_UpdatesChannelRemovesExecutableIdentityAndPreservesOtherFields()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var sidecarPath = Path.Combine(workspace.Path, InstallSidecarReader.SidecarFileName);
        File.WriteAllText(
            sidecarPath,
            """
            {
              "source": "script",
              "channel": "stable",
              "version": "13.5.0",
              "commit": "0123456789abcdef",
              "futureField": { "enabled": true }
            }
            """);

        InstallSidecarWriter.UpdateForSelfUpdate(workspace.Path, "staging");

        using var document = JsonDocument.Parse(File.ReadAllBytes(sidecarPath));
        Assert.Equal("script", document.RootElement.GetProperty("source").GetString());
        Assert.Equal("staging", document.RootElement.GetProperty("channel").GetString());
        Assert.False(document.RootElement.TryGetProperty("version", out _));
        Assert.False(document.RootElement.TryGetProperty("commit", out _));
        Assert.True(document.RootElement.GetProperty("futureField").GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public void UpdateForSelfUpdate_WhenSidecarIsMissing_LeavesSidecarAbsent()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        InstallSidecarWriter.UpdateForSelfUpdate(workspace.Path, "daily");

        var sidecarPath = Path.Combine(workspace.Path, InstallSidecarReader.SidecarFileName);
        Assert.False(File.Exists(sidecarPath));
    }

    [Fact]
    public void UpdateForSelfUpdate_WhenSidecarIsMalformed_PreservesOriginalContent()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var sidecarPath = Path.Combine(workspace.Path, InstallSidecarReader.SidecarFileName);
        const string malformedContent = """{"source":"script","channel":""";
        File.WriteAllText(sidecarPath, malformedContent);

        Assert.ThrowsAny<JsonException>(() => InstallSidecarWriter.UpdateForSelfUpdate(workspace.Path, "staging"));

        Assert.Equal(malformedContent, File.ReadAllText(sidecarPath));
        Assert.Empty(Directory.GetFiles(workspace.Path, $"{InstallSidecarReader.SidecarFileName}.*.tmp"));
    }
}
