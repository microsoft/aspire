// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Security.Cryptography.X509Certificates;
using System.Text;
using Aspire.Cli.Resources;
using Aspire.Shared;

namespace Aspire.Cli.Utils.EnvironmentChecker;

internal sealed class DcpKubeconfig : IDisposable
{
    private const int ReadAttempts = 3;
    private static readonly TimeSpan s_readRetryDelay = TimeSpan.FromMilliseconds(100);

    public required Uri Server { get; init; }

    public string? Token { get; init; }

    public List<X509Certificate2> CertificateAuthorityCertificates { get; init; } = [];

    public X509Certificate2? ClientCertificate { get; init; }

    internal static async Task<DcpKubeconfig> ReadFileWithRetryAsync(string path, Func<TimeSpan, CancellationToken, Task>? delayAsync = null, CancellationToken cancellationToken = default)
    {
        delayAsync ??= Task.Delay;

        for (var attempt = 1; ; attempt++)
        {
            var content = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            try
            {
                return Parse(content);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && attempt < ReadAttempts)
            {
                // DCP creates the kubeconfig path before all content may be flushed. A brief retry
                // avoids treating a transient partial file as a failed connection check.
                await delayAsync(s_readRetryDelay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public static DcpKubeconfig Parse(string content)
    {
        // DCP emits a compact kubeconfig in this shape:
        //   clusters:
        //   - name: dcp
        //     cluster:
        //       server: https://127.0.0.1:<port>
        //       certificate-authority-data: <base64 PEM>
        //   users:
        //   - name: dcp
        //     user:
        //       client-certificate-data: <base64 PEM>
        //       client-key-data: <base64 PEM>
        // The doctor probe only needs connection material, so parse the scalar fields directly
        // instead of adding a YAML dependency to the NativeAOT CLI.
        var data = DcpKubeconfigData.Parse(content);
        var server = data.Server;

        if (string.IsNullOrWhiteSpace(server) || !Uri.TryCreate(server, UriKind.Absolute, out var serverUri))
        {
            throw new InvalidOperationException(DoctorCommandStrings.DcpKubeconfigMissingServerDetails);
        }

        return new DcpKubeconfig
        {
            Server = serverUri,
            Token = data.Token,
            CertificateAuthorityCertificates = data.CertificateAuthorityData is null
                ? []
                : LoadCertificates(data.CertificateAuthorityData),
            ClientCertificate = data.ClientCertificateData is not null && data.ClientKeyData is not null
                ? LoadClientCertificate(data.ClientCertificateData, data.ClientKeyData)
                : null
        };
    }

    public void Dispose()
    {
        foreach (var certificate in CertificateAuthorityCertificates)
        {
            certificate.Dispose();
        }

        ClientCertificate?.Dispose();
    }

    private static List<X509Certificate2> LoadCertificates(string base64Data)
    {
        var certificatePem = Encoding.UTF8.GetString(Convert.FromBase64String(base64Data));
        var certificates = new X509Certificate2Collection();
        certificates.ImportFromPem(certificatePem);

        return certificates.OfType<X509Certificate2>().ToList();
    }

    private static X509Certificate2 LoadClientCertificate(string certificateData, string keyData)
    {
        var certificatePem = Encoding.UTF8.GetString(Convert.FromBase64String(certificateData));
        var keyPem = Encoding.UTF8.GetString(Convert.FromBase64String(keyData));

        using var certificate = X509Certificate2.CreateFromPem(certificatePem, keyPem);
        return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pkcs12), password: null, X509KeyStorageFlags.EphemeralKeySet);
    }
}
