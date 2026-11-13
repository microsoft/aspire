// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Cli.Resources;
using Aspire.Cli.Tests.Agents;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Utils.EnvironmentChecker;

namespace Aspire.Cli.Tests.Utils;

public class AspireSkillsCheckTests(ITestOutputHelper outputHelper) : IDisposable
{
    private readonly AgentConfigurationTestContext _context = new(outputHelper);

    [Fact]
    public async Task CheckAsync_WithoutStaticSkills_ReturnsEmpty()
    {
        Assert.Empty(await CreateCheck().CheckAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CheckAsync_WithWorkspaceAndUserLevelCopies_ReportsBothWithoutModifyingThem()
    {
        var project = Path.Combine(_context.Project.FullName, ".claude", "skills", "aspire-init", "SKILL.md");
        var user = Path.Combine(_context.Home.FullName, ".agents", "skills", "aspire", "SKILL.md");
        await AgentConfigurationTestContext.WriteAsync(project, "project guidance");
        await AgentConfigurationTestContext.WriteAsync(user, "user guidance");

        var result = Assert.Single(await CreateCheck().CheckAsync(TestContext.Current.CancellationToken));

        Assert.Equal(AspireSkillsCheck.CheckName, result.Name);
        Assert.Equal(EnvironmentCheckStatus.Warning, result.Status);
        Assert.Equal(DoctorCommandStrings.AspireSkillsFix, result.Fix);
        Assert.Equal(
            $"""
            {string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.InitCommand_LocationWithAgents, ".claude/skills/aspire-init", "claude")}
            {string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.InitCommand_LocationWithAgents, "~/.agents/skills/aspire", "copilot, opencode")}
            """.ReplaceLineEndings("\n"),
            result.Details);
        Assert.Null(result.Files);
        Assert.Equal("project guidance", await File.ReadAllTextAsync(project));
        Assert.Equal("user guidance", await File.ReadAllTextAsync(user));
    }

    [Fact]
    public async Task CheckAsync_WorkspaceAtHome_ReportsSharedLocationOnce()
    {
        await AgentConfigurationTestContext.WriteAsync(
            Path.Combine(_context.Home.FullName, ".agents", "skills", "aspire", "SKILL.md"), "shared guidance");
        var check = new AspireSkillsCheck(new TestGitRepository(),
            TestExecutionContextHelper.CreateExecutionContext(_context.Home, homeDirectory: _context.Home));

        var result = Assert.Single(await check.CheckAsync(TestContext.Current.CancellationToken));

        Assert.Equal(DoctorCommandStrings.AspireSkillsFix, result.Fix);
        Assert.Equal(
            string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.InitCommand_LocationWithAgents, ".agents/skills/aspire", "copilot, opencode"),
            result.Details);
    }

    [Fact]
    public async Task CheckAsync_WithCancellation_PropagatesCancellation()
    {
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CreateCheck().CheckAsync(new CancellationToken(canceled: true)));
    }

    private AspireSkillsCheck CreateCheck()
        => new(new TestGitRepository(), _context.ExecutionContext);

    public void Dispose() => _context.Dispose();
}
