// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests.Helpers;

internal sealed class ConfigSourceTestContext
{
    private const string NewCommand = "aspire new aspire-empty --language csharp --name SourceApp --output SourceApp " +
        "--suppress-agent-init --localhost-tld false";
    private const string SshRepository = "ssh://git@config-source.example/config";

    private readonly string _root;
    private readonly Hex1bTerminalAutomator _auto;
    private readonly SequenceCounter _counter;
    private readonly CancellationToken _cancellationToken;
    private string _repository;
    private string _configurationRelativePath = "SourceApp/aspire.config.json";

    private ConfigSourceTestContext(
        TemporaryWorkspace workspace, Hex1bTerminalAutomator auto, SequenceCounter counter, CancellationToken cancellationToken)
    {
        _root = workspace.WorkspaceRoot.FullName;
        _auto = auto;
        _counter = counter;
        _cancellationToken = cancellationToken;
        _repository = CliE2ETestHelpers.ToContainerPath(Path.Combine(_root, "configuration source"), workspace);
    }

    public static async Task RunAsync(ITestOutputHelper output, Func<ConfigSourceTestContext, Task> test)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        if (strategy.Mode is not (CliInstallMode.LocalHive or CliInstallMode.PullRequest or CliInstallMode.LocalArchive))
        {
            Assert.Skip("Configuration sources require a current source build. Run with ASPIRE_E2E_ARCHIVE or in pull request CI.");
        }

        using var workspace = TemporaryWorkspace.Create(output);
        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, workspace: workspace);
        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));
        var context = new ConfigSourceTestContext(workspace, auto, counter, cancellationToken);
        await context.WriteSourceAsync("original");
        await File.WriteAllTextAsync(Path.Combine(context._root, "check-source.cs"), SourceCheckProgram, cancellationToken);
        await using var terminalRun = CliE2ETestHelpers.StartRun(terminal, workspace, auto, counter, output, cancellationToken);
        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);
        await context.InitializeRepositoryAsync();
        await test(context);
    }

    public async Task CreateProjectAsync()
    {
        await _auto.RunCommandAsync($"{NewCommand} --use-config-source true --non-interactive", _counter,
            timeout: TimeSpan.FromMinutes(3));
        await ConfigureProjectAsync();
    }

    public async Task InitializeSolutionAsync()
    {
        await _auto.RunCommandAsync("mkdir SourceApp && cd SourceApp && dotnet new sln --name SourceApp", _counter);
        await _auto.RunCommandAsync(
            "aspire init --language csharp --suppress-agent-init --use-config-source true --non-interactive",
            _counter, timeout: TimeSpan.FromMinutes(3));
        await _auto.RunCommandAsync(
            "test ! -f aspire.config.json && test -f SourceApp.AppHost/aspire.config.json && " +
            "cd SourceApp.AppHost && aspire config set docs.custom keep", _counter);
        _configurationRelativePath = "SourceApp/SourceApp.AppHost/aspire.config.json";
        var configuration = await ReadConfigurationAsync();
        Assert.Equal("SourceApp.AppHost.csproj", configuration["appHost"]!["path"]!.GetValue<string>());
    }

    public async Task CreateProjectWithNestedAppHostAsync()
    {
        await CreateProjectAsync();
        // The empty C# template puts the AppHost at the root. Keep its source-bound
        // configuration there so commands in the nested AppHost must discover an ancestor.
        await _auto.RunCommandAsync("mkdir AppHost && mv apphost.cs apphost.run.json AppHost", _counter);
        await _auto.RunCommandAsync("aspire config set appHost.path AppHost/apphost.cs", _counter);
    }

    public async Task CreateProjectWithConsentAsync(bool accept)
    {
        await _auto.TypeAsync(NewCommand);
        await _auto.EnterAsync();
        var consentPrompt = new CellPatternSearcher().Find("Use configuration from this source?");
        await _auto.WaitUntilAsync(snapshot => consentPrompt.Search(snapshot).Count > 0,
            timeout: TimeSpan.FromMinutes(2), description: "waiting for configuration source consent");
        if (!accept)
        {
            await _auto.DownAsync();
        }
        await _auto.EnterAsync();
        await _auto.WaitForSuccessPromptAsync(_counter, timeout: TimeSpan.FromMinutes(3));
        await ConfigureProjectAsync();
    }

    public async Task AssertConsentResultAsync(bool accepted)
    {
        if (accepted)
        {
            await AssertSourceAppliedAsync("original");
        }
        else
        {
            var configuration = await ReadConfigurationAsync();
            Assert.Null(configuration["configSource"]);
            Assert.Equal("""{"custom":"keep"}""", configuration["docs"]!.ToJsonString());
            await AssertSourceNotAcquiredAsync();
        }
    }

    public async Task AssertNonInteractiveConsentRequiredAsync()
    {
        await _auto.RunCommandAsync(
            $"{NewCommand} --non-interactive > \"$ASPIRE_E2E_WORKSPACE/consent.log\" 2>&1; " +
            $"result=$?; cat \"$ASPIRE_E2E_WORKSPACE/consent.log\"; test \"$result\" -eq {CliExitCodes.InvalidCommand}",
            _counter, timeout: TimeSpan.FromMinutes(3));
        var consentOutput = await File.ReadAllTextAsync(Path.Combine(_root, "consent.log"), _cancellationToken);
        Assert.Contains("Specify --use-config-source true to accept it or --use-config-source false",
            Regex.Replace(consentOutput, @"\s+", " "));
        Assert.False(Directory.Exists(Path.Combine(_root, "SourceApp")));
        await AssertSourceNotAcquiredAsync();
    }

    public async Task CreateProjectWithGitAuthenticationAsync()
    {
        await ConfigureGitAuthenticationAsync();
        await _auto.TypeAsync($"{NewCommand} --use-config-source true");
        await _auto.EnterAsync();
        var authenticationPrompt = new CellPatternSearcher().Find("Configuration source Git authentication code:");
        await _auto.WaitUntilAsync(snapshot => authenticationPrompt.Search(snapshot).Count > 0,
            timeout: TimeSpan.FromMinutes(2), description: "waiting for the Git authentication prompt without a trailing newline");
        await _auto.TypeAsync("allow-source");
        await _auto.EnterAsync();
        var authenticated = new CellPatternSearcher().Find("Configuration source Git authentication accepted.");
        await _auto.WaitUntilAsync(snapshot => authenticated.Search(snapshot).Count > 0,
            timeout: TimeSpan.FromSeconds(30), description: "waiting for Git to acknowledge terminal input");
        await _auto.WaitForSuccessPromptAsync(_counter, timeout: TimeSpan.FromMinutes(3));
        Assert.Equal("accepted", await File.ReadAllTextAsync(Path.Combine(_root, "git-authenticated.txt"), _cancellationToken));
        await ConfigureProjectAsync();
    }

    public async Task<JsonObject> AssertSourceAppliedAsync(string version)
    {
        var configuration = await ReadConfigurationAsync();
        AssertSourceValues(configuration, version, await ReadSourceVersionAsync());
        Assert.Equal(_repository, configuration["configSource"]!["repository"]!.GetValue<string>());
        Assert.Equal("teams/platform.json", configuration["configSource"]!["path"]!.GetValue<string>());
        Assert.Equal("main", configuration["configSource"]!["ref"]!.GetValue<string>());
        return configuration;
    }

    public async Task UpdateSourceAsync()
    {
        var previousVersion = await ReadSourceVersionAsync();
        await WriteSourceAsync("updated");
        await CommitSourceAsync();
        Assert.NotEqual(previousVersion, await ReadSourceVersionAsync());
    }

    public async Task UseAppHostDirectoryAsync()
    {
        var configuration = await ReadConfigurationAsync();
        var appHostPath = configuration["appHost"]!["path"]!.GetValue<string>();
        var directory = Path.GetDirectoryName(appHostPath)!.Replace('\\', '/');
        Assert.NotEmpty(directory);
        await _auto.RunCommandAsync($"cd \"{directory}\"", _counter);
        await AssertNoLocalConfigurationAsync();
    }

    public Task AssertNoLocalConfigurationAsync()
        => _auto.RunCommandAsync("test ! -f aspire.config.json", _counter);

    public async Task RunAndAssertUpdateNotificationAsync()
    {
        // Creation records a fresh successful check. Expire only this isolated source's
        // observation, then wait for the new blob version before cancelling the command.
        // This avoids both the 24-hour cadence and a race with command-exit cancellation.
        await _auto.RunCommandAsync("dotnet run \"$ASPIRE_E2E_WORKSPACE/check-source.cs\" -- expire", _counter);
        await _auto.RunCommandAsync(
            "aspire run --non-interactive > \"$ASPIRE_E2E_WORKSPACE/run.log\" 2>&1 </dev/null & source_run_pid=$!", _counter);
        await _auto.RunCommandAsync("dotnet run \"$ASPIRE_E2E_WORKSPACE/check-source.cs\" -- wait \"$source_run_pid\"", _counter,
            timeout: TimeSpan.FromMinutes(3));
        await _auto.RunCommandAsync("kill -INT \"$source_run_pid\"; timeout 120 tail --pid=\"$source_run_pid\" -f /dev/null; wait \"$source_run_pid\"", _counter,
            timeout: TimeSpan.FromMinutes(3));
        await _auto.RunCommandAsync("cat \"$ASPIRE_E2E_WORKSPACE/run.log\"", _counter);
        Assert.Contains("A new version of the configuration source is available.",
            await File.ReadAllTextAsync(Path.Combine(_root, "run.log"), _cancellationToken));
    }

    public async Task RefreshConfigurationAsync(bool expectChanges)
    {
        await _auto.RunCommandAsync(
            "set -o pipefail; aspire update --config-source --yes --non-interactive 2>&1 | tee \"$ASPIRE_E2E_WORKSPACE/update.log\"",
            _counter, timeout: TimeSpan.FromMinutes(2));
        var message = expectChanges ? "configuration updated" : "configuration already up to date";
        var updateOutput = await File.ReadAllTextAsync(Path.Combine(_root, "update.log"), _cancellationToken);
        Assert.Contains(message, Regex.Replace(updateOutput, @"\s+", " "));
    }

    public async Task AssertConfigurationUpdatedAsync(JsonObject original)
    {
        var actual = await ReadConfigurationAsync();
        var version = await ReadSourceVersionAsync();
        AssertSourceValues(actual, "updated", version);
        original["docs"]!["llmsTxtUrl"] = actual["docs"]!["llmsTxtUrl"]!.DeepClone();
        original["docs"]!["api"]!["sitemapUrl"] = actual["docs"]!["api"]!["sitemapUrl"]!.DeepClone();
        original["configSource"]!["appliedVersion"] = version;
        Assert.Equal(original.ToJsonString(), actual.ToJsonString());
    }

    public async Task AssertConfigurationUnchangedAsync(JsonObject original)
    {
        var actual = await ReadConfigurationAsync();
        Assert.Equal(original.ToJsonString(), actual.ToJsonString());
    }

    private async Task InitializeRepositoryAsync()
    {
        await _auto.RunCommandAsync("aspire config set features.updateNotificationsEnabled false -g", _counter);
        // The bind-mounted directory belongs to the host runner, not the container's root user.
        await _auto.RunCommandAsync("git config --global --add safe.directory \"$ASPIRE_E2E_WORKSPACE/configuration source\"", _counter);
        await _auto.RunCommandAsync("git -C 'configuration source' init --initial-branch=main", _counter);
        await _auto.RunCommandAsync("git -C 'configuration source' config user.name 'Aspire E2E'", _counter);
        await _auto.RunCommandAsync("git -C 'configuration source' config user.email aspire-e2e@example.com", _counter);
        await CommitSourceAsync();
        await _auto.RunCommandAsync(
            "export ASPIRE_INIT_SOURCE_URL=\"$ASPIRE_E2E_WORKSPACE/configuration source\" " +
            "ASPIRE_INIT_SOURCE_PATH=teams/platform.json ASPIRE_INIT_SOURCE_REF=main", _counter);
    }

    private async Task ConfigureProjectAsync()
    {
        await _auto.RunCommandAsync("cd SourceApp", _counter);
        await _auto.RunCommandAsync("aspire config set docs.custom keep", _counter);
    }

    private Task AssertSourceNotAcquiredAsync()
        => _auto.RunCommandAsync("test ! -d \"$HOME/.aspire/configsources\"", _counter);

    private async Task<JsonObject> ReadConfigurationAsync()
    {
        // Source materialization creates an owner-only file inside the container. Export its
        // contents without changing those permissions so the host can assert the saved values.
        await _auto.RunCommandAsync(
            $"cat \"$ASPIRE_E2E_WORKSPACE/{_configurationRelativePath}\" > \"$ASPIRE_E2E_WORKSPACE/actual-config.json\"", _counter);
        return JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(_root, "actual-config.json"), _cancellationToken))!.AsObject();
    }

    private async Task<string> ReadSourceVersionAsync()
        => (await File.ReadAllTextAsync(Path.Combine(_root, "source-version.txt"), _cancellationToken)).Trim();

    private Task CommitSourceAsync()
        => _auto.RunCommandAsync(
            "git -C \"$ASPIRE_E2E_WORKSPACE/configuration source\" add teams/platform.json && " +
            "git -C \"$ASPIRE_E2E_WORKSPACE/configuration source\" -c commit.gpgsign=false commit -m 'Update configuration' && " +
            "git -C \"$ASPIRE_E2E_WORKSPACE/configuration source\" rev-parse HEAD:teams/platform.json > \"$ASPIRE_E2E_WORKSPACE/source-version.txt\"", _counter);

    private async Task WriteSourceAsync(string version)
    {
        var directory = Directory.CreateDirectory(Path.Combine(_root, "configuration source", "teams"));
        await File.WriteAllTextAsync(Path.Combine(directory.FullName, "platform.json"), $$"""
            {
              "docs": {
                "llmsTxtUrl": "https://docs.example/{{version}}/llms.txt",
                "api": {
                  "sitemapUrl": "https://docs.example/{{version}}/sitemap.xml"
                }
              }
            }
            """, _cancellationToken);
    }

    private async Task ConfigureGitAuthenticationAsync()
    {
        // Substitute only SSH authentication, not Git acquisition. The launcher challenges the
        // terminal, then serves the local repository with real git-upload-pack. No network or
        // real credentials are needed, and stdout remains exclusively Git's wire protocol.
        await File.WriteAllTextAsync(Path.Combine(_root, "ssh-probe.cs"), SshProbeProgram, _cancellationToken);
        await _auto.RunCommandAsync(
            "dotnet publish \"$ASPIRE_E2E_WORKSPACE/ssh-probe.cs\" -p:PublishAot=false --output \"$ASPIRE_E2E_WORKSPACE/ssh-probe\"",
            _counter, timeout: TimeSpan.FromMinutes(2));
        // Skip Git's SSH-variant discovery so the launcher is invoked only for the fetch.
        await _auto.RunCommandAsync(
            "export GIT_SSH_VARIANT=ssh GIT_SSH_COMMAND=\"dotnet '$ASPIRE_E2E_WORKSPACE/ssh-probe/ssh-probe.dll'\" " +
            $"ASPIRE_INIT_SOURCE_URL={SshRepository}", _counter);
        _repository = SshRepository;
    }

    private static void AssertSourceValues(JsonObject config, string version, string appliedVersion)
    {
        Assert.Equal($"https://docs.example/{version}/llms.txt", config["docs"]!["llmsTxtUrl"]!.GetValue<string>());
        Assert.Equal($"https://docs.example/{version}/sitemap.xml", config["docs"]!["api"]!["sitemapUrl"]!.GetValue<string>());
        Assert.Equal("keep", config["docs"]!["custom"]!.GetValue<string>());
        Assert.Equal(appliedVersion, config["configSource"]!["appliedVersion"]!.GetValue<string>());
    }

    private const string SourceCheckProgram = """
        using System.Diagnostics;
        using System.Globalization;
        using System.Text.Json.Nodes;

        var home = Environment.GetEnvironmentVariable("ASPIRE_HOME")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".aspire");
        var check = Directory.EnumerateDirectories(Path.Combine(home, "configsources"))
            .Select(directory => Path.Combine(directory, "check.json"))
            .Where(File.Exists)
            .Single();
        if (args[0] == "expire")
        {
            var metadata = JsonNode.Parse(await File.ReadAllTextAsync(check))!.AsObject();
            metadata["checkedAt"] = "2000-01-01T00:00:00+00:00";
            await File.WriteAllTextAsync(check, metadata.ToJsonString());
            return;
        }

        if (args[0] != "wait")
        {
            throw new ArgumentException("Expected expire or wait.");
        }

        var workspace = Environment.GetEnvironmentVariable("ASPIRE_E2E_WORKSPACE")
            ?? throw new InvalidOperationException("ASPIRE_E2E_WORKSPACE is not set.");
        var expected = (await File.ReadAllTextAsync(Path.Combine(workspace, "source-version.txt"))).Trim();
        using var process = Process.GetProcessById(int.Parse(args[1], CultureInfo.InvariantCulture));
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < TimeSpan.FromMinutes(2))
        {
            process.Refresh();
            if (process.HasExited)
            {
                throw new InvalidOperationException("The AppHost command exited before completing its configuration source check.");
            }

            var metadata = JsonNode.Parse(await File.ReadAllTextAsync(check))!.AsObject();
            if (metadata["version"]!.GetValue<string>() == expected)
            {
                return;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException("The AppHost command did not complete its configuration source check.");
        """;

    private const string SshProbeProgram = """
        using System.Diagnostics;

        var workspace = Environment.GetEnvironmentVariable("ASPIRE_E2E_WORKSPACE")
            ?? throw new InvalidOperationException("ASPIRE_E2E_WORKSPACE is not set.");
        if (Environment.GetEnvironmentVariable("GIT_TERMINAL_PROMPT") != "1")
        {
            throw new InvalidOperationException("Git terminal authentication was not enabled.");
        }

        var authenticationPath = Path.Combine(workspace, "git-authenticated.txt");
        // Reference resolution and fetch open separate SSH connections. Reuse the accepted
        // authentication like an SSH agent so only the first connection challenges the user.
        if (!File.Exists(authenticationPath))
        {
            Console.Error.Write("Configuration source Git authentication code: ");
            Console.Error.Flush();
            // SSH's stdin carries Git protocol traffic. Like an SSH authentication prompt,
            // read the challenge response from the controlling terminal instead.
            using (var terminal = new StreamReader(new FileStream("/dev/tty", FileMode.Open, FileAccess.Read, FileShare.ReadWrite)))
            {
                if (await terminal.ReadLineAsync() != "allow-source")
                {
                    Console.Error.WriteLine("Configuration source Git authentication failed.");
                    return 1;
                }
            }

            await File.WriteAllTextAsync(authenticationPath, "accepted");
            Console.Error.WriteLine("Configuration source Git authentication accepted.");
        }

        var startInfo = new ProcessStartInfo("git")
        {
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("upload-pack");
        startInfo.ArgumentList.Add(Path.Combine(workspace, "configuration source"));
        // A real SSH server does not inherit the client's repository environment.
        foreach (var name in new[] { "GIT_DIR", "GIT_WORK_TREE", "GIT_INDEX_FILE", "GIT_OBJECT_DIRECTORY", "GIT_ALTERNATE_OBJECT_DIRECTORIES" })
        {
            startInfo.Environment.Remove(name);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start git-upload-pack.");
        await process.WaitForExitAsync();
        return process.ExitCode;
        """;
}
