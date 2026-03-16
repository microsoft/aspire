// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;
using System.Collections;
using System.IO.Pipelines;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Aspire.Dashboard.Utils;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Dcp.Process;
using Aspire.Hosting.Resources;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aspire.Hosting.Dcp;

#pragma warning disable ASPIREINTERACTION001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
#pragma warning disable ASPIRECERTIFICATES001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
#pragma warning disable ASPIREFILESYSTEM001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

internal sealed class DcpHost
{
    private const int LoggingSocketConnectionBacklog = 3;

    private readonly DistributedApplicationModel _applicationModel;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private readonly DcpOptions _dcpOptions;
    private readonly IDcpDependencyCheckService _dependencyCheckService;
    private readonly IInteractionService _interactionService;
    private readonly Locations _locations;
    private readonly TimeProvider _timeProvider;
    private readonly IDeveloperCertificateService _developerCertificateService;
    private readonly IFileSystemService _fileSystemService;
    private readonly IConfiguration _configuration;
    private readonly CancellationTokenSource _shutdownCts = new();
    private string? _dcpTlsCertFilePath;
    private string? _dcpTlsKeyFilePath;
    private Task? _logProcessorTask;

    // These environment variables should never be inherited by DCP from app host.
    private static readonly string[] s_doNotInheritEnvironmentVars =
    {
        "ASPNETCORE_URLS",
        "DOTNET_LAUNCH_PROFILE",
        "ASPNETCORE_ENVIRONMENT",
        "DOTNET_ENVIRONMENT"
    };

    public DcpHost(
        ILoggerFactory loggerFactory,
        IOptions<DcpOptions> dcpOptions,
        IDcpDependencyCheckService dependencyCheckService,
        IInteractionService interactionService,
        Locations locations,
        DistributedApplicationModel applicationModel,
        TimeProvider timeProvider,
        IDeveloperCertificateService developerCertificateService,
        IFileSystemService fileSystemService,
        IConfiguration configuration)
    {
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<DcpHost>();
        _dcpOptions = dcpOptions.Value;
        _dependencyCheckService = dependencyCheckService;
        _interactionService = interactionService;
        _locations = locations;
        _applicationModel = applicationModel;
        _timeProvider = timeProvider;
        _developerCertificateService = developerCertificateService;
        _fileSystemService = fileSystemService;
        _configuration = configuration;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await EnsureDcpContainerRuntimeAsync(cancellationToken).ConfigureAwait(false);
        await EnsureDevelopmentCertificateTrustAsync(cancellationToken).ConfigureAwait(false);
        await PrepareDcpTlsCertificateAsync(cancellationToken).ConfigureAwait(false);
        EnsureDcpHostRunning();
    }

    internal async Task EnsureDcpContainerRuntimeAsync(CancellationToken cancellationToken)
    {
        // Ensure DCP is installed and has all required dependencies
        var dcpInfo = await _dependencyCheckService.GetDcpInfoAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

        if (dcpInfo is null)
        {
            return;
        }

        // If we don't have any resources that need a container then we
        // don't need to check for a healthy container runtime.
        if (!_applicationModel.Resources.Any(c => c.IsContainer()))
        {
            return;
        }

        AspireEventSource.Instance.ContainerRuntimeHealthCheckStart();

        try
        {
            bool requireContainerRuntimeInitialization = _dcpOptions.ContainerRuntimeInitializationTimeout > TimeSpan.Zero;
            if (requireContainerRuntimeInitialization)
            {
                using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCancellation.CancelAfter(_dcpOptions.ContainerRuntimeInitializationTimeout);

                try
                {
                    while (dcpInfo is not null && !IsContainerRuntimeHealthy(dcpInfo))
                    {
                        await Task.Delay(TimeSpan.FromSeconds(2), timeoutCancellation.Token).ConfigureAwait(false);
                        dcpInfo = await _dependencyCheckService.GetDcpInfoAsync(force: true, cancellationToken: timeoutCancellation.Token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (timeoutCancellation.IsCancellationRequested)
                {
                    // Swallow the cancellation exception and let it bubble up as a more helpful error
                    // about the container runtime in CheckDcpInfoAndLogErrors.
                }
            }

            if (dcpInfo is not null)
            {
                DcpDependencyCheck.CheckDcpInfoAndLogErrors(_logger, _dcpOptions, dcpInfo, throwIfUnhealthy: requireContainerRuntimeInitialization);

                // Show UI notification if container runtime is unhealthy
                TryShowContainerRuntimeNotification(dcpInfo, cancellationToken);
            }
        }
        finally
        {
            AspireEventSource.Instance.ContainerRuntimeHealthCheckStop();
        }
    }

    internal async Task EnsureDevelopmentCertificateTrustAsync(CancellationToken cancellationToken)
    {
        AspireEventSource.Instance.DevelopmentCertificateTrustCheckStart();

        try
        {
            // If no resources use HTTPS/TLS, there's no need to warn about untrusted dev certificates.
            if (!_applicationModel.Resources.Any(ResourceUsesTls))
            {
                return;
            }

            // Check and warn if no trusted dev certs exist, or if a newer untrusted cert was detected
            var hasNewerUntrustedCert = _developerCertificateService.LatestCertificateIsUntrusted;
            var hasNoTrustedCerts = _developerCertificateService.Certificates.Count == 0;

            if (hasNoTrustedCerts || hasNewerUntrustedCert)
            {
                string title;
                string message;

                if (hasNoTrustedCerts)
                {
                    title = InteractionStrings.NoDeveloperCertificateTrustedTitle;
                    message = InteractionStrings.NoDeveloperCertificateTrustedMessage;
                    _logger.LogWarning("No trusted Aspire development certificate was found. See https://aka.ms/aspire/devcerts for more information.");
                }
                else
                {
                    title = InteractionStrings.DeveloperCertificateNotFullyTrustedTitle;
                    message = InteractionStrings.DeveloperCertificateNotFullyTrustedMessage;
                    _logger.LogWarning("The most recent development certificate isn't fully trusted. See https://aka.ms/aspire/devcerts for more information.");
                }

                // Check if the interaction service is available (dashboard enabled)
                if (!_interactionService.IsAvailable)
                {
                    return;
                }

                // Send notification to the dashboard
                _ = _interactionService.PromptNotificationAsync(
                    title: title,
                    message: message,
                    options: new NotificationInteractionOptions
                    {
                        Intent = MessageIntent.Error,
                    },
                    cancellationToken: cancellationToken);
            }
        }
        finally
        {
            AspireEventSource.Instance.DevelopmentCertificateTrustCheckStop();
        }
    }

    internal async Task PrepareDcpTlsCertificateAsync(CancellationToken cancellationToken)
    {
        // Using the ASP.NET dev cert for DCP TLS is opt-in; by default DCP uses its own ephemeral certificate.
        if (!_configuration.GetBool(KnownConfigNames.DcpDeveloperCertificate, defaultValue: false))
        {
            return;
        }

        // Check if we have a trusted developer certificate with a private key available
        var certificates = _developerCertificateService.Certificates;
        if (certificates.Count == 0)
        {
            return;
        }

        // Use the first (latest/best) certificate that has a private key
        X509Certificate2? certificate = null;
        foreach (var cert in certificates)
        {
            if (cert.HasPrivateKey)
            {
                certificate = cert;
                break;
            }
        }

        if (certificate is null)
        {
            return;
        }

        try
        {
            var sessionDir = _locations.DcpSessionDir;

            // Export the public certificate chain in PEM format
            var certPem = certificate.ExportCertificatePem();
            var certFilePath = Path.Combine(sessionDir, "dcp-tls.crt");
            File.WriteAllText(certFilePath, certPem);

            // Export the private key using the shared caching logic to avoid macOS keychain prompts
            var (keyPem, _) = await DeveloperCertificateService.GetKeyMaterialAsync(
                certificate, password: null, needKeyPem: true, needPfx: false, cancellationToken).ConfigureAwait(false);

            if (keyPem is null)
            {
                _logger.LogWarning("Failed to export the private key for the developer certificate. DCP will use its default certificate.");
                return;
            }

            var keyFilePath = Path.Combine(sessionDir, "dcp-tls.key");

            if (OperatingSystem.IsWindows())
            {
                File.WriteAllText(keyFilePath, new string(keyPem));
            }
            else
            {
                // Write the key file with restricted permissions on Unix
                using var fs = new FileStream(keyFilePath, FileMode.Create, FileAccess.Write, FileShare.None);
                File.SetUnixFileMode(keyFilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                using var writer = new StreamWriter(fs);
                writer.Write(keyPem);
            }

            Array.Clear(keyPem, 0, keyPem.Length);

            _dcpTlsCertFilePath = certFilePath;
            _dcpTlsKeyFilePath = keyFilePath;

            _logger.LogDebug("Prepared DCP TLS certificate at {CertPath} with key at {KeyPath}.", certFilePath, keyFilePath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to prepare TLS certificate for DCP. DCP will use its default certificate.");
        }
    }

    public async Task StopAsync()
    {
        _shutdownCts.Cancel();

        await TaskHelpers.WaitIgnoreCancelAsync(_logProcessorTask, _logger, "Error in logging socket processor.").ConfigureAwait(false);
    }

    private void EnsureDcpHostRunning()
    {
        AspireEventSource.Instance.DcpApiServerLaunchStart();

        try
        {
            var dcpProcessSpec = CreateDcpProcessSpec(_locations);

            // Enable Unix Domain Socket based log streaming from DCP
            try
            {
                AspireEventSource.Instance.DcpLogSocketCreateStart();
                var loggingSocket = CreateLoggingSocket(_locations.DcpLogSocket);
                loggingSocket.Listen(LoggingSocketConnectionBacklog);

                dcpProcessSpec.EnvironmentVariables.Add("DCP_LOG_SOCKET", _locations.DcpLogSocket);
                if (!string.IsNullOrWhiteSpace(_dcpOptions.LogFileNameSuffix))
                {
                    dcpProcessSpec.EnvironmentVariables.Add("DCP_LOG_FILE_NAME_SUFFIX", _dcpOptions.LogFileNameSuffix);
                }

                _logProcessorTask = Task.Run(() => StartLoggingSocketAsync(loggingSocket));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to enable orchestration logging.");
            }
            finally
            {
                AspireEventSource.Instance.DcpLogSocketCreateStop();
            }

            _ = ProcessUtil.Run(dcpProcessSpec);
        }
        finally
        {
            AspireEventSource.Instance.DcpApiServerLaunchStop();
        }

    }

    private ProcessSpec CreateDcpProcessSpec(Locations locations)
    {
        var dcpExePath = _dcpOptions.CliPath;
        if (!File.Exists(dcpExePath))
        {
            throw new FileNotFoundException($"The Developer Control Plane is not installed at \"{dcpExePath}\". The application cannot be run without it.", dcpExePath);
        }

        var arguments = $"start-apiserver --monitor {Environment.ProcessId} --detach --kubeconfig \"{locations.DcpKubeconfigPath}\"";
        if (!string.IsNullOrEmpty(_dcpOptions.ContainerRuntime))
        {
            arguments += $" --container-runtime \"{_dcpOptions.ContainerRuntime}\"";
        }

        if (_dcpTlsCertFilePath is not null && _dcpTlsKeyFilePath is not null)
        {
            arguments += $" --tls-cert-file \"{_dcpTlsCertFilePath}\" --tls-private-key-file \"{_dcpTlsKeyFilePath}\"";
        }

        var dcpProcessSpec = new ProcessSpec(dcpExePath)
        {
            WorkingDirectory = Directory.GetCurrentDirectory(),
            Arguments = arguments,
            OnOutputData = Console.Out.Write,
            OnErrorData = Console.Error.Write,
            InheritEnv = false,
        };

        _logger.LogInformation("Starting DCP with arguments: {Arguments}", dcpProcessSpec.Arguments);

        if (!string.IsNullOrEmpty(_dcpOptions.ExtensionsPath))
        {
            dcpProcessSpec.EnvironmentVariables.Add("DCP_EXTENSIONS_PATH", _dcpOptions.ExtensionsPath);
        }

        // Set an environment variable to contain session info that should be deleted when DCP is done
        // Currently this contains the Unix socket for logging and the kubeconfig
        dcpProcessSpec.EnvironmentVariables.Add("DCP_SESSION_FOLDER", locations.DcpSessionDir);

        foreach (DictionaryEntry de in Environment.GetEnvironmentVariables())
        {
            var key = de.Key?.ToString();
            var val = de.Value?.ToString();
            if (key is not null && val is not null && !s_doNotInheritEnvironmentVars.Contains(key))
            {
                dcpProcessSpec.EnvironmentVariables[key] = val;
            }
        }

        // Set diagnostic log folder if configured (takes precedence over environment variable)
        if (!string.IsNullOrEmpty(_dcpOptions.DiagnosticsLogFolder))
        {
            dcpProcessSpec.EnvironmentVariables["DCP_DIAGNOSTICS_LOG_FOLDER"] = _dcpOptions.DiagnosticsLogFolder;
        }

        // Set diagnostic log level if configured (takes precedence over environment variable)
        if (!string.IsNullOrEmpty(_dcpOptions.DiagnosticsLogLevel))
        {
            dcpProcessSpec.EnvironmentVariables["DCP_DIAGNOSTICS_LOG_LEVEL"] = _dcpOptions.DiagnosticsLogLevel;
        }

        // Set preserve executable logs if configured (takes precedence over environment variable)
        if (_dcpOptions.PreserveExecutableLogs == true)
        {
            dcpProcessSpec.EnvironmentVariables["DCP_PRESERVE_EXECUTABLE_LOGS"] = "1";
        }

        return dcpProcessSpec;
    }

    private static Socket CreateLoggingSocket(string socketPath)
    {
        var directoryName = Path.GetDirectoryName(socketPath);
        if (!string.IsNullOrEmpty(directoryName))
        {
            if (OperatingSystem.IsWindows())
            {
                Directory.CreateDirectory(directoryName);
            }
            else
            {
                Directory.CreateDirectory(directoryName, UnixFileMode.UserExecute | UnixFileMode.UserWrite | UnixFileMode.UserRead);
            }
        }

        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        socket.Bind(new UnixDomainSocketEndPoint(socketPath));

        return socket;
    }

    private async Task StartLoggingSocketAsync(Socket socket)
    {
        List<Task> outputLoggers = [];
        while (!_shutdownCts.IsCancellationRequested)
        {
            try
            {
                var acceptedSocket = await socket.AcceptAsync(_shutdownCts.Token).ConfigureAwait(false);
                outputLoggers.Add(Task.Run(() => LogSocketOutputAsync(acceptedSocket, _shutdownCts.Token)));
            }
            catch
            {
                // Suppress exceptions reading logs from DCP controllers
            }
        }

        await Task.WhenAll(outputLoggers).ConfigureAwait(false);
        socket.Dispose();
    }

    private async Task LogSocketOutputAsync(Socket socket, CancellationToken cancellationToken)
    {
        using var stream = new NetworkStream(socket, ownsSocket: true);
        using var _ = cancellationToken.Register(s => ((NetworkStream)s!).Close(), stream);
        var reader = PipeReader.Create(stream);

        // Logger cache to avoid creating a new string per log line, for a few categories
        var loggerCache = new Dictionary<int, ILogger>();

        (ILogger, LogLevel, string message) GetLogInfo(ReadOnlySpan<byte> line)
        {
            if (!DcpLogParser.TryParseDcpLog(line, out var parsedMessage, out var logLevel, out var category))
            {
                // If parsing fails, return a default logger and the line as-is
                return (_logger, LogLevel.Debug, Encoding.UTF8.GetString(line));
            }

            var hash = new HashCode();
            hash.AddBytes(Encoding.UTF8.GetBytes(category));
            var hashValue = hash.ToHashCode();

            if (!loggerCache.TryGetValue(hashValue, out var logger))
            {
                // loggerFactory.CreateLogger internally caches, but we may as well cache the logger as well as the string
                // for the lifetime of this socket
                loggerCache[hashValue] = logger = _loggerFactory.CreateLogger($"Aspire.Hosting.Dcp.{category}");
            }

            // Map DCP log levels to Debug/Trace to reduce noise in AppHost output.
            // DCP errors are now flowing to resources and can be hidden from output,
            // so we log them at Debug or Trace level instead of using the original DCP log level.
            var appHostLogLevel = logLevel == LogLevel.Trace ? LogLevel.Trace : LogLevel.Debug;

            return (logger, appHostLogLevel, parsedMessage);
        }

        try
        {
            void LogLines(in ReadOnlySequence<byte> buffer, out SequencePosition position)
            {
                var seq = new SequenceReader<byte>(buffer);
                while (seq.TryReadTo(out ReadOnlySpan<byte> line, (byte)'\n'))
                {
                    var (logger, logLevel, message) = GetLogInfo(line);

                    logger.Log(logLevel, 0, message, null, static (value, ex) => value);
                }

                position = seq.Position;
            }

            while (!cancellationToken.IsCancellationRequested)
            {
                var result = await reader.ReadAsync(CancellationToken.None).ConfigureAwait(false);

                if (result.IsCompleted || result.IsCanceled)
                {
                    break;
                }

                LogLines(result.Buffer, out var position);

                reader.AdvanceTo(position, result.Buffer.End);
            }
        }
        catch
        {
            // Suppress exceptions reading logs from DCP controllers
        }
        finally
        {
            reader.Complete();
        }
    }

    private void TryShowContainerRuntimeNotification(DcpInfo dcpInfo, CancellationToken cancellationToken)
    {
        // Check if the interaction service is available (dashboard enabled)
        if (!_interactionService.IsAvailable)
        {
            return;
        }

        var containerRuntime = _dcpOptions.ContainerRuntime;
        if (string.IsNullOrEmpty(containerRuntime))
        {
            // Default runtime is Docker
            containerRuntime = "docker";
        }

        var installed = dcpInfo.Containers?.Installed ?? false;
        var running = dcpInfo.Containers?.Running ?? false;

        // Early check: if container runtime is not installed, show notification and return immediately (no polling)
        if (!installed)
        {
            string title = InteractionStrings.ContainerRuntimeNotInstalledTitle;
            string message = InteractionStrings.ContainerRuntimeNotInstalledMessage;

            var options = new NotificationInteractionOptions
            {
                Intent = MessageIntent.Error,
                LinkText = InteractionStrings.ContainerRuntimeLinkText,
                LinkUrl = "https://aka.ms/dotnet/aspire/containers"
            };

            // Show notification without polling (non-auto-dismiss)
            _ = _interactionService.PromptNotificationAsync(title, message, options, cancellationToken);
            return;
        }

        // Only show notification if container runtime is installed but not running
        // If not installed, that's usually a more fundamental setup issue that would be addressed differently
        if (installed && !running)
        {
            string title = InteractionStrings.ContainerRuntimeUnhealthyTitle;
            var (message, linkUrl) = DcpDependencyCheck.BuildContainerRuntimeUnhealthyMessage(containerRuntime);

            var options = new NotificationInteractionOptions
            {
                Intent = MessageIntent.Error,
                LinkText = linkUrl is not null ? InteractionStrings.ContainerRuntimeLinkText : null,
                LinkUrl = linkUrl
            };

            // Create a cancellation token source that can be cancelled when runtime becomes healthy
            var notificationCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdownCts.Token);

            // Single background task to show notification and poll for health updates
            _ = Task.Run(async () =>
            {
                try
                {
                    // First, show the notification
                    var notificationTask = _interactionService.PromptNotificationAsync(title, message, options, notificationCts.Token);

                    // Then poll for container runtime health updates every 5 seconds
                    using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5), _timeProvider);
                    while (await timer.WaitForNextTickAsync(notificationCts.Token).ConfigureAwait(false))
                    {
                        try
                        {
                            var dcpInfo = await _dependencyCheckService.GetDcpInfoAsync(force: true, cancellationToken: notificationCts.Token).ConfigureAwait(false);

                            if (dcpInfo is not null && IsContainerRuntimeHealthy(dcpInfo))
                            {
                                // Container runtime is now healthy, exit the polling loop
                                break;
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            // Expected when cancellation is requested
                            break;
                        }
                        catch (Exception ex)
                        {
                            // Log but continue polling
                            _logger.LogDebug(ex, "Error while polling container runtime health for notification");
                        }
                    }

                    // Cancel the notification at the end of the loop
                    notificationCts.Cancel();

                    // Wait for notification task to complete
                    try
                    {
                        await notificationTask.ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        // Expected when notification is cancelled
                    }
                }
                catch (OperationCanceledException)
                {
                    // Expected when cancellation is requested
                }
                catch (Exception ex)
                {
                    // Log but don't propagate notification errors
                    _logger.LogDebug(ex, "Failed to show container runtime notification or poll for health");
                }
                finally
                {
                    notificationCts.Dispose();
                }
            }, cancellationToken);
        }
    }

    private static bool IsContainerRuntimeHealthy(DcpInfo dcpInfo)
    {
        var installed = dcpInfo.Containers?.Installed ?? false;
        var running = dcpInfo.Containers?.Running ?? false;
        return installed && running;
    }

    /// <summary>
    /// Determines whether a resource uses HTTPS/TLS by checking for HTTPS endpoint annotations
    /// or active HTTPS certificate configuration callbacks that haven't been disabled.
    /// </summary>
    private static bool ResourceUsesTls(IResource resource)
    {
        // Check if the resource has any HTTPS endpoints
        if (resource.Annotations.OfType<EndpointAnnotation>().Any(e => e.UriScheme is "https"))
        {
            return true;
        }

        // Check if the resource has an HTTPS certificate configuration callback that hasn't been
        // disabled via WithoutHttpsCertificate(). HttpsCertificateAnnotation has no effect without
        // HttpsCertificateConfigurationCallbackAnnotation, so it's only checked as a filter here.
        if (resource.Annotations.OfType<HttpsCertificateConfigurationCallbackAnnotation>().Any())
        {
            // The callback is present. Check if it's been disabled by WithoutHttpsCertificate()
            // which sets UseDeveloperCertificate = false and Certificate = null.
            if (resource.TryGetLastAnnotation<HttpsCertificateAnnotation>(out var certAnnotation)
                && certAnnotation.UseDeveloperCertificate is false or null
                && certAnnotation.Certificate is null)
            {
                return false;
            }

            return true;
        }

        return false;
    }
}

#pragma warning restore ASPIREINTERACTION001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
