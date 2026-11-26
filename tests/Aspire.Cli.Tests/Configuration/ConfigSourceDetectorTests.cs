// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Configuration;
using Aspire.Cli.Tests.Utils;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;

namespace Aspire.Cli.Tests.Configuration;

public class ConfigSourceDetectorTests(ITestOutputHelper outputHelper)
{
    [Theory]
    [InlineData("msft.ghe.com:\n  user: example\n  users:\n    example: {}\n", true)]
    [InlineData("github.com: {}\nmsft.ghe.com: {}\n", true)]
    [InlineData("'msft.ghe.com': {}\n", true)]
    [InlineData("\"MSFT.GHE.COM\": {}\n", true)]
    [InlineData("github.com:\n  user: msft.ghe.com\n", false)]
    [InlineData("github.com:\n  users:\n    msft.ghe.com: {}\n", false)]
    [InlineData("msft.ghe.com.example: {}\n", false)]
    [InlineData("# msft.ghe.com: {}\ngithub.com: {}\n", false)]
    [InlineData("{}\n", false)]
    public async Task HostConfiguration_OnlyTopLevelMicrosoftHostSelectsDefault(string configuration, bool expected)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await File.WriteAllTextAsync(Path.Combine(workspace.Path, "hosts.yml"), configuration);
        var environment = new TestEnvironment(new Dictionary<string, string?>
        {
            ["GH_CONFIG_DIR"] = workspace.Path
        });
        var detector = new ConfigSourceDetector(environment, NullLogger<ConfigSourceDetector>.Instance);

        Assert.Equal(expected, await detector.IsMicrosoftGitHubHostConfiguredAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("linux", "custom gh", "xdg", "roaming", "custom gh/hosts.yml")]
    [InlineData("windows", "custom gh", "xdg", "roaming", "custom gh/hosts.yml")]
    [InlineData("macos", null, "xdg", "roaming", "xdg/gh/hosts.yml")]
    [InlineData("windows", "", "xdg", "roaming", "xdg/gh/hosts.yml")]
    [InlineData("windows", null, "", "roaming", "roaming/GitHub CLI/hosts.yml")]
    [InlineData("linux", null, null, "roaming", "home/.config/gh/hosts.yml")]
    [InlineData("macos", null, null, "roaming", "home/.config/gh/hosts.yml")]
    [InlineData("windows", "", "", "", "home/.config/gh/hosts.yml")]
    public async Task HostConfiguration_FollowsGitHubCliDirectoryPrecedence(
        string platform, string? configDirectory, string? xdgConfigHome, string? appData, string hostsPath)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        workspace.CreateDirectory(Path.GetDirectoryName(hostsPath)!);
        await File.WriteAllTextAsync(Path.Combine(workspace.Path, hostsPath), "msft.ghe.com: {}");
        string? InWorkspace(string? path) => string.IsNullOrEmpty(path) ? path : Path.Combine(workspace.Path, path);
        var variables = new Dictionary<string, string?>
        {
            ["GH_CONFIG_DIR"] = InWorkspace(configDirectory),
            ["XDG_CONFIG_HOME"] = InWorkspace(xdgConfigHome),
            ["AppData"] = InWorkspace(appData),
            ["HOME"] = InWorkspace("home"),
            ["USERPROFILE"] = InWorkspace("home")
        };
        var detector = new ConfigSourceDetector(CreateEnvironment(platform, variables), NullLogger<ConfigSourceDetector>.Instance);

        Assert.True(await detector.IsMicrosoftGitHubHostConfiguredAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("linux", null, null, null)]
    [InlineData("macos", "", "", "")]
    [InlineData("windows", null, null, null)]
    [InlineData("linux", "gh", null, null)]
    [InlineData("linux", "~", null, null)]
    [InlineData("macos", null, "xdg", null)]
    [InlineData("linux", null, null, "home")]
    [InlineData("windows", null, null, "home")]
    public async Task HostConfiguration_MissingOrRelativeDirectoryDisablesDetection(
        string platform, string? configDirectory, string? xdgConfigHome, string? home)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        foreach (var relativeDirectory in new[] { Path.Combine(".config", "gh"), "gh", "~", Path.Combine("xdg", "gh"), Path.Combine("home", ".config", "gh") })
        {
            workspace.CreateDirectory(relativeDirectory);
            await File.WriteAllTextAsync(Path.Combine(workspace.Path, relativeDirectory, "hosts.yml"), "msft.ghe.com: {}");
        }
        var variables = new Dictionary<string, string?>
        {
            ["GH_CONFIG_DIR"] = configDirectory,
            ["XDG_CONFIG_HOME"] = xdgConfigHome,
            ["HOME"] = home,
            ["USERPROFILE"] = home
        };
        var detector = new ConfigSourceDetector(CreateEnvironment(platform, variables), NullLogger<ConfigSourceDetector>.Instance);

        Assert.False(await detector.IsMicrosoftGitHubHostConfiguredAsync(CancellationToken.None));
    }

    [Fact]
    public async Task HostConfiguration_DoesNotFallBackPastConfiguredDirectory()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var home = workspace.CreateDirectory("home");
        var defaultDirectory = home.CreateSubdirectory(Path.Combine(".config", "gh"));
        await File.WriteAllTextAsync(Path.Combine(defaultDirectory.FullName, "hosts.yml"), "msft.ghe.com: {}");
        var customDirectory = workspace.CreateDirectory("custom-gh");
        var environment = new TestEnvironment(new Dictionary<string, string?>
        {
            ["GH_CONFIG_DIR"] = customDirectory.FullName,
            ["HOME"] = home.FullName,
            ["USERPROFILE"] = home.FullName
        });
        var detector = new ConfigSourceDetector(environment, NullLogger<ConfigSourceDetector>.Instance);

        Assert.False(await detector.IsMicrosoftGitHubHostConfiguredAsync(CancellationToken.None));
        await File.WriteAllTextAsync(Path.Combine(customDirectory.FullName, "hosts.yml"), "github.com: {}");
        Assert.False(await detector.IsMicrosoftGitHubHostConfiguredAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("msft.ghe.com", true)]
    [InlineData("MSFT.GHE.COM", true)]
    [InlineData("github.com", false)]
    [InlineData("msft.ghe.com.example", false)]
    [InlineData(null, false)]
    public async Task HostConfiguration_IncludesExplicitGitHubHostWithoutReadingTokens(string? host, bool expected)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var environment = new TestEnvironment(new Dictionary<string, string?>
        {
            ["GH_HOST"] = host,
            ["GH_TOKEN"] = "test-token",
            ["GH_CONFIG_DIR"] = workspace.Path
        });
        var detector = new ConfigSourceDetector(environment, NullLogger<ConfigSourceDetector>.Instance);

        Assert.Equal(expected, await detector.IsMicrosoftGitHubHostConfiguredAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("", "GitHub CLI host configuration is not a single YAML mapping.")]
    [InlineData("[msft.ghe.com]", "GitHub CLI host configuration is not a single YAML mapping.")]
    [InlineData("msft.ghe.com: {}\n---\ngithub.com: {}\n", "GitHub CLI host configuration is not a single YAML mapping.")]
    [InlineData("msft.ghe.com: [invalid-secret", "Could not read GitHub CLI host configuration (SemanticErrorException).")]
    public async Task HostConfiguration_InvalidYamlDoesNotSelectDefaultOrExposeContents(string configuration, string expectedMessage)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await File.WriteAllTextAsync(Path.Combine(workspace.Path, "hosts.yml"), configuration);
        var logger = new FakeLogger<ConfigSourceDetector>();
        var environment = new TestEnvironment(new Dictionary<string, string?>
        {
            ["GH_CONFIG_DIR"] = workspace.Path
        });
        var detector = new ConfigSourceDetector(environment, logger);

        Assert.False(await detector.IsMicrosoftGitHubHostConfiguredAsync(CancellationToken.None));
        var record = Assert.Single(logger.Collector.GetSnapshot());
        Assert.Null(record.Exception);
        Assert.Equal(expectedMessage, record.Message);
    }

    [Fact]
    public async Task HostConfiguration_ReflectsLoginAndLogoutWithoutCachedDetection()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var home = workspace.CreateDirectory("home");
        var directory = home.CreateSubdirectory(Path.Combine(".config", "gh"));
        var hostsPath = Path.Combine(directory.FullName, "hosts.yml");
        var environment = new TestEnvironment(new Dictionary<string, string?>
        {
            ["HOME"] = home.FullName,
            ["USERPROFILE"] = home.FullName
        });
        var detector = new ConfigSourceDetector(environment, NullLogger<ConfigSourceDetector>.Instance);

        Assert.False(await detector.IsMicrosoftGitHubHostConfiguredAsync(CancellationToken.None));
        await File.WriteAllTextAsync(hostsPath, "msft.ghe.com: {}");
        Assert.True(await detector.IsMicrosoftGitHubHostConfiguredAsync(CancellationToken.None));
        await File.WriteAllTextAsync(hostsPath, "github.com: {}");
        Assert.False(await detector.IsMicrosoftGitHubHostConfiguredAsync(CancellationToken.None));
    }

    [Fact]
    public async Task HostConfiguration_PropagatesCancellationBeforeSelectingHost()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var environment = new TestEnvironment(new Dictionary<string, string?>
        {
            ["GH_HOST"] = "msft.ghe.com"
        });
        var detector = new ConfigSourceDetector(environment, NullLogger<ConfigSourceDetector>.Instance);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => detector.IsMicrosoftGitHubHostConfiguredAsync(cancellation.Token));
    }

    private static TestEnvironment CreateEnvironment(string platform, IReadOnlyDictionary<string, string?> variables) => platform switch
    {
        "windows" => TestEnvironment.CreateWindows(variables),
        "macos" => TestEnvironment.CreateMacOS(variables),
        _ => TestEnvironment.CreateLinux(variables)
    };
}
