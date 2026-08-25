// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.TestUtilities;
using Xunit;

namespace Infrastructure.Tests;

[RequiresTools(["git", "pwsh"])]
public sealed class TrustedExtensionReleasePrTests : IDisposable
{
    private const string BotLogin = "aspire-repo-bot[bot]";
    private const string BasePackageJson = """{"name":"aspire-vscode","version":"1.18.0","scripts":{"test":"mocha"}}""";
    private const string ChangelogPreamble = "# Aspire VS Code Extension Changelog\n\n";
    private const string BaseChangelogBody = "## v1.18.0\n\nExisting notes.\n";
    private const string BaseChangelog = ChangelogPreamble + BaseChangelogBody;
    private const string TrustedPackageJson = """{"name":"aspire-vscode","version":"1.19.0","scripts":{"test":"mocha"}}""";
    private const string TrustedChangelogEntry = "## v1.19.0\n\nNew notes.\n\n";
    private const string TrustedChangelog = ChangelogPreamble + TrustedChangelogEntry + BaseChangelogBody;

    private readonly TemporaryWorkspace _workspace;
    private readonly ITestOutputHelper _output;
    private readonly string _scriptPath;
    private readonly string _githubOutputPath;
    private readonly string _extensionPath;
    private readonly string _baseSha;

    public TrustedExtensionReleasePrTests(ITestOutputHelper output)
    {
        _output = output;
        _workspace = TemporaryWorkspace.Create(output);
        _scriptPath = Path.Combine(
            RepoRoot.Path,
            ".github",
            "actions",
            "is-trusted-extension-release-pr",
            "validate.ps1");
        _githubOutputPath = Path.Combine(_workspace.Path, "github-output.txt");
        _extensionPath = Path.Combine(_workspace.Path, "extension");

        Directory.CreateDirectory(_extensionPath);
        File.WriteAllText(PackageJsonPath, BasePackageJson);
        File.WriteAllText(ChangelogPath, BaseChangelog);

        Git("init", "--initial-branch=main");
        Git("config", "user.name", "Extension Release Test");
        Git("config", "user.email", "extension-release-test@example.com");
        Git("add", "extension/package.json", "extension/CHANGELOG.md");
        Git("commit", "-m", "Base extension release");
        _baseSha = Git("rev-parse", "HEAD");
    }

    public void Dispose() => _workspace.Dispose();

    [Fact]
    public async Task TrustsReleaseEntryInsertedAfterUnchangedChangelogTitle()
    {
        var headSha = CommitTrustedChanges();

        var result = await RunValidator(headSha);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("is_trusted=true", ReadGitHubOutput());
    }

    [Fact]
    public async Task TrustsMaintainerFinalizationOfBotAuthoredRelease()
    {
        var headSha = CommitTrustedChanges();

        var result = await RunValidator(headSha, actor: "maintainer");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("is_trusted=true", ReadGitHubOutput());
    }

    [Fact]
    public async Task TrustsExpectedPatchWhenBaseTipAdvancedAfterBranchPoint()
    {
        Git("switch", "-c", "extension-release/v1.19.0");
        var headSha = CommitTrustedChanges();

        Git("switch", "main");
        File.WriteAllText(Path.Combine(_workspace.Path, "base-only.txt"), "unrelated base change");
        var advancedBaseSha = CommitChanges();

        var result = await RunValidator(headSha, baseSha: advancedBaseSha);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("is_trusted=true", ReadGitHubOutput());
    }

    [Theory]
    [InlineData("develop", "microsoft/aspire", "extension-release/v1.19.0", BotLogin)]
    [InlineData("main", "someone/aspire", "extension-release/v1.19.0", BotLogin)]
    [InlineData("main", "microsoft/aspire", "extension-release/v1.19.0", "someone")]
    [InlineData("main", "microsoft/aspire", "extension-release/1.19.0", BotLogin)]
    public async Task RejectsUntrustedIdentityMetadata(
        string baseRef,
        string headRepo,
        string headRef,
        string author)
    {
        var headSha = CommitTrustedChanges();

        var result = await RunValidator(headSha, baseRef, headRepo, headRef, author);

        AssertRejected(result);
    }

    [Fact]
    public async Task RejectsExtraChangedFile()
    {
        WriteTrustedChanges();
        File.WriteAllText(Path.Combine(_workspace.Path, "unexpected.txt"), "unexpected");
        var headSha = CommitChanges();

        var result = await RunValidator(headSha);

        AssertRejected(result);
    }

    [Fact]
    public async Task RejectsPackageScriptChanges()
    {
        File.WriteAllText(
            PackageJsonPath,
            """{"name":"aspire-vscode","version":"1.19.0","scripts":{"test":"mocha","lint":"eslint ."}}""");
        File.WriteAllText(ChangelogPath, TrustedChangelog);
        var headSha = CommitChanges();

        var result = await RunValidator(headSha);

        AssertRejected(result);
    }

    [Fact]
    public async Task RejectsUnchangedPackageVersion()
    {
        File.WriteAllText(
            PackageJsonPath,
            """
            {
              "name": "aspire-vscode",
              "version": "1.18.0",
              "scripts": {
                "test": "mocha"
              }
            }
            """);
        File.WriteAllText(
            ChangelogPath,
            ChangelogPreamble + "## v1.18.0\n\nNew notes.\n\n" + BaseChangelogBody);
        var headSha = CommitChanges();

        var result = await RunValidator(headSha);

        AssertRejected(result);
    }

    [Theory]
    [InlineData("# Aspire VS Code Extension Changelog\n\n## v1.19.0\n\nNew notes.\n\n## v1.18.0\n\nEdited notes.\n")]
    [InlineData("# Aspire VS Code Extension Changelog\n\n## v1.19.0\n\nNew notes.\n")]
    [InlineData("# Different Changelog\n\n## v1.19.0\n\nNew notes.\n\n## v1.18.0\n\nExisting notes.\n")]
    [InlineData("# Aspire VS Code Extension Changelog\n\n## v1.19.0\n\nNew notes.\n\n## v1.19.0\n\nMore notes.\n\n## v1.18.0\n\nExisting notes.\n")]
    public async Task RejectsChangelogChangesThatDoNotPreserveBaseContent(string headChangelog)
    {
        File.WriteAllText(PackageJsonPath, TrustedPackageJson);
        File.WriteAllText(ChangelogPath, headChangelog);
        var headSha = CommitChanges();

        var result = await RunValidator(headSha);

        AssertRejected(result);
    }

    [Fact]
    public async Task RejectsChangelogHeadingThatDoesNotMatchPackageVersion()
    {
        File.WriteAllText(PackageJsonPath, TrustedPackageJson);
        File.WriteAllText(
            ChangelogPath,
            ChangelogPreamble + "## v1.20.0\n\nNew notes.\n\n" + BaseChangelogBody);
        var headSha = CommitChanges();

        var result = await RunValidator(headSha);

        AssertRejected(result);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RejectsInvalidRevisionSha(bool invalidBaseSha)
    {
        var headSha = CommitTrustedChanges();
        var baseSha = invalidBaseSha ? "not-a-base-sha" : _baseSha;
        headSha = invalidBaseSha ? headSha : "not-a-head-sha";

        var result = await RunValidator(headSha, baseSha: baseSha);

        AssertRejected(result);
    }

    private string PackageJsonPath => Path.Combine(_extensionPath, "package.json");
    private string ChangelogPath => Path.Combine(_extensionPath, "CHANGELOG.md");

    private string CommitTrustedChanges()
    {
        WriteTrustedChanges();

        return CommitChanges();
    }

    private void WriteTrustedChanges()
    {
        File.WriteAllText(PackageJsonPath, TrustedPackageJson);
        File.WriteAllText(ChangelogPath, TrustedChangelog);
    }

    private string CommitChanges()
    {
        Git("add", "--all");
        Git("commit", "-m", "Update extension release");

        return Git("rev-parse", "HEAD");
    }

    private async Task<CommandResult> RunValidator(
        string headSha,
        string baseRef = "main",
        string headRepo = "microsoft/aspire",
        string headRef = "extension-release/v1.19.0",
        string author = BotLogin,
        string actor = BotLogin,
        string? baseSha = null)
    {
        File.WriteAllText(_githubOutputPath, string.Empty);

        using var command = new PowerShellCommand(_scriptPath, _output)
            .WithWorkingDirectory(_workspace.Path)
            .WithTimeout(TimeSpan.FromMinutes(1))
            .WithEnvironmentVariable("GITHUB_OUTPUT", _githubOutputPath)
            .WithEnvironmentVariable("REPOSITORY", "microsoft/aspire")
            .WithEnvironmentVariable("BASE_REF", baseRef)
            .WithEnvironmentVariable("HEAD_REPO", headRepo)
            .WithEnvironmentVariable("HEAD_REF", headRef)
            .WithEnvironmentVariable("AUTHOR", author)
            .WithEnvironmentVariable("ACTOR", actor)
            .WithEnvironmentVariable("BASE_SHA", baseSha ?? _baseSha)
            .WithEnvironmentVariable("HEAD_SHA", headSha);

        return await command.ExecuteAsync();
    }

    private void AssertRejected(CommandResult result)
    {
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("is_trusted=false", ReadGitHubOutput());
    }

    private string ReadGitHubOutput() => File.ReadAllText(_githubOutputPath).Trim();

    private string Git(params string[] args) => GitCli.Run(_workspace.Path, args);
}
