// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Globalization;
using Aspire.Cli.Bundles;
using Aspire.Cli.Layout;
using Aspire.Cli.Processes;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Aspire.Shared;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.Processes;

public class ProcessShutdownServiceTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task TryStopProcessTreeWithDcpAsync_UsesDcpStopProcessTreeArguments()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var dcpDirectory = workspace.WorkspaceRoot.CreateSubdirectory("dcp");
        File.WriteAllText(BundleDiscovery.GetDcpExecutablePath(dcpDirectory.FullName), string.Empty);

        string[]? capturedArguments = null;
        DirectoryInfo? capturedWorkingDirectory = null;
        var executionFactory = new TestProcessExecutionFactory
        {
            AssertionCallback = (arguments, _, workingDirectory, _) =>
            {
                capturedArguments = arguments;
                capturedWorkingDirectory = workingDirectory;
            }
        };
        var startTime = new DateTimeOffset(Process.GetCurrentProcess().StartTime);
        var signaler = CreateService(workspace, dcpDirectory.FullName, executionFactory);

        var result = await signaler.TryStopProcessTreeWithDcpAsync(Environment.ProcessId, startTime, includeStartTime: true, CancellationToken.None);

        Assert.True(result);
        Assert.Equal(1, executionFactory.AttemptCount);
        Assert.NotNull(capturedArguments);
        Assert.NotNull(capturedWorkingDirectory);
        Assert.Equal(workspace.WorkspaceRoot.FullName, capturedWorkingDirectory.FullName);
        Assert.Equal([
            "stop-process-tree",
            "--skip-descendants",
            "--pid",
            Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
            "--process-start-time",
            ProcessShutdownService.FormatDcpProcessStartTime(startTime)
        ], capturedArguments);
    }

    [Fact]
    public async Task TryStopProcessTreeWithDcpAsync_OmitsStartTimeWhenRequested()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var dcpDirectory = workspace.WorkspaceRoot.CreateSubdirectory("dcp");
        File.WriteAllText(BundleDiscovery.GetDcpExecutablePath(dcpDirectory.FullName), string.Empty);

        string[]? capturedArguments = null;
        var executionFactory = new TestProcessExecutionFactory
        {
            AssertionCallback = (arguments, _, _, _) => capturedArguments = arguments
        };
        var signaler = CreateService(workspace, dcpDirectory.FullName, executionFactory);

        var result = await signaler.TryStopProcessTreeWithDcpAsync(
            Environment.ProcessId,
            new DateTimeOffset(Process.GetCurrentProcess().StartTime),
            includeStartTime: false,
            CancellationToken.None);

        Assert.True(result);
        Assert.NotNull(capturedArguments);
        Assert.DoesNotContain("--process-start-time", capturedArguments);
    }

    [Fact]
    public async Task TryStopProcessTreeWithDcpAsync_UsesLeasedBundleDcpPathWhenAvailable()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var mutableDcpDirectory = workspace.WorkspaceRoot.CreateSubdirectory("bundle").CreateSubdirectory("dcp");
        File.WriteAllText(BundleDiscovery.GetDcpExecutablePath(mutableDcpDirectory.FullName), string.Empty);

        var versionRoot = workspace.WorkspaceRoot.CreateSubdirectory("versions").CreateSubdirectory("active-version");
        var leasedDcpDirectory = versionRoot.CreateSubdirectory("dcp");
        var leasedDcpPath = BundleDiscovery.GetDcpExecutablePath(leasedDcpDirectory.FullName);
        File.WriteAllText(leasedDcpPath, string.Empty);

        var executionFactory = new TestProcessExecutionFactory();
        var bundleService = new TestBundleService(isBundle: true)
        {
            Layout = new LayoutConfiguration { LayoutPath = versionRoot.FullName }
        };
        var signaler = CreateService(workspace, mutableDcpDirectory.FullName, executionFactory, bundleService);

        var result = await signaler.TryStopProcessTreeWithDcpAsync(Environment.ProcessId, new DateTimeOffset(Process.GetCurrentProcess().StartTime), includeStartTime: false, CancellationToken.None);

        Assert.True(result);
        Assert.Equal(leasedDcpPath, executionFactory.LastFileName);
    }

    private static ProcessShutdownService CreateService(
        TemporaryWorkspace workspace,
        string dcpDirectory,
        TestProcessExecutionFactory executionFactory,
        IBundleService? bundleService = null)
    {
        var executionContext = new CliExecutionContext(
            workspace.WorkspaceRoot,
            workspace.WorkspaceRoot.CreateSubdirectory("hives"),
            workspace.WorkspaceRoot.CreateSubdirectory("cache"),
            workspace.WorkspaceRoot.CreateSubdirectory("sdks"),
            workspace.WorkspaceRoot.CreateSubdirectory("logs"),
            Path.Combine(workspace.WorkspaceRoot.FullName, "test.log"));

        return new ProcessShutdownService(
            new FixedLayoutDiscovery(dcpDirectory),
            bundleService ?? new NullBundleService(),
            new LayoutProcessRunner(executionFactory),
            executionContext,
            NullLogger<ProcessShutdownService>.Instance,
            TimeProvider.System);
    }

    private sealed class FixedLayoutDiscovery(string dcpDirectory) : ILayoutDiscovery
    {
        public LayoutConfiguration? DiscoverLayout(string? projectDirectory = null) => null;

        public string? GetComponentPath(LayoutComponent component, string? projectDirectory = null)
        {
            return component == LayoutComponent.Dcp ? dcpDirectory : null;
        }

        public bool IsBundleModeAvailable(string? projectDirectory = null) => true;
    }
}
