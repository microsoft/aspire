// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Utils;
using Aspire.Hosting.Devcontainers.Codespaces;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Tests.Codespaces;

[Trait("Partition", "4")]
public class CodespacesUrlRewriterTests(ITestOutputHelper testOutputHelper)
{
    [Theory]
    [InlineData("http://localhost:8080/path?x=1#fragment", true, "https://test-codespace-8080.app.github.dev/path?x=1#fragment")]
    [InlineData("https://localhost:7443/path", true, "https://test-codespace-7443.app.github.dev/path")]
    [InlineData("https://public.example/path", false, null)]
    [InlineData("https://example.devtunnels.ms/path", false, null)]
    [InlineData("tcp://localhost:5432", false, null)]
    [InlineData("http://test-codespace-8080.app.github.dev/path", false, null)]
    public void ResourceProjectionOnlyRewritesLocalHttpUrls(string url, bool expected, string? expectedUrl)
    {
        var rewriter = new CodespacesUrlRewriter(Options.Create(new CodespacesOptions
        {
            IsCodespace = true,
            CodespaceName = "test-codespace",
            PortForwardingDomain = "app.github.dev"
        }));

        Assert.Equal(expected, rewriter.TryRewriteResourceUrl(url, out var rewritten));
        Assert.Equal(expectedUrl, rewritten);
    }

    [Fact]
    public async Task VerifyUrlsRewriterStopsWhenNotInCodespaces()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        // Explicitly disable codespace behavior for this test.
        builder.Configuration["CODESPACES"] = "false";

        builder.Services.AddLogging(logging =>
        {
            logging.AddFakeLogging();
            logging.AddXunit(testOutputHelper);
        });

        var resource = builder.AddResource(new CustomResource("resource"));

        var abortToken = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        using var app = builder.Build();

        await app.StartAsync(abortToken.Token);

        var collector = app.Services.GetFakeLogCollector();

        var urlRewriterStopped = false;

        while (!abortToken.Token.IsCancellationRequested)
        {
            var logs = collector.GetSnapshot();
            urlRewriterStopped = logs.Any(l => l.Message.Contains("Not running in Codespaces, skipping URL rewriting."));
            if (urlRewriterStopped)
            {
                break;
            }
        }

        Assert.True(urlRewriterStopped);

        await app.StopAsync(abortToken.Token);
    }

    [Fact]
    public async Task VerifyUrlsRewrittenWhenInCodespaces()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        builder.Configuration["CODESPACES"] = "true";
        builder.Configuration["GITHUB_CODESPACES_PORT_FORWARDING_DOMAIN"] = "app.github.dev";
        builder.Configuration["CODESPACE_NAME"] = "test-codespace";

        var resource = builder.AddResource(new CustomResource("resource"));

        var abortToken = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        using var app = builder.Build();

        await app.StartAsync(abortToken.Token);

        // Push the URL to the resource state.
        var localhostUrlSnapshot = new UrlSnapshot("Test", "http://localhost:1234", false);
        await app.ResourceNotifications.PublishUpdateAsync(resource.Resource, s => s with
        {
            State = KnownResourceStates.Running,
            Urls = [localhostUrlSnapshot]
        });

        // Wait until
        var resourceEvent = await app.ResourceNotifications.WaitForResourceAsync(
            resource.Resource.Name,
            (re) => {
                var match = re.Snapshot.Urls.Length > 0 && re.Snapshot.Urls[0].Url.Contains("app.github.dev");
                return match;
            },
            abortToken.Token);

        Assert.Collection(
            resourceEvent.Snapshot.Urls,
            u =>
            {
                Assert.Equal("Test", u.Name);
                Assert.Equal("https://test-codespace-1234.app.github.dev/", u.Url);
                Assert.False(u.IsInternal);
            }
            );

        await app.StopAsync(abortToken.Token);
    }

    private sealed class CustomResource(string name) : Resource(name)
    {
    }
}
