// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Aspire.Cli.DotNet;
using Aspire.Cli.Resources;
using Aspire.Shared;
using Microsoft.AspNetCore.Certificates.Generation;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Utils.EnvironmentChecker;

internal interface IDcpConnectionChecker
{
    Task<EnvironmentCheckResult> TestConnectionAsync(string dcpDirectory, bool useDeveloperCertificate, CancellationToken cancellationToken);
}

internal sealed class DcpConnectionChecker(
    CertificateManager certificateManager,
    IProcessExecutionFactory processExecutionFactory,
    CliExecutionContext executionContext,
    ILogger<DcpConnectionChecker> logger) : IDcpConnectionChecker
{
    private static readonly TimeSpan s_connectionTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan s_processExitTimeout = TimeSpan.FromSeconds(5);

    public async Task<EnvironmentCheckResult> TestConnectionAsync(string dcpDirectory, bool useDeveloperCertificate, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(s_connectionTimeout);

        try
        {
            await using var session = await DcpConnectionTestSession.StartAsync(
                dcpDirectory,
                useDeveloperCertificate,
                certificateManager,
                processExecutionFactory,
                executionContext,
                logger,
                timeoutCts.Token).ConfigureAwait(false);

            using var kubeconfig = await session.ReadKubeconfigAsync(timeoutCts.Token).ConfigureAwait(false);
            using var handler = CreateHttpClientHandler(kubeconfig);
            using var client = new HttpClient(handler)
            {
                BaseAddress = kubeconfig.Server,
                Timeout = s_connectionTimeout
            };

            if (!string.IsNullOrWhiteSpace(kubeconfig.Token))
            {
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", kubeconfig.Token);
            }

            using var response = await client.GetAsync(new Uri(kubeconfig.Server, "/version"), timeoutCts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return Failed(
                    useDeveloperCertificate,
                    details: string.Format(
                        CultureInfo.CurrentCulture,
                        DoctorCommandStrings.DcpApiServerReturnedHttpStatusMessageFormat,
                        (int)response.StatusCode,
                        response.StatusCode));
            }

            await session.StopDcpAsync(client, kubeconfig, timeoutCts.Token).ConfigureAwait(false);

            return useDeveloperCertificate
                ? Passed(useDeveloperCertificate, DoctorCommandStrings.DcpDeveloperCertificateConnectionSucceededMessage)
                : Passed(useDeveloperCertificate, DoctorCommandStrings.DcpEphemeralCertificateConnectionSucceededMessage);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Failed(
                useDeveloperCertificate,
                details: string.Format(
                    CultureInfo.CurrentCulture,
                    DoctorCommandStrings.DcpConnectionTimedOutMessageFormat,
                    s_connectionTimeout.TotalSeconds));
        }
        catch (DcpDeveloperCertificateUnavailableException ex)
        {
            return CreateResult(
                useDeveloperCertificate,
                EnvironmentCheckStatus.Warning,
                DoctorCommandStrings.DcpDeveloperCertificateUnavailableMessage,
                details: ex.Message,
                fix: DoctorCommandStrings.DcpDeveloperCertificateTrustFix);
        }
        catch (AuthenticationException ex)
        {
            return Failed(
                useDeveloperCertificate,
                details: string.Format(CultureInfo.CurrentCulture, DoctorCommandStrings.DcpTlsAuthenticationFailedDetailsFormat, ex.Message),
                useDeveloperCertificate ? DoctorCommandStrings.DcpDeveloperCertificateRepairTrustFix : null);
        }
        catch (HttpRequestException ex)
        {
            return Failed(useDeveloperCertificate, details: ex.Message);
        }
        catch (Exception ex)
        {
            return Failed(useDeveloperCertificate, details: ex.Message);
        }
    }

    private static HttpClientHandler CreateHttpClientHandler(DcpKubeconfig kubeconfig)
    {
        var handler = new HttpClientHandler();
        if (kubeconfig.ClientCertificate is not null)
        {
            // DCP writes client certificate material into the generated kubeconfig. Supplying it
            // here validates that the kubeconfig is sufficient for client authentication without
            // relying on AppHost-specific process setup.
            handler.ClientCertificates.Add(kubeconfig.ClientCertificate);
        }

        if (kubeconfig.CertificateAuthorityCertificates.Count > 0)
        {
            // DCP's generated kubeconfig includes the CA that should authenticate the API server.
            // Trust that CA explicitly so this probe validates the DCP-issued connection material
            // instead of relying on the OS/user certificate stores. Other TLS policy failures still
            // fail the request because the goal is only to replace chain trust, not hostname or
            // certificate validity checks.
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

    private static EnvironmentCheckResult Passed(bool useDeveloperCertificate, string message) =>
        CreateResult(useDeveloperCertificate, EnvironmentCheckStatus.Pass, message);

    private static EnvironmentCheckResult Failed(bool useDeveloperCertificate, string? details = null, string? fix = null) =>
        CreateResult(
            useDeveloperCertificate,
            EnvironmentCheckStatus.Fail,
            useDeveloperCertificate
                ? DoctorCommandStrings.DcpDeveloperCertificateConnectionFailedMessage
                : DoctorCommandStrings.DcpEphemeralCertificateConnectionFailedMessage,
            details,
            fix);

    private static EnvironmentCheckResult CreateResult(bool useDeveloperCertificate, EnvironmentCheckStatus status, string message, string? details = null, string? fix = null)
    {
        return new EnvironmentCheckResult
        {
            Category = EnvironmentCheckCategories.Aspire,
            Name = CheckName(useDeveloperCertificate),
            Status = status,
            Message = message,
            Details = details,
            Fix = fix
        };
    }

    private static string CheckName(bool useDeveloperCertificate)
    {
        return useDeveloperCertificate
            ? DcpConnectionHealthCheck.DeveloperCertificateCheckName
            : DcpConnectionHealthCheck.EphemeralCertificateCheckName;
    }

    private sealed class DcpConnectionTestSession : IAsyncDisposable
    {
        private readonly IProcessExecution _process;
        private readonly string _sessionDirectory;
        private readonly string _kubeconfigPath;
        private readonly OutputCollector _output;
        private readonly ILogger _logger;
        private bool _stopRequested;

        private DcpConnectionTestSession(IProcessExecution process, string sessionDirectory, string kubeconfigPath, OutputCollector output, ILogger logger)
        {
            _process = process;
            _sessionDirectory = sessionDirectory;
            _kubeconfigPath = kubeconfigPath;
            _output = output;
            _logger = logger;
        }

        public static async Task<DcpConnectionTestSession> StartAsync(
            string dcpDirectory,
            bool useDeveloperCertificate,
            CertificateManager certificateManager,
            IProcessExecutionFactory processExecutionFactory,
            CliExecutionContext executionContext,
            ILogger logger,
            CancellationToken cancellationToken)
        {
            var sessionDirectory = Directory.CreateTempSubdirectory("aspire-dcp-doctor-").FullName;
            var kubeconfigPath = Path.Combine(sessionDirectory, "kubeconfig");
            var dcpExecutablePath = BundleDiscovery.GetDcpExecutablePath(dcpDirectory);
            var output = new OutputCollector();
            IProcessExecution? process = null;

            try
            {
                var arguments = new List<string>
                {
                    "start-apiserver",
                    "--kubeconfig",
                    kubeconfigPath
                };

                if (useDeveloperCertificate)
                {
                    AddDeveloperCertificateArguments(arguments, certificateManager);
                }

                var environmentVariables = new Dictionary<string, string>();
                var extensionsPath = Path.Combine(dcpDirectory, "ext");
                if (Directory.Exists(extensionsPath))
                {
                    environmentVariables["DCP_EXTENSIONS_PATH"] = extensionsPath;
                }

                // DCP uses this folder for process-scoped state such as the generated kubeconfig.
                // Keeping it under the doctor-owned temp directory prevents overlap with AppHost sessions.
                environmentVariables["DCP_SESSION_FOLDER"] = sessionDirectory;

                var options = new ProcessInvocationOptions
                {
                    StandardOutputCallback = output.AppendOutput,
                    StandardErrorCallback = output.AppendError
                };

                process = processExecutionFactory.CreateExecution(
                    dcpExecutablePath,
                    arguments.ToArray(),
                    environmentVariables,
                    executionContext.WorkingDirectory,
                    options);

                if (!process.Start())
                {
                    throw new InvalidOperationException(DoctorCommandStrings.DcpStartFailedMessage);
                }

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

            return await DcpKubeconfig.ReadFileWithRetryAsync(_kubeconfigPath, cancellationToken: cancellationToken).ConfigureAwait(false);
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
                    Content = new StringContent("""{"status":"Stopping","shutdownResourceCleanup":"None"}""", Encoding.UTF8, "application/merge-patch+json")
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
                    using var exitCts = new CancellationTokenSource(s_processExitTimeout);
                    await _process.WaitForExitAsync(exitCts.Token).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
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
                        string.Format(
                            CultureInfo.CurrentCulture,
                            DoctorCommandStrings.DcpExitedBeforeKubeconfigDetailsFormat,
                            _process.ExitCode,
                            Environment.NewLine,
                            GetRecentOutput(_output)));
                }

                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
            }
        }

        private static string GetRecentOutput(OutputCollector output)
        {
            var lines = output.GetLines()
                .TakeLast(40)
                .Select(line => $"{line.Stream}: {line.Line}")
                .ToArray();

            return lines.Length == 0 ? DoctorCommandStrings.DcpNoOutputDetails : string.Join(Environment.NewLine, lines);
        }

        private static void AddDeveloperCertificateArguments(List<string> arguments, CertificateManager certificateManager)
        {
            var certificates = certificateManager.ListCertificates(StoreName.My, StoreLocation.CurrentUser, isValid: true);
            try
            {
                var certificate = certificates.FirstOrDefault(c =>
                    c.HasPrivateKey &&
                    certificateManager.GetTrustLevel(c) == CertificateManager.TrustLevel.Full);

                if (certificate is null)
                {
                    throw new DcpDeveloperCertificateUnavailableException(DoctorCommandStrings.DcpDeveloperCertificateNoTrustedExportableDetails);
                }

                if (string.IsNullOrWhiteSpace(certificate.Thumbprint))
                {
                    throw new DcpDeveloperCertificateUnavailableException(DoctorCommandStrings.DcpDeveloperCertificateMissingThumbprintDetails);
                }

                arguments.Add("--tls-cert-thumbprint");
                arguments.Add(certificate.Thumbprint);

                if (OperatingSystem.IsWindows())
                {
                    return;
                }

                var certificatePath = DcpDeveloperCertificateCache.EnsureDeveloperCertificateCache(certificateManager, certificate);
                var keyPath = Path.ChangeExtension(certificatePath, ".key");

                arguments.Add("--tls-cert-file");
                arguments.Add(certificatePath);
                arguments.Add("--tls-key-file");
                arguments.Add(keyPath);
            }
            finally
            {
                CertificateManager.DisposeCertificates(certificates);
            }
        }

    }
}

internal sealed class DcpDeveloperCertificateUnavailableException(string message) : Exception(message);
