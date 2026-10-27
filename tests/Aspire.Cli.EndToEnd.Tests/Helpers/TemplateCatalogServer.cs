// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.EndToEnd.Tests.Helpers;

internal sealed class TemplateCatalogServer(WebApplication application, X509Certificate2 certificate) : IAsyncDisposable
{
    private readonly ConcurrentQueue<Request> _requests = new();
    private readonly ConcurrentQueue<string> _cancelledRequests = new();

    public string Url => new UriBuilder(application.Urls.Single())
    {
        Host = OperatingSystem.IsLinux() ? "localhost" : "host.docker.internal"
    }.Uri.GetLeftPart(UriPartial.Authority);
    public Request[] Requests => _requests.ToArray();
    public string[] CancelledRequests => _cancelledRequests.ToArray();

    public static async Task<TemplateCatalogServer> StartAsync(string directory, CancellationToken cancellationToken)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyCertSign, true));
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        names.AddDnsName("host.docker.internal");
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], ContentRootPath = directory });
        builder.Logging.ClearProviders();
        // Desktop containers reach the host through their gateway, not its loopback interface.
        builder.WebHost.ConfigureKestrel(options => options.ListenAnyIP(0, listener => listener.UseHttps(certificate)));
        var application = builder.Build();
        var server = new TemplateCatalogServer(application, certificate);
        application.MapGet("/{**path}", server.HandleAsync);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "catalog-ca.pem"), certificate.ExportCertificatePem(), cancellationToken);
            await application.StartAsync(cancellationToken);
            return server;
        }
        catch
        {
            await server.DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await application.DisposeAsync();
        certificate.Dispose();
    }

    private async Task HandleAsync(HttpContext context)
    {
        var path = context.Request.Path.Value!;
        _requests.Enqueue(new(path, context.Request.QueryString.Value ?? "", context.Request.Headers.Cookie.ToString()));
        using var registration = context.RequestAborted.Register(() => _cancelledRequests.Enqueue(path));
        switch (path)
        {
            case "/health":
                await context.Response.WriteAsJsonAsync(new { templates = Array.Empty<object>() }, context.RequestAborted);
                break;
            case "/pages":
                if (!context.Request.Query.ContainsKey("cursor"))
                {
                    context.Response.Headers.SetCookie = "catalog-session=must-not-be-sent";
                    await context.Response.WriteAsJsonAsync(new { templates = new[] { Entry("unrelated") }, nextLink = "?cursor=a%2Bb%26c%3D" }, context.RequestAborted);
                }
                else
                {
                    await context.Response.WriteAsJsonAsync(new { templates = new[] { Entry("custom-middle") }, nextLink = $"{Url}/last?cursor=%7Bsearch%7D" }, context.RequestAborted);
                }
                break;
            case "/last":
                await context.Response.WriteAsJsonAsync(new { templates = new[] { Entry("custom-last") }, nextLink = (string?)null }, context.RequestAborted);
                break;
            case "/unsafe":
                await PageAsync(context, new UriBuilder(Url)
                {
                    Host = OperatingSystem.IsLinux() ? "127.0.0.1" : "localhost",
                    Path = "/forbidden"
                }.Uri.AbsoluteUri);
                break;
            case "/loop":
                await PageAsync(context, "/loop");
                break;
            case "/limit":
                var page = int.TryParse(context.Request.Query["page"], out var value) ? value : 1;
                await PageAsync(context, $"/limit?page={page + 1}");
                break;
            case "/redirect":
                context.Response.Redirect("/forbidden");
                break;
            case "/error":
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                await context.Response.WriteAsync("private server diagnostics", context.RequestAborted);
                break;
            case "/malformed":
                await context.Response.WriteAsync("{", context.RequestAborted);
                break;
            case "/large":
            case "/chunked":
                var oversized = new string(' ', 4 * 1024 * 1024) + """{"templates":[]}""";
                if (path == "/large")
                {
                    context.Response.ContentLength = oversized.Length;
                }
                else
                {
                    await context.Response.StartAsync(context.RequestAborted);
                }
                await context.Response.WriteAsync(oversized, context.RequestAborted);
                break;
            case "/aggregate":
                var json = context.Request.Query.ContainsKey("last")
                    ? """{"templates":[]}"""
                    : """{"templates":[],"nextLink":"/aggregate?last=true"}""";
                await context.Response.WriteAsync(json.PadRight(2 * 1024 * 1024 + 1), context.RequestAborted);
                break;
            case "/slow-pages":
                await Task.Delay(TimeSpan.FromSeconds(15), context.RequestAborted);
                await PageAsync(context, "/slow-body");
                break;
            case "/slow-body":
                await context.Response.WriteAsync("""{"templates":[""", context.RequestAborted);
                await context.Response.Body.FlushAsync(context.RequestAborted);
                await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted);
                break;
            case "/slow-headers":
                await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted);
                break;
            default:
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                break;
        }
    }

    private static Task PageAsync(HttpContext context, string nextLink) =>
        context.Response.WriteAsJsonAsync(new { templates = Array.Empty<object>(), nextLink }, context.RequestAborted);

    private static object Entry(string name) =>
        new { name, description = name, repository = "https://example.test/not-cloned", @ref = "main", path = "." };

    internal sealed record Request(string Path, string Query, string Cookie);
}
