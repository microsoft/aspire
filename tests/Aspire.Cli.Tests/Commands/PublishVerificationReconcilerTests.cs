// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.Cli.Commands;

namespace Aspire.Cli.Tests.Commands;

public class PublishVerificationReconcilerTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task ReconcileAsync_ExactTextMatch_NormalizesStagedAndLogicalPaths()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var staging = workspace.WorkspaceRoot.CreateSubdirectory("staging");
        var target = workspace.WorkspaceRoot.CreateSubdirectory("target");
        var stagedFile = Path.Combine(staging.FullName, "artifact.json");
        var targetFile = Path.Combine(target.FullName, "artifact.json");
        await File.WriteAllTextAsync(
            stagedFile,
            JsonSerializer.Serialize(new { path = Path.Combine(staging.FullName, "nested") }));
        await File.WriteAllTextAsync(
            targetFile,
            JsonSerializer.Serialize(new { path = Path.Combine(target.FullName, "nested") }));
        var output = CreateDirectoryOutput(staging.FullName, target.FullName);

        var result = await ReconcileAsync(
            workspace,
            [output],
            [targetFile],
            []);

        Assert.False(result.HasDrift);
    }

    [Fact]
    public async Task ReconcileAsync_ReportsAllCategoriesInStableOrder()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var firstStaging = workspace.WorkspaceRoot.CreateSubdirectory("first-staging");
        var firstTarget = workspace.WorkspaceRoot.CreateSubdirectory("z-target");
        var secondStaging = workspace.WorkspaceRoot.CreateSubdirectory("second-staging");
        var secondTarget = workspace.WorkspaceRoot.CreateSubdirectory("a-target");

        await File.WriteAllTextAsync(Path.Combine(firstStaging.FullName, "stale.txt"), "new");
        await File.WriteAllTextAsync(Path.Combine(firstTarget.FullName, "stale.txt"), "old");
        await File.WriteAllTextAsync(Path.Combine(firstStaging.FullName, "missing.txt"), "new");
        await File.WriteAllTextAsync(Path.Combine(firstTarget.FullName, "orphaned.txt"), "old");
        await File.WriteAllTextAsync(Path.Combine(secondStaging.FullName, "b.txt"), "new");
        await File.WriteAllTextAsync(Path.Combine(secondTarget.FullName, "b.txt"), "old");
        await File.WriteAllTextAsync(Path.Combine(secondStaging.FullName, "a.txt"), "new");
        await File.WriteAllTextAsync(Path.Combine(secondTarget.FullName, "a.txt"), "old");

        PublishVerificationOutput[] outputs =
        [
            CreateDirectoryOutput(firstStaging.FullName, firstTarget.FullName, "first"),
            CreateDirectoryOutput(secondStaging.FullName, secondTarget.FullName, "second")
        ];
        var includedFiles = Directory
            .EnumerateFiles(firstTarget.FullName)
            .Concat(Directory.EnumerateFiles(secondTarget.FullName))
            .ToArray();

        var result = await ReconcileAsync(workspace, outputs, includedFiles, []);

        Assert.True(result.HasDrift);
        Assert.Collection(
            result.Groups,
            group =>
            {
                Assert.Equal("a-target", group.Destination);
                Assert.Equal(["a.txt", "b.txt"], group.StaleFiles);
                Assert.Empty(group.MissingFiles);
                Assert.Empty(group.OrphanedFiles);
            },
            group =>
            {
                Assert.Equal("z-target", group.Destination);
                Assert.Equal(["stale.txt"], group.StaleFiles);
                Assert.Equal(["missing.txt"], group.MissingFiles);
                Assert.Equal(["orphaned.txt"], group.OrphanedFiles);
            });
    }

    [Fact]
    public async Task ReconcileAsync_IgnoredAbsentGeneratedFile_IsExcluded()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var staging = workspace.WorkspaceRoot.CreateSubdirectory("staging");
        var target = Path.Combine(workspace.WorkspaceRoot.FullName, "target");
        var stagedFile = Path.Combine(staging.FullName, "ignored.bin");
        var targetFile = Path.Combine(target, "ignored.bin");
        await File.WriteAllBytesAsync(stagedFile, [0, 1, 2, 3]);
        var output = CreateDirectoryOutput(staging.FullName, target);

        var result = await ReconcileAsync(
            workspace,
            [output],
            [],
            [targetFile]);

        Assert.False(result.HasDrift);
    }

    [Fact]
    public async Task ReconcileAsync_BinaryFiles_AreComparedByteForByte()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var staging = workspace.WorkspaceRoot.CreateSubdirectory("staging");
        var target = workspace.WorkspaceRoot.CreateSubdirectory("target");
        var stagedFile = Path.Combine(staging.FullName, "artifact.bin");
        var targetFile = Path.Combine(target.FullName, "artifact.bin");
        await File.WriteAllBytesAsync(stagedFile, [0, 1, 2, 3]);
        await File.WriteAllBytesAsync(targetFile, [0, 1, 2, 4]);
        var output = CreateDirectoryOutput(staging.FullName, target.FullName);

        var result = await ReconcileAsync(
            workspace,
            [output],
            [targetFile],
            []);

        var group = Assert.Single(result.Groups);
        Assert.Equal(["artifact.bin"], group.StaleFiles);
    }

    [Fact]
    public void NormalizeText_ReplacesLongestNativeForwardJsonAndUriPrefixes()
    {
        var root = Path.GetFullPath(Path.Combine("repo", "output"));
        var nested = Path.Combine(root, "nested");
        var staged = Path.GetFullPath(Path.Combine("temp", "staged"));
        PublishVerificationOutput[] outputs =
        [
            CreateDirectoryOutput(staged, root, "root"),
            CreateDirectoryOutput(Path.Combine(staged, "nested"), nested, "nested")
        ];
        var comparer = new PublishVerificationContentComparer(outputs);
        var jsonEscaped = JsonEncodedText.Encode(Path.Combine(staged, "nested", "file.json")).ToString();
        var uri = new Uri(Path.Combine(nested, "file.json")).AbsoluteUri;
        var text = $"{Path.Combine(staged, "nested", "file.json")}\n{jsonEscaped}\n{uri}";

        var normalized = comparer.NormalizeText(text);

        Assert.Equal(3, CountOccurrences(normalized, "aspire-output://named/publisher/nested"));
        Assert.DoesNotContain(staged, normalized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(nested, normalized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CommandFormatter_QuotesArgumentsAndRedactsSecrets()
    {
        var command = PublishVerificationCommandFormatter.Format(
        [
            "aspire",
            "publish",
            "--output-path",
            "path with spaces",
            "--api-key",
            "secret-value",
            "--password=hunter2"
        ]);

        Assert.Contains("'path with spaces'", command);
        Assert.Contains("--api-key '<redacted>'", command);
        Assert.Contains("'--password=<redacted>'", command);
        Assert.DoesNotContain("secret-value", command);
        Assert.DoesNotContain("hunter2", command);
    }

    [Fact]
    public void PublishVerificationFailed_HasDedicatedExitCode()
    {
        Assert.Equal(23, CliExitCodes.PublishVerificationFailed);
        Assert.NotEqual(CliExitCodes.FailedToBuildArtifacts, CliExitCodes.PublishVerificationFailed);
    }

    [Fact]
    public void DisplayFormatter_EscapesTerminalControlCharacters()
    {
        var escaped = PublishVerificationDisplayFormatter.EscapePath("line\nbreak\t\u001B[31m.txt");

        Assert.Equal(@"line\nbreak\t\u001B[31m.txt", escaped);
    }

    private static PublishVerificationOutput CreateDirectoryOutput(
        string staging,
        string target,
        string name = "output")
    {
        return new PublishVerificationOutput(
            false,
            "publisher",
            name,
            PublishVerificationOutputKind.Directory,
            staging,
            target);
    }

    private static async Task<PublishVerificationResult> ReconcileAsync(
        TemporaryWorkspace workspace,
        PublishVerificationOutput[] outputs,
        string[] includedFiles,
        string[] ignoredFiles)
    {
        var inventory = PublishVerificationReconciler.CreateGeneratedInventory(outputs);
        return await PublishVerificationReconciler.ReconcileAsync(
            workspace.WorkspaceRoot.FullName,
            outputs,
            inventory,
            includedFiles.ToHashSet(PublishVerificationPathSafety.PathComparer),
            ignoredFiles.ToHashSet(PublishVerificationPathSafety.PathComparer),
            TestContext.Current.CancellationToken);
    }

    private static int CountOccurrences(string value, string search)
    {
        var count = 0;
        var index = 0;
        while ((index = value.IndexOf(search, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += search.Length;
        }

        return count;
    }
}
