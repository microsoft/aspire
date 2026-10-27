// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Acquisition;
using Aspire.Cli.Configuration;
using Aspire.Cli.Interaction;
using Aspire.Cli.Tests.TestServices;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.DotNet.RemoteExecutor;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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
        var options = new RemoteInvokeOptions();
        options.StartInfo.WorkingDirectory = workspace.Path;

        using (RemoteExecutor.Invoke(static async (homePath, commandLine) =>
        {
            Environment.SetEnvironmentVariable("ASPIRE_HOME", homePath);
            Environment.SetEnvironmentVariable("ASPIRE_EXTENSION_ENDPOINT", null);
            var args = commandLine.Split(' ');
            var loggingOptions = Program.ParseLoggingOptions(args);
            var errorWriter = new TestStartupErrorWriter();
            var logBufferContext = new ConsoleLogBufferContext();
            var (loggerFactory, fileLoggerProvider) = Program.CreateLoggerFactory(args, loggingOptions, errorWriter, logBufferContext);
            using var startupContext = new Program.CliStartupContext(
                loggingOptions, errorWriter, loggerFactory, fileLoggerProvider, logBufferContext,
                loggerFactory.CreateLogger(Program.RootLoggerName),
                new ConsoleCancellationManager(finalDrainBudget: Timeout.InfiniteTimeSpan),
                new IdentityChannelReader(typeof(Program).Assembly));
            using var host = await Program.BuildApplicationAsync(args, startupContext).DefaultTimeout();

            Assert.Equal("False", host.Services.GetRequiredService<IConfiguration>()["features:defaultWatchEnabled"]);
            Assert.Equal("list", host.Services.GetRequiredService<RootCommand>().Parse(args).CommandResult.Command.Name);
        }, home.FullName, arguments, options))
        {
        }

        Assert.Equal(global, File.ReadAllText(globalPath));
        Assert.Equal(project, File.ReadAllText(projectPath));
        if (legacyGlobal)
        {
            Assert.False(File.Exists(Path.Combine(home.FullName, AspireConfigFile.FileName)));
        }
    }
}
