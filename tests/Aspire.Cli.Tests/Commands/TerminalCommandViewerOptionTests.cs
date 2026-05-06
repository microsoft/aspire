// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Commands;
using Aspire.Cli.Tests.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Cli.Tests.Commands;

/// <summary>
/// Phase 11 (multi-head HMP1 wire-up) unit tests for the parsing surface of the
/// <c>terminal</c> command. The <c>--viewer</c> flag toggles whether the CLI takes
/// primary on connect or stays secondary; protocol-level emission of ClientHello and
/// RequestPrimary is exercised by Hex1b's own multi-head test suite.
/// </summary>
public class TerminalCommandViewerOptionTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public void ViewerOption_Help_DescribesPrimarySecondaryBehaviour()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("terminal --help");

        var output = CaptureHelpOutput(() => result.Invoke());
        Assert.Contains("--viewer", output, StringComparison.Ordinal);
        Assert.Contains("primary", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ViewerOption_DefaultIsFalse_WhenNotSpecified()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("terminal myresource");

        Assert.Empty(result.Errors);
        var viewerValue = result.GetValue<bool>("--viewer");
        Assert.False(viewerValue);
    }

    [Fact]
    public void ViewerOption_ParsesToTrue_WhenSpecified()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("terminal myresource --viewer");

        Assert.Empty(result.Errors);
        var viewerValue = result.GetValue<bool>("--viewer");
        Assert.True(viewerValue);
    }

    private static string CaptureHelpOutput(Action invoke)
    {
        var originalOut = Console.Out;
        using var sw = new StringWriter();
        Console.SetOut(sw);
        try
        {
            invoke();
        }
        finally
        {
            Console.SetOut(originalOut);
        }
        return sw.ToString();
    }
}
