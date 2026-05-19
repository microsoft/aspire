// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;
using System.Text;
using Aspire.Cli.Configuration;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Aspire.Cli.Utils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;

namespace Aspire.Cli.Tests;

public class ProgramTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public void ParseLogFileOption_ReturnsNull_WhenArgsAreNull()
    {
        var result = Program.ParseLogFileOption(null);

        Assert.Null(result);
    }

    [Fact]
    public void ParseLogFileOption_ReturnsValue_WhenOptionAppearsBeforeDelimiter()
    {
        var result = Program.ParseLogFileOption(["run", "--log-file", "cli.log", "--", "--log-file", "app.log"]);

        Assert.Equal("cli.log", result);
    }

    [Fact]
    public void ParseLogFileOption_IgnoresValue_WhenOptionAppearsAfterDelimiter()
    {
        var result = Program.ParseLogFileOption(["run", "--", "--log-file", "app.log"]);

        Assert.Null(result);
    }

    [Fact]
    public void BuildAnsiConsole_DoesNotReenablePlaygroundFormatting_WhenHostDisablesAnsi()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ASPIRE_PLAYGROUND"] = "true"
        }).Build());
        services.AddSingleton<ICliHostEnvironment>(new TestCliHostEnvironment(supportsAnsi: false, supportsInteractiveOutput: false));

        var serviceProvider = services.BuildServiceProvider();
        var writer = new StringWriter(new StringBuilder());
        var buildAnsiConsole = typeof(Program).GetMethod("BuildAnsiConsole", BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(buildAnsiConsole);

        var ansiConsole = Assert.IsAssignableFrom<Spectre.Console.IAnsiConsole>(buildAnsiConsole.Invoke(null, [serviceProvider, writer]));

        ansiConsole.MarkupLine("[red]hello[/]");

        var output = writer.ToString();
        Assert.Contains("hello", output, StringComparison.Ordinal);
        Assert.DoesNotContain("\u001b", output, StringComparison.Ordinal);
    }

    [Fact]
    public void WarnIfGlobalSettingsContainAppHostPath_WritesWarning_WhenGlobalConfigHasAppHostPath()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var settingsPath = Path.Combine(workspace.WorkspaceRoot.FullName, AspireConfigFile.FileName);
        File.WriteAllText(settingsPath, """{ "appHost": { "path": "AppHost.csproj" } }""");
        var errorWriter = new TestStartupErrorWriter();

        Program.WarnIfGlobalSettingsContainAppHostPath(new FileInfo(settingsPath), errorWriter);

        Assert.Empty(errorWriter.Lines);
        var line = Assert.Single(errorWriter.MarkupLines);
        Assert.DoesNotContain("[yellow]", line, StringComparison.Ordinal);
        Assert.Contains(settingsPath, line, StringComparison.Ordinal);
        Assert.Contains("appHost.path", line, StringComparison.Ordinal);
    }

    [Fact]
    public void WarnIfGlobalSettingsContainAppHostPath_DoesNotWarn_WhenGlobalConfigHasNoAppHostPath()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var settingsPath = Path.Combine(workspace.WorkspaceRoot.FullName, AspireConfigFile.FileName);
        File.WriteAllText(settingsPath, """{ "channel": "daily" }""");
        var errorWriter = new TestStartupErrorWriter();

        Program.WarnIfGlobalSettingsContainAppHostPath(new FileInfo(settingsPath), errorWriter);

        Assert.Empty(errorWriter.Lines);
        Assert.Empty(errorWriter.MarkupLines);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("true")]
    [InlineData("""{ "appHost": { "path": "AppHost.csproj" }""")]
    public void WarnIfGlobalSettingsContainAppHostPath_DoesNotWarn_WhenGlobalConfigCannotBeLoaded(string content)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var settingsPath = Path.Combine(workspace.WorkspaceRoot.FullName, AspireConfigFile.FileName);
        File.WriteAllText(settingsPath, content);
        var errorWriter = new TestStartupErrorWriter();

        Program.WarnIfGlobalSettingsContainAppHostPath(new FileInfo(settingsPath), errorWriter);

        Assert.Empty(errorWriter.Lines);
        Assert.Empty(errorWriter.MarkupLines);
    }
}
