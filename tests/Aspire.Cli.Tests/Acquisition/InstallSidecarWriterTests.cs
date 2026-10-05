// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.Cli.Acquisition;

namespace Aspire.Cli.Tests.Acquisition;

public class InstallSidecarWriterTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public void PrepareForSelfUpdate_CommitUpdatesChannelRemovesExecutableIdentityAndPreservesOtherFields()
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

        using var update = InstallSidecarWriter.PrepareForSelfUpdate(workspace.Path, "staging");

        Assert.NotNull(update);
        using (var originalDocument = JsonDocument.Parse(File.ReadAllBytes(sidecarPath)))
        {
            Assert.Equal("stable", originalDocument.RootElement.GetProperty("channel").GetString());
        }

        update.Commit();

        using var document = JsonDocument.Parse(File.ReadAllBytes(sidecarPath));
        Assert.Equal("script", document.RootElement.GetProperty("source").GetString());
        Assert.Equal("staging", document.RootElement.GetProperty("channel").GetString());
        Assert.False(document.RootElement.TryGetProperty("version", out _));
        Assert.False(document.RootElement.TryGetProperty("commit", out _));
        Assert.True(document.RootElement.GetProperty("futureField").GetProperty("enabled").GetBoolean());
        Assert.Empty(Directory.GetFiles(workspace.Path, $"{InstallSidecarReader.SidecarFileName}.*.tmp"));
    }

    [Fact]
    public void PrepareForSelfUpdate_WhenSidecarIsMissing_LeavesSidecarAbsent()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        using var update = InstallSidecarWriter.PrepareForSelfUpdate(workspace.Path, "daily");

        var sidecarPath = Path.Combine(workspace.Path, InstallSidecarReader.SidecarFileName);
        Assert.Null(update);
        Assert.False(File.Exists(sidecarPath));
    }

    [Fact]
    public void PrepareForSelfUpdate_WhenSidecarIsMalformed_PreservesOriginalContent()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var sidecarPath = Path.Combine(workspace.Path, InstallSidecarReader.SidecarFileName);
        const string malformedContent = """{"source":"script","channel":""";
        File.WriteAllText(sidecarPath, malformedContent);

        Assert.ThrowsAny<JsonException>(() => InstallSidecarWriter.PrepareForSelfUpdate(workspace.Path, "staging"));

        Assert.Equal(malformedContent, File.ReadAllText(sidecarPath));
        Assert.Empty(Directory.GetFiles(workspace.Path, $"{InstallSidecarReader.SidecarFileName}.*.tmp"));
    }

    [Fact]
    public void PrepareForSelfUpdate_WhenSerializedSidecarExceedsLimit_PreservesOriginalContent()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var sidecarPath = Path.Combine(workspace.Path, InstallSidecarReader.SidecarFileName);

        // UTF-8 stores each U+00E9 character in two bytes, while Utf8JsonWriter's default encoder
        // emits "\u00E9" as six bytes. This keeps the input below 64 KiB but expands the output above it.
        var originalContent = $"{{\"source\":\"script\",\"futureField\":\"{new string('\u00E9', 11_000)}\"}}";
        File.WriteAllText(sidecarPath, originalContent);
        Assert.InRange(new FileInfo(sidecarPath).Length, 1, InstallSidecarReader.MaxSidecarBytes);

        var exception = Assert.Throws<InvalidDataException>(
            () => InstallSidecarWriter.PrepareForSelfUpdate(workspace.Path, "staging"));

        Assert.Contains("exceeds", exception.Message);
        Assert.Equal(originalContent, File.ReadAllText(sidecarPath));
        Assert.Empty(Directory.GetFiles(workspace.Path, $"{InstallSidecarReader.SidecarFileName}.*.tmp"));
    }

    [Fact]
    public void PrepareForSelfUpdate_DisposeWithoutCommitPreservesOriginalContent()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var sidecarPath = Path.Combine(workspace.Path, InstallSidecarReader.SidecarFileName);
        const string originalContent = """{"source":"script","channel":"stable"}""";
        File.WriteAllText(sidecarPath, originalContent);

        InstallSidecarWriter.PrepareForSelfUpdate(workspace.Path, "staging")!.Dispose();

        Assert.Equal(originalContent, File.ReadAllText(sidecarPath));
        Assert.Empty(Directory.GetFiles(workspace.Path, $"{InstallSidecarReader.SidecarFileName}.*.tmp"));
    }
}
