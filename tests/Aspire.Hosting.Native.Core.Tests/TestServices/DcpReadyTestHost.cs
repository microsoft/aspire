// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Native.Core.Tests.TestServices;

/// <summary>Serves authenticated readiness with a real local certificate chain and kubeconfig.</summary>
internal sealed class DcpReadyTestHost(
    WebApplication application, DirectoryInfo directory, X509Certificate2 authority, X509Certificate2 certificate) : IAsyncDisposable
{
    public string KubeconfigPath => Path.Combine(directory.FullName, "kubeconfig");

    public static async Task<DcpReadyTestHost> StartAsync(CancellationToken cancellationToken)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(root.FullName, "Aspire.slnx")))
        {
            root = root.Parent ?? throw new InvalidOperationException("Repository root not found.");
        }
        var directory = Directory.CreateDirectory(Path.Combine(root.FullName, "artifacts", "dcp-ready", Guid.NewGuid().ToString("N")));
        var now = TimeProvider.System.GetUtcNow();
        using var authorityKey = RSA.Create(2048);
        var authorityRequest = new CertificateRequest("CN=Native DCP readiness test authority", authorityKey,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        authorityRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        authorityRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        var authority = authorityRequest.CreateSelfSigned(now.AddMinutes(-1), now.AddDays(1));
        using var serverKey = RSA.Create(2048);
        var serverRequest = new CertificateRequest("CN=localhost", serverKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        serverRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        serverRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        serverRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
        var names = new SubjectAlternativeNameBuilder();
        names.AddIpAddress(IPAddress.Loopback);
        serverRequest.CertificateExtensions.Add(names.Build());
        using var signed = serverRequest.Create(authority, now.AddMinutes(-1), now.AddHours(1), RandomNumberGenerator.GetBytes(16));
        using var keyedCertificate = signed.CopyWithPrivateKey(serverKey);
        // Schannel needs an imported private key rather than the ephemeral key
        // attached by CopyWithPrivateKey. Default key storage is cleaned up on disposal.
        var certificate = X509CertificateLoader.LoadPkcs12(keyedCertificate.Export(X509ContentType.Pfx), password: null);
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
        builder.WebHost.ConfigureKestrel(options =>
            options.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(certificate)));
        var application = builder.Build();
        application.MapGet("/readyz", (HttpContext context) =>
        {
            context.Response.StatusCode = context.Request.Headers.Authorization == "Bearer test-readiness-token" ? 200 : 401;
            return Task.CompletedTask;
        });
        var host = new DcpReadyTestHost(application, directory, authority, certificate);
        try
        {
            await application.StartAsync(cancellationToken);
            var addresses = application.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!;
            await File.WriteAllTextAsync(host.KubeconfigPath, $"""
                clusters:
                - cluster:
                    server: {addresses.Addresses.Single()}
                    certificate-authority-data: {Convert.ToBase64String(authority.RawData)}
                users:
                - user:
                    token: test-readiness-token
                """, cancellationToken);

            return host;
        }
        catch
        {
            await host.DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await application.DisposeAsync();
        certificate.Dispose();
        authority.Dispose();
        directory.Delete(recursive: true);
    }
}
