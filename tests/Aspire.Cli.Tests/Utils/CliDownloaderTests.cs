// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using System.Net.Sockets;
using System.Text;
using Aspire.Cli.Utils;
using Aspire.Cli.Packaging;

namespace Aspire.Cli.Tests.Utils;

public class CliDownloaderTests(ITestOutputHelper outputHelper)
{
    [Theory]
    [InlineData("https://aka.ms/dotnet/9/aspire/ga/daily/aspire-cli-linux-x64.tar.gz", "the stable channel", "aspire-cli-linux-x64.tar.gz from the stable channel")]
    [InlineData("https://aka.ms/dotnet/9/aspire/daily/aspire-cli-osx-arm64.tar.gz", "the daily channel", "aspire-cli-osx-arm64.tar.gz from the daily channel")]
    [InlineData("https://aka.ms/dotnet/9/aspire/rc/daily/aspire-cli-win-x64.zip", "the staging channel", "aspire-cli-win-x64.zip from the staging channel")]
    [InlineData("https://ci.dot.net/public/aspire//13.2.0-preview.1.25366.3/aspire-cli-linux-x64-13.2.0-preview.1.25366.3.tar.gz", "the stable channel", "aspire-cli-linux-x64-13.2.0-preview.1.25366.3.tar.gz from the stable channel")]
    [InlineData("https://ci.dot.net/public-checksums/aspire/13.2.0-preview.1.25366.3/aspire-cli-linux-x64-13.2.0-preview.1.25366.3.tar.gz.sha512", "the stable channel", "aspire-cli-linux-x64-13.2.0-preview.1.25366.3.tar.gz.sha512 from the stable channel")]
    [InlineData("https://example.com/downloads/aspire-cli-linux-x64.tar.gz?sig=123", null, "aspire-cli-linux-x64.tar.gz")]
    [InlineData("not a url", "the stable channel", "not a url")]
    public void GetDownloadDescriptor_ReturnsCompactDescriptor(string url, string? source, string expectedDescriptor)
    {
        var descriptor = CliDownloader.GetDownloadDescriptor(url, source);

        Assert.Equal(expectedDescriptor, descriptor);
        Assert.DoesNotContain("dotnet", descriptor, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ga/", descriptor, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rc/", descriptor, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("http://127.0.0.1:38417", "http://127.0.0.1:38417")]
    [InlineData("https://localhost:38417/", "https://localhost:38417")]
    public void ResolveDownloadBaseUrl_StagingLoopbackOverride_UsesOverride(string overrideUrl, string expected)
    {
        var environment = new TestEnvironment(new Dictionary<string, string?>
        {
            [CliDownloader.StagingDownloadBaseUrlEnvVar] = overrideUrl
        });

        var actual = CliDownloader.ResolveDownloadBaseUrl(
            PackageChannelNames.Staging,
            "https://aka.ms/dotnet/9/aspire/rc/daily",
            environment);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("https://example.com/aspire")]
    [InlineData("file:///tmp/aspire")]
    [InlineData("not-a-url")]
    public void ResolveDownloadBaseUrl_StagingNonLoopbackOverride_Throws(string overrideUrl)
    {
        var environment = new TestEnvironment(new Dictionary<string, string?>
        {
            [CliDownloader.StagingDownloadBaseUrlEnvVar] = overrideUrl
        });

        var exception = Assert.Throws<InvalidOperationException>(() => CliDownloader.ResolveDownloadBaseUrl(
            PackageChannelNames.Staging,
            "https://aka.ms/dotnet/9/aspire/rc/daily",
            environment));

        Assert.Contains(CliDownloader.StagingDownloadBaseUrlEnvVar, exception.Message);
    }

    [Fact]
    public void ResolveDownloadBaseUrl_NonStagingChannel_IgnoresOverride()
    {
        var environment = new TestEnvironment(new Dictionary<string, string?>
        {
            [CliDownloader.StagingDownloadBaseUrlEnvVar] = "https://example.com/aspire"
        });

        var actual = CliDownloader.ResolveDownloadBaseUrl(
            PackageChannelNames.Stable,
            "https://aka.ms/dotnet/9/aspire/ga/daily/",
            environment);

        Assert.Equal("https://aka.ms/dotnet/9/aspire/ga/daily", actual);
    }

    [Fact]
    public async Task DownloadFileAsync_LoopbackOverrideDoesNotFollowRedirect()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var responseTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            var buffer = new byte[1024];
            _ = await stream.ReadAsync(buffer);
            var response = Encoding.ASCII.GetBytes(
                "HTTP/1.1 302 Found\r\n" +
                "Location: https://example.com/aspire-cli.tar.gz\r\n" +
                "Content-Length: 0\r\n" +
                "Connection: close\r\n\r\n");
            await stream.WriteAsync(response);
        });

        await Assert.ThrowsAsync<HttpRequestException>(() => CliDownloader.DownloadFileAsync(
            $"http://127.0.0.1:{port}/aspire-cli.tar.gz",
            Path.Combine(workspace.Path, "aspire-cli.tar.gz"),
            timeoutSeconds: 10,
            TestContext.Current.CancellationToken,
            requireLoopback: true));
        await responseTask;
    }
}
