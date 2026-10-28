// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text.Json.Nodes;
using Aspire.Cli.Acquisition;
using Aspire.Cli.Configuration;
using Aspire.Cli.Interaction;
using Aspire.Cli.Resources;
using Aspire.Cli.Templating;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.DotNet.RemoteExecutor;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using RootCommand = Aspire.Cli.Commands.RootCommand;

namespace Aspire.Cli.Tests.Commands;

public class TemplateSourceStartupTests(ITestOutputHelper outputHelper)
{
    [Theory]
    [InlineData("template source list", true)]
    [InlineData("template source list --format json", true)]
    [InlineData("--log-level Debug template source list", true)]
    [InlineData("--non-interactive true template source list", true)]
    [InlineData("template --nologo source list", true)]
    [InlineData("--log-file template source list", false)]
    [InlineData("new -- template source list", false)]
    [InlineData("-- template source list", false)]
    [InlineData("template source add list", false)]
    public void ListDetection_UsesCommandPathAndGlobalOptionArity(string arguments, bool expected)
    {
        Assert.Equal(expected, RootCommand.MatchesCommandPath(arguments.Split(' '), "template", "source", "list"));
    }

    [Theory]
    [InlineData("template source list", false)]
    [InlineData("--log-level Debug template source list --format json", false)]
    [InlineData("template --nologo source list", false)]
    [InlineData("template source list --format json", true)]
    public void ListStartup_PreservesGlobalAndProjectConfigurationBytes(string arguments, bool legacyGlobal)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var home = workspace.CreateDirectory("aspire-home");
        var globalPath = Path.Combine(home.FullName, legacyGlobal ? "globalsettings.json" : AspireConfigFile.FileName);
        var projectPath = Path.Combine(workspace.Path, AspireConfigFile.FileName);
        const string global = """{ /* global */ "features:defaultWatchEnabled": true, "templateSources": {}, }""";
        const string project = """{ /* project */ "features:defaultWatchEnabled": false, }""";
        File.WriteAllText(globalPath, global);
        File.WriteAllText(projectPath, project);

        RunStartup(home.FullName, workspace.Path, arguments);

        Assert.Equal(global, File.ReadAllText(globalPath));
        Assert.Equal(project, File.ReadAllText(projectPath));
        if (legacyGlobal)
        {
            Assert.False(File.Exists(Path.Combine(home.FullName, AspireConfigFile.FileName)));
        }
    }

    [Theory]
    [InlineData("--log-level Debug template source add company --repository https://example.com/other", false, CliExitCodes.InvalidCommand)]
    [InlineData("--log-level Debug template source add company --repository https://example.com/other", true, CliExitCodes.InvalidCommand)]
    [InlineData("template --nologo source remove missing", false, CliExitCodes.ConfigNotFound)]
    [InlineData("template --nologo source remove missing", true, CliExitCodes.InvalidCommand)]
    public void RejectedCommand_PreservesGlobalAndProjectConfigurationBytes(string arguments, bool legacyGlobal, int exitCode)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var home = workspace.CreateDirectory("aspire-home");
        var globalPath = Path.Combine(home.FullName, legacyGlobal ? "globalsettings.json" : AspireConfigFile.FileName);
        var projectPath = Path.Combine(workspace.Path, AspireConfigFile.FileName);
        const string global = """{ /* global */ "features:defaultWatchEnabled": true, "templateSources:company:repository": "https://example.com/templates", }""";
        const string project = """{ /* project */ "features:defaultWatchEnabled": false, }""";
        File.WriteAllText(globalPath, global);
        File.WriteAllText(projectPath, project);

        RunStartup(home.FullName, workspace.Path, arguments, exitCode);

        Assert.Equal(global, File.ReadAllText(globalPath));
        Assert.Equal(project, File.ReadAllText(projectPath));
        if (legacyGlobal)
        {
            Assert.False(File.Exists(Path.Combine(home.FullName, AspireConfigFile.FileName)));
        }
    }

    [Theory]
    [InlineData("add")]
    [InlineData("remove")]
    public void SuccessfulCommand_NormalizesOnlyTheUpdatedGlobalFile(string action)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var home = workspace.CreateDirectory("aspire-home");
        var globalPath = Path.Combine(home.FullName, AspireConfigFile.FileName);
        var projectPath = Path.Combine(workspace.Path, AspireConfigFile.FileName);
        const string global = """{ /* global */ "features:defaultWatchEnabled": true, "templateSources:company:repository": "https://example.com/templates", }""";
        const string project = """{ /* project */ "features:defaultWatchEnabled": false, }""";
        File.WriteAllText(globalPath, global);
        File.WriteAllText(projectPath, project);
        var (arguments, expected) = action switch
        {
            "add" => (
                "template source add extra --repository https://example.com/other",
                """{"features":{"defaultWatchEnabled":true},"templateSources":{"company":{"repository":"https://example.com/templates"},"extra":{"repository":"https://example.com/other"}}}"""),
            "remove" => (
                "template source remove company",
                """{"features":{"defaultWatchEnabled":true}}"""),
            _ => throw new ArgumentOutOfRangeException(nameof(action))
        };

        RunStartup(home.FullName, workspace.Path, arguments, CliExitCodes.Success);

        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expected), JsonNode.Parse(File.ReadAllText(globalPath))));
        Assert.Equal(project, File.ReadAllText(projectPath));
    }

    [Fact]
    public void NormalStartup_MigratesBeforeSourceManagement()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var home = workspace.CreateDirectory("aspire-home");
        var legacyPath = Path.Combine(home.FullName, "globalsettings.json");
        var migratedPath = Path.Combine(home.FullName, AspireConfigFile.FileName);
        File.WriteAllText(legacyPath, """{"features":{"defaultWatchEnabled":true}}""");
        File.WriteAllText(Path.Combine(workspace.Path, AspireConfigFile.FileName),
            """{"features":{"defaultWatchEnabled":false}}""");

        RunStartup(home.FullName, workspace.Path,
            "template source add company --repository https://example.com/templates", CliExitCodes.InvalidCommand);
        Assert.False(File.Exists(migratedPath));
        var legacy = File.ReadAllText(legacyPath);

        RunStartup(home.FullName, workspace.Path, "config info", CliExitCodes.Success);
        RunStartup(home.FullName, workspace.Path,
            "template source add company --repository https://example.com/templates", CliExitCodes.Success,
            expectedSourceRepository: "https://example.com/templates");

        Assert.True(File.Exists(migratedPath));
        Assert.Equal(legacy, File.ReadAllText(legacyPath));
        Assert.Equal("https://example.com/templates",
            JsonNode.Parse(File.ReadAllText(migratedPath))!["templateSources"]!["company"]!["repository"]!.GetValue<string>());
    }

    [Fact]
    public void SourceListing_RejectsLegacyConfigurationWithoutAttemptingMigration()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var home = workspace.CreateDirectory("aspire-home");
        var legacyPath = Path.Combine(home.FullName, "globalsettings.json");
        const string original = """
            {"features":{"defaultWatchEnabled":true},"templateSources":{"company":{"repository":"https://example.com/templates"}}}
            """;
        File.WriteAllText(legacyPath, original);
        File.WriteAllText(Path.Combine(workspace.Path, AspireConfigFile.FileName),
            """{"features":{"defaultWatchEnabled":false}}""");
        var cache = home.CreateSubdirectory("cache");
        File.WriteAllText(Path.Combine(cache.FullName, "workspace-config-locks"), "Blocks lock directory creation.");

        RunStartup(home.FullName, workspace.Path, "template source list", CliExitCodes.InvalidCommand);

        Assert.False(File.Exists(Path.Combine(home.FullName, AspireConfigFile.FileName)));
        Assert.Equal(original, File.ReadAllText(legacyPath));
    }

    [Theory]
    [InlineData("template source list")]
    [InlineData("template source add extra --repository https://example.com/extra")]
    [InlineData("template source remove company")]
    public void SourceCommands_RejectLegacyConfigurationWithoutWaitingForMutationLock(string arguments)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var home = workspace.CreateDirectory("aspire-home");
        var legacyPath = Path.Combine(home.FullName, "globalsettings.json");
        const string legacy = """{"templateSources":{"company":{"repository":"https://example.com/original"}}}""";
        File.WriteAllText(legacyPath, legacy);
        var options = new RemoteInvokeOptions();
        options.StartInfo.WorkingDirectory = workspace.Path;

        using (RemoteExecutor.Invoke(static async (homePath, command) =>
        {
            Environment.SetEnvironmentVariable("ASPIRE_HOME", homePath);
            Environment.SetEnvironmentVariable("ASPIRE_EXTENSION_ENDPOINT", null);
            var args = command.Split(' ');
            using var cancellationManager = new ConsoleCancellationManager(finalDrainBudget: Timeout.InfiniteTimeSpan);
            using var startupContext = CreateStartupContext(args, cancellationManager);
            using var host = await Program.BuildApplicationAsync(args, startupContext).DefaultTimeout();
            var configuration = host.Services.GetRequiredService<IConfigurationService>();
            var originalPath = Path.Combine(homePath, "globalsettings.json");
            var destination = Path.Combine(homePath, AspireConfigFile.FileName);
            Assert.Equal(originalPath, configuration.GetSettingsFilePath(isGlobal: true));

            using (var configLock = await ConfigurationHelper.TryAcquireWorkspaceConfigLockAsync(
                host.Services.GetRequiredService<CliExecutionContext>(), new FileInfo(destination),
                NullLogger.Instance, CancellationToken.None))
            {
                Assert.NotNull(configLock);
                var store = host.Services.GetRequiredService<TemplateSourceStore>();
                var error = await Assert.ThrowsAsync<TemplateSourceConfigurationException>(() => args[^1] switch
                {
                    "list" => store.ListAsync(CancellationToken.None),
                    "company" => store.RemoveAsync("company", CancellationToken.None),
                    _ => store.AddAsync("extra", new JsonObject { ["repository"] = "https://example.com/extra" }, CancellationToken.None)
                }).DefaultTimeout();
                Assert.Equal(new TemplateSourceConfigurationException(
                    "templateSources", NewCommandStrings.TemplateSourceLegacyConfiguration).Message, error.Message);
                Assert.Equal(CliExitCodes.InvalidCommand,
                    await host.Services.GetRequiredService<RootCommand>().Parse(args).InvokeAsync().DefaultTimeout());
                Assert.False(File.Exists(destination));
            }
        }, home.FullName, arguments, options))
        {
        }

        Assert.Equal(legacy, File.ReadAllText(legacyPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NormalStartup_WaitsForGlobalLockAndRechecksBothFiles(bool destinationPublished)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var home = workspace.CreateDirectory("aspire-home");
        var legacyPath = Path.Combine(home.FullName, "globalsettings.json");
        const string original = """{"templateSources":{}}""";
        File.WriteAllText(legacyPath, original);
        var options = new RemoteInvokeOptions();
        options.StartInfo.WorkingDirectory = workspace.Path;

        using (RemoteExecutor.Invoke(static async (homePath, publishDestination) =>
        {
            Environment.SetEnvironmentVariable("ASPIRE_HOME", homePath);
            Environment.SetEnvironmentVariable("ASPIRE_EXTENSION_ENDPOINT", null);
            string[] readArgs = ["template", "source", "list"];
            using var cancellationManager = new ConsoleCancellationManager(finalDrainBudget: Timeout.InfiniteTimeSpan);
            using var readContext = CreateStartupContext(readArgs, cancellationManager);
            using var readHost = await Program.BuildApplicationAsync(readArgs, readContext).DefaultTimeout();
            string[] migrateArgs = ["new", "--help"];
            using var migrateContext = CreateStartupContext(migrateArgs, cancellationManager);
            var destination = Path.Combine(homePath, AspireConfigFile.FileName);
            var legacy = Path.Combine(homePath, "globalsettings.json");
            Task<IHost> migration;
            using (var configLock = await ConfigurationHelper.TryAcquireWorkspaceConfigLockAsync(
                readHost.Services.GetRequiredService<CliExecutionContext>(), new FileInfo(destination),
                NullLogger.Instance, CancellationToken.None))
            {
                Assert.NotNull(configLock);
                migration = Program.BuildApplicationAsync(migrateArgs, migrateContext);
                await Assert.ThrowsAsync<TimeoutException>(() => migration.WaitAsync(TimeSpan.FromSeconds(1)));
                Assert.False(File.Exists(destination));

                var target = bool.Parse(publishDestination) ? destination : legacy;
                await ConfigurationHelper.WriteSettingsFileAtomicallyAsync(target, JsonNode.Parse("""
                    {"features":{"defaultWatchEnabled":true}}
                    """)!.AsObject(), CancellationToken.None);
            }
            using var migratedHost = await migration.DefaultTimeout();
            Assert.Equal(destination, migratedHost.Services.GetRequiredService<IConfigurationService>().GetSettingsFilePath(isGlobal: true));
            Assert.True(JsonNode.Parse(await File.ReadAllTextAsync(destination))!["features"]!["defaultWatchEnabled"]!.GetValue<bool>());
        }, home.FullName, destinationPublished.ToString(CultureInfo.InvariantCulture), options))
        {
        }

        if (destinationPublished)
        {
            Assert.Equal(original, File.ReadAllText(legacyPath));
        }
    }

    [Theory]
    [InlineData("overrideStagingFeed", "https://example.com/feed")]
    [InlineData("certificates.nssDbPaths", "firefox=/profiles/firefox")]
    public void ConfigSet_MigratesBeforeEditingNonTemplateGlobalSettings(string key, string value)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var home = workspace.CreateDirectory("aspire-home");
        var legacyPath = Path.Combine(home.FullName, "globalsettings.json");
        var migratedPath = Path.Combine(home.FullName, AspireConfigFile.FileName);
        File.WriteAllText(legacyPath, """{"features":{"defaultWatchEnabled":true}}""");
        File.WriteAllText(Path.Combine(workspace.Path, AspireConfigFile.FileName),
            """{"features":{"defaultWatchEnabled":false}}""");

        RunStartup(home.FullName, workspace.Path, $"config set {key} {value} --global", CliExitCodes.Success);

        Assert.True(File.Exists(migratedPath));
        var configurationKey = key.Replace('.', ':');
        Assert.Equal(value, new ConfigurationBuilder().AddJsonFile(migratedPath).Build()[configurationKey]);

        RunStartup(home.FullName, workspace.Path, "new --help", CliExitCodes.Success);

        Assert.Equal(value, new ConfigurationBuilder().AddJsonFile(migratedPath).Build()[configurationKey]);
    }

    [Fact]
    public void ConfigDelete_MigratesBeforeEditingGlobalSettings()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var home = workspace.CreateDirectory("aspire-home");
        var legacyPath = Path.Combine(home.FullName, "globalsettings.json");
        var migratedPath = Path.Combine(home.FullName, AspireConfigFile.FileName);
        const string original = """{"features":{"defaultWatchEnabled":true}}""";
        File.WriteAllText(legacyPath, original);
        File.WriteAllText(Path.Combine(workspace.Path, AspireConfigFile.FileName),
            """{"features":{"defaultWatchEnabled":false}}""");

        RunStartup(home.FullName, workspace.Path, "config delete features.defaultWatchEnabled --global", CliExitCodes.Success);

        Assert.True(File.Exists(migratedPath));
        Assert.Null(new ConfigurationBuilder().AddJsonFile(migratedPath).Build()["features:defaultWatchEnabled"]);
        Assert.Equal(original, File.ReadAllText(legacyPath));
    }

    [Fact]
    public void ConfigSet_AppHostPathPreservesFlattenedLegacyFeatureSettings()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var home = workspace.CreateDirectory("aspire-home");
        var legacyDirectory = workspace.CreateDirectory(".aspire");
        File.WriteAllText(Path.Combine(legacyDirectory.FullName, "settings.json"),
            """{"features:defaultWatchEnabled":false}""");

        RunStartup(home.FullName, workspace.Path, "config set appHost.path apphost.cs", CliExitCodes.Success);

        var migrated = AspireConfigFile.Load(workspace.Path);
        Assert.NotNull(migrated);
        Assert.Equal("apphost.cs", migrated.AppHost?.Path);
        Assert.NotNull(migrated.Features);
        Assert.False(migrated.Features["defaultWatchEnabled"]);
    }

    [Fact]
    public void NormalStartup_MalformedLegacyGlobalConfigurationAllowsRecovery()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var home = workspace.CreateDirectory("aspire-home");
        var legacyPath = Path.Combine(home.FullName, "globalsettings.json");
        var migratedPath = Path.Combine(home.FullName, AspireConfigFile.FileName);
        const string malformed = "this is not valid json {{{";
        File.WriteAllText(legacyPath, malformed);
        File.WriteAllText(Path.Combine(workspace.Path, AspireConfigFile.FileName),
            """{"features":{"defaultWatchEnabled":false}}""");

        RunStartup(home.FullName, workspace.Path, "--version", CliExitCodes.Success);

        Assert.False(File.Exists(migratedPath));
        Assert.Equal(malformed, File.ReadAllText(legacyPath));

        RunStartup(home.FullName, workspace.Path, "config set channel stable --global", CliExitCodes.Success);
        RunStartup(home.FullName, workspace.Path, "config get channel", CliExitCodes.Success);

        Assert.Equal("stable", JsonNode.Parse(File.ReadAllText(migratedPath))!["channel"]!.GetValue<string>());
        Assert.Equal(malformed, File.ReadAllText(legacyPath));
    }

    private static Program.CliStartupContext CreateStartupContext(string[] args, ConsoleCancellationManager cancellationManager)
    {
        var loggingOptions = Program.ParseLoggingOptions(args);
        var errorWriter = new TestStartupErrorWriter();
        var logBufferContext = new ConsoleLogBufferContext();
        var (loggerFactory, fileLoggerProvider) = Program.CreateLoggerFactory(args, loggingOptions, errorWriter, logBufferContext);
        return new Program.CliStartupContext(
            loggingOptions, errorWriter, loggerFactory, fileLoggerProvider, logBufferContext,
            loggerFactory.CreateLogger(Program.RootLoggerName), cancellationManager,
            new IdentityChannelReader(typeof(Program).Assembly));
    }

    private static void RunStartup(string home, string workingDirectory, string arguments, int? expectedExitCode = null, string? expectedSourceRepository = null)
    {
        var options = new RemoteInvokeOptions();
        options.StartInfo.WorkingDirectory = workingDirectory;

        using (RemoteExecutor.Invoke(static async (homePath, commandLine, exitCode, sourceRepository) =>
        {
            Environment.SetEnvironmentVariable("ASPIRE_HOME", homePath);
            Environment.SetEnvironmentVariable("ASPIRE_EXTENSION_ENDPOINT", null);
            var args = commandLine.Split(' ');
            using var cancellationManager = new ConsoleCancellationManager(finalDrainBudget: Timeout.InfiniteTimeSpan);
            using var startupContext = CreateStartupContext(args, cancellationManager);
            using var host = await Program.BuildApplicationAsync(args, startupContext).DefaultTimeout();

            Assert.Equal("False", host.Services.GetRequiredService<IConfiguration>()["features:defaultWatchEnabled"]);
            var parseResult = host.Services.GetRequiredService<RootCommand>().Parse(args);
            Assert.Empty(parseResult.Errors);
            if (exitCode.Length == 0)
            {
                Assert.Equal("list", parseResult.CommandResult.Command.Name);
            }
            else
            {
                Assert.Equal(int.Parse(exitCode, CultureInfo.InvariantCulture), await parseResult.InvokeAsync().DefaultTimeout());
            }
            if (sourceRepository.Length > 0)
            {
                var sources = await host.Services.GetRequiredService<ConfiguredTemplateSourceService>().GetSourcesAsync(CancellationToken.None);
                var source = Assert.Single(sources);
                Assert.Equal("company", source.Key);
                Assert.Equal(sourceRepository, source.Value.Template?.Repository);
            }
        }, home, arguments, expectedExitCode?.ToString(CultureInfo.InvariantCulture) ?? "", expectedSourceRepository ?? "", options))
        {
        }
    }
}
