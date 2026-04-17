// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;
using System.Text;
using Aspire.Cli.Utils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;

namespace Aspire.Cli.Tests;

public class ProgramTests
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
}
