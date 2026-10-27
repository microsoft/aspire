// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text.Json.Nodes;
using Aspire.Cli.Acquisition;
using Aspire.Cli.Configuration;
using Aspire.Cli.Interaction;
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
        const string global = """{ /* global */ "features:defaultWatchEnabled": true, "templates": {}, }""";
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
    [InlineData("--log-level Debug template source add company --href https://example.com/other.json", false, CliExitCodes.InvalidCommand)]
    [InlineData("--log-level Debug template source add company --href https://example.com/other.json", true, CliExitCodes.InvalidCommand)]
    [InlineData("template --nologo source remove missing", false, CliExitCodes.ConfigNotFound)]
    [InlineData("template --nologo source remove missing", true, CliExitCodes.ConfigNotFound)]
    [InlineData("--non-interactive true config set templates.company.repository https://github.com/example/templates --global", false, CliExitCodes.InvalidCommand)]
    [InlineData("--non-interactive true config set templates.company.repository https://github.com/example/templates --global", true, CliExitCodes.InvalidCommand)]
    [InlineData("config delete missing --global", false, CliExitCodes.InvalidCommand)]
    [InlineData("config delete missing --global", true, CliExitCodes.InvalidCommand)]
    public void RejectedCommand_PreservesGlobalAndProjectConfigurationBytes(string arguments, bool legacyGlobal, int exitCode)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var home = workspace.CreateDirectory("aspire-home");
        var globalPath = Path.Combine(home.FullName, legacyGlobal ? "globalsettings.json" : AspireConfigFile.FileName);
        var projectPath = Path.Combine(workspace.Path, AspireConfigFile.FileName);
        const string global = """{ /* global */ "features:defaultWatchEnabled": true, "templates:company:href": "https://example.com/templates.json", }""";
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
    [InlineData("add", false)]
    [InlineData("add", true)]
    [InlineData("remove", false)]
    [InlineData("remove", true)]
    [InlineData("set", false)]
    [InlineData("set", true)]
    [InlineData("delete", false)]
    [InlineData("delete", true)]
    public void SuccessfulCommand_NormalizesOnlyTheUpdatedGlobalFile(string action, bool legacyGlobal)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var home = workspace.CreateDirectory("aspire-home");
        var globalPath = Path.Combine(home.FullName, legacyGlobal ? "globalsettings.json" : AspireConfigFile.FileName);
        var projectPath = Path.Combine(workspace.Path, AspireConfigFile.FileName);
        const string global = """{ /* global */ "features:defaultWatchEnabled": true, "templates:company:href": "https://example.com/templates.json", }""";
        const string project = """{ /* project */ "features:defaultWatchEnabled": false, }""";
        File.WriteAllText(globalPath, global);
        File.WriteAllText(projectPath, project);
        var (arguments, expected) = action switch
        {
            "add" => (
                "template source add extra --href https://example.com/other.json",
                """{"features":{"defaultWatchEnabled":true},"templates":{"company":{"href":"https://example.com/templates.json"},"extra":{"href":"https://example.com/other.json"}}}"""),
            "remove" => (
                "template source remove company",
                """{"features":{"defaultWatchEnabled":true}}"""),
            "set" => (
                "config set templates.company.href https://example.com/updated.json --global",
                """{"features":{"defaultWatchEnabled":true},"templates":{"company":{"href":"https://example.com/updated.json"}}}"""),
            "delete" => (
                "config delete templates.company --global",
                """{"features":{"defaultWatchEnabled":true}}"""),
            _ => throw new ArgumentOutOfRangeException(nameof(action))
        };

        RunStartup(home.FullName, workspace.Path, arguments, CliExitCodes.Success);

        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expected), JsonNode.Parse(File.ReadAllText(globalPath))));
        Assert.Equal(project, File.ReadAllText(projectPath));
        if (legacyGlobal)
        {
            Assert.False(File.Exists(Path.Combine(home.FullName, AspireConfigFile.FileName)));
        }
    }

    [Theory]
    [InlineData("template source add company --href https://example.com/templates.json")]
    [InlineData("config set templates.company.href https://example.com/templates.json --global")]
    public void NormalStartup_PreservesSourcesRegisteredInLegacyGlobalConfiguration(string arguments)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var home = workspace.CreateDirectory("aspire-home");
        var legacyPath = Path.Combine(home.FullName, "globalsettings.json");
        var migratedPath = Path.Combine(home.FullName, AspireConfigFile.FileName);
        File.WriteAllText(legacyPath, """{"features":{"defaultWatchEnabled":true}}""");
        File.WriteAllText(Path.Combine(workspace.Path, AspireConfigFile.FileName),
            """{"features":{"defaultWatchEnabled":false}}""");

        RunStartup(home.FullName, workspace.Path, arguments, CliExitCodes.Success);
        Assert.False(File.Exists(migratedPath));
        var legacy = File.ReadAllText(legacyPath);

        RunStartup(home.FullName, workspace.Path, "new --help", CliExitCodes.Success,
            expectedSourceHref: "https://example.com/templates.json");

        Assert.True(File.Exists(migratedPath));
        Assert.Equal(legacy, File.ReadAllText(legacyPath));
        Assert.True(JsonNode.DeepEquals(
            JsonNode.Parse(legacy)!["templates"],
            JsonNode.Parse(File.ReadAllText(migratedPath))!["templates"]));
    }

    [Theory]
    [InlineData("template source list")]
    [InlineData("new --help")]
    public void UnavailableMigrationLock_KeepsLegacyGlobalConfigurationActive(string arguments)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var home = workspace.CreateDirectory("aspire-home");
        var legacyPath = Path.Combine(home.FullName, "globalsettings.json");
        const string original = """
            {"features":{"defaultWatchEnabled":true},"templates":{"company":{"href":"https://example.com/templates.json"}}}
            """;
        File.WriteAllText(legacyPath, original);
        File.WriteAllText(Path.Combine(workspace.Path, AspireConfigFile.FileName),
            """{"features":{"defaultWatchEnabled":false}}""");
        var cache = home.CreateSubdirectory("cache");
        File.WriteAllText(Path.Combine(cache.FullName, "workspace-config-locks"), "Blocks lock directory creation.");

        RunStartup(home.FullName, workspace.Path, arguments, CliExitCodes.Success,
            expectedSourceHref: "https://example.com/templates.json");

        Assert.False(File.Exists(Path.Combine(home.FullName, AspireConfigFile.FileName)));
        Assert.Equal(original, File.ReadAllText(legacyPath));
    }

    [Theory]
    [InlineData("add")]
    [InlineData("remove")]
    [InlineData("set")]
    [InlineData("delete")]
    public void GlobalMutation_RechecksDestinationAfterMigrationWhileWaitingForLock(string action)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var home = workspace.CreateDirectory("aspire-home");
        var legacyPath = Path.Combine(home.FullName, "globalsettings.json");
        const string legacy = """{"templates":{"company":{"href":"https://example.com/original"}}}""";
        File.WriteAllText(legacyPath, legacy);
        var options = new RemoteInvokeOptions();
        options.StartInfo.WorkingDirectory = workspace.Path;

        using (RemoteExecutor.Invoke(static async (homePath, operation) =>
        {
            Environment.SetEnvironmentVariable("ASPIRE_HOME", homePath);
            Environment.SetEnvironmentVariable("ASPIRE_EXTENSION_ENDPOINT", null);
            var command = operation switch
            {
                "add" => "template source add extra --href https://example.com/extra",
                "remove" => "template source remove company",
                "set" => "config set templates.company.href https://example.com/updated --global",
                "delete" => "config delete templates.company --global",
                _ => throw new ArgumentOutOfRangeException(nameof(operation))
            };
            var args = command.Split(' ');
            using var cancellationManager = new ConsoleCancellationManager(finalDrainBudget: Timeout.InfiniteTimeSpan);
            using var startupContext = CreateStartupContext(args, cancellationManager);
            using var host = await Program.BuildApplicationAsync(args, startupContext).DefaultTimeout();
            var configuration = host.Services.GetRequiredService<IConfigurationService>();
            var originalPath = Path.Combine(homePath, "globalsettings.json");
            var destination = Path.Combine(homePath, AspireConfigFile.FileName);
            Assert.Equal(originalPath, configuration.GetSettingsFilePath(isGlobal: true));

            var root = host.Services.GetRequiredService<RootCommand>();
            Task<int> mutation;
            using (var configLock = await ConfigurationHelper.TryAcquireWorkspaceConfigLockAsync(
                host.Services.GetRequiredService<CliExecutionContext>(), new FileInfo(destination),
                NullLogger.Instance, CancellationToken.None))
            {
                Assert.NotNull(configLock);
                mutation = root.Parse(args).InvokeAsync();
                await Assert.ThrowsAsync<TimeoutException>(() => mutation.WaitAsync(TimeSpan.FromSeconds(1)));
                Assert.False(File.Exists(destination));

                // Publish a migration while owning its destination lock. The waiting command
                // must retain this newer state rather than editing its startup's legacy file.
                await ConfigurationHelper.WriteSettingsFileAtomicallyAsync(destination, JsonNode.Parse("""
                    {"templates":{
                      "company":{"href":"https://example.com/current"},
                      "concurrent":{"href":"https://example.com/concurrent"}
                    }}
                    """)!.AsObject(), CancellationToken.None);
            }
            Assert.Equal(CliExitCodes.Success, await mutation.DefaultTimeout());
            Assert.Equal(destination, configuration.GetSettingsFilePath(isGlobal: true));

            var sources = await host.Services.GetRequiredService<ConfiguredTemplateSourceService>().GetSourcesAsync(CancellationToken.None);
            (string, string?)[] expected = operation switch
            {
                "add" => [("company", "https://example.com/current"), ("concurrent", "https://example.com/concurrent"), ("extra", "https://example.com/extra")],
                "set" => [("company", "https://example.com/updated"), ("concurrent", "https://example.com/concurrent")],
                _ => [("concurrent", "https://example.com/concurrent")]
            };
            Assert.Equal(expected, sources.OrderBy(source => source.Key, StringComparer.Ordinal).Select(source => (source.Key, source.Value.Href)));
            Assert.Equal("https://example.com/concurrent", (await configuration.GetGlobalConfigurationAsync())["templates.concurrent.href"]);
            Assert.Equal("https://example.com/concurrent", (await configuration.GetAllConfigurationAsync())["templates.concurrent.href"]);
            Assert.Equal("https://example.com/concurrent", await configuration.GetConfigurationFromDirectoryAsync(
                "templates.concurrent.href", new DirectoryInfo(Environment.CurrentDirectory)));
        }, home.FullName, action, options))
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
        const string original = """{"templates":{}}""";
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
                    {"templates":{"company":{"href":"https://example.com/latest"}}}
                    """)!.AsObject(), CancellationToken.None);
            }
            using var migratedHost = await migration.DefaultTimeout();
            Assert.Equal(destination, migratedHost.Services.GetRequiredService<IConfigurationService>().GetSettingsFilePath(isGlobal: true));
            var source = Assert.Single(await migratedHost.Services.GetRequiredService<ConfiguredTemplateSourceService>().GetSourcesAsync(CancellationToken.None));
            Assert.Equal("company", source.Key);
            Assert.Equal("https://example.com/latest", source.Value.Href);
        }, home.FullName, destinationPublished.ToString(CultureInfo.InvariantCulture), options))
        {
        }

        if (destinationPublished)
        {
            Assert.Equal(original, File.ReadAllText(legacyPath));
        }
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

    private static void RunStartup(string home, string workingDirectory, string arguments, int? expectedExitCode = null, string? expectedSourceHref = null)
    {
        var options = new RemoteInvokeOptions();
        options.StartInfo.WorkingDirectory = workingDirectory;

        using (RemoteExecutor.Invoke(static async (homePath, commandLine, exitCode, sourceHref) =>
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
            if (sourceHref.Length > 0)
            {
                var sources = await host.Services.GetRequiredService<ConfiguredTemplateSourceService>().GetSourcesAsync(CancellationToken.None);
                var source = Assert.Single(sources);
                Assert.Equal("company", source.Key);
                Assert.Equal(sourceHref, source.Value.Href);
            }
        }, home, arguments, expectedExitCode?.ToString(CultureInfo.InvariantCulture) ?? "", expectedSourceHref ?? "", options))
        {
        }
    }
}
