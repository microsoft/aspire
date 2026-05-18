// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Commands;
using Aspire.Cli.Profiling;
using Aspire.Cli.Tests.Utils;
using Aspire.Cli.Telemetry;
using Aspire.Hosting;
using Aspire.Shared;
using Microsoft.DotNet.RemoteExecutor;

namespace Aspire.Cli.Tests;

public class ProfileCaptureOptionsTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public void TryCreate_ReturnsNull_WhenCaptureProfileIsNotSpecified()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var options = ProfileCaptureOptions.TryCreate(["run"], TimeProvider.System, workspace.WorkspaceRoot, CreatePortProvider());

        Assert.Null(options);
    }

    [Fact]
    public void TryCreate_IgnoresCaptureProfileAfterDelimiter()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var options = ProfileCaptureOptions.TryCreate(["run", "--", "--capture-profile"], TimeProvider.System, workspace.WorkspaceRoot, CreatePortProvider());

        Assert.Null(options);
    }

    [Fact]
    public void TryCreate_UsesExplicitOutputDelayAndAllocatedPorts()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var outputPath = Path.Combine(workspace.WorkspaceRoot.FullName, "profiles", "startup.zip");

        var options = ProfileCaptureOptions.TryCreate(
            ["run", "--capture-profile", "--capture-profile-output", outputPath, "--capture-profile-delay=7"],
            TimeProvider.System,
            workspace.WorkspaceRoot,
            CreatePortProvider(5000, 5001, 5002));

        Assert.NotNull(options);
        Assert.Equal(outputPath, options.OutputPath);
        Assert.Equal("http://127.0.0.1:5000", options.DashboardUrl);
        Assert.Equal("http://127.0.0.1:5001", options.OtlpGrpcUrl);
        Assert.Equal("http://127.0.0.1:5002", options.OtlpHttpUrl);
        Assert.Equal(TimeSpan.FromSeconds(7), options.StartupDelay);
        Assert.Equal(32, options.SessionId.Length);
    }

    [Fact]
    public void TryCreate_DefaultOutputPathIsUnderWorkingDirectory()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var options = ProfileCaptureOptions.TryCreate(["--capture-profile", "ls"], TimeProvider.System, workspace.WorkspaceRoot, CreatePortProvider());

        Assert.NotNull(options);
        Assert.StartsWith(workspace.WorkspaceRoot.FullName, options.OutputPath, StringComparison.Ordinal);
        Assert.EndsWith(".zip", options.OutputPath, StringComparison.Ordinal);
        Assert.Contains("aspire-profile-", Path.GetFileName(options.OutputPath), StringComparison.Ordinal);
        Assert.Equal(TimeSpan.FromSeconds(RootCommand.DefaultCaptureProfileDelaySeconds), options.StartupDelay);
    }

    [Fact]
    public void TryCreate_UsesDefaultOutputFileNameWhenOutputIsExistingDirectory()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var outputDirectory = Directory.CreateDirectory(Path.Combine(workspace.WorkspaceRoot.FullName, "profiles"));

        var options = ProfileCaptureOptions.TryCreate(
            ["--capture-profile", "--capture-profile-output", outputDirectory.FullName],
            TimeProvider.System,
            workspace.WorkspaceRoot,
            CreatePortProvider());

        Assert.NotNull(options);
        Assert.Equal(outputDirectory.FullName, Path.GetDirectoryName(options.OutputPath));
        Assert.EndsWith(".zip", options.OutputPath, StringComparison.Ordinal);
        Assert.Contains("aspire-profile-", Path.GetFileName(options.OutputPath), StringComparison.Ordinal);
    }

    [Fact]
    public void TryCreate_UsesDefaultOutputFileNameWhenOutputEndsInDirectorySeparator()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var outputDirectory = Path.Combine(workspace.WorkspaceRoot.FullName, "profiles") + Path.DirectorySeparatorChar;

        var options = ProfileCaptureOptions.TryCreate(
            ["--capture-profile", "--capture-profile-output", outputDirectory],
            TimeProvider.System,
            workspace.WorkspaceRoot,
            CreatePortProvider());

        Assert.NotNull(options);
        Assert.Equal(Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputDirectory)), Path.GetDirectoryName(options.OutputPath));
        Assert.EndsWith(".zip", options.OutputPath, StringComparison.Ordinal);
        Assert.Contains("aspire-profile-", Path.GetFileName(options.OutputPath), StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveRepoLocalManagedPath_ReturnsNullWithoutRepoRoot()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var managedBaseDirectory = Directory.CreateDirectory(Path.Combine(
            workspace.WorkspaceRoot.FullName,
            "artifacts",
            "bin",
            "Aspire.Managed",
            "Debug",
            "net10.0"));
        var expectedManagedPath = Path.Combine(
            managedBaseDirectory.FullName,
            BundleDiscovery.GetExecutableFileName(BundleDiscovery.ManagedExecutableName));
        File.WriteAllText(expectedManagedPath, string.Empty);

        var resolvedPath = ProfileCaptureService.ResolveRepoLocalManagedPath(repoRoot: null);

        Assert.Null(resolvedPath);
    }

    [Fact]
    public void ResolveRepoLocalManagedPath_UsesOptInRepoRoot()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var managedBaseDirectory = Directory.CreateDirectory(Path.Combine(
            workspace.WorkspaceRoot.FullName,
            "artifacts",
            "bin",
            "Aspire.Managed",
            "Debug",
            "net10.0"));
        var expectedManagedPath = Path.Combine(
            managedBaseDirectory.FullName,
            BundleDiscovery.GetExecutableFileName(BundleDiscovery.ManagedExecutableName));
        File.WriteAllText(expectedManagedPath, string.Empty);

        var resolvedPath = ProfileCaptureService.ResolveRepoLocalManagedPath(workspace.WorkspaceRoot.FullName);

        Assert.Equal(expectedManagedPath, resolvedPath);
    }

    [Fact]
    public void Apply_ConfiguresAndRestoresProfilingEnvironment()
    {
        using var result = RemoteExecutor.Invoke(static () =>
        {
            var options = new ProfileCaptureOptions(
                OutputPath: "profile.zip",
                DashboardUrl: "http://127.0.0.1:5000",
                OtlpGrpcUrl: "http://127.0.0.1:5001",
                OtlpHttpUrl: "http://127.0.0.1:5002",
                SessionId: "test-session",
                StartupDelay: TimeSpan.FromSeconds(3));

            Environment.SetEnvironmentVariable(KnownConfigNames.ProfilingEnabled, "previous");

            using (ProfileCaptureEnvironment.Apply(options))
            {
                Assert.Equal("true", Environment.GetEnvironmentVariable(AspireCliTelemetry.TelemetryOptOutConfigKey));
                Assert.Equal("true", Environment.GetEnvironmentVariable(KnownConfigNames.ProfilingEnabled));
                Assert.Equal("true", Environment.GetEnvironmentVariable(KnownConfigNames.Legacy.StartupProfilingEnabled));
                Assert.Equal("test-session", Environment.GetEnvironmentVariable(KnownConfigNames.ProfilingSessionId));
                Assert.Equal("test-session", Environment.GetEnvironmentVariable(KnownConfigNames.Legacy.StartupOperationId));
                Assert.Equal("http://127.0.0.1:5001", Environment.GetEnvironmentVariable(KnownOtelConfigNames.ExporterOtlpEndpoint));
                Assert.Equal("grpc", Environment.GetEnvironmentVariable(KnownOtelConfigNames.ExporterOtlpProtocol));
                Assert.Equal("1000", Environment.GetEnvironmentVariable(KnownOtelConfigNames.BspScheduleDelay));
            }

            Assert.Equal("previous", Environment.GetEnvironmentVariable(KnownConfigNames.ProfilingEnabled));
        });
    }

    [Fact]
    public void AddCurrentToEnvironment_CopiesAppliedProfilingEnvironment()
    {
        using var result = RemoteExecutor.Invoke(static () =>
        {
            var options = new ProfileCaptureOptions(
                OutputPath: "profile.zip",
                DashboardUrl: "http://127.0.0.1:5000",
                OtlpGrpcUrl: "http://127.0.0.1:5001",
                OtlpHttpUrl: "http://127.0.0.1:5002",
                SessionId: "test-session",
                StartupDelay: TimeSpan.FromSeconds(3));

            using (ProfileCaptureEnvironment.Apply(options))
            {
                var environmentVariables = new Dictionary<string, string>();

                ProfileCaptureEnvironment.AddCurrentToEnvironment(environmentVariables);

                Assert.Equal("true", environmentVariables[AspireCliTelemetry.TelemetryOptOutConfigKey]);
                Assert.Equal("true", environmentVariables[KnownConfigNames.ProfilingEnabled]);
                Assert.Equal("true", environmentVariables[KnownConfigNames.Legacy.StartupProfilingEnabled]);
                Assert.Equal("test-session", environmentVariables[KnownConfigNames.ProfilingSessionId]);
                Assert.Equal("test-session", environmentVariables[KnownConfigNames.Legacy.StartupOperationId]);
                Assert.Equal("http://127.0.0.1:5001", environmentVariables[KnownOtelConfigNames.ExporterOtlpEndpoint]);
                Assert.Equal("grpc", environmentVariables[KnownOtelConfigNames.ExporterOtlpProtocol]);
                Assert.Equal("1000", environmentVariables[KnownOtelConfigNames.BspScheduleDelay]);
            }
        });
    }

    private static Func<int> CreatePortProvider(params int[] ports)
    {
        var queue = new Queue<int>(ports.Length == 0 ? [5000, 5001, 5002] : ports);
        return queue.Dequeue;
    }
}
