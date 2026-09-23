// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using Aspire.Hosting.Dcp;
using Aspire.Hosting.Dcp.Model;
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
            follow: false);
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
}
