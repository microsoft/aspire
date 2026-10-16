// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using System.Text.Json.Nodes;
using Aspire.Cli.Agents;
using Aspire.Cli.Resources;
using Microsoft.AspNetCore.InternalTesting;

namespace Aspire.Cli.Tests.Agents;

public class NativePluginRegistrationTests(ITestOutputHelper output) : IDisposable
{
    private readonly AgentConfigurationTestContext _context = new(output);

    [Fact]
    public async Task SemanticNoOp_PreservesBomCommentsFormattingAndModificationTime()
    {
        var path = Path.Combine(_context.CopilotDirectory, "settings.json");
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("""
            {
             // keep
             "extraKnownMarketplaces": {"aspire-skills": {"source": {"source": "github", "repo": "microsoft/aspire-skills"}}},
             "enabledPlugins": {"aspire@aspire-skills": true},
            }
            """)).ToArray();
        Directory.CreateDirectory(_context.CopilotDirectory);
        await File.WriteAllBytesAsync(path, bytes);
        File.SetLastWriteTimeUtc(path, new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var timestamp = File.GetLastWriteTimeUtc(path);

        var results = await _context.ConfigureNativeAsync(AgentConfigurationScope.Global, [_context.Copilot]);

        Assert.Equal(AgentConfigurationStatus.Unchanged, Assert.Single(results).Status);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        Assert.Equal(timestamp, File.GetLastWriteTimeUtc(path));
    }

    [Fact]
    public async Task SharedSettings_ArePublishedOnceAndReportBothClients()
    {
        _context.SetVariable("COPILOT_HOME", _context.ClaudeDirectory);

        var results = await _context.ConfigureNativeAsync(
            AgentConfigurationScope.Global, [_context.Copilot, _context.ClaudeCode]);

        var result = Assert.Single(results);
        Assert.Equal(AgentConfigurationStatus.Configured, result.Status);
        Assert.Equal([_context.Copilot, _context.ClaudeCode], result.Clients);
        Assert.Equal([result.TargetPath], Directory.EnumerateFiles(_context.Home.FullName, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task SharedSettings_MustSatisfyEverySelectedClientsPolicy()
    {
        _context.SetVariable("COPILOT_HOME", _context.ClaudeDirectory);
        var path = Path.Combine(_context.ClaudeDirectory, "settings.json");
        const string original = """{"preserved":true}""";
        await AgentConfigurationTestContext.WriteAsync(path, original);
        await AgentConfigurationTestContext.WriteAsync(
            Path.Combine(_context.ClaudeManagedDirectory, "managed-settings.json"),
            """{"enabledPlugins":{"aspire@aspire-skills":false}}""");

        var results = await _context.ConfigureNativeAsync(
            AgentConfigurationScope.Global, [_context.Copilot, _context.ClaudeCode]);

        var result = Assert.Single(results);
        Assert.Equal(AgentConfigurationStatus.Skipped, result.Status);
        Assert.Equal([_context.Copilot, _context.ClaudeCode], result.Clients);
        Assert.Equal(original, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task OneUnreadableFile_DoesNotStopIndependentClients()
    {
        var unreadable = Path.Combine(_context.Project.FullName, ".github", "copilot", "settings.json");
        Directory.CreateDirectory(unreadable);

        var results = await _context.ConfigureNativeAsync(
            AgentConfigurationScope.Project, [_context.Copilot, _context.ClaudeCode]);

        Assert.Equal([AgentConfigurationStatus.Failed, AgentConfigurationStatus.Configured], results.Select(result => result.Status));
        Assert.True(File.Exists(Path.Combine(_context.Project.FullName, ".claude", "settings.json")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChangedPolicy_PreventsPublication(bool destinationExists)
    {
        var path = Path.Combine(_context.Project.FullName, "settings.json");
        var policy = Path.Combine(_context.Project.FullName, "policy.json");
        const string original = """{"existing":true}""";
        if (destinationExists)
        {
            await File.WriteAllTextAsync(path, original);
        }
        await File.WriteAllTextAsync(policy, "{}");
        var reader = new AgentConfigurationJson.ReadContext();
        var document = await reader.ReadAsync(path, CancellationToken.None);
        await reader.ReadOptionalAsync(policy, CancellationToken.None);
        await File.WriteAllTextAsync(policy, """{"disabled":true}""");

        var error = await Assert.ThrowsAsync<AgentConfigurationException>(() =>
            reader.SaveAsync(document, new JsonObject { ["ours"] = true }, CancellationToken.None));

        Assert.Equal(AgentCommandStrings.Configuration_ConcurrentChange, error.Message);
        Assert.Equal(destinationExists, File.Exists(path));
        if (destinationExists)
        {
            Assert.Equal(original, await File.ReadAllTextAsync(path));
        }
        Assert.Equal((destinationExists ? new[] { path, policy } : [policy]).Order(),
            Directory.EnumerateFiles(_context.Project.FullName).Order());
    }

    [Fact]
    public async Task ConcurrentEdit_IsNotOverwrittenAndStagingFilesAreRemoved()
    {
        var path = Path.Combine(_context.Project.FullName, "settings.json");
        await File.WriteAllTextAsync(path, "{}");
        var reader = new AgentConfigurationJson.ReadContext();
        var document = await reader.ReadAsync(path, CancellationToken.None);
        const string concurrent = """{"otherWriter":"preserved"}""";
        await File.WriteAllTextAsync(path, concurrent);

        var error = await Assert.ThrowsAsync<AgentConfigurationException>(() =>
            reader.SaveAsync(document, new JsonObject { ["ours"] = true }, CancellationToken.None));

        Assert.Equal(AgentCommandStrings.Configuration_ConcurrentChange, error.Message);
        Assert.Equal(concurrent, await File.ReadAllTextAsync(path));
        Assert.Equal([path], Directory.EnumerateFiles(_context.Project.FullName));
    }

    [Fact]
    public async Task Cancellation_PropagatesWithoutCreatingConfiguration()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _context.ConfigureNativeAsync(
            AgentConfigurationScope.Project, [_context.Copilot], cancellation.Token)).DefaultTimeout();

        Assert.Empty(_context.Project.EnumerateFileSystemInfos());
        Assert.Empty(_context.Home.EnumerateFileSystemInfos());
    }

    public void Dispose() => _context.Dispose();
}
