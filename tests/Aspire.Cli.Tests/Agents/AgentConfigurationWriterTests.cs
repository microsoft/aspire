// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using System.Text.Json.Nodes;
using Aspire.Cli.Agents;
using Aspire.Cli.Resources;
using Aspire.Cli.Tests.TestServices;
using Microsoft.AspNetCore.InternalTesting;

namespace Aspire.Cli.Tests.Agents;

public class AgentConfigurationWriterTests(ITestOutputHelper output) : IDisposable
{
    private readonly AgentConfigurationTestContext _context = new(output);

    private readonly TestAgentEnvironmentScanner _copilot = new("copilot", AgentCommandStrings.Environment_Copilot);

    [Fact]
    public async Task GroupedEdits_AreNotWrittenUntilEveryContributionHasRun()
    {
        var path = Path.Combine(_context.Project.FullName, "settings.json");
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

        var results = await _context.Writer.ApplyAsync([first, second], CancellationToken.None).DefaultTimeout();

        Assert.All(results, result => Assert.Equal(AgentConfigurationStatus.Configured, result.Status));
        var settings = Assert.IsType<JsonObject>(JsonNode.Parse(await File.ReadAllTextAsync(path).DefaultTimeout()));
        Assert.Equal(["preserved", "first", "second"], settings.Select(property => property.Key));
        Assert.True(settings["preserved"]!.GetValue<bool>());
        Assert.Equal(1, settings["first"]!.GetValue<int>());
        Assert.Equal(2, settings["second"]!.GetValue<int>());
    }

    [Fact]
    public async Task SemanticNoOp_PreservesBomCommentsFormattingAndModificationTime()
    {
        var path = Path.Combine(_context.Project.FullName, "settings.json");
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("{\r\n // keep\r\n \"enabled\": true,\r\n}\r\n")).ToArray();
        await File.WriteAllBytesAsync(path, bytes).DefaultTimeout();
        File.SetLastWriteTimeUtc(path, new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var timestamp = File.GetLastWriteTimeUtc(path);
        var target = Target(path, "enabled", (root, _) =>
        {
            root["enabled"] = true;
            return Task.FromResult(AgentConfigurationEdit.Applied("test"));
        });

        var results = await _context.Writer.ApplyAsync([target], CancellationToken.None).DefaultTimeout();

        Assert.Equal(AgentConfigurationStatus.Unchanged, Assert.Single(results).Status);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path).DefaultTimeout());
        Assert.Equal(timestamp, File.GetLastWriteTimeUtc(path));
    }

    [Fact]
    public async Task BlockedMutation_DoesNotLeakPartiallyMutatedState()
    {
        var path = Path.Combine(_context.Project.FullName, "settings.json");
        const string existing = """{"preserved":true}""";
        await AgentConfigurationTestContext.WriteAsync(path, existing).DefaultTimeout();
        var target = Target(path, "blocked", (root, _) =>
        {
            root["shouldNotPersist"] = true;
            return Task.FromResult(AgentConfigurationEdit.Blocked("test"));
        });

        var results = await _context.Writer.ApplyAsync([target], CancellationToken.None).DefaultTimeout();

        Assert.Equal(AgentConfigurationStatus.Blocked, Assert.Single(results).Status);
        Assert.Equal(existing, await File.ReadAllTextAsync(path).DefaultTimeout());
    }

    [Fact]
    public async Task ConcurrentEdit_IsNotOverwrittenAndStagingFilesAreRemoved()
    {
        var path = Path.Combine(_context.Project.FullName, "settings.json");
        await AgentConfigurationTestContext.WriteAsync(path, "{}").DefaultTimeout();
        const string concurrent = """{"otherWriter":"preserved"}""";
        var target = Target(path, "concurrent", async (root, cancellationToken) =>
        {
            root["ours"] = true;
            await File.WriteAllTextAsync(path, concurrent, cancellationToken);
            return AgentConfigurationEdit.Applied("test");
        });

        var results = await _context.Writer.ApplyAsync([target], CancellationToken.None).DefaultTimeout();

        Assert.Equal(AgentConfigurationStatus.Blocked, Assert.Single(results).Status);
        Assert.Equal(concurrent, await File.ReadAllTextAsync(path).DefaultTimeout());
        Assert.Equal([path], Directory.EnumerateFiles(_context.Project.FullName));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PolicyChangedDuringMutation_PreventsWritingTheDependentTarget(bool destinationExists)
    {
        var path = Path.Combine(_context.Project.FullName, "settings.json");
        var policyPath = Path.Combine(_context.Project.FullName, "policy.json");
        await AgentConfigurationTestContext.WriteAsync(policyPath, "{}").DefaultTimeout();
        const string original = """{"existing":true}""";
        if (destinationExists)
        {
            await File.WriteAllTextAsync(path, original).DefaultTimeout();
        }
        var target = new AgentConfigurationTarget(path, AgentConfigurationScope.Project, AgentAssetKind.AspireSkills,
            [_context.ClaudeCode], "plugin", async (root, mutation, cancellationToken) =>
            {
                await mutation.ReadOptionalAsync(policyPath, cancellationToken);
                root["plugin"] = true;
                await File.WriteAllTextAsync(policyPath, """{"disabled":true}""", cancellationToken);
                return AgentConfigurationEdit.Applied("test");
            });

        var results = await _context.Writer.ApplyAsync([target], CancellationToken.None).DefaultTimeout();

        Assert.Equal(AgentConfigurationStatus.Blocked, Assert.Single(results).Status);
        Assert.Equal(AgentCommandStrings.Configuration_ConcurrentChange, results[0].Message);
        Assert.Equal(destinationExists, File.Exists(path));
        if (destinationExists)
        {
            Assert.Equal(original, await File.ReadAllTextAsync(path).DefaultTimeout());
        }
        Assert.Equal(
            (destinationExists ? new[] { policyPath, path } : [policyPath]).Order(),
            Directory.EnumerateFiles(_context.Project.FullName).Order());
    }

    [Fact]
    public async Task AFileThatBecomesADirectory_DoesNotLeaveStagingFiles()
    {
        var path = Path.Combine(_context.Project.FullName, "settings.json");
        var target = Target(path, "replace", (root, _) =>
        {
            Directory.CreateDirectory(path);
            root["ours"] = true;
            return Task.FromResult(AgentConfigurationEdit.Applied("test"));
        });

        var results = await _context.Writer.ApplyAsync([target], CancellationToken.None).DefaultTimeout();

        Assert.Equal(AgentConfigurationStatus.Failed, Assert.Single(results).Status);
        Assert.True(Directory.Exists(path));
        Assert.Empty(Directory.EnumerateFiles(_context.Project.FullName));
    }

    [Fact]
    public async Task OneUnreadableFile_DoesNotStopIndependentTargets()
    {
        var unreadable = Path.Combine(_context.Project.FullName, "directory.json");
        Directory.CreateDirectory(unreadable);
        var independent = Path.Combine(_context.Project.FullName, "valid.json");

        var results = await _context.Writer.ApplyAsync(
            [AddValue(unreadable), AddValue(independent)], CancellationToken.None).DefaultTimeout();

        Assert.Equal([AgentConfigurationStatus.Failed, AgentConfigurationStatus.Configured], results.Select(result => result.Status));
        Assert.True(File.Exists(independent));
    }

    [Fact]
    public async Task Cancellation_PropagatesWithoutCreatingConfiguration()
    {
        var path = Path.Combine(_context.Project.FullName, "settings.json");
        using var cancellation = new CancellationTokenSource();
        var target = Target(path, "cancel", (root, token) =>
        {
            root["ours"] = true;
            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
            return Task.FromResult(AgentConfigurationEdit.Applied("test"));
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _context.Writer.ApplyAsync([target], cancellation.Token)).DefaultTimeout();

        Assert.Empty(Directory.EnumerateFileSystemEntries(_context.Project.FullName));
    }

    [Fact]
    public async Task SymlinkedSettings_AreUpdatedWithoutReplacingTheLinkAndDeduplicated()
    {
        var path = Path.Combine(_context.Project.FullName, "real.json");
        var link = Path.Combine(_context.Project.FullName, "linked.json");
        await AgentConfigurationTestContext.WriteAsync(path, "{}").DefaultTimeout();
        TestSymlinkHelper.TryCreateSymlink(link, path, isDirectory: false);
        var originalLink = new FileInfo(link).LinkTarget;
        var alias = AddValue(link) with { Environments = [_copilot], Scope = AgentConfigurationScope.User };

        var results = await _context.Writer.ApplyAsync([AddValue(path), alias], CancellationToken.None).DefaultTimeout();

        var result = Assert.Single(results);
        Assert.Equal(AgentConfigurationStatus.Configured, result.Status);
        Assert.Equal(AgentConfigurationScope.User, result.Scope);
        Assert.Equal([_copilot], result.Environments);
        Assert.Equal(originalLink, new FileInfo(link).LinkTarget);
        Assert.Equal(await File.ReadAllTextAsync(path).DefaultTimeout(), await File.ReadAllTextAsync(link).DefaultTimeout());
    }

    [Fact]
    public async Task SymlinkedParentDirectories_AreDeduplicated()
    {
        var directory = _context.Workspace.CreateDirectory("real-directory");
        var link = Path.Combine(_context.Project.FullName, "linked-directory");
        TestSymlinkHelper.TryCreateSymlink(link, directory.FullName);
        var realPath = Path.Combine(directory.FullName, "settings.json");
        var linkedPath = Path.Combine(link, "settings.json");

        var results = await _context.Writer.ApplyAsync([AddValue(realPath), AddValue(linkedPath)], CancellationToken.None).DefaultTimeout();

        Assert.Equal(AgentConfigurationStatus.Configured, Assert.Single(results).Status);
        Assert.NotNull(new DirectoryInfo(link).LinkTarget);
        Assert.True(File.Exists(realPath));
    }

    [Fact]
    public async Task SymlinkRetargetedDuringMutation_IsNotFollowedByTheWrite()
    {
        var original = Path.Combine(_context.Project.FullName, "original.json");
        var replacement = Path.Combine(_context.Project.FullName, "replacement.json");
        var link = Path.Combine(_context.Project.FullName, "linked.json");
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

        var results = await _context.Writer.ApplyAsync([target], CancellationToken.None).DefaultTimeout();

        Assert.Equal(AgentConfigurationStatus.Blocked, Assert.Single(results).Status);
        Assert.Equal("{}", await File.ReadAllTextAsync(original).DefaultTimeout());
        Assert.Equal("""{"replacement":true}""", await File.ReadAllTextAsync(replacement).DefaultTimeout());
        Assert.Empty(Directory.EnumerateFiles(_context.Project.FullName, "*.tmp"));
    }

    [Fact]
    public async Task ChangedFiles_PreserveUnixPermissions()
    {
        if (!OperatingSystem.IsWindows())
        {
                var path = Path.Combine(_context.Project.FullName, "settings.json");
            await AgentConfigurationTestContext.WriteAsync(path, "{}").DefaultTimeout();
            var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite;
            File.SetUnixFileMode(path, mode);

            var results = await _context.Writer.ApplyAsync([AddValue(path)], CancellationToken.None).DefaultTimeout();

            Assert.Equal(AgentConfigurationStatus.Configured, Assert.Single(results).Status);
            Assert.Equal(mode, File.GetUnixFileMode(path));
        }
        else
        {
            Assert.Skip("Unix file modes are unavailable on Windows.");
        }
    }

    private AgentConfigurationTarget AddValue(string path)
        => Target(path, "value", (root, _) =>
        {
            root["value"] = true;
            return Task.FromResult(AgentConfigurationEdit.Applied("test"));
        });

    private AgentConfigurationTarget Target(string path, string entry, Func<JsonObject, CancellationToken, Task<AgentConfigurationEdit>> apply)
        => new(path, AgentConfigurationScope.Project, AgentAssetKind.AspireSkills, [_copilot], entry,
            (root, _, cancellationToken) => apply(root, cancellationToken));
    public void Dispose() => _context.Dispose();

}
