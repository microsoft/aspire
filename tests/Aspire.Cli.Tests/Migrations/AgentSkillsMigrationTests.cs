// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Cli.Agents;
using Aspire.Cli.Agents.AspireSkills;
using Aspire.Cli.Migrations;
using Aspire.Cli.Resources;
using Aspire.Cli.Tests.Agents;
using Aspire.Cli.Tests.TestServices;

namespace Aspire.Cli.Tests.Migrations;

public class AgentSkillsMigrationTests(ITestOutputHelper output) : IDisposable
{
    private readonly AgentConfigurationTestContext _context = new(output);
    private readonly MigrationContext _migrationContext = new(AppHostFile: null);
    private readonly TestInteractionService _interactionService = new();

    [Fact]
    public async Task DetectAndApply_ReplacesProjectAndProfileSkills()
    {
        var projectSkill = Skill(_context.Project, ".agents", "aspire");
        var profileSkill = Skill(_context.Home, ".claude", "aspire-init");
        await AgentConfigurationTestContext.WriteAsync(projectSkill, "project skill");
        await AgentConfigurationTestContext.WriteAsync(profileSkill, "profile skill");
        await AgentConfigurationTestContext.WriteAsync(Path.Combine(Path.GetDirectoryName(projectSkill)!, "references", "azure.md"), "reference");
        var userSkill = Skill(_context.Project, ".agents", "my-skill");
        await AgentConfigurationTestContext.WriteAsync(userSkill, "user skill");
        var migration = CreateMigration(AgentEnvironment.CopilotCli | AgentEnvironment.ClaudeCode | AgentEnvironment.OpenCode);

        var descriptor = await migration.DetectAsync(_migrationContext, TestContext.Current.CancellationToken);

        Assert.NotNull(descriptor);
        Assert.Equal([".agents/skills/aspire", "~/.claude/skills/aspire-init"], descriptor.Files);
        Assert.Equal(_context.Project.FullName, descriptor.Metadata?["workspaceRoot"]?.GetValue<string>());

        await migration.ApplyAsync(_migrationContext, TestContext.Current.CancellationToken);

        Assert.False(Directory.Exists(Path.GetDirectoryName(projectSkill)));
        Assert.False(Directory.Exists(Path.GetDirectoryName(profileSkill)));
        Assert.Equal("user skill", await File.ReadAllTextAsync(userSkill));
        // Claude Code does not load .agents/skills, so it only consumes the profile skill.
        (IAgentClient Client, AgentConfigurationScope Scope)[] consumers =
        [
            (_context.Copilot, AgentConfigurationScope.Project),
            (_context.OpenCode, AgentConfigurationScope.Project),
            (_context.Copilot, AgentConfigurationScope.Global),
            (_context.ClaudeCode, AgentConfigurationScope.Global),
            (_context.OpenCode, AgentConfigurationScope.Global)
        ];
        foreach (var (client, scope) in consumers)
        {
            var configuration = AgentConfiguration.ReadObject(client.GetIntegrationConfigPath(scope, _context.Project));
            Assert.NotNull(configuration);
            Assert.True(client == _context.OpenCode
                ? AspireSkillsRemoteConfiguration.HasOpenCodeCatalog(configuration, _context.Sources)
                : configuration["enabledPlugins"]?[AspireSkillsRemoteConfiguration.PluginName]?.GetValue<bool>() is true,
                $"{client.DisplayName} ({scope})");
        }

        Assert.False(File.Exists(_context.ClaudeCode.GetIntegrationConfigPath(AgentConfigurationScope.Project, _context.Project)));
        Assert.Null(await migration.DetectAsync(_migrationContext, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Apply_RegistersOnlyDetectedAgents()
    {
        // GitHub Copilot and OpenCode both load ~/.agents/skills, but only Copilot is in use.
        var skill = Skill(_context.Home, ".agents", "aspire");
        await AgentConfigurationTestContext.WriteAsync(skill, "profile skill");
        var migration = CreateMigration(AgentEnvironment.CopilotCli);

        await migration.ApplyAsync(_migrationContext, TestContext.Current.CancellationToken);

        var configuration = AgentConfiguration.ReadObject(_context.Copilot.GetIntegrationConfigPath(AgentConfigurationScope.Global, _context.Project));
        Assert.NotNull(configuration);
        Assert.True(configuration["enabledPlugins"]?[AspireSkillsRemoteConfiguration.PluginName]?.GetValue<bool>());
        Assert.False(File.Exists(_context.OpenCode.GetIntegrationConfigPath(AgentConfigurationScope.Global, _context.Project)));
        Assert.False(Directory.Exists(Path.GetDirectoryName(skill)));
        Assert.Equal([MigrationStrings.AgentSkillsApplied], _interactionService.DisplayedSuccess);
    }

    [Fact]
    public async Task Apply_NoDetectedConsumerDeletesSkills()
    {
        // Only OpenCode loads .opencode/skills, and with no agent detected the default is GitHub Copilot.
        var skill = Skill(_context.Project, ".opencode", "aspire");
        await AgentConfigurationTestContext.WriteAsync(skill, "project skill");
        var migration = CreateMigration();

        await migration.ApplyAsync(_migrationContext, TestContext.Current.CancellationToken);

        Assert.False(File.Exists(_context.Copilot.GetIntegrationConfigPath(AgentConfigurationScope.Project, _context.Project)));
        Assert.False(File.Exists(_context.OpenCode.GetIntegrationConfigPath(AgentConfigurationScope.Project, _context.Project)));
        Assert.False(Directory.Exists(Path.GetDirectoryName(skill)));
        Assert.Equal([MigrationStrings.AgentSkillsDeletedNoAgents], _interactionService.DisplayedSuccess);
    }

    [Fact]
    public async Task Apply_NoEnabledSourcesDeletesSkills()
    {
        var skill = Skill(_context.Project, ".agents", "aspire");
        await AgentConfigurationTestContext.WriteAsync(skill, "project skill");
        await File.WriteAllTextAsync(Path.Combine(_context.Project.FullName, "aspire.config.json"),
            """{"skillSources":{"default":{"enabled":false}}}""");
        var migration = CreateMigration();

        await migration.ApplyAsync(_migrationContext, TestContext.Current.CancellationToken);

        Assert.False(Directory.Exists(Path.GetDirectoryName(skill)));
        Assert.False(File.Exists(_context.Copilot.GetIntegrationConfigPath(AgentConfigurationScope.Project, _context.Project)));
        Assert.Empty(_interactionService.DisplayedMessages);
    }

    [Fact]
    public async Task Apply_RegistrationFailureLeavesSkillsInPlace()
    {
        var skill = Skill(_context.Project, ".agents", "aspire");
        await AgentConfigurationTestContext.WriteAsync(skill, "project skill");
        await AgentConfigurationTestContext.WriteAsync(
            _context.Copilot.GetIntegrationConfigPath(AgentConfigurationScope.Project, _context.Project),
            """{"enabledPlugins":{"aspire@aspire-skills":false}}""");
        var migration = CreateMigration();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            migration.ApplyAsync(_migrationContext, TestContext.Current.CancellationToken));

        Assert.Equal(MigrationStrings.AgentSkillsRegistrationFailed, exception.Message);
        Assert.Contains("enabledPlugins.aspire@aspire-skills", Assert.Single(_interactionService.DisplayedErrors));
        Assert.Equal("project skill", await File.ReadAllTextAsync(skill));
    }

    [Fact]
    public async Task Apply_RegistersConfiguredSources()
    {
        var skill = Skill(_context.Project, ".agents", "aspire");
        await AgentConfigurationTestContext.WriteAsync(skill, "project skill");
        await File.WriteAllTextAsync(Path.Combine(_context.Project.FullName, "aspire.config.json"),
            """{"skillSources":{"contoso":{"repository":"contoso/skills"}}}""");
        var migration = CreateMigration();

        await migration.ApplyAsync(_migrationContext, TestContext.Current.CancellationToken);

        var configuration = AgentConfiguration.ReadObject(_context.Copilot.GetIntegrationConfigPath(AgentConfigurationScope.Project, _context.Project));
        Assert.NotNull(configuration);
        Assert.Equal("contoso/skills", configuration["extraKnownMarketplaces"]?["contoso"]?["source"]?["repo"]?.GetValue<string>());
        Assert.Contains($"[yellow]{SharedCommandStrings.SourceTrustWarning}[/]", _interactionService.DisplayedMessages.Select(message => message.Message));
        Assert.False(Directory.Exists(Path.GetDirectoryName(skill)));
    }

    [Fact]
    public async Task DetectAndApply_InspectionErrorBlocksApply()
    {
        var skill = Skill(_context.Project, ".agents", "aspire");
        await AgentConfigurationTestContext.WriteAsync(skill, "project skill");
        // OpenCode settings can add skill directories, so unreadable settings can hide copied skills.
        await File.WriteAllTextAsync(Path.Combine(_context.Project.FullName, "opencode.json"), "{broken");
        var migration = CreateMigration();

        var descriptor = await migration.DetectAsync(_migrationContext, TestContext.Current.CancellationToken);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            migration.ApplyAsync(_migrationContext, TestContext.Current.CancellationToken));

        Assert.NotNull(descriptor);
        Assert.Contains("opencode.json", descriptor.Detail);
        Assert.Equal(descriptor.Detail, exception.Message);
        Assert.Equal("project skill", await File.ReadAllTextAsync(skill));
    }

    [Fact]
    public async Task DetectAndApply_InspectionErrorWithoutCopiesIsNotPending()
    {
        await File.WriteAllTextAsync(Path.Combine(_context.Project.FullName, "opencode.json"), "{broken");
        var migration = CreateMigration();

        var descriptor = await migration.DetectAsync(_migrationContext, TestContext.Current.CancellationToken);
        await migration.ApplyAsync(_migrationContext, TestContext.Current.CancellationToken);

        Assert.Null(descriptor);
    }

    [Fact]
    public async Task DetectAndApply_UnreadableSkillWithoutCopiesIsPendingAndBlocksApply()
    {
        // A directory named SKILL.md cannot be opened as a file on any platform.
        Directory.CreateDirectory(Skill(_context.Project, ".agents", "aspire"));
        var migration = CreateMigration();

        var descriptor = await migration.DetectAsync(_migrationContext, TestContext.Current.CancellationToken);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            migration.ApplyAsync(_migrationContext, TestContext.Current.CancellationToken));

        Assert.NotNull(descriptor);
        Assert.Equal([], descriptor.Files);
        Assert.Contains("SKILL.md", descriptor.Detail);
        Assert.Equal(descriptor.Detail, exception.Message);
    }

    [Fact]
    public async Task Apply_SkillsBehindLinkedFolderAreLeftInPlace()
    {
        var shared = Directory.CreateDirectory(Path.Combine(_context.Home.FullName, "shared-skills"));
        var sharedSkill = Path.Combine(shared.FullName, "aspire", "SKILL.md");
        await AgentConfigurationTestContext.WriteAsync(sharedSkill, "shared skill");
        Directory.CreateDirectory(Path.Combine(_context.Project.FullName, ".agents"));
        TestSymlinkHelper.TryCreateSymlink(Path.Combine(_context.Project.FullName, ".agents", "skills"), shared.FullName);
        var migration = CreateMigration(AgentEnvironment.CopilotCli);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            migration.ApplyAsync(_migrationContext, TestContext.Current.CancellationToken));

        Assert.Equal(string.Format(CultureInfo.CurrentCulture, MigrationStrings.AgentSkillsLinked, ".agents/skills/aspire"), exception.Message);
        Assert.Equal("shared skill", await File.ReadAllTextAsync(sharedSkill));
        Assert.False(File.Exists(_context.Copilot.GetIntegrationConfigPath(AgentConfigurationScope.Project, _context.Project)));
    }

    private AgentSkillsMigration CreateMigration(AgentEnvironment detectedEnvironments = AgentEnvironment.None)
        => new(_context.ExecutionContext, new TestGitRepository(),
            new TestConfigurationService { SettingsFilePath = Path.Combine(_context.Home.FullName, ".aspire", "settings.json") },
            _context.Clients, new TestAgentEnvironmentDetector { DetectedEnvironments = detectedEnvironments }, _interactionService);

    private static string Skill(DirectoryInfo root, string agentDirectory, string name)
        => Path.Combine(root.FullName, agentDirectory, "skills", name, "SKILL.md");

    public void Dispose() => _context.Dispose();
}
