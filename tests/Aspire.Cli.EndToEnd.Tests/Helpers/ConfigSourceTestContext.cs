// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO.Compression;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Aspire.Cli.Resources;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests.Helpers;

internal sealed class ConfigSourceTestContext
{
    private const string NewCommand = "aspire new aspire-empty --language csharp --name SourceApp --output SourceApp " +
        "--suppress-agent-init --localhost-tld false";
    private const string SshRepository = "ssh://git@config-source.example/config";

    private readonly string _root;
    private readonly string _feedRoot;
    private readonly string _appHostDirectory;
    private readonly Hex1bTerminalAutomator _auto;
    private readonly SequenceCounter _counter;
    private readonly CancellationToken _cancellationToken;
    private string _repository;
    private string _configurationRelativePath = "SourceApp/aspire.config.json";

    private ConfigSourceTestContext(
        TemporaryWorkspace workspace, Hex1bTerminalAutomator auto, SequenceCounter counter, CancellationToken cancellationToken)
    {
        _root = workspace.WorkspaceRoot.FullName;
        _feedRoot = CliE2ETestHelpers.ToContainerPath(Path.Combine(_root, "integration-feed"), workspace);
        _appHostDirectory = CliE2ETestHelpers.ToContainerPath(Path.Combine(_root, "SourceApp"), workspace);
        _auto = auto;
        _counter = counter;
        _cancellationToken = cancellationToken;
        _repository = CliE2ETestHelpers.ToContainerPath(Path.Combine(_root, "configuration source"), workspace);
    }

    public static async Task RunAsync(ITestOutputHelper output, string objectFormat, Func<ConfigSourceTestContext, Task> test)
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
        await context.InitializeRepositoryAsync(objectFormat);
        await test(context);
    }

    public async Task CreateProjectAsync()
    {
        await _auto.RunCommandAsync($"{NewCommand} --use-config-source --non-interactive", _counter,
            timeout: TimeSpan.FromMinutes(3));
        await ConfigureProjectAsync();
    }

    public async Task<JsonObject> CreateUnboundProjectAsync()
    {
        await _auto.RunCommandAsync($"{NewCommand} --use-config-source false --non-interactive", _counter,
            timeout: TimeSpan.FromMinutes(3));
        await ConfigureProjectAsync();
        // Remove the saved choice to model an existing project created before source adoption.
        await _auto.RunCommandAsync("aspire config delete configSource", _counter);
        await _auto.RunCommandAsync("aspire config set docs.llmsTxtUrl https://local.example/llms.txt", _counter);
        var configuration = await ReadConfigurationAsync();
        Assert.Null(configuration["configSource"]);
        Assert.Equal("https://local.example/llms.txt", configuration["docs"]!["llmsTxtUrl"]!.GetValue<string>());
        await AssertSourceNotAcquiredAsync();
        return configuration;
    }

    public async Task AddAndAssertIntegrationSourcesAsync()
    {
        foreach (var (feed, id) in new[]
        {
            ("team", "Contoso.Hosting.Configured"),
            ("team", "Excluded.Package"),
            ("other", "Fabrikam.Hosting.Configured")
        })
        {
            var directory = Directory.CreateDirectory(Path.Combine(_root, "integration-feed", feed));
            using var archive = ZipFile.Open(Path.Combine(directory.FullName, $"{id}.1.0.0.nupkg"), ZipArchiveMode.Create);
            await using (var writer = new StreamWriter(archive.CreateEntry($"{id}.nuspec").Open()))
            {
                await writer.WriteAsync($"""
                    <?xml version="1.0"?>
                    <package><metadata><id>{id}</id><version>1.0.0</version><authors>Aspire Tests</authors><description>Integration source fixture</description></metadata></package>
                    """);
            }
            await using var content = new StreamWriter(archive.CreateEntry("content/readme.txt").Open());
            await content.WriteAsync("Integration source fixture.");
        }

        await _auto.RunCommandAsync(
            "aspire integration list --format json --non-interactive > \"$ASPIRE_E2E_WORKSPACE/integrations.json\"", _counter);
        var integrations = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(_root, "integrations.json"), _cancellationToken))!.AsArray();
        Assert.Equal(
            ["Contoso.Hosting.Configured", "Fabrikam.Hosting.Configured"],
            integrations.Select(item => item!["package"]!.GetValue<string>()).Order());

        foreach (var id in new[] { "Contoso.Hosting.Configured", "Fabrikam.Hosting.Configured" })
        {
            await _auto.RunCommandAsync($"aspire add {id} --version 1.0.0 --non-interactive", _counter,
                timeout: TimeSpan.FromMinutes(3));
        }

        await _auto.RunCommandAsync("cat apphost.cs > \"$ASPIRE_E2E_WORKSPACE/actual-apphost.cs\"", _counter);
        var appHost = await File.ReadAllTextAsync(Path.Combine(_root, "actual-apphost.cs"), _cancellationToken);
        Assert.Contains("Contoso.Hosting.Configured@1.0.0", appHost);
        Assert.Contains("Fabrikam.Hosting.Configured@1.0.0", appHost);
    }

    public async Task InitializeSolutionAsync()
    {
        await _auto.RunCommandAsync("mkdir SourceApp && cd SourceApp && dotnet new sln --name SourceApp", _counter);
        await _auto.RunCommandAsync(
            "aspire init --language csharp --suppress-agent-init --use-config-source --non-interactive",
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

    public async Task CreateProjectWithConsentAsync(bool accept, bool useOption)
    {
        // A template subcommand or --language bypasses configured-template discovery.
        // Exercise the interactive picker so declining must also suppress runtime inheritance.
        var consentOption = useOption ? accept ? " --use-config-source" : " --use-config-source false" : "";
        await _auto.TypeAsync(
            $"aspire new --name SourceApp --output SourceApp --suppress-agent-init{consentOption}");
        await _auto.EnterAsync();
        if (!useOption)
        {
            var consentPrompt = new CellPatternSearcher().Find("Use configuration from this source? [Y/n]:");
            await _auto.WaitUntilAsync(snapshot => consentPrompt.Search(snapshot).Count > 0,
                timeout: TimeSpan.FromMinutes(2), description: "waiting for configuration source consent");
            // Confirmation consumes a single key; an extra Enter could reach the next prompt.
            await _auto.TypeAsync(accept ? "y" : "n");
        }
        var templatePrompt = new CellPatternSearcher().Find("> Starter App");
        await _auto.WaitUntilAsync(snapshot => templatePrompt.Search(snapshot).Count > 0,
            timeout: TimeSpan.FromMinutes(2), description: "waiting for template selection after configuration source consent");
        await _auto.TypeAsync("Empty AppHost");
        await _auto.EnterAsync();
        var languagePrompt = new CellPatternSearcher().Find("Which language would you like to use?");
        await _auto.WaitUntilAsync(snapshot => languagePrompt.Search(snapshot).Count > 0,
            timeout: TimeSpan.FromSeconds(30), description: "waiting for the empty AppHost language prompt");
        await _auto.EnterAsync();
        var urlsPrompt = new CellPatternSearcher().Find("Use *.dev.localhost URLs");
        await _auto.WaitUntilAsync(snapshot => urlsPrompt.Search(snapshot).Count > 0,
            timeout: TimeSpan.FromMinutes(2), description: "waiting for the local development URLs prompt");
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
            Assert.True(configuration["configSource"]!["optOut"]!.GetValue<bool>());
            Assert.Null(configuration["skillSources"]);
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
        Assert.Contains("Specify --use-config-source to accept it or --use-config-source false",
            Regex.Replace(consentOutput, @"\s+", " "));
        Assert.False(Directory.Exists(Path.Combine(_root, "SourceApp")));
        await AssertSourceNotAcquiredAsync();
    }

    public async Task CreateProjectWithGitAuthenticationAsync()
    {
        await ConfigureGitAuthenticationAsync();
        await _auto.TypeAsync($"{NewCommand} --use-config-source --log-file \"$ASPIRE_E2E_WORKSPACE/git-auth-cli.log\"");
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

    public async Task AssertGitOutputLoggedAsync()
    {
        // Export the owner-only CLI log through the container before reading it on the host.
        await _auto.RunCommandAsync(
            "cat \"$ASPIRE_E2E_WORKSPACE/git-auth-cli.log\" > \"$ASPIRE_E2E_WORKSPACE/actual-git-auth-cli.log\"", _counter);
        var log = await File.ReadAllTextAsync(Path.Combine(_root, "actual-git-auth-cli.log"), _cancellationToken);
        Assert.Contains("[DBUG] [GitCommandRunner] Git stdout: ", log);
        Assert.Contains("[DBUG] [GitCommandRunner] Git stderr: ", log);
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

    public async Task RegisterAndAssertSkillSourcesAsync()
    {
        const string command = "aspire agent init --workspace-root . --agents copilot,claude,opencode --scope project " +
            "--skills aspire --mcp=false --non-interactive";
        const string expectedPluginSettings = """
            {
              "extraKnownMarketplaces": {
                "aspire-skills": { "source": { "source": "github", "repo": "microsoft/aspire-skills" } },
                "platform": { "source": { "source": "github", "repo": "example/platform-skills", "ref": "original" } },
                "team": { "source": { "source": "github", "repo": "example/team-skills" } }
              },
              "enabledPlugins": {
                "aspire@aspire-skills": true,
                "aspire@platform": true,
                "original@team": true,
                "deployment@team": true
              }
            }
            """;
        const string expectedOpenCodeSettings = """
            {
              "skills": {
                "urls": [
                  "https://raw.githubusercontent.com/microsoft/aspire-skills/main/opencode/v1/",
                  "https://skills.example/original/"
                ]
              }
            }
            """;
        (string Path, string Expected)[] settings =
        [
            (".github/copilot/settings.json", expectedPluginSettings),
            (".claude/settings.json", expectedPluginSettings),
            ("opencode.jsonc", expectedOpenCodeSettings)
        ];
        foreach (var repository in new[] { "microsoft/aspire-skills", "https://github.com/microsoft/aspire-skills.git" })
        {
            await _auto.RunCommandAsync(
                $"aspire config set skillSources.default.repository {repository}", _counter);
            await _auto.RunCommandAsync(command, _counter);
            foreach (var (path, expected) in settings)
            {
                await _auto.RunCommandAsync(
                    $"cat \"{path}\" > \"$ASPIRE_E2E_WORKSPACE/actual-agent-settings.json\"", _counter);
                var actual = await File.ReadAllTextAsync(Path.Combine(_root, "actual-agent-settings.json"), _cancellationToken);
                Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expected), JsonNode.Parse(actual)),
                    $"Unexpected skill-source registration in {path}: {actual}");
            }
        }

        Assert.False(Directory.Exists(Path.Combine(_root, "SourceApp", ".agents", "skills")));
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

    public Task RefreshConfigurationAsync()
        => _auto.RunCommandAsync(
            "set -o pipefail; aspire update --config-source --yes --non-interactive 2>&1 | tee \"$ASPIRE_E2E_WORKSPACE/update.log\"",
            _counter, timeout: TimeSpan.FromMinutes(2));

    public async Task AssertRefreshWithoutBindingAsync()
    {
        await _auto.RunCommandAsync(
            "aspire update --config-source --yes --non-interactive > \"$ASPIRE_E2E_WORKSPACE/missing-source.log\" 2>&1",
            _counter, timeout: TimeSpan.FromMinutes(2));
        var output = await File.ReadAllTextAsync(Path.Combine(_root, "missing-source.log"), _cancellationToken);
        Assert.Contains("No configuration source to update.", Regex.Replace(output, @"\s+", " "));
        await AssertSourceNotAcquiredAsync();
    }

    public async Task UpdateProjectWithSourceConsentAsync()
    {
        await _auto.TypeAsync("aspire update --yes --nuget-config-dir .");
        await _auto.EnterAsync();
        var candidate = new CellPatternSearcher().Find("Git source:");
        var consentPrompt = new CellPatternSearcher().Find("Use configuration from this source? [Y/n]:");
        await _auto.WaitUntilAsync(snapshot => candidate.Search(snapshot).Count > 0 && consentPrompt.Search(snapshot).Count > 0,
            timeout: TimeSpan.FromMinutes(2), description: "waiting for brownfield configuration source consent");
        await _auto.TypeAsync("y");
        await _auto.WaitForSuccessPromptAsync(_counter, timeout: TimeSpan.FromMinutes(5));
    }

    public async Task AssertConfigurationSourceAdoptedAsync(JsonObject original)
    {
        var actual = await AssertSourceAppliedAsync("original");
        original["skillSources"] = actual["skillSources"]!.DeepClone();
        original["integrationSources"] = actual["integrationSources"]!.DeepClone();
        original["configSource"] = actual["configSource"]!.DeepClone();
        // Plain update also synchronizes SDK metadata with the AppHost's selected package version.
        var appHost = await File.ReadAllTextAsync(Path.Combine(_root, "SourceApp", "apphost.cs"), _cancellationToken);
        original["sdk"] ??= new JsonObject();
        original["sdk"]!["version"] = CliE2ETestHelpers.GetAppHostVersion(appHost);
        Assert.True(JsonNode.DeepEquals(original, actual), $"Unexpected configuration after source adoption: {actual}");
    }

    public async Task AssertConfigurationUpdatedAsync(JsonObject original)
    {
        var actual = await ReadConfigurationAsync();
        var version = await ReadSourceVersionAsync();
        AssertSourceValues(actual, "updated", version);
        original["skillSources"] = actual["skillSources"]!.DeepClone();
        original["configSource"]!["appliedVersion"] = version;
        Assert.Equal(original.ToJsonString(), actual.ToJsonString());
    }

    public async Task AssertConfigurationUnchangedAsync(JsonObject original)
    {
        var actual = await ReadConfigurationAsync();
        Assert.Equal(original.ToJsonString(), actual.ToJsonString());
    }

    public async Task RunDoctorAndAssertAppHostSourceAsync(bool bound)
    {
        // Other environment checks can independently fail in the container, so accept doctor's
        // documented 0/1 exit codes and assert only the configuration-source checks.
        await _auto.RunCommandAsync(
            "env -u ASPIRE_ANSI_PASS_THRU NO_COLOR=1 aspire doctor --non-interactive --nologo > \"$ASPIRE_E2E_WORKSPACE/doctor.txt\"; " +
            "result=$?; cat \"$ASPIRE_E2E_WORKSPACE/doctor.txt\"; test \"$result\" -le 1",
            _counter, timeout: TimeSpan.FromMinutes(2));
        await _auto.RunCommandAsync(
            "aspire doctor --format json --non-interactive --nologo > \"$ASPIRE_E2E_WORKSPACE/doctor.json\"; result=$?; test \"$result\" -le 1",
            _counter, timeout: TimeSpan.FromMinutes(2));

        // Doctor output can wrap long messages onto indented continuation lines.
        var text = Regex.Replace(await File.ReadAllTextAsync(Path.Combine(_root, "doctor.txt"), _cancellationToken), @"\s+", " ");
        Assert.Contains(ConfigSourceStrings.DoctorAvailable, text);
        Assert.Contains(bound ? ConfigSourceStrings.DoctorBound : ConfigSourceStrings.DoctorUnbound, text);
        if (!bound)
        {
            Assert.Contains(ConfigSourceStrings.DoctorAdoptFix, text);
        }

        var checks = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(_root, "doctor.json"), _cancellationToken))!["checks"]!.AsArray();
        var machine = Assert.Single(checks, check => check!["name"]!.GetValue<string>() == "config-source")!;
        Assert.Equal("pass", machine["status"]!.GetValue<string>());
        Assert.True(machine["metadata"]!["available"]!.GetValue<bool>());
        Assert.Equal("ASPIRE_INIT_SOURCE_URL", machine["metadata"]!["origin"]!.GetValue<string>());
        Assert.Equal(_repository, machine["metadata"]!["repository"]!.GetValue<string>());

        var appHost = Assert.Single(checks, check => check!["name"]!.GetValue<string>() == "apphost-config-source")!;
        var metadata = appHost["metadata"]!;
        Assert.Equal(bound ? "pass" : "warning", appHost["status"]!.GetValue<string>());
        Assert.Equal(bound ? "bound" : "unconfigured", metadata["state"]!.GetValue<string>());
        Assert.Equal($"{_appHostDirectory}/apphost.cs", metadata["appHostPath"]!.GetValue<string>());
        Assert.Equal($"{_appHostDirectory}/aspire.config.json", metadata["settingsFile"]!.GetValue<string>());
        if (bound)
        {
            Assert.Equal(_repository, metadata["repository"]!.GetValue<string>());
            Assert.Equal(await ReadSourceVersionAsync(), metadata["appliedVersion"]!.GetValue<string>());
        }
        else
        {
            Assert.Null(metadata["repository"]);
        }
    }

    private async Task InitializeRepositoryAsync(string objectFormat)
    {
        await _auto.RunCommandAsync("aspire config set features.updateNotificationsEnabled false -g", _counter);
        // The bind-mounted directory belongs to the host runner, not the container's root user.
        await _auto.RunCommandAsync("git config --global --add safe.directory \"$ASPIRE_E2E_WORKSPACE/configuration source\"", _counter);
        await _auto.RunCommandAsync($"git -C 'configuration source' init --initial-branch=main --object-format={objectFormat}", _counter);
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

    public Task AssertSourceNotAcquiredAsync()
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
              "skillSources": {
                "default": {},
                "platform": {
                  "repository": "example/platform-skills",
                  "ref": "{{version}}"
                },
                "team": {
                  "repository": "example/team-skills",
                  "plugins": ["{{version}}", "deployment"],
                  "openCodeV2Catalog": "https://skills.example/{{version}}/"
                }
              },
              "integrationSources": {
                "default": { "enabled": false },
                "team": { "source": "{{_feedRoot}}/team", "patterns": ["Contoso.Hosting.*"] },
                "other": { "source": "{{_feedRoot}}/other", "patterns": ["Fabrikam.Hosting.*"] }
              },
              "docs": {
                "llmsTxtUrl": "https://excluded.example/{{version}}/llms.txt"
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

    private void AssertSourceValues(JsonObject config, string version, string appliedVersion)
    {
        var expectedSources = JsonNode.Parse($$"""
            {
              "default": {},
              "platform": { "repository": "example/platform-skills", "ref": "{{version}}" },
              "team": {
                "repository": "example/team-skills",
                "plugins": ["{{version}}", "deployment"],
                "openCodeV2Catalog": "https://skills.example/{{version}}/"
              }
            }
            """);
        Assert.True(JsonNode.DeepEquals(expectedSources, config["skillSources"]));
        var expectedIntegrationSources = JsonNode.Parse($$"""
            {
              "default": { "enabled": false },
              "team": { "source": "{{_feedRoot}}/team", "patterns": ["Contoso.Hosting.*"] },
              "other": { "source": "{{_feedRoot}}/other", "patterns": ["Fabrikam.Hosting.*"] }
            }
            """);
        Assert.True(JsonNode.DeepEquals(expectedIntegrationSources, config["integrationSources"]));
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
