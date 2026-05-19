// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Cli.Configuration;
using Aspire.Cli.Projects;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Utils;
using Aspire.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.Projects;

public class AppHostServerSessionTests
{
    [Fact]
    public async Task Start_DoesNotMutateCallerEnvironmentVariables()
    {
        // Arrange
        var project = new RecordingAppHostServerProject();
        var environmentVariables = new Dictionary<string, string>
        {
            ["EXISTING_VALUE"] = "present"
        };

        // Act
        await using var session = AppHostServerSession.Start(
            project,
            environmentVariables,
            debug: false,
            NullLogger<AppHostServerSession>.Instance);

        // Assert
        Assert.Equal("present", environmentVariables["EXISTING_VALUE"]);
        Assert.False(environmentVariables.ContainsKey(KnownConfigNames.RemoteAppHostToken));

        Assert.NotNull(project.ReceivedEnvironmentVariables);
        Assert.Equal("present", project.ReceivedEnvironmentVariables["EXISTING_VALUE"]);
        Assert.Equal(session.AuthenticationToken, project.ReceivedEnvironmentVariables[KnownConfigNames.RemoteAppHostToken]);
    }

    [Fact]
    public async Task Start_PropagatesProfilingContextToServerEnvironment()
    {
        var project = new RecordingAppHostServerProject();
        var environmentVariables = new Dictionary<string, string>
        {
            ["EXISTING_VALUE"] = "present"
        };
        using var listener = CreateActivityListener(ProfilingTelemetry.ActivitySourceName);
        using var profilingTelemetry = new ProfilingTelemetry(CreateConfiguration(
            (ProfilingTelemetry.EnvironmentVariables.Enabled, "true"),
            (ProfilingTelemetry.EnvironmentVariables.SessionId, "session-1")));

        await using var session = AppHostServerSession.Start(
            project,
            environmentVariables,
            debug: false,
            NullLogger<AppHostServerSession>.Instance,
            profilingTelemetry);

        Assert.Equal("present", environmentVariables["EXISTING_VALUE"]);
        Assert.False(environmentVariables.ContainsKey(KnownConfigNames.RemoteAppHostToken));
        Assert.False(environmentVariables.ContainsKey(ProfilingTelemetry.EnvironmentVariables.Enabled));

        var receivedEnvironmentVariables = Assert.IsType<Dictionary<string, string>>(project.ReceivedEnvironmentVariables);
        Assert.Equal("present", receivedEnvironmentVariables["EXISTING_VALUE"]);
        Assert.Equal(session.AuthenticationToken, receivedEnvironmentVariables[KnownConfigNames.RemoteAppHostToken]);
        Assert.Equal("true", receivedEnvironmentVariables[ProfilingTelemetry.EnvironmentVariables.Enabled]);
        Assert.Equal("true", receivedEnvironmentVariables[KnownConfigNames.Legacy.StartupProfilingEnabled]);
        Assert.Equal("session-1", receivedEnvironmentVariables[ProfilingTelemetry.EnvironmentVariables.SessionId]);
        Assert.Equal("session-1", receivedEnvironmentVariables[KnownConfigNames.Legacy.StartupOperationId]);
        Assert.StartsWith("00-", receivedEnvironmentVariables[ProfilingTelemetry.EnvironmentVariables.TraceParent], StringComparison.Ordinal);
        Assert.Equal(
            receivedEnvironmentVariables[ProfilingTelemetry.EnvironmentVariables.TraceParent],
            receivedEnvironmentVariables[KnownConfigNames.Legacy.StartupTraceParent]);
    }

    [Fact]
    public async Task Start_DoesNotLeaveServerProcessActivityAmbient()
    {
        var project = new RecordingAppHostServerProject();
        using var parentSource = new ActivitySource("test-apphost-server-parent");
        using var parentListener = CreateActivityListener("test-apphost-server-parent");
        using var listener = CreateActivityListener(ProfilingTelemetry.ActivitySourceName);
        using var profilingTelemetry = new ProfilingTelemetry(CreateConfiguration(
            (ProfilingTelemetry.EnvironmentVariables.Enabled, "true"),
            (ProfilingTelemetry.EnvironmentVariables.SessionId, "session-1")));

        using var parentActivity = parentSource.StartActivity("aspire/cli/run");
        Assert.NotNull(parentActivity);

        await using var session = AppHostServerSession.Start(
            project,
            environmentVariables: null,
            debug: false,
            NullLogger<AppHostServerSession>.Instance,
            profilingTelemetry);

        Assert.Same(parentActivity, Activity.Current);

        var receivedEnvironmentVariables = Assert.IsType<Dictionary<string, string>>(project.ReceivedEnvironmentVariables);
        Assert.NotEqual(parentActivity.Id, receivedEnvironmentVariables[ProfilingTelemetry.EnvironmentVariables.TraceParent]);
    }

    private static ActivityListener CreateActivityListener(string sourceName)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == sourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    private static IConfiguration CreateConfiguration(params (string Key, string? Value)[] values)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(value => new KeyValuePair<string, string?>(value.Key, value.Value)))
            .Build();
    }

    private sealed class RecordingAppHostServerProject : IAppHostServerProject
    {
        public string AppDirectoryPath => Directory.GetCurrentDirectory();

        public Dictionary<string, string>? ReceivedEnvironmentVariables { get; private set; }

        public string GetInstanceIdentifier() => AppDirectoryPath;

        public Task<AppHostServerPrepareResult> PrepareAsync(
            string sdkVersion,
            IEnumerable<IntegrationReference> integrations,
            CancellationToken cancellationToken = default,
            string? requestedChannel = null) =>
            throw new NotSupportedException();

        public (string SocketPath, Process Process, OutputCollector OutputCollector) Run(
            int hostPid,
            IReadOnlyDictionary<string, string>? environmentVariables = null,
            string[]? additionalArgs = null,
            bool debug = false)
        {
            ReceivedEnvironmentVariables = environmentVariables is null
                ? null
                : new Dictionary<string, string>(environmentVariables);

            var process = Process.Start(new ProcessStartInfo("dotnet", "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            })!;

            return ("test.sock", process, new OutputCollector());
        }
    }
}
