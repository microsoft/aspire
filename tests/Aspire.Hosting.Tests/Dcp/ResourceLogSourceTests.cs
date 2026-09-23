// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Text;
using Aspire.Hosting.Dcp;
using Aspire.Hosting.Dcp.Model;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Hosting.Tests.Dcp;

public class ResourceLogSourceTests
{
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
            follow);
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
}
