// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Cli.Commands;
using Aspire.Cli.DotNet;
using Aspire.Cli.Tests.Acquisition;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Aspire.Hosting;
using Aspire.TestUtilities;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.DotNet;

[Collection(EnvVarMutatingTestCollection.Name)]
public sealed class ProcessExecutionFactoryEnvironmentTests(ITestOutputHelper outputHelper)
{
    private const string SelectionOrigin = "explicit-launch-configuration";
    private const string ControlEnvVarName = "ASPIRE_TEST_PROCESS_EXECUTION_FACTORY_CONTROL";

    [Fact]
    public async Task CreateExecution_FromStartInfo_PreservesRawArguments()
    {
        var output = new List<string>();
        var startInfo = new ProcessStartInfo("dotnet")
        {
            Arguments = "--version",
            WorkingDirectory = WorkingDirectory.FullName
        };
        await using var execution = CreateFactory().CreateExecution(startInfo, new ProcessInvocationOptions
        {
            StandardOutputCallback = output.Add
        });
        Assert.True(await execution.StartAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, await execution.WaitForExitAsync(TestContext.Current.CancellationToken));
        Assert.Matches(@"^\d+\.\d+\.\d+", Assert.Single(output));
    }

    [Fact]
    public void SetCommand_WindowsBatchShim_RepresentsEmptyArgumentsDirectly()
    {
        var startInfo = new ProcessStartInfo();

        ProcessStartInfoHelper.SetCommand(startInfo, @"C:\tools\npm.cmd", ["before", "", "after"], isWindows: true);

        Assert.Equal(
            "/D /V:OFF /S /C \"\"%ASPIRE_COMMAND_SHIM_PATH%\" \"%ASPIRE_COMMAND_SHIM_ARGUMENT_0%\" \"\" \"%ASPIRE_COMMAND_SHIM_ARGUMENT_2%\"\"",
            startInfo.Arguments);
        Assert.Empty(startInfo.ArgumentList);
    }

    [Fact]
    public void SetCommand_WindowsBatchShim_EncodesQuotesAndTrailingBackslashes()
    {
        var startInfo = new ProcessStartInfo();
        string[] arguments = ["a \"quoted\" value", "backslash\\\"quote", @"C:\tools\trailing\"];

        ProcessStartInfoHelper.SetCommand(startInfo, @"C:\tools\npm.cmd", arguments, isWindows: true);

        Assert.Equal(
            ["a \"\"quoted\"\" value", "backslash\\\\\"\"quote", @"C:\tools\trailing\\"],
            Enumerable.Range(0, arguments.Length).Select(index => startInfo.Environment[$"ASPIRE_COMMAND_SHIM_ARGUMENT_{index}"]));
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task CreateExecution_WindowsBatchShim_PreservesLiteralArgumentText()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows batch commands require cmd.exe.");
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var directory = workspace.WorkspaceRoot.CreateSubdirectory("literal %TEMP%! with spaces");
        await File.WriteAllTextAsync(Path.Combine(directory.FullName, "argv.cjs"),
            "for (const arg of process.argv.slice(2)) console.log(arg);");
        var shim = ProcessTestHelpers.CreateScript(directory, "npm", "exit 0",
            """
            @echo off
            node "%~dp0argv.cjs" %*
            """);
        string[] arguments =
        [
            "literal %PATH%! & value", "", "a^b|c",
            "a \"quoted\" value", "quoted \"& text\" remains literal",
            "backslash\\\"quote", @"C:\tools\trailing\"
        ];
        var startInfo = new ProcessStartInfo { WorkingDirectory = directory.FullName };
        ProcessStartInfoHelper.SetCommand(startInfo, shim, arguments, isWindows: true);
        var output = new List<string>();
        await using var execution = CreateFactory().CreateExecution(startInfo, new ProcessInvocationOptions
        {
            StandardOutputCallback = line =>
            {
                output.Add(line);
                outputHelper.WriteLine(line);
            },
            StandardErrorCallback = outputHelper.WriteLine
        });
        Assert.True(await execution.StartAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, await execution.WaitForExitAsync(TestContext.Current.CancellationToken));
        Assert.Equal(arguments, output);
    }

    [Fact]
    public void InvocationScopedEnvVarNames_ContainsExpectedVariables()
    {
        Assert.Equal(
            [KnownConfigNames.CliAppHostSelectionOrigin],
            ProcessExecutionFactory.InvocationScopedEnvVarNames);
    }

    [Fact]
    public async Task CreateExecution_StripsAppHostSelectionOriginInheritedFromParentEnvironment()
    {
        using var selectionOrigin = new EnvVarOverride(KnownConfigNames.CliAppHostSelectionOrigin, SelectionOrigin);
        using var control = new EnvVarOverride(ControlEnvVarName, "inherited");

        await using var execution = CreateFactory().CreateExecution(
            "dotnet",
            ["build"],
            env: null,
            WorkingDirectory,
            new ProcessInvocationOptions());

        Assert.False(execution.EnvironmentVariables.ContainsKey(KnownConfigNames.CliAppHostSelectionOrigin));
        Assert.Equal("inherited", execution.EnvironmentVariables[ControlEnvVarName]);
    }

    [Fact]
    public async Task CreateExecution_FromStartInfoStripsAppHostSelectionOriginFromAppHostChild()
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = WorkingDirectory.FullName
        };
        startInfo.Environment[KnownConfigNames.CliAppHostSelectionOrigin] = SelectionOrigin;
        startInfo.Environment[ControlEnvVarName] = "inherited";

        await using var execution = CreateFactory().CreateExecution(startInfo, new ProcessInvocationOptions());

        Assert.False(execution.EnvironmentVariables.ContainsKey(KnownConfigNames.CliAppHostSelectionOrigin));
        Assert.Equal("inherited", execution.EnvironmentVariables[ControlEnvVarName]);
    }

    [Fact]
    public async Task CreateExecution_PreservesAppHostSelectionOriginForwardedToDetachedChildCli()
    {
        using var selectionOrigin = new EnvVarOverride(KnownConfigNames.CliAppHostSelectionOrigin, SelectionOrigin);

        await using var execution = CreateFactory().CreateExecution(
            "aspire",
            ["run"],
            AppHostLauncher.CreateDetachedChildEnvironment(activity: null, appHostSelectionOrigin: SelectionOrigin),
            WorkingDirectory,
            new ProcessInvocationOptions
            {
                Detached = true,
                IsolateConsole = true,
                EnvironmentVariableFilter = AppHostLauncher.IsExtensionEnvironmentVariable
            });

        Assert.Equal(SelectionOrigin, execution.EnvironmentVariables[KnownConfigNames.CliAppHostSelectionOrigin]);
        Assert.Equal("true", execution.EnvironmentVariables[KnownConfigNames.CliRunDetached]);
    }

    private static DirectoryInfo WorkingDirectory => new(AppContext.BaseDirectory);

    private static ProcessExecutionFactory CreateFactory()
        => new(new TestEnvironment(), NullLogger<ProcessExecutionFactory>.Instance);
}
