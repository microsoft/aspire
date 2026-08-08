// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Cli.Commands;
using Aspire.Cli.DotNet;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Aspire.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.DotNet;

/// <summary>
/// Covers which environment variables the factory lets a child process inherit. The invocation-scoped
/// marker (<see cref="KnownConfigNames.CliAppHostSelectionOrigin"/>) is the interesting case: it must
/// reach a detached child CLI, which continues the same logical invocation, but must never reach the
/// AppHost/build tree, where a nested <c>aspire</c> invocation would misread it as its own selection
/// origin and skip recording its workspace default.
/// </summary>
public sealed class ProcessExecutionFactoryEnvironmentTests
{
    private const string SelectionOrigin = "explicit-launch-configuration";

    // Unrelated to the strip-lists, so it proves the child still inherits the parent block and that the
    // assertions below are not passing because the environment came back empty.
    private const string ControlEnvVarName = "ASPIRE_TEST_PROCESS_EXECUTION_FACTORY_CONTROL";

    [Fact]
    public void InvocationScopedEnvVarNames_ContainsAppHostSelectionOrigin()
    {
        // Pinning the set guards against an unbalanced add: a new invocation-scoped marker that is read
        // by the CLI but never added here would silently leak into every AppHost/build child.
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
    public async Task CreateExecution_FromStartInfo_StripsAppHostSelectionOriginFromAppHostChild()
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
        // Mirrors AppHostLauncher's detached spawn: the same options and the same env dictionary, so the
        // test breaks if either the launcher stops forwarding the marker or the factory starts stripping
        // caller-supplied env instead of only the inherited block.
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

    // The executions below are never started, so the directory only has to exist.
    private static DirectoryInfo WorkingDirectory => new(AppContext.BaseDirectory);

    private static ProcessExecutionFactory CreateFactory()
        => new(new TestEnvironment(), NullLogger<ProcessExecutionFactory>.Instance);
}
