// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace Aspire.Tests.Utils;

internal static class PebbleAcmeFixture
{
    internal const string KeyId = "fixture-key-id";
    internal const string DirectoryUrl = "https://pebble-acme.cert-manager-chart.svc:14000/dir";
    private const string Namespace = "cert-manager-chart";
    // Synthetic, public test credentials with no authority outside this disposable CA.
    // Both decode to 33 bytes: HS256 requires at least 256 bits even for the rejected key.
    // See https://www.rfc-editor.org/rfc/rfc7518.html#section-3.2.
    private const string ValidHmacKey = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh_-";
    private const string InvalidHmacKey = "AQECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh_-";

    internal static async Task CreateAsync(string directory, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        using var caKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var caRequest = new CertificateRequest("CN=Aspire EAB Fixture CA", caKey, HashAlgorithmName.SHA256);
        caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        caRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        var notBefore = DateTimeOffset.UtcNow.AddMinutes(-5);
        var notAfter = DateTimeOffset.UtcNow.AddDays(2);
        using var ca = caRequest.CreateSelfSigned(notBefore, notAfter);

        using var serverKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var serverRequest = new CertificateRequest("CN=pebble-acme", serverKey, HashAlgorithmName.SHA256);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("pebble-acme.cert-manager-chart.svc");
        names.AddDnsName("pebble-acme.cert-manager-chart.svc.cluster.local");
        serverRequest.CertificateExtensions.Add(names.Build());
        serverRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        using var serverCertificate = serverRequest.Create(ca, notBefore, notAfter, RandomNumberGenerator.GetBytes(16));

        var config = JsonSerializer.Serialize(new
        {
            pebble = new
            {
                listenAddress = "0.0.0.0:14000",
                // The management API can expose CA private keys and is never needed for registration.
                managementListenAddress = "",
                certificate = "/fixture/tls.crt",
                privateKey = "/fixture/tls.key",
                httpPort = 80,
                tlsPort = 443,
                externalAccountBindingRequired = true,
                externalAccountMACKeys = new Dictionary<string, string> { [KeyId] = ValidHmacKey }
            }
        });
        var labels = new Dictionary<string, string> { ["app"] = "aspire-eab-pebble" };
        var manifest = JsonSerializer.Serialize(new
        {
            apiVersion = "v1",
            kind = "List",
            items = new object[]
            {
                new
                {
                    apiVersion = "v1",
                    kind = "Secret",
                    metadata = new { name = "pebble-acme-fixture", @namespace = Namespace },
                    type = "Opaque",
                    stringData = new Dictionary<string, string>
                    {
                        ["config.json"] = config,
                        ["tls.crt"] = serverCertificate.ExportCertificatePem(),
                        ["tls.key"] = serverKey.ExportPkcs8PrivateKeyPem()
                    }
                },
                new
                {
                    apiVersion = "v1",
                    kind = "Service",
                    metadata = new { name = "pebble-acme", @namespace = Namespace },
                    spec = new
                    {
                        type = "ClusterIP",
                        selector = labels,
                        ports = new[] { new { port = 14000, targetPort = 14000 } }
                    }
                },
                new
                {
                    apiVersion = "apps/v1",
                    kind = "Deployment",
                    metadata = new { name = "pebble-acme", @namespace = Namespace },
                    spec = new
                    {
                        replicas = 1,
                        selector = new { matchLabels = labels },
                        template = new
                        {
                            metadata = new { labels },
                            spec = new
                            {
                                automountServiceAccountToken = false,
                                enableServiceLinks = false,
                                securityContext = new
                                {
                                    runAsNonRoot = true,
                                    runAsUser = 65532,
                                    runAsGroup = 65532,
                                    fsGroup = 65532,
                                    seccompProfile = new { type = "RuntimeDefault" }
                                },
                                containers = new[]
                                {
                                    new
                                    {
                                        name = "pebble",
                                        image = "ghcr.io/letsencrypt/pebble:2.10.1@sha256:ddf230642b1a584f519f32e347de1b05a6e4c1f6c35c1863b33effeab5f78199",
                                        args = new[] { "-config", "/fixture/config.json", "-strict" },
                                        ports = new[] { new { containerPort = 14000 } },
                                        readinessProbe = new { tcpSocket = new { port = 14000 } },
                                        securityContext = new
                                        {
                                            allowPrivilegeEscalation = false,
                                            readOnlyRootFilesystem = true,
                                            capabilities = new { drop = new[] { "ALL" } }
                                        },
                                        resources = new
                                        {
                                            requests = new { cpu = "100m", memory = "128Mi" },
                                            limits = new { cpu = "500m", memory = "256Mi" }
                                        },
                                        volumeMounts = new[] { new { name = "fixture", mountPath = "/fixture", readOnly = true } }
                                    }
                                },
                                volumes = new[]
                                {
                                    new { name = "fixture", secret = new { secretName = "pebble-acme-fixture", defaultMode = 288 } }
                                }
                            }
                        }
                    }
                },
                new
                {
                    apiVersion = "networking.k8s.io/v1",
                    kind = "NetworkPolicy",
                    metadata = new { name = "pebble-acme", @namespace = Namespace },
                    spec = new
                    {
                        podSelector = new { matchLabels = labels },
                        policyTypes = new[] { "Ingress", "Egress" },
                        ingress = new[]
                        {
                            new
                            {
                                from = new[]
                                {
                                    new { podSelector = new { matchLabels = new Dictionary<string, string>
                                        { ["app.kubernetes.io/component"] = "controller" } } }
                                },
                                ports = new[] { new { protocol = "TCP", port = 14000 } }
                            }
                        },
                        // Account registration needs no outbound connections or domain-validation bypass.
                        egress = Array.Empty<object>()
                    }
                }
            }
        });
        var trustPatch = JsonSerializer.Serialize(new
        {
            spec = new { acme = new { caBundle = Convert.ToBase64String(Encoding.ASCII.GetBytes(ca.ExportCertificatePem())) } }
        });
        await WritePrivateFileAsync("pebble.json", manifest);
        await WritePrivateFileAsync("trust.json", trustPatch);
        await WritePrivateFileAsync("valid-key", ValidHmacKey);
        await WritePrivateFileAsync("invalid-key", InvalidHmacKey);

        async Task WritePrivateFileAsync(string name, string content)
        {
            var path = Path.Combine(directory, name);
            await File.WriteAllTextAsync(path, content, cancellationToken);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
    }
}
