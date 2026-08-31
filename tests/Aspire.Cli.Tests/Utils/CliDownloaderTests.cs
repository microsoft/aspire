// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using Aspire.Cli.Utils;
using Aspire.Cli.Packaging;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

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

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();

        // Port 0 lets Kestrel bind a free port. After StartAsync the addresses feature (exposed
        // via app.Urls) is rewritten with the resolved address, so we can read the real port.
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        await using var app = builder.Build();

        // The redirect target is a working endpoint on this same loopback server, so following the
        // redirect would produce a successful download. That is what makes this test discriminating:
        // if AllowAutoRedirect were ever re-enabled the download would succeed and this test fails.
        // Pointing at an unreachable host instead would pass either way, because the failed follow-up
        // request also surfaces as HttpRequestException.
        app.MapGet("/aspire-cli.tar.gz", () => Results.Redirect("/redirected.tar.gz"));
        app.MapGet("/redirected.tar.gz", () => Results.Text("redirected-payload"));
        await app.StartAsync(TestContext.Current.CancellationToken);

        var outputPath = Path.Combine(workspace.Path, "aspire-cli.tar.gz");

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => CliDownloader.DownloadFileAsync(
            $"{app.Urls.First()}/aspire-cli.tar.gz",
            outputPath,
            timeoutSeconds: 10,
            TestContext.Current.CancellationToken,
            requireLoopback: true));

        Assert.Equal(HttpStatusCode.Found, exception.StatusCode);
        Assert.False(File.Exists(outputPath));
    }
}
