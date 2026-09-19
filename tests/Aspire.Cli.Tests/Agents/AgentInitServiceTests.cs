// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Cli.Agents;
using Aspire.Cli.Agents.Configuration;
using Aspire.Cli.Agents.Playwright;
using Aspire.Cli.Tests.TestServices;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.Agents;

public class AgentInitServiceTests(ITestOutputHelper output)
{
    [Fact]
    public async Task NoAssets_DoesNotReadConfigOrInvokeAnyInstaller()
    {
        using var context = new AgentConfigurationTestContext(output);
        await AgentConfigurationTestContext.WriteAsync(Path.Combine(context.Paths.CopilotDirectory, "settings.json"), "{broken").DefaultTimeout();

        var result = await context.Service.ConfigureAsync(context.Request([AgentClientKind.CopilotCli], skills: false), CancellationToken.None).DefaultTimeout();

        Assert.Empty(result.Targets);
        Assert.Empty(context.SkillInstaller.Requests);
        Assert.Equal(0, context.HookInstaller.Calls);
        Assert.Empty(Directory.EnumerateFileSystemEntries(context.Project.FullName));
    }

    [Fact]
    public async Task NoClients_IsANoOpEvenWhenAllAssetsAreSelected()
    {
        using var context = new AgentConfigurationTestContext(output);

        var result = await context.Service.ConfigureAsync(
            context.Request([], mcp: true, playwright: true, dotnetInspect: true), CancellationToken.None).DefaultTimeout();

        Assert.Empty(result.Targets);
        Assert.Empty(context.SkillInstaller.Requests);
        Assert.Equal(0, context.HookInstaller.Calls);
        Assert.Empty(Directory.EnumerateFileSystemEntries(context.Project.FullName));
        Assert.Empty(Directory.EnumerateFileSystemEntries(context.Home.FullName));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ToolSkills_UseOneManagedInvocationAndNeverConfigureNativeSourcesOrHooks(bool playwright, bool dotnetInspect)
    {
        using var context = new AgentConfigurationTestContext(output);
        var request = context.Request([AgentClientKind.CopilotCli, AgentClientKind.ClaudeCode],
            skills: false, playwright: playwright, dotnetInspect: dotnetInspect);
        context.SkillInstaller.Results =
        [
            new(playwright ? AgentAssetKind.Playwright : AgentAssetKind.DotnetInspect, request.Clients,
                Path.Combine(context.Project.FullName, "managed-skill"), AgentConfigurationScope.Project, AgentConfigurationStatus.Configured, "test")
        ];

        var result = await context.Service.ConfigureAsync(request, CancellationToken.None).DefaultTimeout();

        Assert.Same(request, Assert.Single(context.SkillInstaller.Requests));
        Assert.Equal(context.SkillInstaller.Results, result.Targets);
        Assert.Equal(0, context.HookInstaller.Calls);
        Assert.Empty(Directory.EnumerateFileSystemEntries(context.Project.FullName));
        Assert.Empty(Directory.EnumerateFileSystemEntries(context.Home.FullName));
    }

    [Fact]
    public async Task NativeSkills_AreOfflineAndInstallOnlyOneSetOfEmbeddedHooks()
    {
        using var context = new AgentConfigurationTestContext(output);
        var request = context.Request([AgentClientKind.CopilotCli, AgentClientKind.CopilotApp, AgentClientKind.ClaudeCode, AgentClientKind.VsCode, AgentClientKind.OpenCode]);

        var result = await context.Service.ConfigureAsync(request, CancellationToken.None).DefaultTimeout();

        Assert.False(result.HasErrors);
        Assert.False(result.HasWarnings);
        Assert.Empty(context.SkillInstaller.Requests);
        Assert.Equal(1, context.HookInstaller.Calls);
        Assert.Equal(2, result.Targets.Count(target => target.Asset is AgentAssetKind.TelemetryHooks));
        Assert.All(result.Targets.Where(target => target.Asset is AgentAssetKind.TelemetryHooks),
            target => Assert.Equal(AgentConfigurationScope.User, target.Scope));
        Assert.Equal(request.Clients.Order(), result.RegisteredClients.Order());
        Assert.False(File.Exists(Path.Combine(context.Paths.CopilotDirectory, "config.json")));
        Assert.False(Directory.Exists(Path.Combine(context.Paths.CopilotDirectory, "installed-plugins")));
    }

    [Fact]
    public async Task NativeFailures_DoNotPreventIndependentTargetsAndDoNotQualifyForHooks()
    {
        using var context = new AgentConfigurationTestContext(output);
        var path = Path.Combine(context.Paths.CopilotDirectory, "settings.json");
        await AgentConfigurationTestContext.WriteAsync(path, "{broken").DefaultTimeout();

        var result = await context.Service.ConfigureAsync(
            context.Request([AgentClientKind.CopilotCli, AgentClientKind.OpenCode]), CancellationToken.None).DefaultTimeout();

        Assert.True(result.HasErrors);
        Assert.True(result.HasWarnings);
        Assert.Equal(0, context.HookInstaller.Calls);
        Assert.All(result.Targets.Where(target => target.Asset is AgentAssetKind.AspireSkills && target.Clients.Contains(AgentClientKind.CopilotCli)),
            target => Assert.Equal(AgentConfigurationStatus.Blocked, target.Status));
        Assert.All(result.Targets.Where(target => target.Clients.Contains(AgentClientKind.OpenCode)),
            target => Assert.Equal(AgentConfigurationStatus.Configured, target.Status));
        Assert.Equal(AgentConfigurationStatus.Skipped, Assert.Single(result.Targets, target => target.Asset is AgentAssetKind.TelemetryHooks).Status);
    }

    [Fact]
    public async Task HookInstallationFailures_AreExplicitAndAdvisory()
    {
        using var context = new AgentConfigurationTestContext(output);
        context.HookInstaller.Error = new IOException("Hook directory is locked.");

        var result = await context.Service.ConfigureAsync(
            context.Request([AgentClientKind.CopilotCli, AgentClientKind.ClaudeCode]), CancellationToken.None).DefaultTimeout();

        Assert.False(result.HasErrors);
        Assert.True(result.HasWarnings);
        Assert.Equal(1, context.HookInstaller.Calls);
        Assert.All(result.Targets.Where(target => target.Asset is AgentAssetKind.AspireSkills),
            target => Assert.Equal(AgentConfigurationStatus.Configured, target.Status));
        Assert.All(result.Targets.Where(target => target.Asset is AgentAssetKind.TelemetryHooks), target =>
        {
            Assert.Equal(AgentConfigurationStatus.Failed, target.Status);
            Assert.NotEmpty(target.Message!);
        });
        var claudeSettings = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(context.Paths.ClaudeDirectory, "settings.json")).DefaultTimeout())!;
        Assert.True((bool)claudeSettings["enabledPlugins"]!["aspire@aspire-skills"]!);
        Assert.Null(claudeSettings["hooks"]);
    }

    [Theory]
    [InlineData(nameof(AgentClientKind.CopilotCli))]
    [InlineData(nameof(AgentClientKind.ClaudeCode))]
    public async Task SuccessfulMcp_QualifiesForUserHookWithoutAspireSkills(string clientName)
    {
        var client = Enum.Parse<AgentClientKind>(clientName);
        using var context = new AgentConfigurationTestContext(output);

        var result = await context.Service.ConfigureAsync(context.Request([client], skills: false, mcp: true), CancellationToken.None).DefaultTimeout();

        Assert.False(result.HasErrors);
        Assert.Empty(result.RegisteredClients);
        Assert.Equal(1, context.HookInstaller.Calls);
        var hook = Assert.Single(result.Targets, target => target.Asset is AgentAssetKind.TelemetryHooks);
        Assert.Equal(AgentConfigurationStatus.Configured, hook.Status);
        Assert.Equal(AgentConfigurationScope.User, hook.Scope);
        Assert.Empty(context.SkillInstaller.Requests);
    }

    [Fact]
    public async Task AlreadyConfiguredNativeSources_StillQualifyForAMissingHook()
    {
        using var context = new AgentConfigurationTestContext(output);
        var request = context.Request([AgentClientKind.CopilotCli]);
        await context.ConfigureNativeAsync(request).DefaultTimeout();

        var result = await context.Service.ConfigureAsync(request, CancellationToken.None).DefaultTimeout();

        Assert.All(result.Targets.Where(target => target.Asset is AgentAssetKind.AspireSkills),
            target => Assert.Equal(AgentConfigurationStatus.Unchanged, target.Status));
        Assert.Equal(AgentConfigurationStatus.Configured, Assert.Single(result.Targets, target => target.Asset is AgentAssetKind.TelemetryHooks).Status);
    }

    [Fact]
    public async Task ClaudeSettingsAndHook_AreMergedAndRemainByteStableOnRepeat()
    {
        using var context = new AgentConfigurationTestContext(output);
        var path = Path.Combine(context.Paths.ClaudeDirectory, "settings.json");
        await AgentConfigurationTestContext.WriteAsync(path, """{"model":"preserved","hooks":{"PreToolUse":[]}}""").DefaultTimeout();
        var request = context.Request([AgentClientKind.ClaudeCode]);

        var first = await context.Service.ConfigureAsync(request, CancellationToken.None).DefaultTimeout();
        var bytes = await File.ReadAllBytesAsync(path).DefaultTimeout();
        File.SetLastWriteTimeUtc(path, new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var timestamp = File.GetLastWriteTimeUtc(path);
        var second = await context.Service.ConfigureAsync(request, CancellationToken.None).DefaultTimeout();

        Assert.False(first.HasErrors);
        Assert.All(second.Targets, result => Assert.Equal(AgentConfigurationStatus.Unchanged, result.Status));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path).DefaultTimeout());
        Assert.Equal(timestamp, File.GetLastWriteTimeUtc(path));
        var root = JsonNode.Parse(bytes)!.AsObject();
        var args = root["hooks"]!["PostToolUse"]![0]!["hooks"]![0]!["args"]!.AsArray();
        args[^1] = "[embedded-script]";
        var json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        await Verify(json, "json")
            .UseFileName($"AgentInitServiceTests.ClaudeSettingsAndHook.{(OperatingSystem.IsWindows() ? "Windows" : "Unix")}");
    }

    [Fact]
    public async Task DetectedButUnselectedClients_AreNeverConfigured()
    {
        using var context = new AgentConfigurationTestContext(output);
        var request = context.Request([AgentClientKind.OpenCode],
            detections: [new(AgentClientKind.CopilotCli, "1.0.0", false), new(AgentClientKind.ClaudeCode, "2.1.0", false)]);

        var result = await context.Service.ConfigureAsync(request, CancellationToken.None).DefaultTimeout();

        Assert.All(result.Targets, target => Assert.Equal([AgentClientKind.OpenCode], target.Clients));
        Assert.Equal(0, context.HookInstaller.Calls);
        Assert.False(Directory.Exists(context.Paths.CopilotDirectory));
        Assert.False(Directory.Exists(context.Paths.ClaudeDirectory));
    }

    [Fact]
    public async Task CancellationFromHookInstallation_Propagates()
    {
        using var context = new AgentConfigurationTestContext(output);
        using var cancellation = new CancellationTokenSource();
        context.HookInstaller.Error = new OperationCanceledException(cancellation.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            context.Service.ConfigureAsync(context.Request([AgentClientKind.CopilotCli], playwright: true), cancellation.Token)).DefaultTimeout();

        Assert.Empty(context.SkillInstaller.Requests);
    }

    [Fact]
    public async Task InvalidNativeOverride_ProducesTypedFailuresWithoutStoppingIndependentClients()
    {
        using var context = new AgentConfigurationTestContext(output);
        context.SetVariable("COPILOT_HOME", "\0invalid");

        var result = await context.Service.ConfigureAsync(
            context.Request([AgentClientKind.CopilotCli, AgentClientKind.OpenCode]), CancellationToken.None).DefaultTimeout();

        Assert.True(result.HasErrors);
        Assert.All(result.Targets.Where(target => target.Clients.Contains(AgentClientKind.CopilotCli)),
            target => Assert.True(target.Status is AgentConfigurationStatus.Blocked or AgentConfigurationStatus.Failed));
        Assert.All(result.Targets.Where(target => target.Clients.Contains(AgentClientKind.OpenCode)),
            target => Assert.Equal(AgentConfigurationStatus.Configured, target.Status));
        Assert.Equal(0, context.HookInstaller.Calls);
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
        using var context = new AgentConfigurationTestContext(output);
        context.SetVariable("OPENCODE_CONFIG_CONTENT", inline);
        context.SkillInstaller.Results =
        [
            new(AgentAssetKind.DotnetInspect, [AgentClientKind.OpenCode, AgentClientKind.CopilotCli],
                Path.Combine(context.Project.FullName, ".agents", "skills", "dotnet-inspect"),
                AgentConfigurationScope.Project, AgentConfigurationStatus.Configured, "test")
        ];
        var request = context.Request([AgentClientKind.OpenCode, AgentClientKind.CopilotCli], mcp: true, dotnetInspect: true);

        var result = await context.Service.ConfigureAsync(request, CancellationToken.None).DefaultTimeout();

        Assert.True(result.HasErrors);
        var openCode = result.Targets.Where(target => target.Clients.Contains(AgentClientKind.OpenCode) &&
            target.Asset is AgentAssetKind.AspireSkills or AgentAssetKind.Mcp).ToArray();
        Assert.Equal(4, openCode.Length);
        Assert.All(openCode, target => Assert.Equal(AgentConfigurationStatus.Blocked, target.Status));
        var copilot = result.Targets.Where(target => target.Clients.Contains(AgentClientKind.CopilotCli) &&
            target.Asset is AgentAssetKind.AspireSkills or AgentAssetKind.Mcp).ToArray();
        Assert.Equal(4, copilot.Length);
        Assert.All(copilot, target => Assert.Equal(AgentConfigurationStatus.Configured, target.Status));
        Assert.Same(request, Assert.Single(context.SkillInstaller.Requests));
        Assert.Equal(context.SkillInstaller.Results[0], Assert.Single(result.Targets, target => target.Asset is AgentAssetKind.DotnetInspect));
        Assert.False(File.Exists(Path.Combine(context.Project.FullName, "opencode.json")));
        Assert.False(Directory.Exists(context.Paths.OpenCodeDirectory));
    }

    [Theory]
    [InlineData("~", "")]
    [InlineData("~/claude-work", "claude-work")]
    [InlineData(@"~\claude-work", "claude-work")]
    public async Task ClaudeHomeOverride_IsSharedByNativeRegistrationAndRealManagedSkills(string value, string relative)
    {
        using var context = new AgentConfigurationTestContext(output);
        context.SetVariable("CLAUDE_CONFIG_DIR", value);
        var npm = new FakeNpmRunner();
        var playwright = new FakePlaywrightCliRunner();
        var playwrightInstaller = new PlaywrightCliInstaller(npm, new FakeNpmProvenanceChecker(), playwright,
            new TestInteractionService(), new ConfigurationBuilder().Build(), NullLogger<PlaywrightCliInstaller>.Instance);
        var managed = new AgentSkillInstaller(playwrightInstaller, context.ExecutionContext, context.Paths, NullLogger<AgentSkillInstaller>.Instance);
        var service = new AgentInitService(context.Planner, context.Writer, managed, context.Hooks);
        var expectedDirectory = relative.Length == 0 ? context.Home.FullName : Path.Combine(context.Home.FullName, relative);

        var result = await service.ConfigureAsync(
            context.Request([AgentClientKind.ClaudeCode], dotnetInspect: true), CancellationToken.None).DefaultTimeout();

        Assert.False(result.HasErrors);
        Assert.All(result.Targets, target => Assert.Equal(AgentConfigurationStatus.Configured, target.Status));
        var registration = Assert.Single(result.Targets, target => target.Asset is AgentAssetKind.AspireSkills && target.Scope is AgentConfigurationScope.User);
        var skill = Assert.Single(result.Targets, target => target.Asset is AgentAssetKind.DotnetInspect && target.Scope is AgentConfigurationScope.User);
        Assert.Equal(AgentConfigurationPath.Resolve(Path.Combine(expectedDirectory, "settings.json")),
            AgentConfigurationPath.Resolve(registration.TargetPath));
        Assert.Equal(AgentConfigurationPath.Resolve(Path.Combine(expectedDirectory, "skills", "dotnet-inspect")),
            AgentConfigurationPath.Resolve(skill.TargetPath));
        Assert.Equal(CommonAgentApplicators.DotnetInspectSkillFileContent,
            await File.ReadAllTextAsync(Path.Combine(skill.TargetPath, "SKILL.md")).DefaultTimeout());
        Assert.Equal([".claude"], context.Project.EnumerateDirectories().Select(directory => directory.Name));
        Assert.Equal(0, npm.ResolveCallCount);
        Assert.Equal(0, npm.PackCallCount);
        Assert.Equal(0, npm.InstallGlobalCallCount);
        Assert.Equal(0, playwright.GetVersionCallCount);
        Assert.Equal(0, playwright.InstallSkillsCallCount);
    }
}
