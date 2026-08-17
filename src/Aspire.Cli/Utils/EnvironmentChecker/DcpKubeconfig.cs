// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Security.Cryptography.X509Certificates;
using System.Text;
using Aspire.Cli.Resources;

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
        string? server = null;
        string? token = null;
        string? certificateAuthorityData = null;
        string? clientCertificateData = null;
        string? clientKeyData = null;

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
        foreach (var line in content.Split('\n'))
        {
            server ??= TryReadScalar(line, "server");
            token ??= TryReadScalar(line, "token");
            certificateAuthorityData ??= TryReadScalar(line, "certificate-authority-data");
            clientCertificateData ??= TryReadScalar(line, "client-certificate-data");
            clientKeyData ??= TryReadScalar(line, "client-key-data");
        }

        if (string.IsNullOrWhiteSpace(server) || !Uri.TryCreate(server, UriKind.Absolute, out var serverUri))
        {
            throw new InvalidOperationException(DoctorCommandStrings.DcpKubeconfigMissingServerDetails);
        }

        return new DcpKubeconfig
        {
            Server = serverUri,
            Token = token,
            CertificateAuthorityCertificates = certificateAuthorityData is null
                ? []
                : LoadCertificates(certificateAuthorityData),
            ClientCertificate = clientCertificateData is not null && clientKeyData is not null
                ? LoadClientCertificate(clientCertificateData, clientKeyData)
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

    private static string? TryReadScalar(string line, string key)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0 || trimmed[0] == '#')
        {
            return null;
        }

        var prefix = key + ":";
        if (!trimmed.StartsWith(prefix, StringComparison.Ordinal))
        {
            return null;
        }

        var value = trimmed[prefix.Length..].Trim();
        if (value.Length == 0)
        {
            return null;
        }

        return TrimYamlQuotes(value);
    }

    private static string TrimYamlQuotes(string value)
    {
        return value.Length >= 2 && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\''))
            ? value[1..^1]
            : value;
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
