// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Resources;
using Aspire.Cli.Tests.Agents;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Utils.EnvironmentChecker;

namespace Aspire.Cli.Tests.Utils;

public class DeprecatedAgentConfigCheckTests(ITestOutputHelper outputHelper) : IDisposable
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

        Assert.Equal(DeprecatedAgentConfigCheck.AspireSkillsCheckName, result.Name);
        Assert.Equal(EnvironmentCheckStatus.Warning, result.Status);
        Assert.Equal(DoctorCommandStrings.AspireSkillsFix, result.Fix);
        Assert.Equal([".claude/skills/aspire-init", "~/.agents/skills/aspire"], result.Files);
        Assert.Equal("project guidance", await File.ReadAllTextAsync(project));
        Assert.Equal("user guidance", await File.ReadAllTextAsync(user));
    }

    [Fact]
    public async Task CheckAsync_WithDeprecatedMcpConfigAndStaticSkills_ReportsSeparateWarnings()
    {
        await AgentConfigurationTestContext.WriteAsync(Path.Combine(_context.Project.FullName, ".mcp.json"),
            """{"mcpServers":{"aspire":{"command":"aspire","args":["mcp","start"]}}}""");
        await AgentConfigurationTestContext.WriteAsync(
            Path.Combine(_context.Project.FullName, ".agents", "skills", "aspire", "SKILL.md"), "static guidance");

        var results = await CreateCheck().CheckAsync(TestContext.Current.CancellationToken);

        Assert.Collection(results,
            result =>
            {
                Assert.Equal(DeprecatedAgentConfigCheck.GetCheckName("Claude Code"), result.Name);
                Assert.Equal(EnvironmentCheckStatus.Warning, result.Status);
                Assert.Equal(AgentCommandStrings.DeprecatedConfigFix, result.Fix);
                Assert.Null(result.Files);
            },
            result =>
            {
                Assert.Equal(DeprecatedAgentConfigCheck.AspireSkillsCheckName, result.Name);
                Assert.Equal(EnvironmentCheckStatus.Warning, result.Status);
                Assert.Equal(DoctorCommandStrings.AspireSkillsFix, result.Fix);
                Assert.Equal([".agents/skills/aspire"], result.Files);
            });
    }

    [Fact]
    public async Task CheckAsync_WithCancellation_PropagatesCancellation()
    {
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CreateCheck().CheckAsync(new CancellationToken(canceled: true)));
    }

    private DeprecatedAgentConfigCheck CreateCheck()
        => new(new TestGitRepository(), _context.ExecutionContext);

    public void Dispose() => _context.Dispose();
}
