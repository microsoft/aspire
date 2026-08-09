// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Backchannel;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Tests.Telemetry;
using Aspire.Cli.Tests.Utils;
using Aspire.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.Backchannel;

public class AppHostCliBackchannelTests
{
    [Fact]
    public async Task UploadFileAsync_ExceedsConfiguredLimit_ThrowsWithConfigHint()
    {
        var environment = new TestEnvironment(new Dictionary<string, string?>
        {
            [KnownConfigNames.MaxFileUploadSize] = "1024" // 1 KB limit
        });
        var telemetry = TestTelemetryHelper.CreateInitializedTelemetry();
        using var profilingTelemetry = new ProfilingTelemetry(new ConfigurationBuilder().Build());
        var backchannel = new AppHostCliBackchannel(
            NullLogger<AppHostCliBackchannel>.Instance,
            environment,
            telemetry,
            profilingTelemetry);

        var tempDir = Directory.CreateTempSubdirectory("aspire-upload-test-");
        try
        {
            var tempFile = Path.Combine(tempDir.FullName, "large.bin");
            await File.WriteAllBytesAsync(tempFile, new byte[2048]); // 2 KB exceeds the 1 KB limit

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => backchannel.UploadFileAsync(tempFile, "large.bin", CancellationToken.None));

            Assert.Contains("large.bin", ex.Message);
            Assert.Contains("2048", ex.Message);
            Assert.Contains("1024", ex.Message);
            Assert.Contains(KnownConfigNames.MaxFileUploadSize, ex.Message);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task UploadFileAsync_FileBelowConfiguredLimit_PassesSizeCheck()
    {
        var environment = new TestEnvironment(new Dictionary<string, string?>
        {
            [KnownConfigNames.MaxFileUploadSize] = "4096" // 4 KB limit
        });
        var telemetry = TestTelemetryHelper.CreateInitializedTelemetry();
        using var profilingTelemetry = new ProfilingTelemetry(new ConfigurationBuilder().Build());
        var backchannel = new AppHostCliBackchannel(
            NullLogger<AppHostCliBackchannel>.Instance,
            environment,
            telemetry,
            profilingTelemetry);

        var tempDir = Directory.CreateTempSubdirectory("aspire-upload-test-");
        try
        {
            var tempFile = Path.Combine(tempDir.FullName, "small.bin");
            await File.WriteAllBytesAsync(tempFile, new byte[1024]); // 1 KB, below the 4 KB limit

            // The file is within the limit, so it should pass the size check and fail later
            // when trying to get the RPC connection (no connection available). A cancellation
            // exception proves the size check passed.
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => backchannel.UploadFileAsync(tempFile, "small.bin", cts.Token));

            // If we get a cancellation exception (waiting for RPC), the size check passed.
            Assert.DoesNotContain("exceeds the maximum upload size", ex.Message);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }
}
