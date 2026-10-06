// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;
using Aspire.Cli.Agents;
using Aspire.Cli.Commands;
using Aspire.Cli.Migrations;
using Aspire.Cli.Resources;
using Aspire.Cli.Tests.Agents;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Aspire.Cli.Utils.EnvironmentChecker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.Migrations;

public class LocalAspireSkillsMigrationTests(ITestOutputHelper output) : IDisposable
{
    private readonly AgentConfigurationTestContext _context = new(output);

    [Fact]
    public async Task Doctor_ReportsLocalFilesAndOptionalMigrationWithoutWriting()
    {
        var path = await CreateSkillAsync(_context.Project, ".github");
        var interaction = new TestInteractionService();
        using var provider = CreateProvider(interaction, interactive: false);
        var migration = CreateMigration(provider, interaction);
        var check = new PendingMigrationsCheck([migration], NullLogger<PendingMigrationsCheck>.Instance);
        var before = Directory.GetFileSystemEntries(_context.Workspace.Path, "*", SearchOption.AllDirectories).Order().ToArray();

        var result = Assert.Single(await check.CheckAsync());

        Assert.Equal("local-aspire-skills", result.Name);
        Assert.Equal(EnvironmentCheckStatus.Warning, result.Status);
        Assert.Contains(path, result.Message);
        Assert.Equal(string.Format(AgentCommandStrings.LocalSkills_MigrationGuidance, AspireSkillsPluginConfiguration.RepositoryUrl), result.Fix);
        Assert.True(result.Metadata!["preservesLocalFiles"]!.GetValue<bool>());
        Assert.Empty(interaction.BooleanPromptCalls);
        Assert.Equal(before, Directory.GetFileSystemEntries(_context.Workspace.Path, "*", SearchOption.AllDirectories).Order());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApplyAsync_UsesChosenScopeAndPreservesCustomizedFilesOnRepeat(bool userScope)
    {
        var scope = userScope ? AgentConfigurationScope.User : AgentConfigurationScope.Project;
        var path = await CreateSkillAsync(userScope ? _context.Home : _context.Project, ".claude");
        var timestamp = File.GetLastWriteTimeUtc(path);
        var interaction = new TestInteractionService
        {
            PromptForSelectionsCallback = (_, choices, _, _) => [choices.Cast<IAgentEnvironmentScanner>().Single(agent => agent.Id == "claude")],
            PromptForSelectionCallback = (_, _, _, _) => scope
        };
        using var provider = CreateProvider(interaction, interactive: true);
        var migration = CreateMigration(provider, interaction);

        await migration.ApplyAsync(MigrationContext.CurrentDirectory, TestContext.Current.CancellationToken);
        var settings = Path.Combine(userScope ? _context.ClaudeDirectory : Path.Combine(_context.Project.FullName, ".claude"), "settings.json");
        var bytes = await File.ReadAllBytesAsync(settings);
        File.SetLastWriteTimeUtc(settings, new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var settingsTimestamp = File.GetLastWriteTimeUtc(settings);
        await migration.ApplyAsync(MigrationContext.CurrentDirectory, TestContext.Current.CancellationToken);

        Assert.Equal(bytes, await File.ReadAllBytesAsync(settings));
        Assert.Equal(settingsTimestamp, File.GetLastWriteTimeUtc(settings));
        Assert.Equal("Customized local Aspire skill.", await File.ReadAllTextAsync(path));
        Assert.Equal(timestamp, File.GetLastWriteTimeUtc(path));
        Assert.Contains(interaction.DisplayedMessages, message => message.Message == AgentCommandStrings.LocalSkills_MigrationReview);
        Assert.False(File.Exists(Path.Combine(userScope ? Path.Combine(_context.Project.FullName, ".claude") : _context.ClaudeDirectory, "settings.json")));
        Assert.Empty(_context.SkillInstaller.Requests);
    }

    [Fact]
    public async Task ApplyAsync_NonInteractive_UsesDetectedAgentsAndExistingUserScope()
    {
        await CreateSkillAsync(_context.Home, ".claude");
        _context.SetVariable("TERM_PROGRAM", "vscode");
        var interaction = new TestInteractionService
        {
            PromptForSelectionsCallback = (_, _, _, _) => throw new InvalidOperationException("No interactive agent prompt expected."),
            PromptForSelectionCallback = (_, _, _, _) => throw new InvalidOperationException("No interactive scope prompt expected.")
        };
        using var provider = CreateProvider(interaction, interactive: false);

        await CreateMigration(provider, interaction).ApplyAsync(MigrationContext.CurrentDirectory, TestContext.Current.CancellationToken);

        Assert.True(File.Exists(Path.Combine(_context.CopilotDirectory, "settings.json")));
        Assert.False(File.Exists(Path.Combine(_context.Project.FullName, ".github", "copilot", "settings.json")));
        Assert.Equal(0, _context.HookInstaller.Calls);
    }

    [Fact]
    public async Task ApplyAsync_BlockedRegistrationPreservesLocalFiles()
    {
        var path = await CreateSkillAsync(_context.Project, ".claude");
        await AgentConfigurationTestContext.WriteAsync(Path.Combine(_context.ClaudeDirectory, "settings.json"),
            """{"enabledPlugins":{"aspire@aspire-skills":false}}""");
        var interaction = new TestInteractionService();
        using var provider = CreateProvider(interaction, interactive: false);

        await CreateMigration(provider, interaction).ApplyAsync(MigrationContext.CurrentDirectory, TestContext.Current.CancellationToken);

        Assert.Equal("Customized local Aspire skill.", await File.ReadAllTextAsync(path));
        Assert.False(File.Exists(Path.Combine(_context.Project.FullName, ".claude", "settings.json")));
        Assert.Contains(interaction.DisplayedMessages, message => message.Message == AgentCommandStrings.LocalSkills_MigrationIncomplete);
    }

    [Fact]
    public async Task ApplyAsync_NoDetectedAgentsPreservesFilesAndExplainsExplicitSelection()
    {
        var path = await CreateSkillAsync(_context.Project, ".agents");
        var interaction = new TestInteractionService();
        using var provider = CreateProvider(interaction, interactive: false);

        await CreateMigration(provider, interaction).ApplyAsync(MigrationContext.CurrentDirectory, TestContext.Current.CancellationToken);

        Assert.Equal("Customized local Aspire skill.", await File.ReadAllTextAsync(path));
        Assert.Empty(_context.Home.EnumerateFileSystemInfos());
        Assert.Contains(interaction.DisplayedMessages, message => message.Message == AgentCommandStrings.LocalSkills_MigrationIncomplete);
    }

    [Fact]
    public async Task DetectAndApply_ResolveTheSelectedAppHostRepositoryNotTheWorkingDirectory()
    {
        var other = _context.Workspace.CreateDirectory("other-repository");
        Directory.CreateDirectory(Path.Combine(other.FullName, ".git"));
        var appHost = other.CreateSubdirectory("AppHost");
        var path = await CreateSkillAsync(other, ".github");
        _context.SetVariable("TERM_PROGRAM", "vscode");
        var interaction = new TestInteractionService();
        using var provider = CreateProvider(interaction, interactive: false);

        var migration = CreateMigration(provider, interaction);
        var context = new MigrationContext(new FileInfo(Path.Combine(appHost.FullName, "AppHost.csproj")));
        var descriptor = await migration.DetectAsync(context, TestContext.Current.CancellationToken);
        await migration.ApplyAsync(context, TestContext.Current.CancellationToken);

        Assert.NotNull(descriptor);
        Assert.Equal(other.FullName, descriptor.Metadata!["workspaceRoot"]!.GetValue<string>());
        Assert.Contains(path, descriptor.Detail);
        Assert.True(File.Exists(Path.Combine(other.FullName, ".github", "copilot", "settings.json")));
        Assert.Empty(_context.Project.EnumerateFileSystemInfos());
        Assert.Empty(_context.Home.EnumerateFileSystemInfos());
    }

    [Theory]
    [InlineData("App.sln", "Microsoft Visual Studio Solution File, Format Version 12.00")]
    [InlineData("App.slnx", "<Solution><Project Path=\"AppHost\\AppHost.csproj\" /></Solution>")]
    public async Task DoctorAndUpdate_FindTheSameSkillsInANonGitSolution(string solutionName, string solution)
    {
        var appHost = _context.Project.CreateSubdirectory("AppHost");
        var appHostFile = new FileInfo(Path.Combine(appHost.FullName, "AppHost.csproj"));
        await AgentConfigurationTestContext.WriteAsync(appHostFile.FullName, "<Project />");
        await AgentConfigurationTestContext.WriteAsync(Path.Combine(_context.Project.FullName, solutionName), solution);
        var path = await CreateSkillAsync(_context.Project, ".github");
        var timestamp = File.GetLastWriteTimeUtc(path);
        _context.SetVariable("TERM_PROGRAM", "vscode");
        var interaction = new TestInteractionService();
        using var provider = CreateProvider(interaction, interactive: false);
        var migration = CreateMigration(provider, interaction);
        var check = new PendingMigrationsCheck([migration], NullLogger<PendingMigrationsCheck>.Instance);
        var before = Directory.GetFileSystemEntries(_context.Workspace.Path, "*", SearchOption.AllDirectories).Order().ToArray();

        var doctor = Assert.Single(await check.CheckAsync());
        var context = new MigrationContext(appHostFile);
        var update = await migration.DetectAsync(context, TestContext.Current.CancellationToken);

        Assert.Equal(EnvironmentCheckStatus.Warning, doctor.Status);
        Assert.NotNull(update);
        Assert.Equal(_context.Project.FullName, update.Metadata!["workspaceRoot"]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(doctor.Metadata!["files"], update.Metadata["files"]));
        Assert.Equal(before, Directory.GetFileSystemEntries(_context.Workspace.Path, "*", SearchOption.AllDirectories).Order());

        await migration.ApplyAsync(context, TestContext.Current.CancellationToken);

        Assert.True(File.Exists(Path.Combine(_context.Project.FullName, ".github", "copilot", "settings.json")));
        Assert.False(Directory.Exists(Path.Combine(appHost.FullName, ".github")));
        Assert.Equal("Customized local Aspire skill.", await File.ReadAllTextAsync(path));
        Assert.Equal(timestamp, File.GetLastWriteTimeUtc(path));
        Assert.Contains(interaction.DisplayedMessages, message => message.Message == AgentCommandStrings.LocalSkills_MigrationReview);
        Assert.Equal(0, _context.HookInstaller.Calls);
    }

    [Fact]
    public async Task DetectAndApply_UseTheSelectedAppHostForActiveSkillAndAgentDiscovery()
    {
        Directory.CreateDirectory(Path.Combine(_context.Project.FullName, ".git"));
        var appHost = _context.Project.CreateSubdirectory("active");
        var sibling = _context.Project.CreateSubdirectory("sibling");
        var selected = await CreateSkillAsync(appHost, ".claude");
        var unrelated = await CreateSkillAsync(sibling, ".claude");
        var interaction = new TestInteractionService();
        using var provider = CreateProvider(interaction, interactive: false);
        var migration = CreateMigration(provider, interaction);
        var context = new MigrationContext(new FileInfo(Path.Combine(appHost.FullName, "AppHost.csproj")));

        var descriptor = await migration.DetectAsync(context, TestContext.Current.CancellationToken);
        await migration.ApplyAsync(context, TestContext.Current.CancellationToken);

        Assert.NotNull(descriptor);
        Assert.Equal(selected, Assert.Single(descriptor.Metadata!["files"]!.AsArray())!["path"]!.GetValue<string>());
        Assert.True(File.Exists(Path.Combine(_context.Project.FullName, ".claude", "settings.json")));
        Assert.False(File.Exists(Path.Combine(appHost.FullName, ".claude", "settings.json")));
        Assert.False(File.Exists(Path.Combine(sibling.FullName, ".claude", "settings.json")));
        Assert.Equal("Customized local Aspire skill.", await File.ReadAllTextAsync(selected));
        Assert.Equal("Customized local Aspire skill.", await File.ReadAllTextAsync(unrelated));
        Assert.Contains(interaction.DisplayedMessages, message => message.Message == AgentCommandStrings.LocalSkills_MigrationReview);
    }

    [Fact]
    public async Task DetectAndApply_DoNotAdoptSolutionsInOrAboveHome()
    {
        await AgentConfigurationTestContext.WriteAsync(Path.Combine(_context.Workspace.Path, "Unrelated.slnx"), "<Solution />");
        await AgentConfigurationTestContext.WriteAsync(Path.Combine(_context.Home.FullName, "Unrelated.slnx"), "<Solution />");
        await CreateSkillAsync(_context.Workspace.WorkspaceRoot, ".github");
        await CreateSkillAsync(_context.Home, ".github");
        var appHost = _context.Home.CreateSubdirectory(Path.Combine("code", "AppHost"));
        var selected = await CreateSkillAsync(appHost, ".claude");
        var interaction = new TestInteractionService();
        using var provider = CreateProvider(interaction, interactive: false);
        var migration = CreateMigration(provider, interaction);
        var context = new MigrationContext(new FileInfo(Path.Combine(appHost.FullName, "AppHost.csproj")));

        var descriptor = await migration.DetectAsync(context, TestContext.Current.CancellationToken);
        await migration.ApplyAsync(context, TestContext.Current.CancellationToken);

        Assert.NotNull(descriptor);
        Assert.Equal(appHost.FullName, descriptor.Metadata!["workspaceRoot"]!.GetValue<string>());
        Assert.Equal(selected, Assert.Single(descriptor.Metadata["files"]!.AsArray())!["path"]!.GetValue<string>());
        Assert.True(File.Exists(Path.Combine(appHost.FullName, ".claude", "settings.json")));
        Assert.False(File.Exists(Path.Combine(_context.Home.FullName, ".github", "copilot", "settings.json")));
        Assert.False(File.Exists(Path.Combine(_context.Workspace.Path, ".github", "copilot", "settings.json")));
        Assert.Empty(_context.Project.EnumerateFileSystemInfos());
    }

    [Fact]
    public async Task DetectAndApply_DoNotUseAnUnrelatedAncestorConfiguration()
    {
        await AgentConfigurationTestContext.WriteAsync(Path.Combine(_context.Workspace.Path, "aspire.config.json"), "{}");
        var unrelated = await CreateSkillAsync(_context.Workspace.WorkspaceRoot, ".github");
        var selected = await CreateSkillAsync(_context.Project, ".claude");
        var interaction = new TestInteractionService();
        using var provider = CreateProvider(interaction, interactive: false);
        var migration = CreateMigration(provider, interaction);

        var descriptor = await migration.DetectAsync(MigrationContext.CurrentDirectory, TestContext.Current.CancellationToken);
        await migration.ApplyAsync(MigrationContext.CurrentDirectory, TestContext.Current.CancellationToken);

        Assert.NotNull(descriptor);
        Assert.Equal(_context.Project.FullName, descriptor.Metadata!["workspaceRoot"]!.GetValue<string>());
        Assert.Equal(selected, Assert.Single(descriptor.Metadata["files"]!.AsArray())!["path"]!.GetValue<string>());
        Assert.True(File.Exists(Path.Combine(_context.Project.FullName, ".claude", "settings.json")));
        Assert.False(File.Exists(Path.Combine(_context.Workspace.Path, ".github", "copilot", "settings.json")));
        Assert.Equal("Customized local Aspire skill.", await File.ReadAllTextAsync(unrelated));
    }

    [Fact]
    public async Task ApplyAsync_RedetectsDeletedFilesAndDoesNotConfigure()
    {
        var path = await CreateSkillAsync(_context.Project, ".claude");
        var interaction = new TestInteractionService();
        using var provider = CreateProvider(interaction, interactive: false);
        var migration = CreateMigration(provider, interaction);
        Assert.NotNull(await migration.DetectAsync(MigrationContext.CurrentDirectory, TestContext.Current.CancellationToken));
        File.Delete(path);

        await migration.ApplyAsync(MigrationContext.CurrentDirectory, TestContext.Current.CancellationToken);

        Assert.Empty(interaction.BooleanPromptCalls);
        Assert.Empty(interaction.DisplayedMessages);
        Assert.False(File.Exists(Path.Combine(_context.Project.FullName, ".claude", "settings.json")));
    }

    [Fact]
    public async Task DetectAndApply_PropagateCancellation()
    {
        var interaction = new TestInteractionService();
        using var provider = CreateProvider(interaction, interactive: false);
        var migration = CreateMigration(provider, interaction);
        var token = new CancellationToken(canceled: true);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => migration.DetectAsync(MigrationContext.CurrentDirectory, token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => migration.ApplyAsync(MigrationContext.CurrentDirectory, token));
        Assert.Empty(interaction.DisplayedMessages);
    }

    private ServiceProvider CreateProvider(TestInteractionService interaction, bool interactive)
    {
        var services = CliTestHelper.CreateServiceCollection(_context.Workspace, output, options =>
        {
            options.WorkingDirectory = _context.Project;
            options.InteractionServiceFactory = _ => interaction;
            options.CliHostEnvironmentFactory = _ => interactive ? TestHelpers.CreateInteractiveHostEnvironment() : TestHelpers.CreateNonInteractiveHostEnvironment();
        });
        services.AddSingleton(_context.ExecutionContext);
        services.AddSingleton<IEnvironment>(_context.Environment);
        services.RemoveAll<IAgentEnvironmentScanner>();
        foreach (var scanner in _context.Environments)
        {
            services.AddSingleton(scanner);
        }
        return services.BuildServiceProvider();
    }

    private LocalAspireSkillsMigration CreateMigration(IServiceProvider provider, TestInteractionService interaction)
        => new(_context.Environments, provider.GetRequiredService<AgentInitCommand>(), _context.ExecutionContext, _context.Environment, interaction);

    private static async Task<string> CreateSkillAsync(DirectoryInfo root, string directory)
    {
        var path = Path.Combine(root.FullName, directory, "skills", "aspire", "SKILL.md");
        await AgentConfigurationTestContext.WriteAsync(path, "Customized local Aspire skill.");
        return path;
    }

    public void Dispose() => _context.Dispose();
}
