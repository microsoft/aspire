// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Aspire.Shared;

/// <summary>Owns fully parsed and authenticated connection material from a local DCP kubeconfig.</summary>
internal sealed class DcpConnectionMaterial : IDisposable
{
    internal const string ActivitySourceName = "Aspire.Dcp.Connection";
    private static readonly ActivitySource s_activities = new(ActivitySourceName);
    private readonly X509Certificate2 _authority;
    private readonly X509Certificate2? _certificate;
    public HttpClient Client { get; }

    private DcpConnectionMaterial(DcpKubeconfigData configuration, TimeSpan requestTimeout)
    {
        if (!Uri.TryCreate(configuration.Server, UriKind.Absolute, out var server) ||
            server.Scheme != Uri.UriSchemeHttps || !server.IsLoopback)
        {
            throw new InvalidDataException("DCP must advertise a loopback HTTPS endpoint.");
        }
        _authority = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(configuration.CertificateAuthorityData!));
        HttpClientHandler? handler = null;
        try
        {
            handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (_, certificate, chain, errors) =>
                {
                    if (certificate is null || chain is null || (errors & SslPolicyErrors.RemoteCertificateNameMismatch) != 0)
                    {
                        return false;
                    }
                    chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                    chain.ChainPolicy.CustomTrustStore.Add(_authority);
                    chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;

                    return chain.Build(certificate);
                }
            };
            if (configuration.ClientCertificateData is not null && configuration.ClientKeyData is not null)
            {
                _certificate = X509Certificate2.CreateFromPem(
                    Encoding.UTF8.GetString(Convert.FromBase64String(configuration.ClientCertificateData)),
                    Encoding.UTF8.GetString(Convert.FromBase64String(configuration.ClientKeyData)));
                handler.ClientCertificates.Add(_certificate);
            }
            Client = new HttpClient(handler) { BaseAddress = server, Timeout = requestTimeout };
            if (configuration.Token is not null)
            {
                Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", configuration.Token);
            }
        }
        catch
        {
            handler?.Dispose();
            _certificate?.Dispose();
            _authority.Dispose();
            throw;
        }
    }

    public static Task<DcpConnectionMaterial> WaitAsync(string kubeconfig, Func<bool> hasExited,
        TimeSpan requestTimeout, TimeSpan retryInterval, Action<string> diagnostic, CancellationToken cancellationToken)
        => WaitCoreAsync(kubeconfig, hasExited, requestTimeout, retryInterval, diagnostic,
            TimeProvider.System, requestTimeout, cancellationToken);

    public static Task<DcpConnectionMaterial> WaitAsync(string kubeconfig, Func<bool> hasExited,
        TimeSpan requestTimeout, TimeSpan retryInterval, Action<string> diagnostic, TimeProvider timeProvider,
        CancellationToken cancellationToken)
        => WaitCoreAsync(kubeconfig, hasExited, requestTimeout, retryInterval, diagnostic,
            timeProvider, Timeout.InfiniteTimeSpan, cancellationToken);

    private static async Task<DcpConnectionMaterial> WaitCoreAsync(string kubeconfig, Func<bool> hasExited,
        TimeSpan requestTimeout, TimeSpan retryInterval, Action<string> diagnostic, TimeProvider timeProvider,
        TimeSpan clientTimeout, CancellationToken cancellationToken)
    {
        // Only classified readiness/authentication metadata is traced. Never
        // attach kubeconfig paths, credentials, certificate bytes or HTTP bodies.
        using var activity = s_activities.StartActivity("dcp.connection.ready");
        var attempts = 0;
        try
        {
            while (true)
            {
                attempts++;
                cancellationToken.ThrowIfCancellationRequested();
                if (hasExited())
                {
                    throw new InvalidOperationException("DCP exited before publishing authenticated connection material.");
                }
                DcpConnectionMaterial? material = null;
                try
                {
                    // DCP writes scalar kubeconfig values such as:
                    //   certificate-authority-data: LS0t...
                    // A nonempty value can still be a partial base64/PEM write. Parse
                    // certificates and authenticate /readyz inside the retry boundary.
                    if (File.Exists(kubeconfig))
                    {
                        var configuration = DcpKubeconfigData.Parse(
                            await File.ReadAllTextAsync(kubeconfig, cancellationToken).ConfigureAwait(false));
                        if (configuration.Server is not null && configuration.CertificateAuthorityData is not null &&
                            (configuration.Token is not null ||
                             configuration.ClientCertificateData is not null && configuration.ClientKeyData is not null))
                        {
                            // HttpClient.Timeout cannot change after the first request.
                            // Legacy consumers retain their configured client budget;
                            // provider-aware consumers own each request deadline.
                            material = new DcpConnectionMaterial(configuration, clientTimeout);
                            using var timeout = new CancellationTokenSource(requestTimeout, timeProvider);
                            using var request = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancellationToken);
                            using var response = await material.Client.GetAsync("/readyz", request.Token).ConfigureAwait(false);
                            if (response.IsSuccessStatusCode)
                            {
                                activity?.SetTag("dcp.authentication", configuration.Token is null ? "client_certificate" : "bearer");
                                activity?.SetStatus(ActivityStatusCode.Ok);
                                return material;
                            }
                            if (response.StatusCode is not (HttpStatusCode.ServiceUnavailable or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden))
                            {
                                throw new InvalidOperationException($"DCP readiness failed ({(int)response.StatusCode}).");
                            }
                        }
                    }
                }
                catch (Exception exception) when (!hasExited() &&
                    exception is FormatException or CryptographicException or AuthenticationException or IOException ||
                    !hasExited() && exception is HttpRequestException
                    {
                        InnerException: SocketException { SocketErrorCode: SocketError.ConnectionRefused } or AuthenticationException
                    })
                {
                    diagnostic($"Waiting for complete DCP connection material: {exception.GetType().Name}.");
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !hasExited())
                {
                    diagnostic("Waiting for the DCP readiness request to complete.");
                }
                catch
                {
                    material?.Dispose();
                    throw;
                }
                material?.Dispose();
                diagnostic("Waiting for authenticated DCP readiness.");
                await Task.Delay(retryInterval, timeProvider, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            activity?.SetStatus(ActivityStatusCode.Error);
            activity?.SetTag("error.type", exception.GetType().FullName);
            throw;
        }
        finally
        {
            activity?.SetTag("dcp.readiness.attempts", attempts);
        }
    }

    public void Dispose()
    {
        Client.Dispose();
        _certificate?.Dispose();
        _authority.Dispose();
    }
}
