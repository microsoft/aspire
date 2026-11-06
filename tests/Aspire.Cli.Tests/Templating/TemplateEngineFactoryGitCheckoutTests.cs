// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Git;
using Aspire.Cli.Resources;
using Aspire.Cli.Templating;
using Aspire.Cli.Tests.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;

namespace Aspire.Cli.Tests.Templating;

public class TemplateEngineFactoryGitCheckoutTests(ITestOutputHelper outputHelper)
{
    [Theory]
    [InlineData("sha1", "settings[1].json")]
    [InlineData("sha256", "settings with spaces.json")]
    [InlineData("sha1", "\u65e5\u672c\u8a9e.json")]
    public async Task RepositoryTemplate_ChecksOutExactCommit(string objectFormat, string path)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        byte[] content = [0, 1, 10, 13, 10, 0xc3, 0xa9, 0xff, 42];
        var (repository, commit) = await CreateRepositoryAsync(workspace, path, content, objectFormat);
        using var provider = CreateServices(workspace).BuildServiceProvider();
        var factory = GetFactory(provider);

        Assert.Single(await factory.GetTemplatesAsync(
            new TemplateSource(".", Path.GetRelativePath(workspace.WorkspaceRoot.FullName, repository.FullName), commit),
            TestContext.Current.CancellationToken));
        var destination = GetCheckout(workspace, commit);
        var actual = await File.ReadAllBytesAsync(Path.Combine(destination, path));

        Assert.Equal(content, actual);
        Assert.Equal(commit, (await File.ReadAllTextAsync(Path.Combine(Directory.GetParent(destination)!.FullName, ".git", "HEAD"))).Trim());
    }

    [Fact]
    public async Task RepositoryTemplate_ChecksOutPinnedCommitOutsideDefaultBranchTip()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        byte[] content = [1, 2, 3];
        var (repository, commit) = await CreateRepositoryAsync(workspace, "content", content);
        await File.WriteAllBytesAsync(Path.Combine(repository.FullName, "content"), [4, 5, 6]);
        await GitTestHelper.RunGitAsync(repository.FullName, outputHelper, "commit", "-am", "Move branch");
        using var provider = CreateServices(workspace).BuildServiceProvider();
        var factory = GetFactory(provider);

        Assert.Single(await factory.GetTemplatesAsync(new TemplateSource(".", repository.FullName, commit), TestContext.Current.CancellationToken));

        Assert.Equal(content, await File.ReadAllBytesAsync(Path.Combine(GetCheckout(workspace, commit), "content")));
    }

    [Fact]
    public async Task RepositoryTemplate_PrefersBranchAndPeelsExplicitAnnotatedTag()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var (repository, tagCommit) = await CreateRepositoryAsync(workspace, "content", [1, 2, 3]);
        await GitTestHelper.RunGitAsync(repository.FullName, outputHelper, "tag", "-a", "same", "-m", "Annotated tag");
        await File.WriteAllBytesAsync(Path.Combine(repository.FullName, "content"), [4, 5, 6]);
        await GitTestHelper.RunGitAsync(repository.FullName, outputHelper, "commit", "-am", "Move branch");
        await GitTestHelper.RunGitAsync(repository.FullName, outputHelper, "branch", "same");
        var branchCommit = (await File.ReadAllTextAsync(Path.Combine(repository.FullName, ".git", "refs", "heads", "same"))).Trim();
        using var provider = CreateServices(workspace).BuildServiceProvider();
        var factory = GetFactory(provider);

        Assert.Single(await factory.GetTemplatesAsync(new TemplateSource(".", repository.FullName, "same"), TestContext.Current.CancellationToken));
        Assert.Equal(new byte[] { 4, 5, 6 }, await File.ReadAllBytesAsync(Path.Combine(GetCheckout(workspace, branchCommit), "content")));
        Assert.Single(await factory.GetTemplatesAsync(new TemplateSource(".", repository.FullName, "refs/tags/same"), TestContext.Current.CancellationToken));
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(Path.Combine(GetCheckout(workspace, tagCommit), "content")));
    }

    [Fact]
    public async Task RepositoryTemplate_FailureDoesNotLogPrivateRepositoryOrDiagnostics()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await GitTestHelper.EnsureGitAvailableAsync(outputHelper);
        var logger = new FakeLogger<GitCommandRunner>();
        var services = CreateServices(workspace);
        services.AddSingleton<ILogger<GitCommandRunner>>(logger);
        using var provider = services.BuildServiceProvider();

        var exception = await Assert.ThrowsAsync<TemplateDiscoveryException>(() => GetFactory(provider).GetTemplatesAsync(
            new TemplateSource(".", Path.Combine(workspace.Path, "private-repository-secret-marker"), null), TestContext.Current.CancellationToken));

        Assert.Equal(CliExitCodes.FailedToInstallTemplates, exception.ExitCode);
        Assert.Equal(string.Format(NewCommandStrings.TemplateRepositoryGitExited, 128, ErrorStrings.GitRepositoryUnavailable), exception.Message);
        Assert.Null(exception.InnerException);
        var logs = logger.Collector.GetSnapshot();
        Assert.Collection(logs,
            entry => Assert.Equal("Starting Git process.", entry.Message),
            entry => Assert.StartsWith("Git process ", entry.Message),
            entry => Assert.StartsWith("Git process ", entry.Message),
            entry => Assert.StartsWith("Git process completed after ", entry.Message));
        Assert.All(logs, entry => Assert.False(entry.Message.Contains("private-repository-secret-marker", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task RepositoryTemplate_DoesNotModifySourceRepository()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var (repository, commit) = await CreateRepositoryAsync(workspace, "content", [1, 2, 3]);
        var services = CreateServices(workspace);
        using var provider = services.BuildServiceProvider();
        var factory = GetFactory(provider);

        Assert.Single(await factory.GetTemplatesAsync(new TemplateSource(".", repository.FullName, commit), TestContext.Current.CancellationToken));

        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(Path.Combine(GetCheckout(workspace, commit), "content")));
        Assert.Equal(commit, (await File.ReadAllTextAsync(Path.Combine(repository.FullName, ".git", "refs", "heads", "main"))).Trim());
    }

    private IServiceCollection CreateServices(TemporaryWorkspace workspace)
    {
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        services.AddSingleton<IGitClient, GitClient>();
        return services;
    }

    private static TemplateEngineFactory GetFactory(IServiceProvider provider) =>
        Assert.Single(provider.GetServices<ITemplateFactory>().OfType<TemplateEngineFactory>());

    private static string GetCheckout(TemporaryWorkspace workspace, string commit)
    {
        var cacheRoot = Path.Combine(workspace.Path, ".aspire", "cache", "git");
        var cached = Assert.Single(Directory.GetDirectories(cacheRoot),
            directory => File.ReadAllText(Path.Combine(directory, ".git", "HEAD")).Trim() == commit);
        return Path.Combine(cached, "repository");
    }

    private async Task<(DirectoryInfo Repository, string Commit)> CreateRepositoryAsync(
        TemporaryWorkspace workspace, string path, byte[] content, string objectFormat = "sha1")
    {
        await GitTestHelper.EnsureGitAvailableAsync(outputHelper);
        var repository = workspace.CreateDirectory("source");
        var file = Path.Combine(repository.FullName, path);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllBytesAsync(file, content);
        var configuration = repository.CreateSubdirectory(".template.config");
        await File.WriteAllTextAsync(Path.Combine(configuration.FullName, "template.json"),
            """{ "identity": "Aspire.GitCheckout", "name": "Git checkout", "shortName": "git-checkout" }""");
        await GitTestHelper.RunGitAsync(repository.FullName, outputHelper, "init", "--initial-branch=main", $"--object-format={objectFormat}");
        await GitTestHelper.ConfigureGitIdentityAsync(repository.FullName, outputHelper);
        await GitTestHelper.RunGitAsync(repository.FullName, outputHelper, "config", "core.autocrlf", "false");
        await GitTestHelper.RunGitAsync(repository.FullName, outputHelper, "add", ".");
        await GitTestHelper.RunGitAsync(repository.FullName, outputHelper, "commit", "-m", "Add content");
        var commit = (await File.ReadAllTextAsync(Path.Combine(repository.FullName, ".git", "refs", "heads", "main"))).Trim();
        return (repository, commit);
    }
}
