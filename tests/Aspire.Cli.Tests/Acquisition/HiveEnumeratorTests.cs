// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Acquisition;
using Aspire.Cli.Tests.Utils;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.Acquisition;

public class HiveEnumeratorTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public void EnumerateHives_ReturnsDirect_ChildDirectories()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var context = workspace.CreateExecutionContext();
        var hivesDir = context.HivesDirectory.FullName;
        Directory.CreateDirectory(Path.Combine(hivesDir, "stable"));
        Directory.CreateDirectory(Path.Combine(hivesDir, "staging"));

        var enumerator = new HiveEnumerator(context, NullLogger<HiveEnumerator>.Instance);
        var hives = enumerator.EnumerateHives().OrderBy(h => h.Name).ToList();

        Assert.Equal(2, hives.Count);
        Assert.Equal("stable", hives[0].Name);
        Assert.Equal(Path.Combine(hivesDir, "stable"), hives[0].Path);
        Assert.Equal("staging", hives[1].Name);
        Assert.Equal(Path.Combine(hivesDir, "staging"), hives[1].Path);
    }

    [Fact]
    public void EnumerateHives_ExcludesNestedChildrenAndRootFiles()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var context = workspace.CreateExecutionContext();
        var hivesDir = context.HivesDirectory.FullName;
        Directory.CreateDirectory(Path.Combine(hivesDir, "stable"));
        // Nested subdirectory — must not appear in output.
        Directory.CreateDirectory(Path.Combine(hivesDir, "stable", "nested"));
        // File at hives root — must not appear in output.
        File.WriteAllText(Path.Combine(hivesDir, "some-file.txt"), "data");

        var enumerator = new HiveEnumerator(context, NullLogger<HiveEnumerator>.Instance);
        var hives = enumerator.EnumerateHives().ToList();

        var single = Assert.Single(hives);
        Assert.Equal("stable", single.Name);
    }

    [Fact]
    public void EnumerateHives_WithMissingRoot_ReturnsEmpty()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        // Point the context at a non-existent hives directory.
        var missingHivesDir = new DirectoryInfo(
            Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "hives-does-not-exist"));
        var context = TestExecutionContextHelper.CreateExecutionContext(
            workspace.WorkspaceRoot, hivesDirectory: missingHivesDir);

        var enumerator = new HiveEnumerator(context, NullLogger<HiveEnumerator>.Instance);
        var hives = enumerator.EnumerateHives().ToList();

        Assert.Empty(hives);
    }

    [Fact]
    public void EnumerateHives_RespectsCancellation()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var context = workspace.CreateExecutionContext();
        var hivesDir = context.HivesDirectory.FullName;
        Directory.CreateDirectory(Path.Combine(hivesDir, "stable"));

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var enumerator = new HiveEnumerator(context, NullLogger<HiveEnumerator>.Instance);

        // EnumerateDirectoriesSafe calls ThrowIfCancellationRequested before each MoveNext,
        // so a pre-cancelled token must propagate as OperationCanceledException.
        Assert.Throws<OperationCanceledException>(
            () => enumerator.EnumerateHives(cts.Token).ToList());
    }
}
