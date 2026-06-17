// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Aspire.Hosting.Utils;
using Aspire.Shared;
using Microsoft.AspNetCore.Certificates.Generation;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Utils.EnvironmentChecker;

internal interface IDcpConnectionTester
{
    Task<DcpConnectionTestResult> TestConnectionAsync(string dcpDirectory, DcpConnectionSecurityMode mode, CancellationToken cancellationToken);
}

internal enum DcpConnectionSecurityMode
{
    EphemeralCertificate,
    DeveloperCertificate
}

internal sealed record DcpConnectionTestResult(
    DcpConnectionSecurityMode Mode,
    EnvironmentCheckStatus Status,
    string Message,
    string? Details = null,
    string? Fix = null);

internal sealed class DcpConnectionTester(
    CertificateManager certificateManager,
    CliExecutionContext executionContext,
    ILogger<DcpConnectionTester> logger) : IDcpConnectionTester
{
    private static readonly TimeSpan s_connectionTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan s_processExitTimeout = TimeSpan.FromSeconds(5);

    public async Task<DcpConnectionTestResult> TestConnectionAsync(string dcpDirectory, DcpConnectionSecurityMode mode, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(s_connectionTimeout);

        try
        {
            await using var session = await DcpConnectionTestSession.StartAsync(
                dcpDirectory,
                mode,
                certificateManager,
                executionContext,
                logger,
                timeoutCts.Token).ConfigureAwait(false);

            using var kubeconfig = await session.ReadKubeconfigAsync(timeoutCts.Token).ConfigureAwait(false);
            using var handler = CreateHttpClientHandler(kubeconfig);
            using var client = new HttpClient(handler)
            {
                BaseAddress = kubeconfig.Server,
                Timeout = s_connectionTimeout,
                DefaultRequestVersion = HttpVersion.Version20,
                DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower
            };

            if (!string.IsNullOrWhiteSpace(kubeconfig.Token))
            {
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", kubeconfig.Token);
            }

            using var response = await client.GetAsync(new Uri(kubeconfig.Server, "/version"), timeoutCts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return Failed(mode, $"DCP API server returned HTTP {(int)response.StatusCode} ({response.StatusCode}).");
            }

            await session.StopDcpAsync(client, kubeconfig, timeoutCts.Token).ConfigureAwait(false);

            return mode switch
            {
                DcpConnectionSecurityMode.EphemeralCertificate => Passed(mode, "DCP connection using an ephemeral DCP-managed certificate succeeded"),
                DcpConnectionSecurityMode.DeveloperCertificate => Passed(mode, "DCP connection using the ASP.NET Core HTTPS development certificate succeeded"),
                _ => Passed(mode, "DCP connection succeeded")
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            return Failed(mode, $"Timed out after {s_connectionTimeout.TotalSeconds.ToString(CultureInfo.InvariantCulture)} seconds while checking DCP connection health.", ex.Message);
        }
        catch (DcpDeveloperCertificateUnavailableException ex)
        {
            return new DcpConnectionTestResult(
                mode,
                EnvironmentCheckStatus.Warning,
                "No trusted ASP.NET Core HTTPS development certificate was available for the DCP developer-certificate connection check",
                ex.Message,
                "Run `aspire certs trust` to create and trust a development certificate.");
        }
        catch (AuthenticationException ex)
        {
            return Failed(
                mode,
                FailureMessage(mode),
                $"TLS authentication failed: {ex.Message}",
                mode == DcpConnectionSecurityMode.DeveloperCertificate ? "Run `aspire certs trust` to repair development certificate trust." : null);
        }
        catch (HttpRequestException ex)
        {
            return Failed(mode, FailureMessage(mode), ex.Message);
        }
        catch (Exception ex)
        {
            return Failed(mode, FailureMessage(mode), ex.Message);
        }
    }

    private static HttpClientHandler CreateHttpClientHandler(DcpKubeconfig kubeconfig)
    {
        var handler = new HttpClientHandler();
        if (kubeconfig.ClientCertificate is not null)
        {
            handler.ClientCertificates.Add(kubeconfig.ClientCertificate);
        }

        if (kubeconfig.CertificateAuthorityCertificates.Count > 0)
        {
            handler.ServerCertificateCustomValidationCallback = (request, certificate, _, sslPolicyErrors) =>
            {
                if (certificate is null)
                {
                    return false;
                }

                if ((sslPolicyErrors & ~SslPolicyErrors.RemoteCertificateChainErrors) != SslPolicyErrors.None)
                {
                    return false;
                }

                using var serverCertificate = X509CertificateLoader.LoadCertificate(certificate.Export(X509ContentType.Cert));
                using var chain = new X509Chain();
                chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;

                foreach (var authorityCertificate in kubeconfig.CertificateAuthorityCertificates)
                {
                    chain.ChainPolicy.CustomTrustStore.Add(authorityCertificate);
                }

                return chain.Build(serverCertificate);
            };
        }

        return handler;
    }

    private static DcpConnectionTestResult Passed(DcpConnectionSecurityMode mode, string message) =>
        new(mode, EnvironmentCheckStatus.Pass, message);

    private static DcpConnectionTestResult Failed(DcpConnectionSecurityMode mode, string message, string? details = null, string? fix = null) =>
        new(mode, EnvironmentCheckStatus.Fail, message, details, fix);

    private static string FailureMessage(DcpConnectionSecurityMode mode)
    {
        return mode switch
        {
            DcpConnectionSecurityMode.EphemeralCertificate => "DCP connection using an ephemeral DCP-managed certificate failed",
            DcpConnectionSecurityMode.DeveloperCertificate => "DCP connection using the ASP.NET Core HTTPS development certificate failed",
            _ => "DCP connection failed"
        };
    }

    private sealed class DcpConnectionTestSession : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly string _sessionDirectory;
        private readonly string _kubeconfigPath;
        private readonly ProcessOutputBuffer _output;
        private readonly ILogger _logger;
        private bool _stopRequested;

        private DcpConnectionTestSession(Process process, string sessionDirectory, string kubeconfigPath, ProcessOutputBuffer output, ILogger logger)
        {
            _process = process;
            _sessionDirectory = sessionDirectory;
            _kubeconfigPath = kubeconfigPath;
            _output = output;
            _logger = logger;
        }

        public static async Task<DcpConnectionTestSession> StartAsync(
            string dcpDirectory,
            DcpConnectionSecurityMode mode,
            CertificateManager certificateManager,
            CliExecutionContext executionContext,
            ILogger logger,
            CancellationToken cancellationToken)
        {
            var sessionDirectory = Directory.CreateTempSubdirectory("aspire-dcp-doctor-").FullName;
            var kubeconfigPath = Path.Combine(sessionDirectory, "kubeconfig");
            var dcpExecutablePath = BundleDiscovery.GetDcpExecutablePath(dcpDirectory);
            var output = new ProcessOutputBuffer();
            Process? process = null;

            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = dcpExecutablePath,
                    WorkingDirectory = executionContext.WorkingDirectory.FullName,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                startInfo.ArgumentList.Add("start-apiserver");
                startInfo.ArgumentList.Add("--kubeconfig");
                startInfo.ArgumentList.Add(kubeconfigPath);

                if (mode == DcpConnectionSecurityMode.DeveloperCertificate)
                {
                    AddDeveloperCertificateArguments(startInfo, certificateManager);
                }

                var extensionsPath = Path.Combine(dcpDirectory, "ext");
                if (Directory.Exists(extensionsPath))
                {
                    startInfo.Environment["DCP_EXTENSIONS_PATH"] = extensionsPath;
                }

                // DCP uses this folder for process-scoped state such as the generated kubeconfig.
                // Keeping it under the doctor-owned temp directory prevents overlap with AppHost sessions.
                startInfo.Environment["DCP_SESSION_FOLDER"] = sessionDirectory;

                process = new Process
                {
                    StartInfo = startInfo,
                    EnableRaisingEvents = true
                };

                process.OutputDataReceived += (_, e) => output.Add(e.Data);
                process.ErrorDataReceived += (_, e) => output.Add(e.Data);

                if (!process.Start())
                {
                    throw new InvalidOperationException("Failed to start DCP.");
                }

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                var session = new DcpConnectionTestSession(process, sessionDirectory, kubeconfigPath, output, logger);
                await session.WaitForKubeconfigFileAsync(cancellationToken).ConfigureAwait(false);
                return session;
            }
            catch
            {
                if (process is not null)
                {
                    try
                    {
                        if (!process.HasExited)
                        {
                            process.Kill(entireProcessTree: true);
                        }
                    }
                    catch (InvalidOperationException) when (process.HasExited)
                    {
                    }
                    finally
                    {
                        process.Dispose();
                    }
                }

                try
                {
                    Directory.Delete(sessionDirectory, recursive: true);
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Failed to delete DCP doctor session directory '{SessionDirectory}'.", sessionDirectory);
                }

                throw;
            }
        }

        public async Task<DcpKubeconfig> ReadKubeconfigAsync(CancellationToken cancellationToken)
        {
            await WaitForKubeconfigFileAsync(cancellationToken).ConfigureAwait(false);

            var content = await File.ReadAllTextAsync(_kubeconfigPath, cancellationToken).ConfigureAwait(false);
            return DcpKubeconfig.Parse(content);
        }

        public async Task StopDcpAsync(HttpClient client, DcpKubeconfig kubeconfig, CancellationToken cancellationToken)
        {
            if (_stopRequested)
            {
                return;
            }

            _stopRequested = true;

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Patch, new Uri(kubeconfig.Server, "/admin/execution"))
                {
                    Content = new StringContent("""{"status":"Stopping","shutdownResourceCleanup":"None"}""", Encoding.UTF8, "application/merge-patch+json"),
                    Version = HttpVersion.Version20,
                    VersionPolicy = HttpVersionPolicy.RequestVersionOrLower
                };

                using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogDebug("DCP doctor stop request returned HTTP {StatusCode}.", response.StatusCode);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Failed to request DCP shutdown for doctor connection check.");
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                    await _process.WaitForExitAsync().WaitAsync(s_processExitTimeout).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
            {
                _logger.LogDebug(ex, "Failed to stop DCP doctor process.");
            }
            finally
            {
                _process.Dispose();

                try
                {
                    Directory.Delete(_sessionDirectory, recursive: true);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to delete DCP doctor session directory '{SessionDirectory}'.", _sessionDirectory);
                }
            }
        }

        private async Task WaitForKubeconfigFileAsync(CancellationToken cancellationToken)
        {
            while (!File.Exists(_kubeconfigPath))
            {
                if (_process.HasExited)
                {
                    throw new InvalidOperationException(
                        $"DCP exited before writing kubeconfig. Exit code: {_process.ExitCode}.{Environment.NewLine}{_output.GetOutput()}");
                }

                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
            }
        }

        private static void AddDeveloperCertificateArguments(ProcessStartInfo startInfo, CertificateManager certificateManager)
        {
            var certificates = certificateManager.ListCertificates(StoreName.My, StoreLocation.CurrentUser, isValid: true);
            try
            {
                var certificate = certificates.FirstOrDefault(c =>
                    c.HasPrivateKey &&
                    certificateManager.GetTrustLevel(c) == CertificateManager.TrustLevel.Full);

                if (certificate is null)
                {
                    throw new DcpDeveloperCertificateUnavailableException("No fully trusted exportable ASP.NET Core HTTPS development certificate with a private key was found.");
                }

                if (string.IsNullOrWhiteSpace(certificate.Thumbprint))
                {
                    throw new DcpDeveloperCertificateUnavailableException("The ASP.NET Core HTTPS development certificate did not have a thumbprint.");
                }

                startInfo.ArgumentList.Add("--tls-cert-thumbprint");
                startInfo.ArgumentList.Add(certificate.Thumbprint);

                if (OperatingSystem.IsWindows())
                {
                    return;
                }

                var certificatePath = DcpDeveloperCertificateCache.EnsureDeveloperCertificateCache(certificateManager, certificate);
                var keyPath = Path.ChangeExtension(certificatePath, ".key");

                startInfo.ArgumentList.Add("--tls-cert-file");
                startInfo.ArgumentList.Add(certificatePath);
                startInfo.ArgumentList.Add("--tls-key-file");
                startInfo.ArgumentList.Add(keyPath);
            }
            finally
            {
                CertificateManager.DisposeCertificates(certificates);
            }
        }

    }

    private sealed class ProcessOutputBuffer
    {
        private const int MaxLines = 40;
        private readonly ConcurrentQueue<string> _lines = new();

        public void Add(string? line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return;
            }

            _lines.Enqueue(line);
            while (_lines.Count > MaxLines && _lines.TryDequeue(out _))
            {
            }
        }

        public string GetOutput()
        {
            var lines = _lines.ToArray();
            return lines.Length == 0 ? "DCP did not write any output." : string.Join(Environment.NewLine, lines);
        }
    }
}

internal sealed class DcpDeveloperCertificateUnavailableException(string message) : Exception(message);

internal static class DcpDeveloperCertificateCache
{
    public static string EnsureDeveloperCertificateCache(CertificateManager certificateManager, X509Certificate2 certificate)
    {
        if (!certificate.IsAspNetCoreDevelopmentCertificate() || string.IsNullOrWhiteSpace(certificate.Thumbprint))
        {
            throw new DcpDeveloperCertificateUnavailableException("The ASP.NET Core HTTPS development certificate could not be cached because it was not a valid development certificate.");
        }

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(userProfile))
        {
            throw new DcpDeveloperCertificateUnavailableException("The ASP.NET Core HTTPS development certificate could not be cached because the user profile directory could not be determined.");
        }

        // This matches DeveloperCertificateService.GetKeyMaterialCacheLookup, which uses
        // SHA256(thumbprint) for unencrypted ASP.NET Core development certificate caches.
        var lookup = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(certificate.Thumbprint)));
        var cacheDirectory = Path.Combine(userProfile, ".aspire", "dev-certs", "https");
        var certificatePath = Path.Combine(cacheDirectory, $"{lookup}.crt");
        var keyPath = Path.ChangeExtension(certificatePath, ".key");

        if (!File.Exists(keyPath))
        {
            certificateManager.ExportCertificate(certificate, certificatePath, includePrivateKey: true, password: null, CertificateKeyExportFormat.Pem);
        }
        else
        {
            // The public certificate does not require private key access, so fill older caches
            // without re-exporting the cached key.
            if (!File.Exists(certificatePath))
            {
                File.WriteAllText(certificatePath, certificate.ExportCertificatePem());
            }
        }

        return certificatePath;
    }
}

internal sealed class DcpKubeconfig : IDisposable
{
    public required Uri Server { get; init; }

    public string? Token { get; init; }

    public List<X509Certificate2> CertificateAuthorityCertificates { get; init; } = [];

    public X509Certificate2? ClientCertificate { get; init; }

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
        //       certificate-authority-data: <base64 PEM or DER>
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
            throw new InvalidOperationException("DCP kubeconfig did not contain a valid server URI.");
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
        var bytes = Convert.FromBase64String(base64Data);
        var text = Encoding.UTF8.GetString(bytes);

        if (!text.Contains("-----BEGIN CERTIFICATE-----", StringComparison.Ordinal))
        {
            return [X509CertificateLoader.LoadCertificate(bytes)];
        }

        var certificates = new X509Certificate2Collection();
        certificates.ImportFromPem(text);

        return certificates.OfType<X509Certificate2>().ToList();
    }

    private static X509Certificate2 LoadClientCertificate(string certificateData, string keyData)
    {
        var certificatePem = Encoding.UTF8.GetString(Convert.FromBase64String(certificateData));
        var keyPem = Encoding.UTF8.GetString(Convert.FromBase64String(keyData));

        if (!certificatePem.Contains("-----BEGIN CERTIFICATE-----", StringComparison.Ordinal) ||
            !keyPem.Contains("PRIVATE KEY-----", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("DCP kubeconfig client certificate data was not PEM encoded.");
        }

        using var certificate = X509Certificate2.CreateFromPem(certificatePem, keyPem);
        return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pkcs12), password: null, X509KeyStorageFlags.EphemeralKeySet);
    }
}
