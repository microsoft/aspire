// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Cli.Agents;
using Aspire.Cli.Agents.Playwright;
using Aspire.Cli.Commands;
using Aspire.Cli.Tests.Agents;
using Aspire.Cli.Tests.TestServices;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.Commands;

public class AgentInitCommandConfigurationTests(ITestOutputHelper output) : IDisposable
{
    private readonly AgentConfigurationTestContext _context = new(output);

    [Fact]
    public async Task NoAssets_DoesNotReadConfigOrInvokeAnyInstaller()
    {
        await AgentConfigurationTestContext.WriteAsync(Path.Combine(_context.CopilotDirectory, "settings.json"), "{broken").DefaultTimeout();

        var result = await _context.ConfigureAsync(
            _context.Request(AgentConfigurationScope.User, [_context.Copilot], skills: false, detections: [new(AgentClientKind.CopilotCli, null, false)]),
            CancellationToken.None).DefaultTimeout();

        Assert.Empty(result.Targets);
        Assert.Empty(_context.SkillInstaller.Requests);
        Assert.Equal(0, _context.HookInstaller.Calls);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_context.Project.FullName));
    }

    [Fact]
    public async Task NoClients_IsANoOpEvenWhenAllAssetsAreSelected()
    {

        var result = await _context.ConfigureAsync(
            _context.Request(AgentConfigurationScope.User, [], mcp: true, playwright: true, dotnetInspect: true,
                detections: [new(AgentClientKind.CopilotCli, null, false), new(AgentClientKind.ClaudeCode, null, false)]),
            CancellationToken.None).DefaultTimeout();

        Assert.Empty(result.Targets);
        Assert.Empty(_context.SkillInstaller.Requests);
        Assert.Equal(0, _context.HookInstaller.Calls);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_context.Project.FullName));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_context.Home.FullName));
    }

    [Theory]
    [InlineData("Project")]
    [InlineData("User")]
    public async Task AllAssets_OnlyWriteTheSelectedScope(string scopeName)
    {
        var scope = Enum.Parse<AgentConfigurationScope>(scopeName);
        var request = _context.Request(scope, _context.Environments, mcp: true, playwright: true, dotnetInspect: true,
            detections: [new(AgentClientKind.CopilotCli, null, false), new(AgentClientKind.ClaudeCode, null, false)]);

        var result = await AgentInitCommand.ConfigureAsync(request, _context.Writer,
            _context.CreateManagedSkillInstaller(), _context.Hooks, CancellationToken.None).DefaultTimeout();

        Assert.False(result.HasErrors);
        Assert.NotEmpty(result.Targets);
        Assert.All(result.Targets, target => Assert.Equal(scope, target.Scope));
        var untouched = scope is AgentConfigurationScope.Project ? _context.Home : _context.Project;
        Assert.Empty(untouched.EnumerateFileSystemInfos());
        Assert.Equal(scope is AgentConfigurationScope.User ? 1 : 0, _context.HookInstaller.Calls);
        Assert.Equal(scope is AgentConfigurationScope.User ? 2 : 0, result.Targets.Count(target => target.Asset is AgentAssetKind.TelemetryHooks));
        Assert.Equal(1, _context.Playwright.InstallSkillsCallCount);
    }

    [Fact]
    public async Task EditorHint_DoesNotCreateCliHooksOrEditorSettings()
    {
        var request = _context.Request(AgentConfigurationScope.User, [_context.Copilot],
            detections: [new(AgentClientKind.VsCode, "1.120.0", false)]);

        var result = await _context.ConfigureAsync(request, CancellationToken.None).DefaultTimeout();

        var target = Assert.Single(result.Targets);
        Assert.Equal(AgentAssetKind.AspireSkills, target.Asset);
        Assert.Equal(Path.Combine(_context.CopilotDirectory, "settings.json"), target.TargetPath);
        Assert.Equal(0, _context.HookInstaller.Calls);
        Assert.Empty(_context.Project.EnumerateFileSystemInfos());
        Assert.Equal([".copilot"], _context.Home.EnumerateDirectories().Select(directory => directory.Name));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ToolSkills_UseOneManagedInvocationAndConfigureOnlyDetectedHooks(bool playwright, bool dotnetInspect)
    {
        var request = _context.Request(AgentConfigurationScope.User, [_context.Copilot, _context.ClaudeCode],
            skills: false, playwright: playwright, dotnetInspect: dotnetInspect,
            detections: [new(AgentClientKind.CopilotCli, null, false)]);
        _context.SkillInstaller.Results =
        [
            new(playwright ? AgentAssetKind.Playwright : AgentAssetKind.DotnetInspect, request.Environments,
                Path.Combine(_context.Home.FullName, "managed-skill"), AgentConfigurationScope.User, AgentConfigurationStatus.Configured, "test")
        ];

        var result = await _context.ConfigureAsync(request, CancellationToken.None).DefaultTimeout();

        Assert.Same(request, Assert.Single(_context.SkillInstaller.Requests));
        Assert.Equal(2, result.Targets.Count);
        Assert.Equal(_context.SkillInstaller.Results[0], result.Targets[1]);
        var hook = Assert.Single(result.Targets, target => target.Asset is AgentAssetKind.TelemetryHooks);
        Assert.Equal([_context.Copilot], hook.Environments);
        Assert.Equal(AgentConfigurationStatus.Configured, hook.Status);
        Assert.Equal(AgentConfigurationScope.User, hook.Scope);
        Assert.Equal(1, _context.HookInstaller.Calls);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_context.Project.FullName));
        Assert.False(File.Exists(Path.Combine(_context.CopilotDirectory, "settings.json")));
        Assert.False(Directory.Exists(_context.ClaudeDirectory));
    }

    [Fact]
    public async Task NativeSkills_AreOfflineAndInstallOnlyOneSetOfEmbeddedHooks()
    {
        var request = _context.Request(AgentConfigurationScope.User,
            [_context.Copilot, _context.ClaudeCode, _context.OpenCode],
            detections: [new(AgentClientKind.CopilotCli, null, false), new(AgentClientKind.CopilotCli, null, false), new(AgentClientKind.ClaudeCode, null, false)]);

        var result = await _context.ConfigureAsync(request, CancellationToken.None).DefaultTimeout();

        Assert.False(result.HasErrors);
        Assert.False(result.HasWarnings);
        Assert.Empty(_context.SkillInstaller.Requests);
        Assert.Equal(1, _context.HookInstaller.Calls);
        Assert.Equal(2, result.Targets.Count(target => target.Asset is AgentAssetKind.TelemetryHooks));
        Assert.All(result.Targets.Where(target => target.Asset is AgentAssetKind.TelemetryHooks),
            target => Assert.Equal(AgentConfigurationScope.User, target.Scope));
        Assert.Equal(request.Environments.OrderBy(client => client.Id), result.RegisteredEnvironments.OrderBy(client => client.Id));
        Assert.False(File.Exists(Path.Combine(_context.CopilotDirectory, "config.json")));
        Assert.False(Directory.Exists(Path.Combine(_context.CopilotDirectory, "installed-plugins")));
    }

    [Fact]
    public async Task NativeFailures_DoNotPreventIndependentTargetsAndMalformedPolicyStillBlocksHooks()
    {
        var path = Path.Combine(_context.CopilotDirectory, "settings.json");
        await AgentConfigurationTestContext.WriteAsync(path, "{broken").DefaultTimeout();

        var result = await _context.ConfigureAsync(
            _context.Request(AgentConfigurationScope.User, [_context.Copilot, _context.OpenCode],
                detections: [new(AgentClientKind.CopilotCli, null, false)]),
            CancellationToken.None).DefaultTimeout();

        Assert.True(result.HasErrors);
        Assert.True(result.HasWarnings);
        Assert.Equal(0, _context.HookInstaller.Calls);
        Assert.All(result.Targets.Where(target => target.Asset is AgentAssetKind.AspireSkills && target.Environments.Contains(_context.Copilot)),
            target => Assert.Equal(AgentConfigurationStatus.Blocked, target.Status));
        Assert.All(result.Targets.Where(target => target.Environments.Contains(_context.OpenCode)),
            target => Assert.Equal(AgentConfigurationStatus.Configured, target.Status));
        Assert.Equal(AgentConfigurationStatus.Blocked, Assert.Single(result.Targets, target => target.Asset is AgentAssetKind.TelemetryHooks).Status);
    }

    [Theory]
    [InlineData("copilot")]
    [InlineData("claude")]
    public async Task NativeRegistrationBlocked_DoesNotPreventDetectedClientHooks(string clientName)
    {
        var client = _context.Environments.Single(client => client.Id == clientName);
        var detectedClient = clientName == "copilot" ? AgentClientKind.CopilotCli : AgentClientKind.ClaudeCode;
        var directory = client == _context.ClaudeCode ? _context.ClaudeDirectory : _context.CopilotDirectory;
        var path = Path.Combine(directory, "settings.json");
        await AgentConfigurationTestContext.WriteAsync(path,
            """{"extraKnownMarketplaces":{"aspire-skills":{"source":{"source":"github","repo":"example/custom-skills"}}}}""").DefaultTimeout();

        var result = await _context.ConfigureAsync(
            _context.Request(AgentConfigurationScope.User, [client], detections: [new(detectedClient, null, false)]),
            CancellationToken.None).DefaultTimeout();

        Assert.True(result.HasErrors);
        Assert.All(result.Targets.Where(target => target.Asset is AgentAssetKind.AspireSkills),
            target => Assert.Equal(AgentConfigurationStatus.Blocked, target.Status));
        var hook = Assert.Single(result.Targets, target => target.Asset is AgentAssetKind.TelemetryHooks);
        Assert.Equal(AgentConfigurationStatus.Configured, hook.Status);
        Assert.Equal([client], hook.Environments);
        Assert.Equal(1, _context.HookInstaller.Calls);
        var settings = JsonNode.Parse(await File.ReadAllTextAsync(path).DefaultTimeout())!;
        Assert.Equal("example/custom-skills", settings["extraKnownMarketplaces"]!["aspire-skills"]!["source"]!["repo"]!.GetValue<string>());
    }

    [Fact]
    public async Task HookInstallationFailures_AreExplicitAndAdvisory()
    {
        _context.HookInstaller.Error = new IOException("Hook directory is locked.");

        var result = await _context.ConfigureAsync(
            _context.Request(AgentConfigurationScope.User, [_context.Copilot, _context.ClaudeCode],
                detections: [new(AgentClientKind.CopilotCli, null, false), new(AgentClientKind.ClaudeCode, null, false)]),
            CancellationToken.None).DefaultTimeout();

        Assert.False(result.HasErrors);
        Assert.True(result.HasWarnings);
        Assert.Equal(1, _context.HookInstaller.Calls);
        Assert.All(result.Targets.Where(target => target.Asset is AgentAssetKind.AspireSkills),
            target => Assert.Equal(AgentConfigurationStatus.Configured, target.Status));
        Assert.All(result.Targets.Where(target => target.Asset is AgentAssetKind.TelemetryHooks), target =>
        {
            Assert.Equal(AgentConfigurationStatus.Failed, target.Status);
            Assert.NotEmpty(target.Message!);
        });
        var claudeSettings = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(_context.ClaudeDirectory, "settings.json")).DefaultTimeout())!;
        Assert.True((bool)claudeSettings["enabledPlugins"]!["aspire@aspire-skills"]!);
        Assert.Null(claudeSettings["hooks"]);
    }

    [Theory]
    [InlineData("copilot")]
    [InlineData("claude")]
    public async Task McpSetup_AddsUserHookForDetectedClientsWithoutAspireSkills(string clientName)
    {
        var client = _context.Environments.Single(client => client.Id == clientName);
        var detectedClient = clientName == "copilot" ? AgentClientKind.CopilotCli : AgentClientKind.ClaudeCode;

        var result = await _context.ConfigureAsync(
            _context.Request(AgentConfigurationScope.User, [client], skills: false, mcp: true, detections: [new(detectedClient, null, false)]),
            CancellationToken.None).DefaultTimeout();

        Assert.False(result.HasErrors);
        Assert.Empty(result.RegisteredEnvironments);
        Assert.Equal(1, _context.HookInstaller.Calls);
        var hook = Assert.Single(result.Targets, target => target.Asset is AgentAssetKind.TelemetryHooks);
        Assert.Equal(AgentConfigurationStatus.Configured, hook.Status);
        Assert.Equal(AgentConfigurationScope.User, hook.Scope);
        Assert.Empty(_context.SkillInstaller.Requests);
    }

    [Fact]
    public async Task AlreadyConfiguredNativeSources_DoNotPreventAMissingDetectedClientHook()
    {
        var request = _context.Request(AgentConfigurationScope.User, [_context.Copilot],
            detections: [new(AgentClientKind.CopilotCli, null, false)]);
        await _context.ConfigureNativeAsync(request).DefaultTimeout();

        var result = await _context.ConfigureAsync(request, CancellationToken.None).DefaultTimeout();

        Assert.All(result.Targets.Where(target => target.Asset is AgentAssetKind.AspireSkills),
            target => Assert.Equal(AgentConfigurationStatus.Unchanged, target.Status));
        Assert.Equal(AgentConfigurationStatus.Configured, Assert.Single(result.Targets, target => target.Asset is AgentAssetKind.TelemetryHooks).Status);
    }

    [Fact]
    public async Task ClaudeSettingsAndHook_AreMergedAndRemainByteStableOnRepeat()
    {
        var path = Path.Combine(_context.ClaudeDirectory, "settings.json");
        await AgentConfigurationTestContext.WriteAsync(path, """{"model":"preserved","hooks":{"PreToolUse":[]}}""").DefaultTimeout();
        var request = _context.Request(AgentConfigurationScope.User, [_context.ClaudeCode],
            detections: [new(AgentClientKind.ClaudeCode, null, false)]);

        var first = await _context.ConfigureAsync(request, CancellationToken.None).DefaultTimeout();
        var bytes = await File.ReadAllBytesAsync(path).DefaultTimeout();
        File.SetLastWriteTimeUtc(path, new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var timestamp = File.GetLastWriteTimeUtc(path);
        var second = await _context.ConfigureAsync(request, CancellationToken.None).DefaultTimeout();

        Assert.False(first.HasErrors);
        Assert.All(second.Targets, result => Assert.Equal(AgentConfigurationStatus.Unchanged, result.Status));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path).DefaultTimeout());
        Assert.Equal(timestamp, File.GetLastWriteTimeUtc(path));
        var root = JsonNode.Parse(bytes)!.AsObject();
        var args = root["hooks"]!["PostToolUse"]![0]!["hooks"]![0]!["args"]!.AsArray();
        args[^1] = "[embedded-script]";
        var json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        await Verify(json, "json")
            .UseFileName($"AgentInitCommandConfigurationTests.ClaudeSettingsAndHook.{(OperatingSystem.IsWindows() ? "Windows" : "Unix")}");
    }

    [Fact]
    public async Task DetectedButUnselectedClients_ReceiveOnlyUserHooks()
    {
        var request = _context.Request(AgentConfigurationScope.User, [_context.OpenCode],
            detections: [new(AgentClientKind.CopilotCli, "1.0.0", false), new(AgentClientKind.ClaudeCode, "2.1.0", false), new(AgentClientKind.VsCode, "1.120.0", false)]);

        var result = await _context.ConfigureAsync(request, CancellationToken.None).DefaultTimeout();

        Assert.False(result.HasErrors);
        Assert.False(result.HasWarnings);
        Assert.All(result.Targets.Where(target => target.Asset is AgentAssetKind.AspireSkills),
            target => Assert.Equal([_context.OpenCode], target.Environments));
        Assert.Equal([_context.OpenCode], result.RegisteredEnvironments);
        Assert.Equal(1, _context.HookInstaller.Calls);
        Assert.Collection(result.Targets.Where(target => target.Asset is AgentAssetKind.TelemetryHooks),
            copilot =>
            {
                Assert.Equal([_context.Copilot], copilot.Environments);
                Assert.Equal(AgentConfigurationScope.User, copilot.Scope);
                Assert.Equal(AgentConfigurationStatus.Configured, copilot.Status);
                Assert.True(File.Exists(copilot.TargetPath));
            },
            claude =>
            {
                Assert.Equal([_context.ClaudeCode], claude.Environments);
                Assert.Equal(AgentConfigurationScope.User, claude.Scope);
                Assert.Equal(AgentConfigurationStatus.Configured, claude.Status);
                Assert.True(File.Exists(claude.TargetPath));
            });
        Assert.False(File.Exists(Path.Combine(_context.CopilotDirectory, "settings.json")));
        var claudeSettings = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(_context.ClaudeDirectory, "settings.json")).DefaultTimeout())!.AsObject();
        Assert.Equal(["hooks"], claudeSettings.Select(property => property.Key));
        Assert.Empty(_context.Project.EnumerateFileSystemInfos());
        Assert.Equal(["opencode.json"], Directory.EnumerateFiles(_context.OpenCodeDirectory).Select(Path.GetFileName));
    }

    [Theory]
    [InlineData("copilot")]
    [InlineData("claude")]
    public async Task SelectedButUndetectedClient_ReceivesNativeConfigurationWithoutHooks(string clientName)
    {
        var client = _context.Environments.Single(client => client.Id == clientName);

        var result = await _context.ConfigureAsync(_context.Request(AgentConfigurationScope.User, [client]), CancellationToken.None).DefaultTimeout();

        Assert.False(result.HasErrors);
        Assert.False(result.HasWarnings);
        Assert.Single(result.Targets);
        Assert.All(result.Targets, target =>
        {
            Assert.Equal(AgentAssetKind.AspireSkills, target.Asset);
            Assert.Equal(AgentConfigurationStatus.Configured, target.Status);
            Assert.Equal([client], target.Environments);
        });
        Assert.Equal(0, _context.HookInstaller.Calls);
    }

    [Fact]
    public async Task CancellationFromHookInstallation_Propagates()
    {
        using var cancellation = new CancellationTokenSource();
        _context.HookInstaller.Error = new OperationCanceledException(cancellation.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _context.ConfigureAsync(
                _context.Request(AgentConfigurationScope.User, [_context.Copilot], playwright: true, detections: [new(AgentClientKind.CopilotCli, null, false)]),
                cancellation.Token)).DefaultTimeout();

        Assert.Empty(_context.SkillInstaller.Requests);
    }

    [Fact]
    public async Task InvalidNativeOverride_ProducesTypedFailuresWithoutStoppingIndependentClients()
    {
        _context.SetVariable("COPILOT_HOME", "\0invalid");

        var result = await _context.ConfigureAsync(
            _context.Request(AgentConfigurationScope.User, [_context.Copilot, _context.OpenCode],
                detections: [new(AgentClientKind.CopilotCli, null, false)]),
            CancellationToken.None).DefaultTimeout();

        Assert.True(result.HasErrors);
        Assert.All(result.Targets.Where(target => target.Environments.Contains(_context.Copilot)),
            target => Assert.True(target.Status is AgentConfigurationStatus.Blocked or AgentConfigurationStatus.Failed));
        Assert.All(result.Targets.Where(target => target.Environments.Contains(_context.OpenCode)),
            target => Assert.Equal(AgentConfigurationStatus.Configured, target.Status));
        Assert.Equal(0, _context.HookInstaller.Calls);
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("[]")]
    [InlineData("""{"duplicate":1,"duplicate":2}""")]
    [InlineData("""{"nested":{"duplicate":1,"duplicate":2}}""")]
    [InlineData("""{"items":[{"duplicate":1,"duplicate":2}]}""")]
    [InlineData("""{"skills":[],"skills":{}}""")]
    public async Task MalformedOpenCodeInlineConfiguration_DoesNotAbortOtherNativeOrManagedRequests(string inline)
    {
        _context.SetVariable("OPENCODE_CONFIG_CONTENT", inline);
        _context.SkillInstaller.Results =
        [
            new(AgentAssetKind.DotnetInspect, [_context.OpenCode, _context.Copilot],
                Path.Combine(_context.Home.FullName, ".agents", "skills", "dotnet-inspect"),
                AgentConfigurationScope.User, AgentConfigurationStatus.Configured, "test")
        ];
        var request = _context.Request(AgentConfigurationScope.User, [_context.OpenCode, _context.Copilot], mcp: true, dotnetInspect: true);

        var result = await _context.ConfigureAsync(request, CancellationToken.None).DefaultTimeout();

        Assert.True(result.HasErrors);
        var openCode = result.Targets.Where(target => target.Environments.Contains(_context.OpenCode) &&
            target.Asset is AgentAssetKind.AspireSkills or AgentAssetKind.Mcp).ToArray();
        Assert.Equal(2, openCode.Length);
        Assert.All(openCode, target => Assert.Equal(AgentConfigurationStatus.Blocked, target.Status));
        var copilot = result.Targets.Where(target => target.Environments.Contains(_context.Copilot) &&
            target.Asset is AgentAssetKind.AspireSkills or AgentAssetKind.Mcp).ToArray();
        Assert.Equal(2, copilot.Length);
        Assert.All(copilot, target => Assert.Equal(AgentConfigurationStatus.Configured, target.Status));
        Assert.Same(request, Assert.Single(_context.SkillInstaller.Requests));
        Assert.Equal(_context.SkillInstaller.Results[0], Assert.Single(result.Targets, target => target.Asset is AgentAssetKind.DotnetInspect));
        Assert.False(File.Exists(Path.Combine(_context.Project.FullName, "opencode.json")));
        Assert.False(Directory.Exists(_context.OpenCodeDirectory));
    }

    [Theory]
    [InlineData("~", "")]
    [InlineData("~/claude-work", "claude-work")]
    [InlineData(@"~\claude-work", "claude-work")]
    public async Task ClaudeHomeOverride_IsSharedByNativeRegistrationAndRealManagedSkills(string value, string relative)
    {
        _context.SetVariable("CLAUDE_CONFIG_DIR", value);
        var npm = new FakeNpmRunner();
        var playwright = new FakePlaywrightCliRunner();
        var playwrightInstaller = new PlaywrightCliInstaller(npm, new FakeNpmProvenanceChecker(), playwright,
            new TestInteractionService(), new ConfigurationBuilder().Build(), NullLogger<PlaywrightCliInstaller>.Instance);
        var managed = new AgentSkillInstaller(playwrightInstaller, _context.ExecutionContext, _context.Environment, NullLogger<AgentSkillInstaller>.Instance);
        var expectedDirectory = relative.Length == 0 ? _context.Home.FullName : Path.Combine(_context.Home.FullName, relative);

        var result = await AgentInitCommand.ConfigureAsync(
            _context.Request(AgentConfigurationScope.User, [_context.ClaudeCode], dotnetInspect: true), _context.Writer, managed, _context.Hooks, CancellationToken.None).DefaultTimeout();

        Assert.False(result.HasErrors);
        Assert.All(result.Targets, target => Assert.Equal(AgentConfigurationStatus.Configured, target.Status));
        var registration = Assert.Single(result.Targets, target => target.Asset is AgentAssetKind.AspireSkills && target.Scope is AgentConfigurationScope.User);
        var skill = Assert.Single(result.Targets, target => target.Asset is AgentAssetKind.DotnetInspect && target.Scope is AgentConfigurationScope.User);
        Assert.Equal(AgentPath.Resolve(Path.Combine(expectedDirectory, "settings.json")),
            AgentPath.Resolve(registration.TargetPath));
        Assert.Equal(AgentPath.Resolve(Path.Combine(expectedDirectory, "skills", "dotnet-inspect")),
            AgentPath.Resolve(skill.TargetPath));
        Assert.Equal(DotnetInspectSkill.Content,
            await File.ReadAllTextAsync(Path.Combine(skill.TargetPath, "SKILL.md")).DefaultTimeout());
        Assert.Empty(_context.Project.EnumerateFileSystemInfos());
        Assert.Equal(0, npm.ResolveCallCount);
        Assert.Equal(0, npm.PackCallCount);
        Assert.Equal(0, npm.InstallGlobalCallCount);
        Assert.Equal(0, playwright.GetVersionCallCount);
        Assert.Equal(0, playwright.InstallSkillsCallCount);
    }
    public void Dispose() => _context.Dispose();

}
