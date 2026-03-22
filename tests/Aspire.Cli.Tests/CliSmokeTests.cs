// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.AspNetCore.InternalTesting;
using Microsoft.DotNet.RemoteExecutor;

namespace Aspire.Cli.Tests;

public class CliSmokeTests(ITestOutputHelper outputHelper)
{
    private static readonly RemoteInvokeOptions s_remoteInvokeOptions = new()
    {
        StartInfo = { RedirectStandardOutput = true }
    };

    [Theory]
    [InlineData(new string[] { }, ExitCodeConstants.InvalidCommand)]
    [InlineData(new[] { "-d", "--help" }, ExitCodeConstants.Success)]
    [InlineData(new[] { "--help" }, ExitCodeConstants.Success)]
    [InlineData(new[] { "--version" }, ExitCodeConstants.Success)]
    public async Task MainReturnsExpectedExitCode(string[] args, int expectedExitCode)
    {
        var exitCode = await Program.Main(args).DefaultTimeout();
        Assert.Equal(expectedExitCode, exitCode);
    }

    [Theory]
    [InlineData("invalid-locale", false)]
    [InlineData("", true)]
    [InlineData("en-US", true)]
    [InlineData("fr", true)]
    [InlineData("fr", true, "DOTNET_CLI_UI_LANGUAGE")]
    [InlineData("el", false)]
    public void LocaleOverrideReturnsExitCode(string locale, bool isValid, string environmentVariableName = "ASPIRE_LOCALE_OVERRIDE")
    {
        using var result = RemoteExecutor.Invoke(async (loc, validStr, envVar) =>
        {
            var valid = bool.Parse(validStr);
            await using var errorWriter = new StringWriter();
            var oldErrorOutput = Console.Error;
            Console.SetError(errorWriter);
            Environment.SetEnvironmentVariable(envVar, loc);
            // Suppress first-time use notice to avoid extra lines in stderr
            Environment.SetEnvironmentVariable(CliConfigNames.NoLogo, "true");
            await Program.Main([]).DefaultTimeout();
            Environment.SetEnvironmentVariable(envVar, null);
            Environment.SetEnvironmentVariable(CliConfigNames.NoLogo, null);
            Console.SetError(oldErrorOutput);

            var errorOutput = errorWriter.ToString();
            var lines = errorOutput.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

            // Write to stdout so it can be captured by the test harness
            Console.WriteLine($"Error output: {errorOutput}");

            // Valid locales should not produce locale error messages
            if (valid)
            {
                Assert.DoesNotContain(lines, line => line.Contains("locale", StringComparison.OrdinalIgnoreCase));
            }
            else
            {
                Assert.Contains(lines, line => line.Contains("locale", StringComparison.OrdinalIgnoreCase));
            }
        }, locale, isValid.ToString(), environmentVariableName, options: s_remoteInvokeOptions);

        outputHelper.WriteLine(result.Process.StandardOutput.ReadToEnd());
    }

    [Fact]
    public void DebugOutputWritesToStderr()
    {
        using var result = RemoteExecutor.Invoke(async () =>
        {
            await using var errorWriter = new StringWriter();
            var oldErrorOutput = Console.Error;
            Console.SetError(errorWriter);

            await Program.Main(["-d", "--help"]).DefaultTimeout();

            Console.SetError(oldErrorOutput);
            var errorOutput = errorWriter.ToString();

            // Write to stdout so it can be captured by the test harness
            Console.WriteLine($"Error output: {errorOutput}");

            // Debug mode should write log output to stderr (SpectreConsoleLogger uses [HH:mm:ss] [level] Category: message format)
            var lines = errorOutput.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
            Assert.Contains(lines, line => line.EndsWith("[dbug] Program: Parsing arguments: -d --help"));
        }, options: s_remoteInvokeOptions);

        outputHelper.WriteLine(result.Process.StandardOutput.ReadToEnd());
    }

    [Fact]
    public void VersionFlagSuppressesBanner()
    {
        using var result = RemoteExecutor.Invoke(async () =>
        {
            await using var outputWriter = new StringWriter();
            var oldOutput = Console.Out;
            Console.SetOut(outputWriter);

            await Program.Main(["--version"]).DefaultTimeout();

            Console.SetOut(oldOutput);
            var output = outputWriter.ToString();

            // Write to stdout so it can be captured by the test harness
            Console.WriteLine($"Output: {output}");

            // The output should only contain the version, not the animated banner
            // The banner contains "Welcome to the" and ASCII art
            Assert.DoesNotContain("Welcome to the", output);
            Assert.DoesNotContain("█████", output);
            
            // The output should contain a version number
            Assert.Contains(".", output); // Version should have at least one dot
        }, options: s_remoteInvokeOptions);

        outputHelper.WriteLine(result.Process.StandardOutput.ReadToEnd());
    }
}
