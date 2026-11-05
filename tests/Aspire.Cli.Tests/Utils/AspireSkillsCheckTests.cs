// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Agents;
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
    public async Task CheckAsync_WithDisabledSources_StillWarnsAboutRegisteredStaticSkills()
    {
        await _context.ConfigureIntegrationAsync(AgentConfigurationScope.Global, [_context.Copilot]);
        var skill = Path.Combine(_context.Home.FullName, ".agents", "skills", "aspire", "SKILL.md");
        await AgentConfigurationTestContext.WriteAsync(skill, "user guidance");
        await AgentConfigurationTestContext.WriteAsync(Path.Combine(_context.Project.FullName, "aspire.config.json"),
            """{"skillSources":{"default":{"enabled":false}}}""");

        var result = Assert.Single(await CreateCheck().CheckAsync(TestContext.Current.CancellationToken));

        Assert.Equal(EnvironmentCheckStatus.Warning, result.Status);
        Assert.Equal(["~/.agents/skills/aspire/SKILL.md"], result.Files);
        Assert.True(_context.Copilot.HasIntegrationRegistration(_context.Project, AgentConfigurationScope.Global, _context.Sources));
        Assert.Equal("user guidance", await File.ReadAllTextAsync(skill));
    }

    [Fact]
    public async Task CheckAsync_WithSkillsOutsideWorkspaceAndHome_PreservesAbsoluteDisplayPath()
    {
        var external = _context.Workspace.CreateDirectory("[external]");
        _context.SetVariable("OPENCODE_CONFIG_DIR", external.FullName);
        var skill = Path.Combine(external.FullName, "skills", "aspire", "SKILL.md");
        await AgentConfigurationTestContext.WriteAsync(skill, "static guidance");

        var result = Assert.Single(await CreateCheck().CheckAsync(TestContext.Current.CancellationToken));

        Assert.Equal([skill], result.Files);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CheckAsync_WithUnreadableSkill_ReportsInspectionWarning(bool global)
    {
        var root = global ? _context.Home : _context.Project;
        var unreadable = Path.Combine(root.FullName, ".agents", "skills", "aspire", "SKILL.md");
        Directory.CreateDirectory(unreadable);

        var result = Assert.Single(await CreateCheck().CheckAsync(TestContext.Current.CancellationToken));

        Assert.Equal(EnvironmentCheckStatus.Warning, result.Status);
        Assert.Equal(DoctorCommandStrings.AspireSkillsInspectionFailedMessage, result.Message);
        Assert.Contains(unreadable, result.Details, StringComparison.Ordinal);
        Assert.Null(result.Fix);
        Assert.True(Directory.Exists(unreadable));
    }

    [Fact]
    public async Task CheckAsync_WithCancellation_PropagatesCancellation()
    {
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CreateCheck().CheckAsync(new CancellationToken(canceled: true)));
    }

    private AspireSkillsCheck CreateCheck()
        => new(_context.Clients, new TestGitRepository(), _context.ExecutionContext);

    public void Dispose() => _context.Dispose();
}
