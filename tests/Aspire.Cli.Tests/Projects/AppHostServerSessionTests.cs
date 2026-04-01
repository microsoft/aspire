// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Cli.Configuration;
using Aspire.Cli.Projects;
using Aspire.Cli.Utils;
using Aspire.Hosting;
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

    private sealed class RecordingAppHostServerProject : IAppHostServerProject
    {
        public string AppDirectoryPath => Directory.GetCurrentDirectory();

        public Dictionary<string, string>? ReceivedEnvironmentVariables { get; private set; }

        public string GetInstanceIdentifier() => AppDirectoryPath;

        public Task<AppHostServerPrepareResult> PrepareAsync(
            string sdkVersion,
            IEnumerable<IntegrationReference> integrations,
            CancellationToken cancellationToken = default) =>
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
