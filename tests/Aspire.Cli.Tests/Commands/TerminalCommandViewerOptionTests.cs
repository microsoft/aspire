// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Commands;
using Aspire.Cli.Tests.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Cli.Tests.Commands;

/// <summary>
/// Unit tests for the parsing surface of the <c>terminal</c> command's
/// <c>--viewer</c> flag, which toggles whether the CLI takes primary on connect
/// or stays secondary. Protocol-level emission of ClientHello and RequestPrimary
/// is exercised by Hex1b's own multi-head test suite.
/// </summary>
public class TerminalCommandViewerOptionTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public void ViewerOption_Help_DescribesPrimarySecondaryBehaviour()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, o => o.EnabledFeatures = [KnownFeatures.TerminalCommandsEnabled]);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("terminal attach --help");

        // Route --help output through System.CommandLine's InvocationConfiguration
        // rather than swapping Console.Out. Console.SetOut mutates process-wide
        // state and this test project enables parallel execution across classes,
        // so any concurrent test invoking a command that writes to Console.Out
        // would silently steer its output into this StringWriter (or vice versa).
        using var sw = new StringWriter();
        var invokeConfig = new System.CommandLine.InvocationConfiguration
        {
            EnableDefaultExceptionHandler = false,
            Output = sw,
        };
        result.Invoke(invokeConfig);

        var output = sw.ToString();
        Assert.Contains("--viewer", output, StringComparison.Ordinal);
        Assert.Contains("primary", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ViewerOption_DefaultIsFalse_WhenNotSpecified()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, o => o.EnabledFeatures = [KnownFeatures.TerminalCommandsEnabled]);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("terminal attach myresource");

        Assert.Empty(result.Errors);
        var viewerValue = result.GetValue<bool>("--viewer");
        Assert.False(viewerValue);
    }

    [Fact]
    public void ViewerOption_ParsesToTrue_WhenSpecified()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, o => o.EnabledFeatures = [KnownFeatures.TerminalCommandsEnabled]);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("terminal attach myresource --viewer");

        Assert.Empty(result.Errors);
        var viewerValue = result.GetValue<bool>("--viewer");
        Assert.True(viewerValue);
    }
}
