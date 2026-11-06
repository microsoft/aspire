// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Microsoft.DotNet.XUnitExtensions;
using Xunit;

namespace Aspire.Acquisition.Tests.Scripts;

[SkipOnPlatform(TestPlatforms.Windows, "Exec of a freshly written script (ETXTBSY) is a Unix-only failure mode")]
public class TestEnvironmentTests(ITestOutputHelper testOutput)
{
    // Regression test for https://github.com/microsoft/aspire/issues/18732. Other tests in the same host
    // fork processes concurrently; a child forked while the mock gh script was open for writing in the host
    // kept that descriptor until exec, so executing the script failed with "Text file busy".
    [Fact]
    public async Task MockGhScript_CanBeExecutedImmediatelyWhileOtherThreadsStartProcesses()
    {
        const int Iterations = 1000;
        const int Concurrency = 4;

        using var cts = new CancellationTokenSource();
        var spawners = Enumerable.Range(0, Concurrency).Select(_ => Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                using var process = Process.Start(new ProcessStartInfo("true") { UseShellExecute = false })!;
                await process.WaitForExitAsync();
            }
        })).ToArray();

        var failures = new List<string>();
        try
        {
            await Parallel.ForEachAsync(
                Enumerable.Range(0, Iterations),
                new ParallelOptions { MaxDegreeOfParallelism = Concurrency },
                async (_, cancellationToken) =>
                {
                    using var env = new TestEnvironment();
                    var mockBinPath = await env.CreateMockGhScriptAsync(NullTestOutputHelper.Instance);
                    var startInfo = new ProcessStartInfo(Path.Combine(mockBinPath, "gh"))
                    {
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                    };
                    startInfo.ArgumentList.Add("--version");

                    try
                    {
                        using var process = Process.Start(startInfo)!;
                        await process.WaitForExitAsync(cancellationToken);
                    }
                    catch (System.ComponentModel.Win32Exception ex)
                    {
                        lock (failures)
                        {
                            failures.Add(ex.Message);
                        }
                    }
                });
        }
        finally
        {
            cts.Cancel();
            await Task.WhenAll(spawners);
        }

        testOutput.WriteLine($"{failures.Count}/{Iterations} launches of a freshly written mock gh script failed.");
        Assert.True(failures.Count == 0, $"{failures.Count}/{Iterations} launches failed. First error: {failures.FirstOrDefault()}");
    }

    private sealed class NullTestOutputHelper : ITestOutputHelper
    {
        public static readonly NullTestOutputHelper Instance = new();

        public string Output => string.Empty;

        public void Write(string message)
        {
        }

        public void Write(string format, params object[] args)
        {
        }

        public void WriteLine(string message)
        {
        }

        public void WriteLine(string format, params object[] args)
        {
        }
    }
}
