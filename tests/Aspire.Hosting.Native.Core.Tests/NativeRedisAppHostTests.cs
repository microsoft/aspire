// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Hosting.Native.Core.Tests.TestServices;

namespace Aspire.Hosting.Native.Core.Tests;

public class NativeRedisAppHostTests
{
    [Theory]
    [InlineData("CSHARP", false)]
    [InlineData("TYPESCRIPT", false)]
    [InlineData("CSHARP", true)]
    [InlineData("TYPESCRIPT", true)]
    public async Task RealRedisIntegrationUsesDeclaredWorkloadsAuthenticatedHealthAndRetainedVolume(string language, bool revisions)
    {
        var server = Environment.GetEnvironmentVariable("ASPIRE_NATIVE_SYMBOLIC_TEST_SERVER");
        var dcp = Environment.GetEnvironmentVariable("ASPIRE_NATIVE_SYMBOLIC_TEST_DCP");
        var apphost = Environment.GetEnvironmentVariable($"ASPIRE_NATIVE_REDIS_{language}_{(revisions ? "VALIDATION" : "APPHOST")}");
        var provider = Environment.GetEnvironmentVariable($"ASPIRE_NATIVE_REDIS_{language}_PROVIDER");
        var node = Environment.GetEnvironmentVariable("ASPIRE_NATIVE_SYMBOLIC_TEST_NODE");
        if (server is null || dcp is null || apphost is null || provider is null || language == "TYPESCRIPT" && node is null)
        {
            Assert.Skip("The dedicated native lane supplies actual Redis integration AppHosts/providers, AOT, DCP and Node.");
        }
        var prefix = $"native-redis-authoring-{Guid.NewGuid():N}";
        var volume = $"{prefix}-cache-data";
        try
        {
            await using var host = await NativeClientTestHost.StartWithWorkloadsAsync(server, dcp);
            var executable = language == "CSHARP" ? apphost : node!;
            var configuration = new[]
            {
                $"--native-endpoint={host.Endpoint}",
                $"--native-provider={(language == "CSHARP" ? "example.csharp.redis" : "example.ts.redis")}={(language == "CSHARP" ? provider : $"{node}|{provider}")}",
                $"--native-volume-prefix={prefix}",
                "--native-exit-on-completion=consumer",
                $"--consumer-path={provider}",
                $"--data-volume={volume}"
            };
            var arguments = language == "CSHARP" ? configuration : new[] { apphost }.Concat(configuration).ToArray();
            var output = await NativeIntegrationAppHostRunner.RunAsync(executable, arguments, host.Token, TimeProvider.System);
            Assert.Equal(revisions ? "redis-persistence-ok" : "", output.Trim());
            // The provider's retiring ATS callback requests authenticated SAVE
            // through its native phase scope. AOF alone never creates dump.rdb;
            // this proves the authored callback ran after public handle revocation.
            var verify = new ProcessStartInfo("docker")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                ArgumentList =
                {
                    "run", "--rm", "--volume", $"{volume}:/data:ro", "--entrypoint", "/bin/sh",
                    "redis:7.4-alpine", "-c", "test -s /data/dump.rdb"
                }
            };
            using var snapshot = Process.Start(verify)!;
            var snapshotOutput = snapshot.StandardOutput.ReadToEndAsync();
            var snapshotError = snapshot.StandardError.ReadToEndAsync();
            await snapshot.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30), TimeProvider.System);
            await Task.WhenAll(snapshotOutput, snapshotError);
            Assert.Equal(0, snapshot.ExitCode);
        }
        finally
        {
            // Only this scenario's retained named volume is removed. Integration
            // retirement intentionally preserves it across application generations.
            var start = new ProcessStartInfo("docker") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            start.ArgumentList.Add("volume");
            start.ArgumentList.Add("rm");
            start.ArgumentList.Add(volume);
            using var cleanup = Process.Start(start)!;
            var output = cleanup.StandardOutput.ReadToEndAsync();
            var error = cleanup.StandardError.ReadToEndAsync();
            await cleanup.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15), TimeProvider.System);
            await Task.WhenAll(output, error);
            Assert.Equal(0, cleanup.ExitCode);
        }
    }
}
