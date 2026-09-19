// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using System.Text.Json.Nodes;
using Aspire.Cli.Agents;
using Aspire.Cli.Agents.Configuration;
using Aspire.Cli.Tests.TestServices;
using Microsoft.AspNetCore.InternalTesting;

namespace Aspire.Cli.Tests.Agents;

public class AgentConfigurationWriterTests(ITestOutputHelper output)
{
    [Fact]
    public async Task GroupedEdits_AreNotWrittenUntilEveryContributionHasRun()
    {
        using var context = new AgentConfigurationTestContext(output);
        var path = Path.Combine(context.Project.FullName, "settings.json");
        const string existing = """{/* JSONC */"preserved":true,}""";
        await AgentConfigurationTestContext.WriteAsync(path, existing).DefaultTimeout();
        var first = Target(path, "first", (root, _) =>
        {
            root["first"] = 1;
            return Task.FromResult(AgentConfigurationEdit.Applied("test"));
        });
        var second = Target(path, "second", async (root, cancellationToken) =>
        {
            Assert.Equal(1, (int)root["first"]!);
            Assert.Equal(existing, await File.ReadAllTextAsync(path, cancellationToken));
            root["second"] = 2;
            return AgentConfigurationEdit.Applied("test");
        });

        var results = await context.Writer.ApplyAsync([first, second], CancellationToken.None).DefaultTimeout();

        Assert.All(results, result => Assert.Equal(AgentConfigurationStatus.Configured, result.Status));
        await Verify(await File.ReadAllTextAsync(path).DefaultTimeout(), "json");
    }

    [Fact]
    public async Task SemanticNoOp_PreservesBomCommentsFormattingAndModificationTime()
    {
        using var context = new AgentConfigurationTestContext(output);
        var path = Path.Combine(context.Project.FullName, "settings.json");
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("{\r\n // keep\r\n \"enabled\": true,\r\n}\r\n")).ToArray();
        await File.WriteAllBytesAsync(path, bytes).DefaultTimeout();
        File.SetLastWriteTimeUtc(path, new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var timestamp = File.GetLastWriteTimeUtc(path);
        var target = Target(path, "enabled", (root, _) =>
        {
            root["enabled"] = true;
            return Task.FromResult(AgentConfigurationEdit.Applied("test"));
        });

        var results = await context.Writer.ApplyAsync([target], CancellationToken.None).DefaultTimeout();

        Assert.Equal(AgentConfigurationStatus.Unchanged, Assert.Single(results).Status);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path).DefaultTimeout());
        Assert.Equal(timestamp, File.GetLastWriteTimeUtc(path));
    }

    [Fact]
    public async Task BlockedMutation_DoesNotLeakPartiallyMutatedState()
    {
        using var context = new AgentConfigurationTestContext(output);
        var path = Path.Combine(context.Project.FullName, "settings.json");
        const string existing = """{"preserved":true}""";
        await AgentConfigurationTestContext.WriteAsync(path, existing).DefaultTimeout();
        var target = Target(path, "blocked", (root, _) =>
        {
            root["shouldNotPersist"] = true;
            return Task.FromResult(AgentConfigurationEdit.Blocked("test"));
        });

        var results = await context.Writer.ApplyAsync([target], CancellationToken.None).DefaultTimeout();

        Assert.Equal(AgentConfigurationStatus.Blocked, Assert.Single(results).Status);
        Assert.Equal(existing, await File.ReadAllTextAsync(path).DefaultTimeout());
    }

    [Fact]
    public async Task ConcurrentEdit_IsNotOverwrittenAndStagingFilesAreRemoved()
    {
        using var context = new AgentConfigurationTestContext(output);
        var path = Path.Combine(context.Project.FullName, "settings.json");
        await AgentConfigurationTestContext.WriteAsync(path, "{}").DefaultTimeout();
        const string concurrent = """{"otherWriter":"preserved"}""";
        var target = Target(path, "concurrent", async (root, cancellationToken) =>
        {
            root["ours"] = true;
            await File.WriteAllTextAsync(path, concurrent, cancellationToken);
            return AgentConfigurationEdit.Applied("test");
        });

        var results = await context.Writer.ApplyAsync([target], CancellationToken.None).DefaultTimeout();

        Assert.Equal(AgentConfigurationStatus.Blocked, Assert.Single(results).Status);
        Assert.Equal(concurrent, await File.ReadAllTextAsync(path).DefaultTimeout());
        Assert.Equal([path], Directory.EnumerateFiles(context.Project.FullName));
    }

    [Fact]
    public async Task PolicyChangedDuringMutation_PreventsWritingTheDependentTarget()
    {
        using var context = new AgentConfigurationTestContext(output);
        var path = Path.Combine(context.Project.FullName, "settings.json");
        var policyPath = Path.Combine(context.Project.FullName, "policy.json");
        await AgentConfigurationTestContext.WriteAsync(policyPath, "{}").DefaultTimeout();
        var target = new AgentConfigurationTarget(path, AgentConfigurationScope.Project, AgentAssetKind.AspireSkills,
            [AgentClientKind.ClaudeCode], "plugin", async (root, mutation, cancellationToken) =>
            {
                await mutation.ReadOptionalAsync(policyPath, cancellationToken);
                root["plugin"] = true;
                await File.WriteAllTextAsync(policyPath, """{"disabled":true}""", cancellationToken);
                return AgentConfigurationEdit.Applied("test");
            });

        var results = await context.Writer.ApplyAsync([target], CancellationToken.None).DefaultTimeout();

        Assert.Equal(AgentConfigurationStatus.Blocked, Assert.Single(results).Status);
        Assert.False(File.Exists(path));
        Assert.Equal([policyPath], Directory.EnumerateFiles(context.Project.FullName));
    }

    [Fact]
    public async Task AFileThatBecomesADirectory_DoesNotLeaveStagingFiles()
    {
        using var context = new AgentConfigurationTestContext(output);
        var path = Path.Combine(context.Project.FullName, "settings.json");
        var target = Target(path, "replace", (root, _) =>
        {
            Directory.CreateDirectory(path);
            root["ours"] = true;
            return Task.FromResult(AgentConfigurationEdit.Applied("test"));
        });

        var results = await context.Writer.ApplyAsync([target], CancellationToken.None).DefaultTimeout();

        Assert.Equal(AgentConfigurationStatus.Failed, Assert.Single(results).Status);
        Assert.True(Directory.Exists(path));
        Assert.Empty(Directory.EnumerateFiles(context.Project.FullName));
    }

    [Fact]
    public async Task OneUnreadableFile_DoesNotStopIndependentTargets()
    {
        using var context = new AgentConfigurationTestContext(output);
        var unreadable = Path.Combine(context.Project.FullName, "directory.json");
        Directory.CreateDirectory(unreadable);
        var independent = Path.Combine(context.Project.FullName, "valid.json");

        var results = await context.Writer.ApplyAsync(
            [AddValue(unreadable), AddValue(independent)], CancellationToken.None).DefaultTimeout();

        Assert.Equal([AgentConfigurationStatus.Failed, AgentConfigurationStatus.Configured], results.Select(result => result.Status));
        Assert.True(File.Exists(independent));
    }

    [Fact]
    public async Task Cancellation_PropagatesWithoutCreatingConfiguration()
    {
        using var context = new AgentConfigurationTestContext(output);
        var path = Path.Combine(context.Project.FullName, "settings.json");
        using var cancellation = new CancellationTokenSource();
        var target = Target(path, "cancel", (root, token) =>
        {
            root["ours"] = true;
            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
            return Task.FromResult(AgentConfigurationEdit.Applied("test"));
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            context.Writer.ApplyAsync([target], cancellation.Token)).DefaultTimeout();

        Assert.Empty(Directory.EnumerateFileSystemEntries(context.Project.FullName));
    }

    [Fact]
    public async Task SymlinkedSettings_AreUpdatedWithoutReplacingTheLinkAndDeduplicated()
    {
        using var context = new AgentConfigurationTestContext(output);
        var path = Path.Combine(context.Project.FullName, "real.json");
        var link = Path.Combine(context.Project.FullName, "linked.json");
        await AgentConfigurationTestContext.WriteAsync(path, "{}").DefaultTimeout();
        TestSymlinkHelper.TryCreateSymlink(link, path, isDirectory: false);
        var originalLink = new FileInfo(link).LinkTarget;
        var alias = AddValue(link) with { Clients = [AgentClientKind.CopilotApp], Scope = AgentConfigurationScope.User };

        var results = await context.Writer.ApplyAsync([AddValue(path), alias], CancellationToken.None).DefaultTimeout();

        var result = Assert.Single(results);
        Assert.Equal(AgentConfigurationStatus.Configured, result.Status);
        Assert.Equal(AgentConfigurationScope.User, result.Scope);
        Assert.Equal([AgentClientKind.CopilotCli, AgentClientKind.CopilotApp], result.Clients);
        Assert.Equal(originalLink, new FileInfo(link).LinkTarget);
        Assert.Equal(await File.ReadAllTextAsync(path).DefaultTimeout(), await File.ReadAllTextAsync(link).DefaultTimeout());
    }

    [Fact]
    public async Task SymlinkedParentDirectories_AreDeduplicated()
    {
        using var context = new AgentConfigurationTestContext(output);
        var directory = context.Workspace.CreateDirectory("real-directory");
        var link = Path.Combine(context.Project.FullName, "linked-directory");
        TestSymlinkHelper.TryCreateSymlink(link, directory.FullName);
        var realPath = Path.Combine(directory.FullName, "settings.json");
        var linkedPath = Path.Combine(link, "settings.json");

        var results = await context.Writer.ApplyAsync([AddValue(realPath), AddValue(linkedPath)], CancellationToken.None).DefaultTimeout();

        Assert.Equal(AgentConfigurationStatus.Configured, Assert.Single(results).Status);
        Assert.NotNull(new DirectoryInfo(link).LinkTarget);
        Assert.True(File.Exists(realPath));
    }

    [Fact]
    public async Task SymlinkRetargetedDuringMutation_IsNotFollowedByTheWrite()
    {
        using var context = new AgentConfigurationTestContext(output);
        var original = Path.Combine(context.Project.FullName, "original.json");
        var replacement = Path.Combine(context.Project.FullName, "replacement.json");
        var link = Path.Combine(context.Project.FullName, "linked.json");
        await AgentConfigurationTestContext.WriteAsync(original, "{}").DefaultTimeout();
        await AgentConfigurationTestContext.WriteAsync(replacement, """{"replacement":true}""").DefaultTimeout();
        TestSymlinkHelper.TryCreateSymlink(link, original, isDirectory: false);
        var target = Target(link, "retarget", (root, _) =>
        {
            File.Delete(link);
            TestSymlinkHelper.TryCreateSymlink(link, replacement, isDirectory: false);
            root["ours"] = true;
            return Task.FromResult(AgentConfigurationEdit.Applied("test"));
        });

        var results = await context.Writer.ApplyAsync([target], CancellationToken.None).DefaultTimeout();

        Assert.Equal(AgentConfigurationStatus.Blocked, Assert.Single(results).Status);
        Assert.Equal("{}", await File.ReadAllTextAsync(original).DefaultTimeout());
        Assert.Equal("""{"replacement":true}""", await File.ReadAllTextAsync(replacement).DefaultTimeout());
        Assert.Empty(Directory.EnumerateFiles(context.Project.FullName, "*.tmp"));
    }

    [Fact]
    public async Task ChangedFiles_PreserveUnixPermissions()
    {
        if (!OperatingSystem.IsWindows())
        {
            using var context = new AgentConfigurationTestContext(output);
            var path = Path.Combine(context.Project.FullName, "settings.json");
            await AgentConfigurationTestContext.WriteAsync(path, "{}").DefaultTimeout();
            var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite;
            File.SetUnixFileMode(path, mode);

            var results = await context.Writer.ApplyAsync([AddValue(path)], CancellationToken.None).DefaultTimeout();

            Assert.Equal(AgentConfigurationStatus.Configured, Assert.Single(results).Status);
            Assert.Equal(mode, File.GetUnixFileMode(path));
        }
        else
        {
            Assert.Skip("Unix file modes are unavailable on Windows.");
        }
    }

    private static AgentConfigurationTarget AddValue(string path)
        => Target(path, "value", (root, _) =>
        {
            root["value"] = true;
            return Task.FromResult(AgentConfigurationEdit.Applied("test"));
        });

    private static AgentConfigurationTarget Target(string path, string entry, Func<JsonObject, CancellationToken, Task<AgentConfigurationEdit>> apply)
        => new(path, AgentConfigurationScope.Project, AgentAssetKind.AspireSkills, [AgentClientKind.CopilotCli], entry,
            (root, _, cancellationToken) => apply(root, cancellationToken));
}
