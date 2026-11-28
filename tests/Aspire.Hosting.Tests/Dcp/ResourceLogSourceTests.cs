// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Text;
using Aspire.Hosting.Dcp;
using Aspire.Hosting.Dcp.Model;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Hosting.Tests.Dcp;

[Trait("Partition", "4")]
public sealed class ResourceLogSourceTests
{
    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData(ExecutionType.Process, true)]
    [InlineData(ExecutionType.IDE, false)]
    public async Task WorkingDirectoryIsIncludedOnlyForProcessExecution(string? executionType, bool expectWorkingDirectory)
    {
        const string WorkingDirectory = "/app";
        const string Error = "all available Executable runners have been tried and failed";

        var executable = Executable.Create("app", "dotnet");
        executable.Spec.ExecutionType = executionType;
        executable.Spec.WorkingDirectory = WorkingDirectory;

        var kubernetesService = new TestKubernetesService(startStream: (_, logStreamType) =>
        {
            if (logStreamType != Logs.StreamTypeSystem)
            {
                return new MemoryStream();
            }

            var systemLog =
                $"2024-08-19T06:10:01.000Z\terror\tdcp.ExecutableReconciler\tThe Executable failed to start\t{{\"error\":\"{Error}\"}}" +
                Environment.NewLine;
            return new MemoryStream(Encoding.UTF8.GetBytes(systemLog));
        });
        var logSource = new ResourceLogSource<Executable>(
            NullLogger.Instance,
            kubernetesService,
            executable,
            follow: false,
            skipLineCounts: null);
        var entries = new List<ResourceLogEntry>();

        await foreach (var batch in logSource)
        {
            entries.AddRange(batch);
        }

        var entry = Assert.Single(entries);
        var expected = expectWorkingDirectory
            ? $"[sys] The Executable failed to start: WorkingDirectory = {WorkingDirectory}, Error = {Error}"
            : $"[sys] The Executable failed to start: Error = {Error}";
        Assert.Equal(expected, entry.Content);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TerminalExecutableRequestsOnlyAvailableLogStreams(bool follow)
    {
        var executable = Executable.Create("terminal-executable", "command");
        executable.Spec.Terminal = new TerminalSpec();

        var requestedStreams = new ConcurrentQueue<string>();
        var kubernetesService = new TestKubernetesService(startStreamWithFollow: (_, streamType, actualFollow) =>
        {
            Assert.Equal(follow, actualFollow);
            requestedStreams.Enqueue(streamType);

            var content = streamType switch
            {
                Logs.StreamTypeStartupStdErr => "startup stderr",
                Logs.StreamTypeStartupStdOut => "startup stdout",
                Logs.StreamTypeSystem => "system",
                _ => streamType
            };

            return new MemoryStream(Encoding.UTF8.GetBytes(content));
        });
        var logSource = new ResourceLogSource<Executable>(
            NullLogger.Instance,
            kubernetesService,
            executable,
            follow,
            skipLineCounts: null);
        using var cancellationTokenSource = AsyncTestHelpers.CreateDefaultTimeoutTokenSource();
        var entries = new List<ResourceLogEntry>();

        await foreach (var batch in logSource.WithCancellation(cancellationTokenSource.Token))
        {
            entries.AddRange(batch);
        }

        Assert.Equal(
            [
                Logs.StreamTypeStartupStdErr,
                Logs.StreamTypeStartupStdOut,
                Logs.StreamTypeSystem
            ],
            requestedStreams);
        Assert.Equal(
            ["startup stderr", "startup stdout", "system"],
            entries.Select(entry => entry.Content).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task SkipLineCountsArePassedPerStreamAndEntriesAreTaggedWithTheirStream()
    {
        var executable = Executable.Create("app", "command");

        var requestedSkips = new ConcurrentDictionary<string, long?>();
        var kubernetesService = new TestKubernetesService(startStreamWithSkip: (_, streamType, _, skip) =>
        {
            requestedSkips[streamType] = skip;
            return new MemoryStream(Encoding.UTF8.GetBytes(streamType + Environment.NewLine));
        });
        var logSource = new ResourceLogSource<Executable>(
            NullLogger.Instance,
            kubernetesService,
            executable,
            follow: false,
            skipLineCounts: new Dictionary<string, long>
            {
                [Logs.StreamTypeStdOut] = 3,
                [Logs.StreamTypeStdErr] = 0
            });
        var entries = new List<ResourceLogEntry>();

        await foreach (var batch in logSource)
        {
            entries.AddRange(batch);
        }

        // DCP rejects skip values below 1, so streams with nothing to skip omit the option.
        Assert.Equal(
            [
                new(Logs.StreamTypeStartupStdErr, null),
                new(Logs.StreamTypeStartupStdOut, null),
                new(Logs.StreamTypeStdErr, null),
                new(Logs.StreamTypeStdOut, 3),
                new(Logs.StreamTypeSystem, null)
            ],
            requestedSkips.OrderBy(pair => pair.Key, StringComparer.Ordinal));
        Assert.All(entries, entry => Assert.Equal(entry.StreamType, entry.Content));
    }
}
