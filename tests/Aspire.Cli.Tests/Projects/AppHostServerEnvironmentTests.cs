// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Cli.DotNet;
using Aspire.Cli.Layout;
using Aspire.Cli.NuGet;
using Aspire.Cli.Projects;
using Aspire.Cli.Tests.Mcp;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Aspire.Cli.Utils;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.Projects;

public class AppHostServerEnvironmentTests(ITestOutputHelper outputHelper)
{
    [Theory]
    [InlineData("Aspire__LaunchGroup")]
    [InlineData("ASPIRE__LAUNCHGROUP")]
    [InlineData("aspire__launchgroup")]
    [InlineData("Aspire:LaunchGroup")]
    [InlineData("ASPIRE:LAUNCHGROUP")]
    [InlineData("aspire:launchgroup")]
    public void Apply_RemovesInheritedLaunchGroupSpellings(string inheritedName)
    {
        // Model Unix's case-sensitive environment even when this test runs on Windows.
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [inheritedName] = "basket",
            ["Aspire__LaunchGroup"] = "another-group",
            ["UNRELATED"] = "upper",
            ["unrelated"] = "lower",
            ["Aspire__LaunchGroupOther"] = "preserved",
            ["Aspire:LaunchGroupOther"] = "also-preserved"
        };

        AppHostServerEnvironment.Apply(environment, new Dictionary<string, string>
        {
            ["Aspire__LaunchGroup"] = "catalog"
        });

        Assert.Equal(
            new Dictionary<string, string?>
            {
                ["UNRELATED"] = "upper",
                ["unrelated"] = "lower",
                ["Aspire__LaunchGroupOther"] = "preserved",
                ["Aspire:LaunchGroupOther"] = "also-preserved",
                ["Aspire__LaunchGroup"] = "catalog"
            },
            environment);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Apply_WithoutLaunchGroupOverride_PreservesInheritedSelection(bool hasOtherOverrides)
    {
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["ASPIRE__LAUNCHGROUP"] = "basket"
        };

        AppHostServerEnvironment.Apply(environment, hasOtherOverrides
            ? new Dictionary<string, string> { ["OTHER"] = "value" }
            : null);

        var expected = new Dictionary<string, string?> { ["ASPIRE__LAUNCHGROUP"] = "basket" };
        if (hasOtherOverrides)
        {
            expected["OTHER"] = "value";
        }
        Assert.Equal(expected, environment);
    }

    [Theory]
    [InlineData("Aspire__LaunchGroup", "ASPIRE__LAUNCHGROUP")]
    [InlineData("ASPIRE__LAUNCHGROUP", "Aspire__LaunchGroup")]
    [InlineData("Aspire__LaunchGroup", "ASPIRE:LAUNCHGROUP")]
    [InlineData("Aspire:LaunchGroup", "ASPIRE__LAUNCHGROUP")]
    [InlineData("ASPIRE:LAUNCHGROUP", "Aspire:LaunchGroup")]
    public void Apply_ProfileSelectionOverridesInheritedSelection(string profileName, string inheritedName)
    {
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [inheritedName] = "inherited"
        };

        AppHostServerEnvironment.Apply(environment, new Dictionary<string, string> { [profileName] = "basket" });

        Assert.Equal(new KeyValuePair<string, string?>(profileName, "basket"), Assert.Single(environment));
    }

    [Theory]
    [InlineData("development", false, "ASPIRE__LAUNCHGROUP")]
    [InlineData("development", true, "ASPIRE__LAUNCHGROUP")]
    [InlineData("prebuilt", false, "ASPIRE__LAUNCHGROUP")]
    [InlineData("prebuilt", true, "ASPIRE__LAUNCHGROUP")]
    [InlineData("native", false, "ASPIRE__LAUNCHGROUP")]
    [InlineData("native", true, "ASPIRE__LAUNCHGROUP")]
    [InlineData("development", false, "ASPIRE:LAUNCHGROUP")]
    [InlineData("development", true, "ASPIRE:LAUNCHGROUP")]
    [InlineData("prebuilt", false, "ASPIRE:LAUNCHGROUP")]
    [InlineData("prebuilt", true, "ASPIRE:LAUNCHGROUP")]
    [InlineData("native", false, "ASPIRE:LAUNCHGROUP")]
    [InlineData("native", true, "ASPIRE:LAUNCHGROUP")]
    public async Task ServerSession_ChildProcessReceivesOnlyOneLaunchGroupSelection(string serverKind, bool isolateConsole, string inheritedName)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var layout = new LayoutConfiguration
        {
            LayoutPath = workspace.Path,
            Components = new LayoutComponents { Managed = "." }
        };
        var executable = layout.GetManagedPath()!;
        await File.WriteAllTextAsync(executable, string.Empty);
        var environment = new TestEnvironment();
        var realProcesses = new ProcessExecutionFactory(environment, NullLogger<ProcessExecutionFactory>.Instance);
        var processes = new TestProcessExecutionFactory
        {
            CreateExecutionFromStartInfoCallback = (startInfo, options) =>
            {
                // Keep the production server's complete child environment and lifetime options;
                // only substitute a short-lived executable that reports what the child received.
                startInfo.Arguments = string.Empty;
                startInfo.ArgumentList.Clear();
                if (OperatingSystem.IsWindows())
                {
                    startInfo.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
                    foreach (var argument in new[] { "/D", "/C", "set Aspire__LaunchGroup & set Aspire:LaunchGroup 2>nul & exit /b 0" })
                    {
                        startInfo.ArgumentList.Add(argument);
                    }
                }
                else
                {
                    startInfo.FileName = "/bin/sh";
                    startInfo.ArgumentList.Add("-c");
                    startInfo.ArgumentList.Add("env | grep -Ei '^Aspire(__|:)LaunchGroup='");
                }

                return realProcesses.CreateExecution(startInfo, options);
            }
        };
        var runner = new TestDotNetCliRunner();
        var packaging = MockPackagingServiceFactory.Create();
        IAppHostServerProject project = serverKind switch
        {
            "development" => new DotNetBasedAppHostServerProject(
                workspace.Path, "test.sock", workspace.Path, runner, packaging, processes, environment,
                NullLogger<DotNetBasedAppHostServerProject>.Instance),
            "prebuilt" => new PrebuiltAppHostServer(
                workspace.Path, "test.sock", layout,
                new BundleNuGetService(NullLogger<BundleNuGetService>.Instance, new FakeNuGetClient(), new TestConfigurationService()),
                runner, new TestDotNetSdkInstaller(), packaging, TestExecutionContextFactory.CreateTestContext(),
                processes, environment, NullLogger.Instance),
            "native" => new NativeAppHostServerProject(workspace.Path, executable, processes, NullLogger.Instance),
            _ => throw new UnreachableException()
        };
        await using var session = new AppHostServerSession(
            project,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [inheritedName] = "basket",
                ["Aspire__LaunchGroup"] = "catalog"
            },
            debug: false, environment, NullLogger.Instance, profilingTelemetry: null,
            gracefulShutdownSignaler: null, shutdownService: null, isolateConsole,
            ownsProject: true, TestContext.Current.CancellationToken);

        await session.StartAsync();
        Assert.Equal(0, await session.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.NotNull(session.Output);
        var launchGroupLines = session.Output.GetLines()
            .Where(line => line.Line.StartsWith("Aspire__LaunchGroup=", StringComparison.OrdinalIgnoreCase)
                || line.Line.StartsWith("Aspire:LaunchGroup=", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(
            [(OutputLineStream.StdOut, "Aspire__LaunchGroup=catalog")],
            launchGroupLines);
    }
}
