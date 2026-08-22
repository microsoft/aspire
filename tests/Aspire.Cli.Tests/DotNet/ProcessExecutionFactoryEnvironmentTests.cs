// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Cli.Commands;
using Aspire.Cli.DotNet;
using Aspire.Cli.Tests.Acquisition;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Aspire.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.DotNet;

[Collection(EnvVarMutatingTestCollection.Name)]
public sealed class ProcessExecutionFactoryEnvironmentTests
{
    private const string SelectionOrigin = "explicit-launch-configuration";
    private const string ControlEnvVarName = "ASPIRE_TEST_PROCESS_EXECUTION_FACTORY_CONTROL";

    [Fact]
    public void InvocationScopedEnvVarNames_ContainsAppHostSelectionOrigin()
    {
        Assert.Equal(
            new[] { KnownConfigNames.CliAppHostSelectionOrigin },
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
